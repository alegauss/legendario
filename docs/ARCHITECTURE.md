# Architecture

The decisions that frame the backlog in [ROADMAP.md](ROADMAP.md). Move this into STRATEGY.md once roadkeep RK1742 (strategy `section add` crash) is fixed.

Legendario is Windows Live Captions with translation, running only on the user's PC. It is a WPF app on .NET 10 with the Fluent ThemeMode, the stack Revelei and Modelado share. Their UI-free inference code (providers, sessions, downloads, settings, logs) is ported rather than shared, which is how the two siblings already relate.

**Pipeline:**

```
WASAPI loopback / process loopback / mic → 16 kHz mono → Silero VAD
  → streaming ASR (LocalAgreement commit) → sentence segmenter
  → MT (direct, pivot or multilingual) → caption model → overlay
```

**Decisions:**
- **Capture.** WASAPI, not DirectSound. Per-app capture through process loopback is the feature Live Captions lacks.
- **Inference.** Windows ML's ONNX Runtime with Revelei's provider ladder: TensorRT-RTX or OpenVINO, then DirectML, then CPU. Placement is chosen per stage, VAD always runs on the CPU, and a user's forced provider fails loudly.
- **Real time.** Unlike the siblings' batch queues, every stage is bounded, has a deadline, and sheds work in a fixed order: longer windows, then no drafts, then a smaller model, then source text only.
- **Latency target.** 1.5 s from speech to source text and 2.5 s to translated text (p95) on a mid-range RTX GPU, twice that on CPU only. The bench measures it on a fixed licensed corpus.
- **Licences.** Only MIT, Apache-2.0, BSD and CC-BY-4.0 models ship, each with its attribution.
- **Privacy.** Audio stays in memory, caption text is never logged, and transcripts are opt-in.

**Order of work.** The Block A spikes decide the engines (ASR, MT and policy) before Blocks C–F build them. Block B can start at once.
