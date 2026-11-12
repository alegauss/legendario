using System.Diagnostics;
using System.Globalization;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Legendario.Spike.AudioCapture;

/// <summary>Stereo float at 44.1 or 48 kHz to 16 kHz mono float, three ways, fed in 10 ms chunks.</summary>
internal static class ResampleBench
{
    private const int OutRate = 16000;

    private delegate float[] Resampler(float[] stereo, int inRate);

    public static string Run()
    {
        var inv = CultureInfo.InvariantCulture;
        var methods = new (string Name, Resampler Fn)[]
        {
            ("Media Foundation resampler DSP (quality 60)", MediaFoundation),
            ("NAudio WDL (managed)", Wdl),
            ("Own polyphase FIR (managed, 128 taps per phase, Kaiser)", Polyphase),
        };
        var b = new StringBuilder();
        foreach (var inRate in new[] { 48000, 44100 })
        {
            b.AppendLine(inv, $"## {inRate} Hz stereo -> 16 kHz mono");
            b.AppendLine("| Method | 1 kHz gain | 7 kHz gain | 9 kHz alias | 12 kHz alias | Delay | 60 s of audio in | CPU of one core |");
            b.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var (name, fn) in methods)
            {
                fn(Sine(inRate, 1000, 0.5), inRate); // warm-up: JIT and MF startup stay out of the numbers
                var g1 = GainDb(fn(Sine(inRate, 1000, 0.5), inRate));
                var g7 = GainDb(fn(Sine(inRate, 7000, 0.5), inRate));
                var a9 = GainDb(fn(Sine(inRate, 9000, 0.5), inRate));
                var a12 = GainDb(fn(Sine(inRate, 12000, 0.5), inRate));
                var delay = DelayMs(fn(Impulse(inRate), inRate));

                var long60 = Sine(inRate, 1000, 0.5, 60);
                var best = double.MaxValue;
                for (var i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    fn(long60, inRate);
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                }
                b.AppendLine(inv, $"| {name} | {g1:F2} dB | {g7:F2} dB | {a9:F1} dB | {a12:F1} dB | {delay:F2} ms | {best:F0} ms | {best / 60_000 * 100:F3} % |");
            }
            b.AppendLine();
        }
        return b.ToString();
    }

    private static float[] Sine(int rate, double freq, double amp, double seconds = 1)
    {
        var n = (int)(rate * seconds);
        var x = new float[n * 2];
        for (var i = 0; i < n; i++)
        {
            var v = (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate));
            x[2 * i] = v;
            x[2 * i + 1] = v;
        }
        return x;
    }

    private static float[] Impulse(int rate)
    {
        var x = new float[rate * 2];
        x[rate / 10 * 2] = 1;
        x[rate / 10 * 2 + 1] = 1;
        return x;
    }

    // RMS of the middle of the output against a 0.5-amplitude sine's RMS.
    private static double GainDb(float[] y)
    {
        var from = OutRate / 5;
        var to = Math.Max(from + 1, y.Length - OutRate / 5);
        double sum = 0;
        for (var i = from; i < to; i++)
        {
            sum += y[i] * y[i];
        }
        var rms = Math.Sqrt(sum / (to - from));
        return 20 * Math.Log10(Math.Max(rms, 1e-12) / (0.5 / Math.Sqrt(2)));
    }

    private static double DelayMs(float[] y)
    {
        var at = 0;
        for (var i = 1; i < y.Length; i++)
        {
            if (Math.Abs(y[i]) > Math.Abs(y[at]))
            {
                at = i;
            }
        }
        return at * 1000.0 / OutRate - 100;
    }

    private static byte[] Bytes(float[] x)
    {
        var bytes = new byte[x.Length * 4];
        Buffer.BlockCopy(x, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] MediaFoundation(float[] stereo, int inRate)
    {
        using var src = new RawSourceWaveStream(new MemoryStream(Bytes(stereo)), WaveFormat.CreateIeeeFloatWaveFormat(inRate, 2));
        using var mf = new MediaFoundationResampler(src, WaveFormat.CreateIeeeFloatWaveFormat(OutRate, 1)) { ResamplerQuality = 60 };
        var outBytes = new MemoryStream();
        var chunk = new byte[OutRate / 100 * 4];
        int read;
        while ((read = mf.Read(chunk, 0, chunk.Length)) > 0)
        {
            outBytes.Write(chunk, 0, read);
        }
        var bytes = outBytes.ToArray();
        var y = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, y, 0, y.Length * 4);
        return y;
    }

    private static float[] Wdl(float[] stereo, int inRate)
    {
        var src = new RawSourceWaveStream(new MemoryStream(Bytes(stereo)), WaveFormat.CreateIeeeFloatWaveFormat(inRate, 2));
        var mono = new StereoToMonoSampleProvider(src.ToSampleProvider()) { LeftVolume = 0.5f, RightVolume = 0.5f };
        var wdl = new WdlResamplingSampleProvider(mono, OutRate);
        var y = new List<float>((int)((long)stereo.Length / 2 * OutRate / inRate) + 1024);
        var chunk = new float[OutRate / 100];
        int read;
        while ((read = wdl.Read(chunk, 0, chunk.Length)) > 0)
        {
            y.AddRange(chunk.AsSpan(0, read));
        }
        return y.ToArray();
    }

    private static float[] Polyphase(float[] stereo, int inRate)
    {
        var r = new PolyphaseResampler(inRate, OutRate);
        var frames = stereo.Length / 2;
        var chunkFrames = inRate / 100;
        var mono = new float[chunkFrames];
        var output = new float[chunkFrames];
        var y = new List<float>((int)((long)frames * OutRate / inRate) + 1024);
        for (var at = 0; at < frames; at += chunkFrames)
        {
            var n = Math.Min(chunkFrames, frames - at);
            for (var i = 0; i < n; i++)
            {
                mono[i] = 0.5f * (stereo[2 * (at + i)] + stereo[2 * (at + i) + 1]);
            }
            var produced = r.Process(mono.AsSpan(0, n), output);
            y.AddRange(output.AsSpan(0, produced));
        }
        return y.ToArray();
    }
}

