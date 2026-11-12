using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Legendario.Spike.MtEngines;

/// <summary>
/// One Opus-MT (MarianMT) direction as three ONNX graphs and a greedy loop: the same shape as
/// LE2's Whisper loop, with SentencePiece pieces mapped through Marian's own vocab.json.
/// </summary>
internal sealed class MarianModel : IDisposable
{
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly InferenceSession _decoderWithPast;
    private readonly RunOptions _run = new();
    private readonly SentencePieceTokenizer _source;
    private readonly Dictionary<string, long> _vocab = new();
    private readonly Dictionary<long, string> _pieces = new();
    private readonly long _eos;
    private readonly long _pad;
    private readonly long _unk;

    public MarianModel(string dir, bool quantized, Func<SessionOptions> options)
    {
        using (var s = File.OpenRead(Path.Combine(dir, "source.spm")))
        {
            _source = SentencePieceTokenizer.Create(s, false, false);
        }
        using (var v = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "vocab.json"))))
        {
            foreach (var p in v.RootElement.EnumerateObject())
            {
                _vocab[p.Name] = p.Value.GetInt64();
                _pieces[p.Value.GetInt64()] = p.Name;
            }
        }
        _eos = _vocab["</s>"];
        _pad = _vocab["<pad>"];
        _unk = _vocab["<unk>"];
        var suffix = quantized ? "_quantized" : "";
        var onnx = Path.Combine(dir, "onnx");
        _encoder = new InferenceSession(Path.Combine(onnx, $"encoder_model{suffix}.onnx"), options());
        _decoder = new InferenceSession(Path.Combine(onnx, $"decoder_model{suffix}.onnx"), options());
        _decoderWithPast = new InferenceSession(Path.Combine(onnx, $"decoder_with_past_model{suffix}.onnx"), options());
    }

    public int UnknownPieces { get; private set; }

    /// <summary>Translates one sentence. <paramref name="target"/> is a token such as "&gt;&gt;pt_br&lt;&lt;" for multi-target models.</summary>
    public string Translate(string text, string? target = null)
    {
        var ids = new List<long>();
        if (target != null)
        {
            ids.Add(_vocab[target]);
        }
        foreach (var t in _source.EncodeToTokens(text, out _))
        {
            if (_vocab.TryGetValue(t.Value, out var id))
            {
                ids.Add(id);
            }
            else
            {
                ids.Add(_unk);
                UnknownPieces++;
            }
        }
        ids.Add(_eos);
        var n = ids.Count;

        using var inputIds = OrtValue.CreateTensorValueFromMemory(ids.ToArray(), [1, n]);
        using var mask = OrtValue.CreateTensorValueFromMemory(Enumerable.Repeat(1L, n).ToArray(), [1, n]);
        using var encoded = _encoder.Run(_run, ["input_ids", "attention_mask"], [inputIds, mask], ["last_hidden_state"]);

        var decIn = _decoder.InputMetadata.Keys.ToArray();
        var decOut = _decoder.OutputMetadata.Keys.ToArray();
        using var start = OrtValue.CreateTensorValueFromMemory(new[] { _pad }, [1, 1]);
        OrtValue Common(string name, OrtValue ids1) => name switch
        {
            "input_ids" => ids1,
            "encoder_attention_mask" => mask,
            "encoder_hidden_states" => encoded[0],
            _ => throw new InvalidOperationException(name),
        };
        using var first = _decoder.Run(_run, decIn, decIn.Select(x => Common(x, start)).ToArray(), decOut);
        var next = ArgMax(first[0]);

        var cache = new Dictionary<string, OrtValue>();
        for (var i = 0; i < decOut.Length; i++)
        {
            if (decOut[i].StartsWith("present.", StringComparison.Ordinal))
            {
                cache["past_key_values." + decOut[i]["present.".Length..]] = first[i];
            }
        }

        var pastIn = _decoderWithPast.InputMetadata.Keys.ToArray();
        var pastOut = _decoderWithPast.OutputMetadata.Keys.ToArray();
        var output = new List<long>();
        IDisposable? previous = null;
        var max = 2 * n + 10;
        while (next != _eos && output.Count < max)
        {
            output.Add(next);
            using var one = OrtValue.CreateTensorValueFromMemory(new[] { next }, [1, 1]);
            var step = _decoderWithPast.Run(_run, pastIn, pastIn.Select(x => cache.TryGetValue(x, out var c) ? c : Common(x, one)).ToArray(), pastOut);
            next = ArgMax(step[0]);
            for (var i = 0; i < pastOut.Length; i++)
            {
                if (pastOut[i].StartsWith("present.", StringComparison.Ordinal))
                {
                    cache["past_key_values." + pastOut[i]["present.".Length..]] = step[i];
                }
            }
            previous?.Dispose();
            previous = step;
        }
        previous?.Dispose();

        var b = new StringBuilder();
        foreach (var id in output)
        {
            var piece = _pieces[id];
            if (!piece.StartsWith(">>", StringComparison.Ordinal) && piece != "<unk>")
            {
                b.Append(piece);
            }
        }
        return b.Replace('▁', ' ').ToString().Trim();
    }

    // Greedy, with <pad> suppressed as Marian's generation config asks (bad_words_ids).
    private long ArgMax(OrtValue logits)
    {
        var shape = logits.GetTensorTypeAndShape().Shape;
        var vocab = (int)shape[^1];
        var span = logits.GetTensorDataAsSpan<float>().Slice((int)((shape[1] - 1) * vocab), vocab);
        var best = 0L;
        var bestValue = float.NegativeInfinity;
        for (var i = 0; i < vocab; i++)
        {
            if (i != _pad && span[i] > bestValue)
            {
                bestValue = span[i];
                best = i;
            }
        }
        return best;
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _decoderWithPast.Dispose();
        _run.Dispose();
    }
}

