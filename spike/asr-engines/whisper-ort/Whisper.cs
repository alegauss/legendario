using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Legendario.Spike.WhisperOrt;

/// <summary>
/// Whisper as three ONNX graphs (encoder, first-step decoder, decoder with past) and a greedy
/// loop around them. The encoder's output and the KV caches stay as OrtValues between calls.
/// </summary>
internal sealed class WhisperModel : IDisposable
{
    private const int MaxTokens = 200;
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly InferenceSession _decoderWithPast;
    private readonly RunOptions _run = new();
    private readonly Tokenizer _tokenizer;
    private readonly long[] _prompt;

    public WhisperModel(string dir, string language, Func<SessionOptions> options)
    {
        using var pre = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "preprocessor_config.json")));
        Mels = pre.RootElement.GetProperty("feature_size").GetInt32();
        _tokenizer = new Tokenizer(dir);
        _prompt =
        [
            _tokenizer.Special("<|startoftranscript|>"),
            _tokenizer.Special($"<|{language}|>"),
            _tokenizer.Special("<|transcribe|>"),
            _tokenizer.Special("<|notimestamps|>"),
        ];

        var onnx = Path.Combine(dir, "onnx");
        _encoder = new InferenceSession(Path.Combine(onnx, "encoder_model_fp16.onnx"), options());
        _decoder = new InferenceSession(Path.Combine(onnx, "decoder_model_fp16.onnx"), options());
        _decoderWithPast = new InferenceSession(Path.Combine(onnx, "decoder_with_past_model_fp16.onnx"), options());
    }

    public int Mels { get; }
    public double EncoderMs { get; private set; }
    public double DecoderMs { get; private set; }
    public int Tokens { get; private set; }

    /// <summary>
    /// When set, every run goes through an IoBinding and every tensor except the logits stays in
    /// this device memory, so the encoder output and both KV caches never cross to the CPU.
    /// </summary>
    public OrtMemoryInfo? DeviceMemory { get; init; }

    public string Transcribe(float[] samples) => DeviceMemory == null ? TranscribeCopying(samples) : TranscribeBound(samples, DeviceMemory);

    private IDisposableReadOnlyCollection<OrtValue> RunBound(InferenceSession session, Func<string, OrtValue> input, OrtMemoryInfo device, out string[] names)
    {
        var binding = session.CreateIoBinding();
        _bindings.Add(binding); // disposed when the transcription ends, after its outputs
        foreach (var n in session.InputMetadata.Keys)
        {
            binding.BindInput(n, input(n));
        }
        names = session.OutputMetadata.Keys.ToArray();
        foreach (var n in names)
        {
            binding.BindOutputToDevice(n, n == "logits" || Environment.GetEnvironmentVariable("LE2_CPU_OUT") == "1" ? OrtMemoryInfo.DefaultInstance : device);
        }
        session.RunWithBinding(_run, binding);
        return binding.GetOutputValues();
    }

    private readonly List<IDisposable> _bindings = new();

    private string TranscribeBound(float[] samples, OrtMemoryInfo device)
    {
        try
        {
            return TranscribeBoundCore(samples, device);
        }
        finally
        {
            _bindings.ForEach(b => b.Dispose());
            _bindings.Clear();
        }
    }

    private string TranscribeBoundCore(float[] samples, OrtMemoryInfo device)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mel = LogMel.Compute(samples, Mels);
        using var features = Tensor(_encoder, "input_features", mel, [1, Mels, LogMel.Frames]);
        using var encoded = RunBound(_encoder, _ => features, device, out _);
        EncoderMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var tokens = new List<long>();
        var eot = _tokenizer.Special("<|endoftext|>");
        using var ids = OrtValue.CreateTensorValueFromMemory(_prompt.ToArray(), [1, _prompt.Length]);
        using var first = RunBound(_decoder, n => n == "input_ids" ? ids : encoded[0], device, out var firstNames);
        var cache = new Dictionary<string, OrtValue>();
        var next = 0L;
        for (var i = 0; i < firstNames.Length; i++)
        {
            if (firstNames[i] == "logits")
            {
                next = ArgMax(first[i], eot);
            }
            else
            {
                cache["past_key_values." + firstNames[i]["present.".Length..]] = first[i];
            }
        }

        IDisposable? previous = null;
        while (next != eot && tokens.Count < MaxTokens)
        {
            tokens.Add(next);
            using var one = OrtValue.CreateTensorValueFromMemory(new[] { next }, [1, 1]);
            var step = RunBound(_decoderWithPast, n => n == "input_ids" ? one : n == "encoder_hidden_states" ? encoded[0] : cache[n], device, out var names);
            for (var i = 0; i < names.Length; i++)
            {
                if (names[i] == "logits")
                {
                    next = ArgMax(step[i], eot);
                }
                else
                {
                    cache["past_key_values." + names[i]["present.".Length..]] = step[i];
                }
            }
            previous?.Dispose();
            previous = step;
        }
        previous?.Dispose();
        DecoderMs = sw.Elapsed.TotalMilliseconds;
        Tokens = tokens.Count;
        return _tokenizer.Decode(tokens);
    }

    private string TranscribeCopying(float[] samples)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mel = LogMel.Compute(samples, Mels);
        using var features = Tensor(_encoder, "input_features", mel, [1, Mels, LogMel.Frames]);
        using var encoded = _encoder.Run(_run, ["input_features"], [features], ["last_hidden_state"]);
        EncoderMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var tokens = new List<long>();
        var eot = _tokenizer.Special("<|endoftext|>");

        // First step: the whole prompt, which also yields the cross-attention cache.
        var decIn = _decoder.InputMetadata.Keys.ToArray();
        var decOut = _decoder.OutputMetadata.Keys.ToArray();
        using var ids = OrtValue.CreateTensorValueFromMemory(_prompt.ToArray(), [1, _prompt.Length]);
        var firstInputs = decIn.Select(n => n == "input_ids" ? ids : encoded[0]).ToArray();
        using var first = _decoder.Run(_run, decIn, firstInputs, decOut);
        var next = ArgMax(first[0], eot);

        var pastIn = _decoderWithPast.InputMetadata.Keys.ToArray();
        var pastOut = _decoderWithPast.OutputMetadata.Keys.ToArray();
        var cache = new Dictionary<string, OrtValue>();
        for (var i = 0; i < decOut.Length; i++)
        {
            if (decOut[i].StartsWith("present.", StringComparison.Ordinal))
            {
                cache["past_key_values." + decOut[i]["present.".Length..]] = first[i];
            }
        }

        IDisposable? previous = null;
        while (next != eot && tokens.Count < MaxTokens)
        {
            tokens.Add(next);
            using var one = OrtValue.CreateTensorValueFromMemory(new[] { next }, [1, 1]);
            var inputs = pastIn.Select(n => n == "input_ids" ? one : n == "encoder_hidden_states" ? encoded[0] : cache[n]).ToArray();
            var step = _decoderWithPast.Run(_run, pastIn, inputs, pastOut);
            next = ArgMax(step[0], eot);
            for (var i = 0; i < pastOut.Length; i++)
            {
                if (pastOut[i].StartsWith("present.", StringComparison.Ordinal))
                {
                    cache["past_key_values." + pastOut[i]["present.".Length..]] = step[i];
                }
            }
            previous?.Dispose(); // the self-attention cache it held has just been replaced
            previous = step;
        }
        previous?.Dispose();
        DecoderMs = sw.Elapsed.TotalMilliseconds;
        Tokens = tokens.Count;
        return _tokenizer.Decode(tokens);
    }

    // Greedy over the text vocabulary plus end-of-text: no timestamps, no other specials.
    private static long ArgMax(OrtValue logits, long eot)
    {
        var shape = logits.GetTensorTypeAndShape().Shape;
        var vocab = (int)shape[^1];
        var offset = (int)((shape[1] - 1) * vocab);
        var best = 0L;
        var bestValue = float.NegativeInfinity;
        if (logits.GetTensorTypeAndShape().ElementDataType == TensorElementType.Float16)
        {
            var span = logits.GetTensorDataAsSpan<Float16>().Slice(offset, vocab);
            for (var i = 0; i <= eot; i++)
            {
                var v = (float)span[i];
                if (v > bestValue)
                {
                    bestValue = v;
                    best = i;
                }
            }
        }
        else
        {
            var span = logits.GetTensorDataAsSpan<float>().Slice(offset, vocab);
            for (var i = 0; i <= eot; i++)
            {
                if (span[i] > bestValue)
                {
                    bestValue = span[i];
                    best = i;
                }
            }
        }
        return best;
    }

    private static OrtValue Tensor(InferenceSession session, string name, float[] data, long[] shape)
    {
        if (session.InputMetadata[name].ElementDataType == TensorElementType.Float16)
        {
            var half = new Float16[data.Length];
            for (var i = 0; i < data.Length; i++)
            {
                half[i] = (Float16)data[i];
            }
            return OrtValue.CreateTensorValueFromMemory(half, shape);
        }
        return OrtValue.CreateTensorValueFromMemory(data, shape);
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _decoderWithPast.Dispose();
        _run.Dispose();
    }
}

