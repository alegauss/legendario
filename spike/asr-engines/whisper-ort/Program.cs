using System.Diagnostics;
using System.Globalization;
using System.Text;
using Legendario.Spike.WhisperOrt;
using Microsoft.ML.OnnxRuntime;

// LE2, second slice: Whisper ONNX (fp16) on DirectML, measured the same way as AsrBench.
// Run from spike\asr-engines so cache\ and out\ resolve:
//   whisper-ort\bin\Release\net10.0-windows\win-x64\WhisperOrt.exe <onnx-whisper-small|onnx-whisper-large-v3-turbo> <lang> [--device N] [--clips N]

var inv = CultureInfo.InvariantCulture;
var model = args[0];
var lang = args[1];
string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var device = int.Parse(Opt("--device") ?? "0", inv);
var clips = int.Parse(Opt("--clips") ?? "30", inv);
var bind = args.Contains("--bind");
var mode = bind ? "ort-dml-iobinding" : "ort-dml";
var cache = Path.GetFullPath("cache");

var corpus = Corpus.Load(Path.Combine(cache, "fleurs", lang), clips);
var vram = new VramSampler();
var sw = Stopwatch.StartNew();
using var whisper = new WhisperModel(Path.Combine(cache, model), lang[..2], () =>
{
    var o = new SessionOptions
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        EnableMemoryPattern = false,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
    };
    o.AppendExecutionProvider_DML(device);
    return o;
})
{
    DeviceMemory = bind ? new OrtMemoryInfo("DML", OrtAllocatorType.DeviceAllocator, device, OrtMemType.Default) : null,
};
var loadMs = sw.Elapsed.TotalMilliseconds;

sw.Restart();
whisper.Transcribe(corpus[0].Samples.AsSpan(0, Math.Min(corpus[0].Samples.Length, 32000)).ToArray());
var firstMs = sw.Elapsed.TotalMilliseconds;

var encoderTimes = new List<double>();
var perToken = new List<double>();
double Window(double seconds)
{
    var times = new List<double>();
    foreach (var c in corpus.Where(c => c.Samples.Length >= 16000 * seconds).Take(8))
    {
        var w = c.Samples.AsSpan(0, (int)(16000 * seconds)).ToArray();
        var t = Stopwatch.StartNew();
        whisper.Transcribe(w);
        times.Add(t.Elapsed.TotalMilliseconds);
        encoderTimes.Add(whisper.EncoderMs);
        if (whisper.Tokens > 0)
        {
            perToken.Add(whisper.DecoderMs / (whisper.Tokens + 1));
        }
    }
    times.Sort();
    return times.Count == 0 ? double.NaN : times[times.Count / 2];
}
var w2 = Window(2);
var w10 = Window(10);

double audioS = 0, workMs = 0;
long errors = 0, words = 0;
var hyp = new StringBuilder();
foreach (var c in corpus)
{
    var t = Stopwatch.StartNew();
    var text = whisper.Transcribe(c.Samples);
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
encoderTimes.Sort();
perToken.Sort();
var name = $"{mode}{device}-{model}-{lang}";
Directory.CreateDirectory("out");
File.WriteAllText(Path.Combine("out", name + ".txt"), hyp.ToString());
var line = string.Create(inv,
    $"| {mode} (device {device}):{model} fp16 | {lang} | {corpus.Count} clips, {audioS / 60:F1} min | {workMs / 1000 / audioS:F3} | {w2:F0} ms | {w10:F0} ms | {100.0 * errors / words:F1} % | {peakRamMb:F0} MB | {vramMb:F0} MB | {loadMs:F0} + {firstMs:F0} ms | encoder {encoderTimes[encoderTimes.Count / 2]:F0} ms, {perToken[perToken.Count / 2]:F1} ms/token |");
Console.WriteLine(line);
File.AppendAllText(Path.Combine("out", "results.md"), line + Environment.NewLine);
return 0;
