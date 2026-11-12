using System.Diagnostics;
using System.Globalization;
using Legendario.Spike.AudioCapture;

// LE1 audio capture spike. Usage (from this folder):
//   AudioCaptureSpike capture <raw|naudio> <loopback|mic|process> [options]
//     --seconds N       run length (default 60)
//     --buffer MS       WASAPI buffer duration (default 10)
//     --16k             ask the engine for 16 kHz mono float (AUTOCONVERTPCM), raw only
//     --tone HZ         play a quiet sine from a child process during the run
//     --pid N           process loopback target (default: the --tone child)
//     --exclude         process loopback: everything except the target's tree
//     --no-mmcss        leave the capture thread out of MMCSS "Pro Audio"
//     --follow-default  reopen when the default device changes, raw only
//     --name NAME       output file stem under out\
//   AudioCaptureSpike tone <seconds> <hz> [gain]
//   AudioCaptureSpike resample

var inv = CultureInfo.InvariantCulture;
if (args.Length == 0)
{
    Console.WriteLine("capture | tone | resample; see Program.cs");
    return 2;
}

switch (args[0])
{
    case "tone":
        Tone.Play(double.Parse(args[1], inv), double.Parse(args[2], inv), args.Length > 3 ? float.Parse(args[3], inv) : 0.05f);
        return 0;

    case "resample":
        var table = ResampleBench.Run();
        Directory.CreateDirectory("out");
        File.WriteAllText(Path.Combine("out", "resample.md"), table);
        Console.WriteLine(table);
        return 0;

    case "capture":
        return Capture(args.Skip(1).ToArray());

    default:
        Console.WriteLine("unknown command " + args[0]);
        return 2;
}

int Capture(string[] a)
{
    var raw = a[0] == "raw";
    var source = a[1] switch
    {
        "loopback" => Source.Loopback,
        "mic" => Source.Mic,
        "process" => Source.Process,
        _ => throw new ArgumentException(a[1]),
    };
    string? Opt(string name) => Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    bool Flag(string name) => a.Contains(name);

    var seconds = double.Parse(Opt("--seconds") ?? "60", inv);
    var bufferMs = int.Parse(Opt("--buffer") ?? "10", inv);
    var toneHz = Opt("--tone");
    var name = Opt("--name") ?? $"{a[0]}-{a[1]}-{bufferMs}ms{(Flag("--16k") ? "-16k" : "")}";

    Process? child = null;
    if (toneHz != null)
    {
        child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"tone {(seconds + 2).ToString(inv)} {toneHz}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Thread.Sleep(300); // let the child's stream start before capture opens
    }

    var pid = int.Parse(Opt("--pid") ?? (child?.Id ?? 0).ToString(inv), inv);
    var o = new RawOptions(source, bufferMs, Flag("--16k"), pid, Flag("--exclude"), !Flag("--no-mmcss"), Flag("--follow-default"));
    var format = raw ? RawCapture.PlanFormat(o) : NAudioCapture.PlanFormat(source);

    Console.WriteLine($"{name}: {seconds} s, {format}");
    using (var rec = new Recording(name, format, "out"))
    {
        if (toneHz != null)
        {
            rec.Notes.Add($"tone: {toneHz} Hz from child pid {child!.Id}");
        }
        if (raw)
        {
            RawCapture.Run(o, rec, TimeSpan.FromSeconds(seconds));
        }
        else
        {
            NAudioCapture.Run(source, bufferMs, rec, TimeSpan.FromSeconds(seconds));
        }
        Console.WriteLine(rec.WriteReport());
    }

    if (child is { HasExited: false })
    {
        child.Kill();
    }
    return 0;
}
