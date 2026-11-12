using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Legendario.Spike.MtEngines;
using Microsoft.ML.OnnxRuntime;

// LE3 MT bench, Opus-MT slice. From this folder:
//   MtBench <cpu|cpu-int8|dml> [--n 200] [--threads N]
// Translates the first n FLORES-200 devtest sentences for each pair, one sentence at a time
// as the caption pipeline would, and prints latency and chrF2 per pair.

var inv = CultureInfo.InvariantCulture;
var mode = args[0];
string? Opt(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
var count = int.Parse(Opt("--n") ?? "200", inv);
var threads = int.Parse(Opt("--threads") ?? "0", inv);
var cache = Path.GetFullPath("cache");
string[] Flores(string code) => File.ReadLines(Path.Combine(cache, "flores200_dataset", "devtest", code + ".devtest")).Take(count).ToArray();
var en = Flores("eng_Latn");
var pt = Flores("por_Latn");
var es = Flores("spa_Latn");
var ja = Flores("jpn_Jpan");

SessionOptions Options()
{
    var o = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
    if (threads > 0)
    {
        o.IntraOpNumThreads = threads;
    }
    if (mode == "dml")
    {
        o.EnableMemoryPattern = false;
        o.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        o.AppendExecutionProvider_DML(0);
    }
    return o;
}

var vram = new VramSampler();
var quantized = mode == "cpu-int8";
var sw = Stopwatch.StartNew();
using var enRomance = new MarianModel(Path.Combine(cache, "opus-mt-en-ROMANCE"), quantized, Options);
using var romanceEn = new MarianModel(Path.Combine(cache, "opus-mt-ROMANCE-en"), quantized, Options);
using var jaEn = new MarianModel(Path.Combine(cache, "opus-mt-ja-en"), quantized, Options);
var loadMs = sw.Elapsed.TotalMilliseconds;
enRomance.Translate("Warm-up sentence.", ">>pt_br<<");
romanceEn.Translate("Frase de aquecimento.");
jaEn.Translate("ウォームアップ。");

var pairs = new (string Name, string[] Source, string[] Reference, Func<string, string> Translate)[]
{
    ("en→pt", en, pt, s => enRomance.Translate(s, ">>pt_br<<")),
    ("pt→en", pt, en, s => romanceEn.Translate(s)),
    ("es→pt (pivot en)", es, pt, s => enRomance.Translate(romanceEn.Translate(s), ">>pt_br<<")),
    ("ja→pt (pivot en)", ja, pt, s => enRomance.Translate(jaEn.Translate(s), ">>pt_br<<")),
    ("en→pt, ASR-style source", en.Select(Disfluent).ToArray(), pt, s => enRomance.Translate(s, ">>pt_br<<")),
    ("es→en", es, en, s => romanceEn.Translate(s)),
    ("ja→en", ja, en, s => jaEn.Translate(s)),
};

Directory.CreateDirectory("out");
var rows = new StringBuilder();
foreach (var (name, source, reference, translate) in pairs)
{
    var times = new List<double>();
    var output = new List<string>();
    foreach (var s in source)
    {
        var t = Stopwatch.StartNew();
        output.Add(translate(s));
        times.Add(t.Elapsed.TotalMilliseconds);
    }
    times.Sort();
    var chrf = ChrF.Score(output, reference);
    var row = string.Create(inv, $"| Opus-MT {mode}{(threads > 0 ? $" ({threads} threads)" : "")} | {name} | {source.Length} | {times[times.Count / 2]:F0} ms | {times[(int)(times.Count * 0.95)]:F0} ms | {chrf:F1} |");
    Console.WriteLine(row);
    rows.AppendLine(row);
    File.WriteAllLines(Path.Combine("out", $"opus-{mode}-{name.Split(' ')[0].Replace("→", "-", StringComparison.Ordinal)}{(name.Contains("ASR", StringComparison.Ordinal) ? "-asr" : "")}.txt"),
        source.Zip(output, reference).Select(x => $"SRC\t{x.First}\nHYP\t{x.Second}\nREF\t{x.Third}\n"));
}
var summary = string.Create(inv, $"Opus-MT {mode}: load {loadMs:F0} ms, peak RAM {Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0:F0} MB, GPU memory rise {vram.Stop():F0} MB, unknown pieces {enRomance.UnknownPieces + romanceEn.UnknownPieces + jaEn.UnknownPieces}");
Console.WriteLine(summary);
File.AppendAllText(Path.Combine("out", "results.md"), rows + summary + Environment.NewLine + Environment.NewLine);
return 0;

// What a caption source looks like before punctuation is restored: lower case, no punctuation.
static string Disfluent(string s) => Regex.Replace(Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}' ]", " "), @"\s+", " ").Trim();