/// <summary>chrF2 as sacrebleu defines it: character 1- to 6-grams, whitespace removed, corpus-level statistics, beta 2.</summary>
internal static class ChrF
{
    public static double Score(IReadOnlyList<string> hypotheses, IReadOnlyList<string> references)
    {
        const int order = 6;
        var match = new double[order];
        var hypCount = new double[order];
        var refCount = new double[order];
        for (var s = 0; s < hypotheses.Count; s++)
        {
            var h = Strip(hypotheses[s]);
            var r = Strip(references[s]);
            for (var n = 1; n <= order; n++)
            {
                var hg = Grams(h, n);
                var rg = Grams(r, n);
                hypCount[n - 1] += hg.Values.Sum();
                refCount[n - 1] += rg.Values.Sum();
                foreach (var (g, c) in hg)
                {
                    if (rg.TryGetValue(g, out var rc))
                    {
                        match[n - 1] += Math.Min(c, rc);
                    }
                }
            }
        }
        // sacrebleu 2.x: an F-beta per order, averaged over the orders both sides have.
        const double beta2 = 4;
        const double eps = 1e-16;
        double score = 0;
        var effective = 0;
        for (var n = 0; n < order; n++)
        {
            var p = hypCount[n] > 0 ? match[n] / hypCount[n] : eps;
            var r = refCount[n] > 0 ? match[n] / refCount[n] : eps;
            var denom = beta2 * p + r;
            score += denom > 0 ? (1 + beta2) * p * r / denom : eps;
            if (hypCount[n] > 0 && refCount[n] > 0)
            {
                effective++;
            }
        }
        return effective == 0 ? 0 : 100 * score / effective;
    }

    private static string Strip(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static Dictionary<string, int> Grams(string s, int n)
    {
        var d = new Dictionary<string, int>();
        for (var i = 0; i + n <= s.Length; i++)
        {
            var g = s.Substring(i, n);
            d[g] = d.GetValueOrDefault(g) + 1;
        }
        return d;
    }
}
