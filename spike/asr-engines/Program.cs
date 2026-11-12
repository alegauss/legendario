using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SherpaOnnx;
using Whisper.net;
using Whisper.net.LibraryLoader;

// LE2 ASR engine bench. One engine and backend per process: whisper.cpp's native library is
// chosen once per process, so a run never mixes backends.
//
//   AsrBench <engine> <lang> [--clips N]
//     engine: whispercpp-cpu:<ggml file> | whispercpp-vulkan:<ggml file> | parakeet-cpu
//     lang:   pt_br | en_us | es_419   (FLEURS dev split, extracted under cache\fleurs\<lang>)

var inv = CultureInfo.InvariantCulture;
var engine = args[0];
var lang = args[1];
var clips = args.Length > 3 && args[2] == "--clips" ? int.Parse(args[3], inv) : 30;
var cache = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "cache");
cache = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "cache"));

var corpus = Corpus.Load(Path.Combine(cache, "fleurs", lang), clips);
var vram = new VramSampler();
var sw = Stopwatch.StartNew();
using IEngine asr = engine switch
{
    _ when engine.StartsWith("whispercpp-", StringComparison.Ordinal) => new WhisperCppEngine(engine, cache, lang),
    "parakeet-cpu" => new ParakeetEngine(cache),
    _ => throw new ArgumentException(engine),
};
var loadMs = sw.Elapsed.TotalMilliseconds;

// The first decode pays for allocation and, on a GPU, shader or kernel compilation.
sw.Restart();
asr.Transcribe(corpus[0].Samples.AsSpan(0, Math.Min(corpus[0].Samples.Length, 16000 * 2)).ToArray());
var firstMs = sw.Elapsed.TotalMilliseconds;

// Fixed-length windows, as a streaming loop would decode them.
double Window(double seconds)
{
    var times = new List<double>();
    foreach (var c in corpus.Where(c => c.Samples.Length >= 16000 * seconds).Take(8))
    {
        var w = c.Samples.AsSpan(0, (int)(16000 * seconds)).ToArray();
        var t = Stopwatch.StartNew();
        asr.Transcribe(w);
        times.Add(t.Elapsed.TotalMilliseconds);
    }
    times.Sort();
    return times.Count == 0 ? double.NaN : times[times.Count / 2];
}
var w2 = Window(2);
var w10 = Window(10);

// Whole clips: real-time factor and WER.
double audioS = 0, workMs = 0;
long errors = 0, words = 0;
var hyp = new StringBuilder();
foreach (var c in corpus)
{
    var t = Stopwatch.StartNew();
    var text = asr.Transcribe(c.Samples);
    workMs += t.Elapsed.TotalMilliseconds;
    audioS += c.Samples.Length / 16000.0;
    var (e, n) = Wer.Count(c.Reference, text);
    errors += e;
    words += n;
    hyp.AppendLine(inv, $"{c.Id}\tREF\t{Wer.Normalise(c.Reference)}");
    hyp.AppendLine(inv, $"{c.Id}\tHYP\t{Wer.Normalise(text)}");
}

var peakRamMb = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0;
var vramMb = vram.Stop();
var name = $"{Environment.GetEnvironmentVariable("LE2_TAG")}{engine.Replace(':', '-').Replace(".bin", "", StringComparison.Ordinal)}-{lang}";
Directory.CreateDirectory("out");
File.WriteAllText(Path.Combine("out", name + ".txt"), hyp.ToString());
var line = string.Create(inv,
    $"| {engine}{Environment.GetEnvironmentVariable("LE2_TAG")} | {lang} | {corpus.Count} clips, {audioS / 60:F1} min | {workMs / 1000 / audioS:F3} | {w2:F0} ms | {w10:F0} ms | {100.0 * errors / words:F1} % | {peakRamMb:F0} MB | {vramMb:F0} MB | {loadMs:F0} + {firstMs:F0} ms | {asr.Timestamps} |");
Console.WriteLine(line);
File.AppendAllText(Path.Combine("out", "results.md"), line + Environment.NewLine);
return 0;

internal interface IEngine : IDisposable
{
    string Timestamps { get; }
    string Transcribe(float[] samples);
}

