# MT engine spike (LE3), Opus-MT slice

Which translation model Legendario runs, per language pair, and where. This folder is a
record of the answer, not live code: it is outside any solution and no gate builds it.

This slice measures Opus-MT (MarianMT) on ONNX Runtime from C#. Still open on LE3:

- the direct Opus-MT models that have no ONNX export yet;
- MADLAD-400 3B;
- a small LLM tier;
- beam search, context and COMET;
- tokenizer parity against Python.

## How it was measured

`MtBench` loads three Opus-MT directions in one process: `en-ROMANCE` (target token
`>>pt_br<<`), `ROMANCE-en` and `ja-en`. The exports are Xenova's ONNX
(`encoder_model`, `decoder_model`, `decoder_with_past_model`, in fp32 and `_quantized`
int8). The decoder is a greedy loop with a KV cache, the same shape as LE2's Whisper loop,
with `<pad>` suppressed as Marian's generation config asks. Source text is split by
Microsoft.ML.Tokenizers 3.0.0's `SentencePieceTokenizer` on `source.spm`, and the pieces
are mapped through Marian's `vocab.json`. Each sentence is translated alone, as the caption
pipeline will.

```
dotnet build -c Release
bin\Release\net10.0-windows\win-x64\MtBench.exe cpu --n 100 --threads 4
bin\Release\net10.0-windows\win-x64\MtBench.exe cpu-int8 --n 100 --threads 4
bin\Release\net10.0-windows\win-x64\MtBench.exe dml --n 100
```

- **Corpus.** The first 100 sentences of FLORES-200 devtest (CC-BY-SA-4.0,
  `flores200_dataset.tar.gz` from dl.fbaipublicfiles.com) for eng_Latn, por_Latn,
  spa_Latn and jpn_Jpan, in `cache\` (gitignored).
- **Score.** chrF2 computed in C# (`ChrF` in `Marian.cs`) following sacrebleu 2.x:
  character 1- to 6-grams, whitespace removed, an F-beta per order averaged over orders.
  Not checked against sacrebleu itself, which is not installed here. Translations are in
  `out\*.txt`.
- **ASR-style source.** "en→pt, ASR-style source" feeds the English lower-cased with
  punctuation removed, which is what an ASR segment looks like before any punctuation
  is restored.
- **Machine.** The same as LE2: i7-14700 (28 threads), RTX 4060, Windows 11 26300.
  Measured 2026-11-12.

## Results

Latency is per sentence, including tokenization.

| Pair | CPU fp32, 4 threads (p50 / p95) | CPU fp32, 1 thread | CPU int8, 4 threads | DirectML fp32 | chrF2 (fp32) |
| --- | --- | --- | --- | --- | --- |
| en→pt | 269 / 529 ms | 397 / 809 ms | 552 / 1,043 ms | 516 / 880 ms | 67.9 |
| pt→en | 203 / 412 ms | 365 / 707 ms | 509 / 894 ms | 461 / 967 ms | 69.6 |
| es→pt (pivot en) | 632 / 1,114 ms | 782 / 1,312 ms | 1,159 / 2,049 ms | 1,011 / 2,051 ms | 54.1 |
| ja→pt (pivot en) | 498 / 976 ms | 720 / 1,474 ms | 1,056 / 1,806 ms | 900 / 1,697 ms | 44.4 |
| en→pt, ASR-style source | 241 / 500 ms | 411 / 852 ms | 537 / 951 ms | 476 / 1,008 ms | 62.2 |
| es→en | 280 / 482 ms | 407 / 762 ms | 528 / 896 ms | 496 / 1,010 ms | 57.0 |
| ja→en | 197 / 340 ms | 379 / 673 ms | 453 / 840 ms | 471 / 837 ms | 48.5 |

Peak RAM with all three models loaded: 2.9 GB in fp32 and 2.1 GB in int8. Load time: 6 s
for fp32 and 2.6 s for int8 on the CPU, 8.8 s on DirectML. DirectML raised GPU memory by
about 3 GB.

What the runs showed:

- **ONNX Runtime's default thread count is a trap.** With no `IntraOpNumThreads` set, ONNX
  Runtime used every core and en→pt took 1.2 to 1.5 s per sentence (5-sentence smoke
  test). With 4 threads it took 0.27 s, and even 1 thread beat the default. Per-sentence
  MT is too small a job to spread over 28 threads. The MT session must set its own small
  thread count, which also leaves the cores to ASR.
- **The CPU beats DirectML for Marian.** DirectML was about twice as slow as 4 CPU threads
  and took 3 GB of GPU memory. It is the same per-step cost of dynamic shapes that LE2
  measured for Whisper, and a 75M-parameter model gives the GPU nothing to win back.
- **int8 is slower, not faster.** Xenova's dynamically quantised graphs took twice as long
  as fp32 on this CPU, at the same chrF. They save 0.8 GB of RAM and half the load time.
- **The pivots are the weak point.** es→pt through English loses 14 chrF against en→pt and
  doubles the latency. ja→pt loses 24. A direct model exists for both on Hugging Face,
  `Helsinki-NLP/opus-mt-ja-pt` and `opus-mt-tc-big-itc-itc` (Italic to Italic, so es→pt),
  as does `opus-mt-tc-big-en-pt`. None has an ONNX export, so they are the next slice.
  There is no small `opus-mt-es-pt`.
- **Unpunctuated, lower-cased input costs 5.7 chrF** on en→pt (67.9 to 62.2). LE28's
  segmenter should restore case and punctuation, or pass sentences with them where ASR
  gives them; Whisper's output usually has both.
- **Microsoft.ML.Tokenizers handled the SentencePiece models.** One unknown piece in 700
  source sentences across three vocabularies, and the translations read correctly. A
  piece-for-piece comparison against Python's `sentencepiece` was not run.
- **Licences.** Opus-MT `en-ROMANCE`, `ROMANCE-en` and `ja-en` are Apache-2.0 on their
  model cards; the `tc-big` models are CC-BY-4.0. Both are on the whitelist.
  - MADLAD-400 and Qwen3 are Apache-2.0.
  - Gemma 3 is under Google's Gemma terms, and NLLB-200 is CC-BY-NC-4.0. Both are off the
    whitelist.
  - **Tencent HY-MT 1.5 is excluded.** Its Tencent HY Community License does not apply in
    the EU, the UK or South Korea, and it is not on the whitelist.

## Decision so far

- **Default for en↔pt: Opus-MT fp32 on the CPU, 4 intra-op threads, beside the ASR on
  the GPU.** At 0.2 to 0.3 s per sentence (p95 0.4 to 0.5 s) it fits well inside the 1 s
  that the 2.5 s translated-caption budget leaves after ASR.
- **The pairs that pivot today** (es→pt, ja→pt) wait for the direct models in the next
  slice before a default is named.
- **The LLM "quality" tier is undecided** until the next slice measures Qwen3 1.7B and 4B.
  HY-MT is not a candidate.
