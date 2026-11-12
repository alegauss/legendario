using System.Diagnostics;
using System.Globalization;
using System.Text;
using NAudio.Wave;

namespace Legendario.Spike.AudioCapture;

/// <summary>One packet as the capture loop saw it.</summary>
internal readonly record struct Packet(
    double WakeMs,        // when the thread woke, ms since start
    double ArrivalMs,     // when GetBuffer returned this packet
    int Frames,
    BufferFlags Flags,
    ulong DevicePosition,
    double LatencyMs,     // arrival minus the packet's QPC position (NaN when the API gives none)
    float Rms);

/// <summary>Collects packets and audio, then writes WAV, CSV and a summary.</summary>
internal sealed class Recording : IDisposable
{
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
    private readonly long _t0 = Stopwatch.GetTimestamp();
    private readonly List<Packet> _packets = new(10_000);
    private readonly List<string> _events = new();
    private readonly WaveFileWriter _wav;
    private readonly TimeSpan _cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
    private TimeSpan _cpu1;
    private double _endMs;

    public Recording(string name, WaveFormat format, string outDir)
    {
        Name = name;
        Format = format;
        OutDir = outDir;
        Directory.CreateDirectory(outDir);
        _wav = new WaveFileWriter(Path.Combine(outDir, name + ".wav"), format);
    }

    public string Name { get; }
    public WaveFormat Format { get; }
    public string OutDir { get; }
    public int Timeouts { get; set; }
    public List<string> Notes { get; } = new();

    public double NowMs => (Stopwatch.GetTimestamp() - _t0) * TicksToMs;

    /// <summary>QPC position (100 ns units) to ms on this recording's clock.</summary>
    public double QpcToMs(ulong qpc100ns)
    {
        var ticks = (long)(qpc100ns * (ulong)Stopwatch.Frequency / 10_000_000UL);
        return (ticks - _t0) * TicksToMs;
    }

    public void Event(string text)
    {
        lock (_events)
        {
            _events.Add($"{NowMs:F1} ms: {text}");
        }
        Console.WriteLine($"  [{NowMs / 1000:F1} s] {text}");
    }

    public void Add(Packet p, ReadOnlySpan<byte> audio)
    {
        _packets.Add(p);
        _wav.Write(audio);
    }

    public void Finish()
    {
        _endMs = NowMs;
        _cpu1 = Process.GetCurrentProcess().TotalProcessorTime;
        _wav.Flush();
    }

    public void Dispose() => _wav.Dispose();

