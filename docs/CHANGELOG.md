# Shipped Ledger

## Block A — Feasibility spikes

- ✅ **LE1** **Spike: no measured way yet to capture system, per-app and mic audio from C# at low latency** — Capture spike measured all three WASAPI sources: 10 ms packets, under 22 ms latency, under 1.5 % of a core (design recorded in `spike/audio-capture/README.md`).
- ✅ **LE2 (whisper.cpp and Parakeet slice)** **Spike: no ASR engine chosen; Whisper on ONNX, whisper.cpp and sherpa-onnx are unmeasured on DirectX GPUs** — whisper.cpp turbo on Vulkan decodes a 2 s window in 0.5 s at 3.9-5.4 % WER; Parakeet int8 leads on CPU.

## Block B — Project foundation

## Block C — Audio capture

## Block D — Local inference runtime and models

## Block E — Streaming speech recognition

## Block F — Real-time translation

## Block G — Caption overlay

## Block H — App shell, settings and first use

## Block I — Delivery: installers, docs and benchmarks

