# ASR engine spike (LE2), first slice

Which speech recognition engine and model Legendario runs, on which hardware. This folder
is a record of the answer, not live code: it is outside any solution and no gate builds it.

This slice measures the two engines that ship as NuGet packages: whisper.cpp through
Whisper.net, and sherpa-onnx with Parakeet. It ran on this machine only. Whisper exported
to ONNX on the Windows ML provider stack, onnxruntime-genai, the streaming zipformer,
captured clips (podcast, game, call) and other GPUs are still open on LE2.

## How it was measured

`AsrBench` loads one engine in its own process, because whisper.cpp picks its native
backend once per process. It then decodes:

- **Two fixed windows.** The first 2 s and the first 10 s of eight clips, as a streaming
  loop would decode them; the table gives the median.
- **Whole clips.** Thirty clips from the FLEURS dev split (CC-BY-4.0), one recording per
  sentence: 6.1 min of pt_br, 4.5 min of en_us and 5.6 min of es_419.

```
dotnet build -c Release
bin\Release\net10.0-windows\win-x64\AsrBench.exe whispercpp-vulkan:ggml-small.bin pt_br
bin\Release\net10.0-windows\win-x64\AsrBench.exe whispercpp-cpu:ggml-large-v3-turbo-q5_0.bin en_us
bin\Release\net10.0-windows\win-x64\AsrBench.exe parakeet-cpu es_419
```