    public static float Rms(ReadOnlySpan<byte> audio, WaveFormat format)
    {
        if (audio.Length == 0)
        {
            return 0;
        }
        double sum = 0;
        int n;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat || (format is WaveFormatExtensible x && x.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT))
        {
            var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(audio);
            foreach (var v in s)
            {
                sum += v * v;
            }
            n = s.Length;
        }
        else
        {
            var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(audio);
            foreach (var v in s)
            {
                var f = v / 32768.0;
                sum += f * f;
            }
            n = s.Length;
        }
        return (float)Math.Sqrt(sum / Math.Max(1, n));
    }

    public string WriteReport()
    {
        var csv = new StringBuilder("wake_ms,arrival_ms,frames,flags,device_position,latency_ms,rms\n");
        foreach (var p in _packets)
        {
            csv.Append(CultureInfo.InvariantCulture,
                $"{p.WakeMs:F3},{p.ArrivalMs:F3},{p.Frames},{(uint)p.Flags},{p.DevicePosition},{p.LatencyMs:F3},{p.Rms:F5}\n");
        }
        File.WriteAllText(Path.Combine(OutDir, Name + ".csv"), csv.ToString());

        var s = Summarise();
        File.WriteAllText(Path.Combine(OutDir, Name + ".summary.txt"), s);
        return s;
    }

    private string Summarise()
    {
        var inv = CultureInfo.InvariantCulture;
        var b = new StringBuilder();
        b.AppendLine(inv, $"run: {Name}");
        b.AppendLine(inv, $"format: {Format.SampleRate} Hz, {Format.Channels} ch, {Format.BitsPerSample} bit {Format.Encoding}");
        var seconds = _endMs / 1000;
        long frames = _packets.Sum(p => (long)p.Frames);
        b.AppendLine(inv, $"duration: {seconds:F1} s, packets: {_packets.Count}, frames: {frames} ({frames / (double)Format.SampleRate:F1} s of audio)");

        // Wake-to-wake interval: the jitter the thread actually experiences.
        var wakes = _packets.Select(p => p.WakeMs).Distinct().ToArray();
        var intervals = wakes.Zip(wakes.Skip(1), (a, c) => c - a).ToArray();
        if (intervals.Length > 0)
        {
            Array.Sort(intervals);
            double mean = intervals.Average();
            double sd = Math.Sqrt(intervals.Average(v => (v - mean) * (v - mean)));
            b.AppendLine(inv, $"wake interval ms: mean {mean:F2}, sd {sd:F2}, p50 {Pct(intervals, 50):F2}, p95 {Pct(intervals, 95):F2}, p99 {Pct(intervals, 99):F2}, max {intervals[^1]:F1}");
            b.AppendLine(inv, $"wakes > 30 ms apart: {intervals.Count(v => v > 30)}, > 100 ms: {intervals.Count(v => v > 100)}");
        }
        var sizes = _packets.GroupBy(p => p.Frames).OrderByDescending(g => g.Count()).Take(3)
            .Select(g => $"{g.Key} frames x{g.Count()}");
        b.AppendLine(inv, $"packet sizes: {string.Join(", ", sizes)}");

        var lat = _packets.Select(p => p.LatencyMs).Where(v => !double.IsNaN(v)).ToArray();
        if (lat.Length > 0)
        {
            Array.Sort(lat);
            b.AppendLine(inv, $"capture latency ms (arrival - QPC position): p50 {Pct(lat, 50):F2}, p95 {Pct(lat, 95):F2}, max {lat[^1]:F2}");
        }
        else
        {
            b.AppendLine("capture latency: no QPC positions available");
        }

        b.AppendLine(inv, $"flags: discontinuity {_packets.Count(p => p.Flags.HasFlag(BufferFlags.DataDiscontinuity))}, silent {_packets.Count(p => p.Flags.HasFlag(BufferFlags.Silent))}, timestamp error {_packets.Count(p => p.Flags.HasFlag(BufferFlags.TimestampError))}");

        int posJumps = 0;
        for (int i = 1; i < _packets.Count; i++)
        {
            var expected = _packets[i - 1].DevicePosition + (ulong)_packets[i - 1].Frames;
            if (_packets[i].DevicePosition != 0 && _packets[i].DevicePosition != expected)
            {
                posJumps++;
            }
        }
        b.AppendLine(inv, $"device position jumps: {posJumps}");

        // Gaps: stretches of the run with no packet at all (loopback goes quiet when nothing plays).
        var arrivals = _packets.Select(p => p.ArrivalMs).Prepend(0).Append(_endMs).ToArray();
        var gaps = arrivals.Zip(arrivals.Skip(1), (a, c) => c - a).Where(g => g > 100).ToArray();
        b.AppendLine(inv, $"gaps with no packets > 100 ms: {gaps.Length}, total {gaps.Sum() / 1000:F1} s, longest {(gaps.Length > 0 ? gaps.Max() / 1000 : 0):F1} s; wait timeouts: {Timeouts}");

        var cpu = (_cpu1 - _cpu0).TotalMilliseconds;
        b.AppendLine(inv, $"process CPU: {cpu:F0} ms over {seconds:F1} s = {cpu / _endMs * 100:F3} % of one core, {cpu / _endMs * 3600:F1} s per hour");

        var rms = _packets.Where(p => p.Rms > 0).Select(p => (double)p.Rms).DefaultIfEmpty(0).Average();
        b.AppendLine(inv, $"mean packet RMS (non-zero packets): {rms:F4} ({20 * Math.Log10(Math.Max(rms, 1e-9)):F1} dBFS)");
        foreach (var n in Notes)
        {
            b.AppendLine(n);
        }
        lock (_events)
        {
            foreach (var e in _events)
            {
                b.AppendLine("event " + e);
            }
        }
        return b.ToString();
    }

    private static double Pct(double[] sorted, double pct)
    {
        var i = (int)Math.Clamp(Math.Ceiling(pct / 100 * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[i];
    }
}
