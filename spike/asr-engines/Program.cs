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
var cache = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "cache"));

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
