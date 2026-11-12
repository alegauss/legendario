using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

// Shared by AsrBench and whisper-ort: the FLEURS corpus, the WER count and the VRAM sampler.

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