/// <summary>Whisper's byte-level BPE, decode direction only.</summary>
internal sealed class Tokenizer
{
    private readonly Dictionary<long, string> _vocab = new();
    private readonly Dictionary<string, long> _special = new();
    private readonly Dictionary<char, byte> _bytes = new();

    public Tokenizer(string dir)
    {
        using (var v = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "vocab.json"))))
        {
            foreach (var p in v.RootElement.EnumerateObject())
            {
                _vocab[p.Value.GetInt64()] = p.Name;
                if (p.Name.StartsWith("<|", StringComparison.Ordinal))
                {
                    _special[p.Name] = p.Value.GetInt64();
                }
            }
        }
        using (var a = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "added_tokens.json"))))
        {
            foreach (var p in a.RootElement.EnumerateObject())
            {
                _special[p.Name] = p.Value.GetInt64();
            }
        }

        // GPT-2's bytes_to_unicode: printable bytes map to themselves, the rest to 256 + n.
        var n = 0;
        for (var b = 0; b < 256; b++)
        {
            var printable = (b >= '!' && b <= '~') || (b >= 0xA1 && b <= 0xAC) || (b >= 0xAE && b <= 0xFF);
            _bytes[printable ? (char)b : (char)(256 + n++)] = (byte)b;
        }
    }

    public long Special(string token) => _special[token];

    public string Decode(IEnumerable<long> ids)
    {
        var bytes = new List<byte>();
        foreach (var id in ids)
        {
            if (_vocab.TryGetValue(id, out var piece))
            {
                foreach (var c in piece)
                {
                    bytes.Add(_bytes[c]);
                }
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

/// <summary>Whisper's log-mel front end: 25 ms Hann window, 10 ms hop, Slaney mel, 30 s input.</summary>
internal static class LogMel
{
    public const int Frames = 3000;
    private const int NFft = 400;
    private const int Hop = 160;
    private const int Bins = NFft / 2 + 1;
    private static readonly float[] Window = Enumerable.Range(0, NFft).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / NFft))).ToArray();
    private static readonly float[] Cos = new float[Bins * NFft];
    private static readonly float[] Sin = new float[Bins * NFft];
    private static readonly Dictionary<int, float[]> Filters = new();

    static LogMel()
    {
        for (var k = 0; k < Bins; k++)
        {
            for (var n = 0; n < NFft; n++)
            {
                Cos[k * NFft + n] = (float)Math.Cos(2 * Math.PI * k * n / NFft);
                Sin[k * NFft + n] = (float)Math.Sin(2 * Math.PI * k * n / NFft);
            }
        }
    }

    public static float[] Compute(float[] samples, int mels)
    {
        var filters = FilterBank(mels);
        // Pad to 30 s, then reflect-pad by n_fft/2 on both sides, as torch.stft(center=True) does.
        var total = 480_000;
        var x = new float[total + NFft];
        for (var i = 0; i < total + NFft; i++)
        {
            var j = i - NFft / 2;
            if (j < 0)
            {
                j = -j;
            }
            else if (j >= total)
            {
                j = 2 * (total - 1) - j;
            }
            x[i] = j < samples.Length ? samples[j] : 0;
        }

        var logMel = new float[mels * Frames];
        var power = new float[Bins];
        var frame = new float[NFft];
        var lastAudioFrame = (samples.Length + NFft) / Hop + 1;
        var max = float.NegativeInfinity;
        for (var t = 0; t < Frames; t++)
        {
            if (t <= lastAudioFrame)
            {
                for (var n = 0; n < NFft; n++)
                {
                    frame[n] = x[t * Hop + n] * Window[n];
                }
                for (var k = 0; k < Bins; k++)
                {
                    var re = System.Numerics.Tensors.TensorPrimitives.Dot(frame, Cos.AsSpan(k * NFft, NFft));
                    var im = System.Numerics.Tensors.TensorPrimitives.Dot(frame, Sin.AsSpan(k * NFft, NFft));
                    power[k] = re * re + im * im;
                }
            }
            else
            {
                Array.Clear(power); // zero padding: nothing to transform
            }
            for (var m = 0; m < mels; m++)
            {
                var v = System.Numerics.Tensors.TensorPrimitives.Dot(power, filters.AsSpan(m * Bins, Bins));
                var l = MathF.Log10(MathF.Max(v, 1e-10f));
                logMel[m * Frames + t] = l;
                max = MathF.Max(max, l);
            }
        }
        for (var i = 0; i < logMel.Length; i++)
        {
            logMel[i] = (MathF.Max(logMel[i], max - 8) + 4) / 4;
        }
        return logMel;
    }

    // librosa.filters.mel(sr=16000, n_fft=400, n_mels, htk=False, norm="slaney").
    private static float[] FilterBank(int mels)
    {
        lock (Filters)
        {
            if (Filters.TryGetValue(mels, out var cached))
            {
                return cached;
            }
            static double HzToMel(double f) => f < 1000 ? f / (200.0 / 3) : 15 + Math.Log(f / 1000) / (Math.Log(6.4) / 27);
            static double MelToHz(double m) => m < 15 ? m * (200.0 / 3) : 1000 * Math.Exp((m - 15) * (Math.Log(6.4) / 27));
            var lo = HzToMel(0);
            var hi = HzToMel(8000);
            var melF = Enumerable.Range(0, mels + 2).Select(i => MelToHz(lo + (hi - lo) * i / (mels + 1))).ToArray();
            var w = new float[mels * Bins];
            for (var m = 0; m < mels; m++)
            {
                var enorm = 2.0 / (melF[m + 2] - melF[m]);
                for (var k = 0; k < Bins; k++)
                {
                    var f = 8000.0 * k / (Bins - 1);
                    var lower = (f - melF[m]) / (melF[m + 1] - melF[m]);
                    var upper = (melF[m + 2] - f) / (melF[m + 2] - melF[m + 1]);
                    w[m * Bins + k] = (float)(Math.Max(0, Math.Min(lower, upper)) * enorm);
                }
            }
            Filters[mels] = w;
            return w;
        }
    }
}