/// <summary>
/// Rational L/M resampler: a Kaiser-windowed sinc prototype split into L phases. Streaming and
/// allocation-free after construction; 48 kHz -> 16 kHz is L=1, M=3 and 44.1 kHz is L=160, M=441.
/// </summary>
internal sealed class PolyphaseResampler
{
    private readonly int _l;
    private readonly int _m;
    private readonly int _taps;
    private readonly float[] _phases; // [phase * taps + k], k-th tap reversed for a forward dot product
    private readonly float[] _history;
    private int _historyLength;
    private long _t; // next output's position on the upsampled grid, relative to _history[0]

    public PolyphaseResampler(int inRate, int outRate, int tapsPerPhase = 128, double beta = 8.0, double rolloff = 0.9)
    {
        var g = Gcd(inRate, outRate);
        _l = outRate / g;
        _m = inRate / g;
        _taps = tapsPerPhase;
        var total = _l * _taps;
        var fc = 0.5 * Math.Min(1.0 / _l, 1.0 / _m) * rolloff; // cycles per upsampled sample
        var proto = new double[total];
        var centre = (total - 1) / 2.0;
        var i0Beta = I0(beta);
        for (var n = 0; n < total; n++)
        {
            var x = n - centre;
            var sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            var w = I0(beta * Math.Sqrt(1 - Math.Pow(2 * n / (double)(total - 1) - 1, 2))) / i0Beta;
            proto[n] = sinc * w * _l;
        }
        _phases = new float[total];
        for (var p = 0; p < _l; p++)
        {
            for (var k = 0; k < _taps; k++)
            {
                // y uses x[i - k] * h[p + k*L]; store reversed so the inner loop walks x forwards.
                _phases[p * _taps + (_taps - 1 - k)] = (float)proto[p + k * _l];
            }
        }
        _history = new float[_taps * 2 + 48000];
        _historyLength = _taps - 1; // zeros: the filter starts on silence
        _t = (long)(_taps - 1) * _l;
    }

    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        input.CopyTo(_history.AsSpan(_historyLength));
        _historyLength += input.Length;
        var produced = 0;
        while (true)
        {
            var i = _t / _l;
            if (i >= _historyLength)
            {
                break;
            }
            var p = (int)(_t % _l);
            var h = _phases.AsSpan(p * _taps, _taps);
            var x = _history.AsSpan((int)i - _taps + 1, _taps);
            float acc = 0;
            for (var k = 0; k < _taps; k++)
            {
                acc += h[k] * x[k];
            }
            output[produced++] = acc;
            _t += _m;
        }
        var drop = (int)Math.Max(0, _t / _l - (_taps - 1));
        drop = Math.Min(drop, _historyLength);
        _history.AsSpan(drop, _historyLength - drop).CopyTo(_history);
        _historyLength -= drop;
        _t -= (long)drop * _l;
        return produced;
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    private static double I0(double x)
    {
        double sum = 1, term = 1;
        for (var k = 1; k < 50; k++)
        {
            term *= x / (2 * k) * (x / (2 * k));
            sum += term;
        }
        return sum;
    }
}