The models and the corpus go in `cache\` (gitignored):

- `ggml-small.bin` and `ggml-large-v3-turbo-q5_0.bin` from `ggerganov/whisper.cpp`;
- `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8` from the sherpa-onnx `asr-models` release;
- `fleurs/<lang>/dev.tsv` and the extracted `dev/` folder from `google/fleurs`.

What the columns mean:

- **RTF**: decode time over audio time, for whole clips.
- **Window**: the median decode time of one 2 s or 10 s window.
- **WER**: computed in C# (`Wer` in `Program.cs`), not with jiwer. Text is lower-cased,
  punctuation is removed and words are compared by edit distance. There is no number
  normaliser, so Whisper writing "10" where FLEURS has "dez" counts as an error. The
  hypotheses are in `out\*.txt`.
- **Cold start**: model load time plus the first decode.
- **VRAM**: the rise in total GPU memory reported by `nvidia-smi`. Other applications move
  this number too: the CPU-only runs show 77 to 1,171 MB of that noise.

Machine: Windows 11 Pro 26300, Intel Core i7-14700 (28 threads, 62 GB RAM), RTX 4060
8 GB (driver 610.74), Intel UHD 770, no NPU. Engines used half the logical processors (14
threads). Packages: Whisper.net 1.9.1 with its CPU and Vulkan runtimes, and
org.k2fsa.sherpa.onnx 1.13.8. Measured 2026-11-12.

## Results

| Engine and model | Device | Lang | RTF | 2 s window | 10 s window | WER | Peak RAM | Cold start |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| whisper.cpp small | RTX 4060 (Vulkan) | pt_br | 0.036 | 270 ms | 445 ms | 6.6 % | 662 MB | 0.9 s + 0.3 s |
| whisper.cpp small | RTX 4060 (Vulkan) | en_us | 0.036 | 268 ms | 354 ms | 5.1 % | 655 MB | 1.1 s + 0.3 s |
| whisper.cpp small | RTX 4060 (Vulkan) | es_419 | 0.033 | 255 ms | 376 ms | 7.6 % | 662 MB | 0.8 s + 0.3 s |
| whisper.cpp large-v3-turbo q5_0 | RTX 4060 (Vulkan) | pt_br | 0.048 | 480 ms | 637 ms | 5.4 % | 673 MB | 0.8 s + 14.0 s (first run ever) |
| whisper.cpp large-v3-turbo q5_0 | RTX 4060 (Vulkan) | en_us | 0.060 | 540 ms | 622 ms | 4.8 % | 666 MB | 1.1 s + 0.6 s |
| whisper.cpp large-v3-turbo q5_0 | RTX 4060 (Vulkan) | es_419 | 0.048 | 480 ms | 534 ms | 3.9 % | 673 MB | 1.0 s + 0.5 s |
| whisper.cpp small | UHD 770 (Vulkan) | pt_br | 0.155 | 1,685 ms | 1,823 ms | 6.4 % | 1,340 MB | 1.0 s + 10.2 s |
| whisper.cpp large-v3-turbo q5_0 | UHD 770 (Vulkan) | pt_br | 0.429 | 4,780 ms | 5,211 ms | 5.3 % | 1,322 MB | 1.0 s + 8.8 s |
| whisper.cpp small | CPU | pt_br | 0.249 | 2,699 ms | 3,094 ms | 6.6 % | 958 MB | 0.5 s + 2.6 s |
| whisper.cpp small | CPU | en_us | 0.319 | 2,662 ms | 3,066 ms | 5.1 % | 955 MB | 0.6 s + 2.5 s |
| whisper.cpp small | CPU | es_419 | 0.289 | 2,638 ms | 3,371 ms | 7.6 % | 962 MB | 0.5 s + 2.6 s |
| whisper.cpp large-v3-turbo q5_0 | CPU | pt_br | 1.149 | 12,569 ms | 13,609 ms | 5.4 % | 1,099 MB | 0.5 s + 12.5 s |
| whisper.cpp large-v3-turbo q5_0 | CPU | en_us | 1.443 | 12,876 ms | 12,930 ms | 4.9 % | 1,040 MB | 0.5 s + 12.8 s |
| whisper.cpp large-v3-turbo q5_0 | CPU | es_419 | 1.309 | 13,795 ms | 13,531 ms | 3.8 % | 1,049 MB | 0.6 s + 13.4 s |
| sherpa-onnx Parakeet-TDT-0.6B-v3 int8 | CPU | pt_br | 0.363 | 2,088 ms | 3,965 ms | 7.0 % | 1,299 MB | 2.3 s + 2.0 s |
| sherpa-onnx Parakeet-TDT-0.6B-v3 int8 | CPU | en_us | 0.152 | 648 ms | 1,293 ms | 8.5 % | 1,024 MB | 2.1 s + 0.4 s |
| sherpa-onnx Parakeet-TDT-0.6B-v3 int8 | CPU | es_419 | 0.157 | 2,179 ms | 4,443 ms | 3.9 % | 1,043 MB | 2.1 s + 1.1 s |

The Vulkan runs raised GPU memory by about 0.8 to 1.0 GB for small and 1.1 to 1.2 GB for
large-v3-turbo q5_0. The model files are 488 MB and 574 MB. Given the noise above, read
these as upper bounds.

What the runs showed:

- **whisper.cpp on Vulkan fits the latency budget easily.** On the RTX 4060,
  large-v3-turbo q5_0 decodes a 2 s window in about 0.5 s and a 10 s window in about
  0.6 s, which leaves most of the 1.5 s source-text budget for the commit policy.
  small takes half that time at 1.2 to 3.7 points more WER.
- **Vulkan compiles its shaders on the very first run** (14 s on the RTX 4060, 9 to 10 s
  on the UHD 770). Later processes started in 0.5 to 0.6 s, so the driver caches the
  shaders across processes. The first-run cost belongs in setup (LE23), not in the first
  caption.
- **Whisper costs the same for any window shorter than 30 s.** It pads every input to
  30 s, so on the CPU a 2 s window costs as much as a 10 s one (2.6 s with small, 12.5 s
  with turbo). That rules out short-window streaming of Whisper on the CPU.
  large-v3-turbo on the CPU is slower than real time (RTF 1.15 to 1.44).
- **The UHD 770 iGPU can run small live,** at 1.7 s per window. That is too slow for
  2 s windows but workable with longer windows and a looser commit policy.
- **Parakeet on the CPU is the strongest CPU option, but its per-call cost varies.** RTF
  ranged from 0.15 to 0.36, and a 2 s window took 0.65 to 2.2 s. The pt_br run, the
  first one made, was the slowest; it was not repeated. Its WER sits close to Whisper
  small's: 3.9 % in es, 7.0 % in pt and 8.5 % in en. Its per-token timestamps suit
  streaming commits better than whisper.cpp's segment times.
- **The sherpa-onnx NuGet package ships a CPU-only `onnxruntime.dll`.** Running Parakeet
  on a GPU means building sherpa-onnx against the DirectML or Windows ML ONNX Runtime,
  or running Parakeet's ONNX files through our own loop on Revelei's provider stack.
- **In-process hosting worked.** Whisper.net loaded its native runtime in-process with no
  crash over 17 runs. Whether a host process is still worth having for crash isolation
  is a question for LE18, not for this measurement.
- **Licences.** Whisper weights, whisper.cpp and Whisper.net are MIT; sherpa-onnx is
  Apache-2.0; Parakeet-TDT-0.6B-v3 is CC-BY-4.0. All four are on the whitelist.

## Decision so far

- **GPU default: whisper.cpp large-v3-turbo q5_0 on Vulkan**, with small as the
  lower-tier model. Vulkan reaches NVIDIA, AMD and Intel GPUs without CUDA. That holds
  unless the ONNX path on Windows ML, measured in the next slice, is clearly faster on the
  same card. If it is, Revelei's provider stack applies unchanged.
- **CPU default: Parakeet-TDT-0.6B-v3 int8 through sherpa-onnx**, with Whisper on the CPU
  ruled out for live use. Its uneven per-call cost needs a second look before it is
  final.
- **NPU: not measured.** This machine has none.
- **Host process: not needed for performance.** In-process Whisper.net and sherpa-onnx
  both ran cleanly.