internal sealed class WhisperCppEngine : IEngine
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;

    public WhisperCppEngine(string spec, string cache, string lang)
    {
        var parts = spec.Split(':');
        RuntimeOptions.RuntimeLibraryOrder = parts[0] == "whispercpp-vulkan"
            ? [RuntimeLibrary.Vulkan]
            : [RuntimeLibrary.Cpu];
        _factory = WhisperFactory.FromPath(Path.Combine(cache, parts[1]));
        _processor = _factory.CreateBuilder()
            .WithLanguage(lang[..2])
            .WithThreads(Math.Max(1, Environment.ProcessorCount / 2))
            .Build();
        Console.WriteLine($"whisper.cpp runtime: {RuntimeOptions.LoadedLibrary}");
    }

    public string Timestamps => "segment start/end (10 ms), token-level opt-in";

    public string Transcribe(float[] samples)
    {
        var b = new StringBuilder();
        foreach (var s in _processor.ProcessAsync(samples).ToBlockingEnumerable())
        {
            b.Append(s.Text);
        }
        return b.ToString();
    }

    public void Dispose()
    {
        _processor.Dispose();
        _factory.Dispose();
    }
}

internal sealed class ParakeetEngine : IEngine
{
    private readonly OfflineRecognizer _recognizer;

    public ParakeetEngine(string cache)
    {
        var dir = Path.Combine(cache, "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8");
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Path.Combine(dir, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(dir, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(dir, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(dir, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = Math.Max(1, Environment.ProcessorCount / 2);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        _recognizer = new OfflineRecognizer(config);
    }

    public string Timestamps => "per token (start and duration)";

    public string Transcribe(float[] samples)
    {
        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(16000, samples);
        _recognizer.Decode(stream);
        return stream.Result.Text;
    }

    public void Dispose() => _recognizer.Dispose();
}

internal sealed record Clip(string Id, string Reference, float[] Samples);

internal static class Corpus
{
    // FLEURS dev.tsv: id, file name, raw transcription, normalised transcription, ...
    public static List<Clip> Load(string dir, int count)
    {
        var list = new List<Clip>();
        var seen = new HashSet<string>();
        foreach (var row in File.ReadLines(Path.Combine(dir, "dev.tsv")))
        {
            var f = row.Split('\t');
            var wav = Path.Combine(dir, "dev", f[1]);
            if (!File.Exists(wav) || !seen.Add(f[0]))
            {
                continue; // one recording per sentence id: FLEURS repeats each sentence by several speakers
            }
            list.Add(new Clip(f[1], f[2], ReadWav(wav)));
            if (list.Count == count)
            {
                break;
            }
        }
        return list;
    }

    private static float[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var at = 12;
        int channels = 1, bits = 16, rate = 16000;
        while (at + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, at, 4);
            var size = BitConverter.ToInt32(bytes, at + 4);
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(bytes, at + 10);
                rate = BitConverter.ToInt32(bytes, at + 12);
                bits = BitConverter.ToInt16(bytes, at + 22);
            }
            else if (id == "data")
            {
                if (rate != 16000 || (bits != 16 && bits != 32))
                {
                    throw new InvalidDataException($"{path}: {rate} Hz {bits} bit");
                }
                var stride = bits / 8 * channels;
                var n = size / stride;
                var x = new float[n];
                for (var i = 0; i < n; i++)
                {
                    x[i] = bits == 32
                        ? BitConverter.ToSingle(bytes, at + 8 + i * stride)
                        : BitConverter.ToInt16(bytes, at + 8 + i * stride) / 32768f;
                }
                return x;
            }
            at += 8 + size + (size & 1);
        }
        throw new InvalidDataException(path);
    }
}

internal static partial class Wer
{
    // Lower case, no punctuation, single spaces: the usual Whisper-style basic normaliser.
    public static string Normalise(string s)
    {
        s = s.ToLowerInvariant();
        s = Punctuation().Replace(s, " ");
        return Spaces().Replace(s, " ").Trim();
    }

    public static (int Errors, int Words) Count(string reference, string hypothesis)
    {
        var r = Normalise(reference).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var h = Normalise(hypothesis).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var d = new int[h.Length + 1];
        for (var j = 0; j <= h.Length; j++)
        {
            d[j] = j;
        }
        for (var i = 1; i <= r.Length; i++)
        {
            var prev = d[0];
            d[0] = i;
            for (var j = 1; j <= h.Length; j++)
            {
                var tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (r[i - 1] == h[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return (d[h.Length], r.Length);
    }

    [GeneratedRegex(@"[^\p{L}\p{N}' ]")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>Peak dedicated GPU memory in use, sampled from nvidia-smi, less what was in use before.</summary>
internal sealed class VramSampler
{
    private readonly double _baseline;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task<double> _peak;

    public VramSampler()
    {
        _baseline = Query();
        _peak = Task.Run(async () =>
        {
            var peak = _baseline;
            while (!_stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, Query());
                try
                {
                    await Task.Delay(250, _stop.Token);
                }
                catch (TaskCanceledException)
                {
                }
            }
            return peak;
        });
    }

    public double Stop()
    {
        _stop.Cancel();
        return _peak.Result - _baseline;
    }

    private static double Query()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var s = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return double.Parse(s.Trim().Split('\n')[0], CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
