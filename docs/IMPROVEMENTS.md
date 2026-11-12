# Improvements

## Block A — Feasibility spikes

### §LE2 ASR engine spike: Whisper ONNX vs whisper.cpp vs sherpa-onnx

Build `spike/asr-engines/` as a C# bench that runs the same corpus through each
candidate. The corpus is about 20 min of pt-BR, en and es from FLEURS (CC-BY-4.0) plus
captured clips of a podcast, a game and a call.

Candidates:
- **Whisper** large-v3-turbo, small and base, exported to ONNX as encoder and decoder with a KV cache. Run it two ways: onnxruntime-genai (C# API, still in preview), and our own greedy loop on the Windows ML ONNX Runtime, so the provider stack ported from Revelei (DirectML, TensorRT-RTX, OpenVINO, QNN) applies as it is.
- **whisper.cpp through Whisper.net** with the Vulkan and CPU backends. Run it out of process, following Revelei's `Generation.Host` pattern.
- **sherpa-onnx C#** with Parakeet-TDT-0.6B-v3 (offline, CC-BY-4.0, 25 European languages) and a streaming zipformer.

Measure for each:
- real-time factor;
- latency to the first token on a 2 s chunk and on a 10 s chunk;
- WER per language, scored with jiwer in Python;
- peak VRAM and RAM;
- the cold-start time, including the TensorRT-RTX engine build;
- whether the output timestamps can drive streaming commits.

Run on an RTX card, an Intel iGPU, an AMD card if one is available, and CPU only. Record
each licence against Modelado's whitelist (MIT, Apache-2.0, BSD, CC-BY-4.0).

**Decision output:** the default engine and model for GPU, for CPU, and for NPU where
one exists, and whether a native host process is needed.

### §LE3 MT engine spike: dedicated MT models vs small LLMs

Build `spike/mt-engines/` to translate the FLORES-200 devtest sentences for en→pt,
es→pt, pt→en and ja→pt. Score the output with sacrebleu chrF, plus COMET-22 where a GPU
is available.

Candidates:
- **Opus-MT / MarianMT** per language pair (CC-BY-4.0, about 300 MB each), exported with optimum to an encoder and a decoder with past key values.
- **MADLAD-400 3B** (Apache-2.0).
- **NLLB-200-distilled-600M**, measured as a quality reference only, because its CC-BY-NC licence keeps it out of the shipped catalog.
- **Tencent HY-MT 1.5 1.8B**. Check its licence.
- A **small LLM** (Qwen3 1.7B/4B or Gemma 3 4B, int4) through onnxruntime-genai, and through llama.cpp Vulkan in a host process.

Tokenization: test whether `Microsoft.ML.Tokenizers` handles each SentencePiece model
exactly, compared with the Python tokenizer on 1,000 sentences.

Measure per sentence:
- p50 and p95 latency with greedy decoding and with beam 2;
- VRAM;
- the effect of passing the two previous sentences as context. Only LLMs can use context this way; Marian needs a concatenation trick.

Note also:
- how each model handles disfluent ASR output with no punctuation;
- which pairs need English as a pivot.

**Decision output:**
- the default model per pair;
- whether it runs on the GPU next to ASR or on the CPU;
- whether an LLM tier is worth offering as a "quality" option.

### §LE4 Streaming policy spike: chunking, commit and when to translate

Replay the LE2 corpus offline, in simulated real time, through the chosen ASR and MT
engines. Log every hypothesis the engines emit, with timestamps.

ASR policies to compare:
- **(a)** utterances cut by Silero VAD and recognised once the speaker pauses;
- **(b)** a sliding 2–15 s window that keeps the prefix two consecutive passes agree on (LocalAgreement-2, from whisper_streaming), with the committed text fed back as the prompt;
- **(c)** a model that streams natively, if LE2 picked one.

Translation policies to compare:
- translate only committed sentences;
- also translate the stable unfinished prefix as a draft and replace it when the sentence commits;
- wait-k style, translating after k words.

Metrics:
- latency from the end of the spoken word to the source text on screen, and to the translated text on screen (p50 and p95);
- flicker, counted as words rewritten after their first display;
- WER and chrF against the corpus references;
- GPU duty cycle.

Then pick the default policy and parameters: the window length, the minimum chunk, the
commit threshold, and the timeout that forces an unfinished sentence to be translated.

The target is 1.5 s from speech to source text and 2.5 s to translated text on a
mid-range RTX GPU, and twice that on CPU only. If no policy meets the target, record
which parts of the latency could not be reduced.

### §LE5 Overlay window spike: transparent, topmost, click-through, docked

Build `spike/overlay/` as a WPF app on .NET 10 with the Fluent `ThemeMode`. Try three
ways of making the transparent caption window:

- **(a)** WPF `AllowsTransparency=True` with `WindowStyle=None`, which renders through a layered window in software;
- **(b)** a normal window with a DWM-extended frame and a transparent backdrop (`DwmExtendFrameIntoClientArea`, `DWMWA_SYSTEMBACKDROP_TYPE`), which keeps hardware rendering;
- **(c)** a D3D11 flip-model swapchain with DirectComposition (`DCompositionCreateDevice`, premultiplied alpha), rendering the text with DirectWrite through Vortice. This reuses Revelei's `Revelei.Render`.

For each, check:
- the extended styles `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, plus `WS_EX_TRANSPARENT` toggled for click-through;
- per-monitor V2 DPI;
- that it stays on top of borderless fullscreen games, the full-screen players in Edge, Chrome and VLC, and PowerPoint presenter mode, with true exclusive fullscreen documented as unsupported;
- a docked mode that reserves a screen edge with `SHAppBarMessage(ABM_NEW/ABM_SETPOS)`, the way Live Captions does;
- `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`, so screen sharing can include or leave out the captions.

Measure the GPU and CPU cost of 10 updates per second of text with outlined 32 px
glyphs. Measure the input-to-paint latency.

**Decision output:** the technique for the floating mode, the technique for the docked
mode, and the measured cost of each.

### §LE6 GPU sharing spike: device placement and contention

This spike reuses the LE2 and LE3 bench.

**Placements to compare:**
- ASR and MT on the same GPU device in one process, with separate sessions;
- ASR on the GPU and MT on the CPU;
- ASR on an NPU, through the Windows ML QNN, OpenVINO or VitisAI execution providers on Copilot+ or Core Ultra hardware where available, with MT on the GPU or CPU;
- everything on the CPU, with the intra-op thread count capped.

**Load:**
- No other load.
- A game benchmark running in a loop. Use the Unigine Superposition demo or a 3DMark loop.
- 4K AV1 playback in a browser.

**Record:**
- caption latency at p50 and p95;
- inference time per pass;
- the frame rate the game loses, and its frame-time spikes;
- VRAM;
- the power the package draws on battery.

**Also test:**
- whether lowering the thread priority, or setting the process to `PROCESS_POWER_THROTTLING_EXECUTION_SPEED` (EcoQoS), helps;
- whether DirectML command queues starve under a fullscreen game;
- whether TensorRT-RTX's faster passes free GPU time for the game.

**Decision output:** a placement table keyed on the hardware profile (from
`HardwareFit`) and on the power state, which the scheduler (Block D) applies by default.

### §LE7 Live Captions as an ASR source via UI Automation

Windows 11 22H2 and later runs Live Captions entirely on the device. The open-source
LiveCaptions-Translator project (Apache-2.0) reads its caption text with UI Automation
and sends that text to a translator.

This spike checks whether Legendario could offer the same as an `IRecognizer` source:

- start or attach to `LiveCaptions.exe`;
- find the caption text element by AutomationId;
- poll it or subscribe to `TextChanged` events;
- diff successive snapshots into partial and committed text;
- hide or minimise the Live Captions window without stopping recognition.

Measure:
- the extra latency compared with the native ASR;
- how stable the element tree is across Windows builds (24H2, 25H2);
- which source languages Live Captions supports;
- what happens on Copilot+ PCs, where Live Captions already translates into English.

Risks:
- the element tree is an undocumented UI, not an API, so any Windows update can break it;
- the user must have downloaded the Live Captions language pack.

If the spike succeeds, the source is filed as an optional "System captions" engine, with
a clear notice that it depends on Windows. If it fails, write the reason here and retire
the idea.

### §LE49 Manual capture runs: device changes and DRM

LE1 measured cadence, latency, silence and CPU unattended, but three behaviours need a
person at the machine. Run them with `spike/audio-capture` and add the numbers to its
README.

**Device changes.** Run `capture raw loopback --tone 440 --follow-default --seconds 120`
and, during the run:
- plug in and unplug a wired headset;
- switch the default playback device in Settings;
- connect a Bluetooth headset, then start a call so it switches from A2DP to HFP.

Record, from the summary's event lines, which of `OnDefaultDeviceChanged` and
`AUDCLNT_E_DEVICE_INVALIDATED` arrives first, how long the reopen takes, how many frames
are lost, and whether the mix format changes. Repeat with `capture raw process` to see
whether process loopback survives a device change without reopening. Do the same for the
microphone (`capture raw mic --follow-default`).

**DRM.** Play a PlayReady-protected stream (Netflix in Edge) while running endpoint
loopback and process loopback on the browser's process. Record whether the capture is
exact digital zero, `SILENT`-flagged, or real audio. LE17's "possibly protected content"
heuristic depends on the answer.

**Decision output:** the reconnect timing LE12 can promise, and the signature LE17 uses
to recognise protected content.

## Block B — Project foundation

### §LE8 Solution skeleton ported from Revelei

Create `Legendario.slnx`, targeting x64 only, and add the projects below. Write
`CLAUDE.md` (the build and test commands, plus the rules below) and a short `README.md`
stub.

**Build settings:**
- `global.json` pins SDK 10.0.100 (`rollForward latestFeature`).
- `Directory.Build.props` copies Revelei's: `TreatWarningsAsErrors`, Nullable, `AnalysisLevel latest` with Performance set to All, `GenerateDocumentationFile`, a single `<Version>0.1.0`, and `Platforms=x64`.
- `tests/Directory.Build.props` sets up xunit.v3 on Microsoft.Testing.Platform as exe test projects, with HangDump, and copies `onnxruntime.dll` next to each test exe. That copy is Revelei's lesson that Windows 11 otherwise loads the System32 1.17 build.

**Projects (`src/`):**

| Project | Target | Holds |
|---|---|---|
| `Legendario.App` | `net10.0-windows10.0.19041.0`, WPF, `ThemeMode` (NoWarn WPF0001), per-monitor V2 manifest | Shell |
| `Legendario.Audio` | | Capture, resampling, VAD |
| `Legendario.Inference` | | Providers, sessions, models |
| `Legendario.Speech` | | Recognisers |
| `Legendario.Translation` | | Translators |
| `Legendario.Pipeline` | | Real-time scheduler and caption model, no UI |
| `Legendario.Overlay` | | Caption window and rendering |

**Tests and tools:**
- one `*.Tests` project for each library;
- `Legendario.Architecture.Tests`, which ports Revelei's `LayeringTests` (UI-free libraries must not reference WPF, and Pipeline sees only interfaces), the string-resource test and the version consistency test;
- `tools/Legendario.Bench`.

**Gate:** `run-tests.cmd [Config]` builds the solution with warnings as errors and runs
every test exe with `--hangdump`. That command is the gate `execute-roadkeep-tasks` runs
before each commit.

### §LE9 Settings, operation log and command line

Port the following from Revelei into `Legendario.App` and a small `Legendario.Core` if
layering needs it.

**Settings (`AppSettings`):**
- a snake_case JSON record at `%LOCALAPPDATA%\Legendario\settings.json`;
- atomic writes (write a `.tmp` file, then move it);
- the `LOCALAPPDATA` override for tests;
- unknown keys preserved on save.

Initial keys:
- `source` (system, app:<exe>, mic:<id>)
- `source_language` (auto or BCP-47)
- `target_language`
- `provider` (auto, winml, dml, cpu)
- `asr_model`, `mt_model`
- `overlay` (mode, position, style)
- `transcript_history`, off by default

**Operation log (`OperationLog`):**
- rotating JSONL under `logs\`, 1 MB per file, five files kept;
- one line per session start, model load, provider fallback and error;
- every 10 s while running, a latency summary (p50 and p95 per stage);
- caption text is never logged unless a debug flag is set. That is a privacy rule, and a test enforces it.

**Packaging helpers:** `PackageIdentity` and `PackagedPaths`, which map `%LOCALAPPDATA%`
to `LocalCache` for MSIX.

**Command line (`CommandLine`)**, documented in `docs/COMMAND-LINE.md`:
- `--provider`, `--source`, `--from`, `--to`, `--models <dir>`;
- `--replay <wav>`, which feeds a file through the pipeline instead of live capture. The bench, the UI cases and bug reports all use it.

**Tests:**
- settings round-trip, and a corrupt file read as defaults;
- log rotation;
- argument parsing, including invalid combinations.

### §LE10 Evaluation corpus, replay source and Legendario.Bench

**Corpus.** `scripts/fetch_corpus.py` downloads a pinned subset of FLEURS (CC-BY-4.0)
for pt-BR, en, es, fr, de, ja and zh. For each it stores 10 min of audio with reference
transcripts and the parallel FLORES translations in every other language. Files go to
`.cache/corpus/<lang>/`, with the source revision pinned and the hashes checked.

Add three synthetic "hard" clips made from CC-licensed material:
- speech over music;
- two speakers overlapping;
- long silences, which exercise hallucination handling.

The corpus is never committed. Tests that need it skip when it is absent, as Modelado's
model tests do. A tiny 3 s fixture WAV is committed for the unit tests.

**Replay source.** `ReplayAudioSource : IAudioSource` reads a WAV and emits frames on a
`TimeProvider` clock, either at real-time pace or as fast as possible. Its timestamps
match live capture, so the pipeline cannot tell the difference.

**Bench.** `tools/Legendario.Bench` runs corpus × engine × provider and writes JSON and Markdown containing:
- RTF;
- latency from speech end to source text and to translated text (p50 and p95);
- WER, CER and chrF. These are computed in C# so the bench has no Python dependency. Cross-check them once against jiwer and sacrebleu.
- flicker (rewritten words);
- peak VRAM and RAM.

It reuses Revelei's `Bench` conventions, and its output becomes the README's numbers
table (Block I).

### §LE11 CI on windows-latest

Add `.github/workflows/tests.yml`, running on push and on pull requests to main with
`windows-latest`. It:

1. sets up .NET from `global.json`;
2. restores and builds `Legendario.slnx` in Release with warnings as errors;
3. runs every test exe except the classes marked `[Trait("needs", "gpu")]`, `"models"` or `"desktop"`;
4. uploads hang dumps and TRX results when a step fails.

**What CI can and cannot cover.** A hosted runner has no audio endpoint and no GPU:
- audio tests use the replay source and the committed fixture WAV;
- inference tests run only the CPU execution provider, with the tiny probe model from Revelei's `ProbeModel` and a stand-in model. This proves the session code paths, not model quality.

**Caching.** Cache the NuGet packages, keyed on `Directory.Packages.props`. Use central
package management from day one, so the Windows ML, Vortice and NAudio versions are
pinned in one place.

**Later.** The release job is added in Block I (installer on a `v*` tag). The existing
`roadkeep.yml` stays as it is.

**Done when** a deliberate analyzer warning fails the job, and a passing run takes under
10 minutes.

## Block C — Audio capture

### §LE12 System loopback capture source

Implement `LoopbackAudioSource : IAudioSource` in `Legendario.Audio`, using the library
and buffer size LE1 chose.

**Contract.** `IAudioSource` (introduced in LE10) exposes `StartAsync`, `StopAsync` and a `ChannelReader<AudioFrame>`. Each `AudioFrame` carries:
- interleaved float32 samples;
- the native rate and channel count;
- a QPC timestamp for the first sample, taken from the device position, not from `DateTime.Now`;
- a discontinuity flag.

**Capture thread:**
- An event-driven capture thread, registered with MMCSS ("Audio") through `AvSetMmThreadCharacteristics`, copies each packet into a pooled buffer (`ArrayPool<float>`) and never allocates on the hot path.
- A bounded channel (about 2 s) sits between the capture thread and the pipeline. When it is full, the oldest frames are dropped and the drop is counted. The capture thread must never block.
- Loopback delivers no packets while nothing plays, so the source synthesises silent frames to keep the downstream clock continuous. LE1 showed the device position jumps by exactly the gap, and the first packet's discontinuity flag is always set.

**Device changes.** An `IMMNotificationClient` follows default-device changes. On
`OnDefaultDeviceChanged`, or on `AUDCLNT_E_DEVICE_INVALIDATED`, the source reopens on
the new endpoint within 500 ms and emits a discontinuity.

**Tests:**
- the frame contract and timestamps, with a fake `IAudioClient`;
- the drop policy;
- reconnection after an injected device-invalidated error;
- a `[Trait("needs","desktop")]` test that plays a sine wave through the default endpoint and verifies it is captured.

### §LE13 Downmix and resample to 16 kHz mono

Implement `AudioConverter` as a streaming stage, `AudioFrame` in and `Pcm16kFrame` out.
LE1 chose the audio engine's own converter (`AUTOCONVERTPCM`, 16 kHz mono float) for
every WASAPI source, so this stage serves only other input: the LE10 replay corpus and
files.

**Pipeline:**
1. Downmix to mono. Average the channels; for 5.1 and 7.1, weight the centre channel up, because dialogue lives there.
2. A DC blocker.
3. Resample with the Media Foundation resampler DSP at quality 60, which LE1 measured flat to 7 kHz with over 100 dB of alias rejection at 0.1 % of a core. A 64-tap Kaiser filter cannot pair a 7.6 kHz cutoff with −80 dB.
4. Optional peak normalisation for very quiet sources, with a capped gain.

**Implementation:**
- `System.Numerics.Vector<float>` or `TensorPrimitives` for the dot products.
- Filter state carried across frames, so frame boundaries add no clicks.
- Timestamps carried through: the delay the filter adds is reported and subtracted.

**Output** is 20 ms frames (320 samples), matching the VAD frame size, in a ring buffer
the ASR stage reads windows from without copying (`ReadOnlyMemory<float>` slices).

**Tests:**
- a sine sweep: the passband ripple and the stopband rejection are measured;
- splitting the input at random frame boundaries gives the same output, bit for bit, as one large call;
- timestamp drift stays under 1 ms over one hour of simulated input;
- a benchmark: under 0.5% of one core for 48 kHz stereo input.

### §LE14 Silero VAD stage

Implement `VoiceActivityDetector` in `Legendario.Audio`, running Silero VAD v5 (MIT) as
ONNX on the CPU execution provider. The model is about 2 MB, so it ships inside the app
and is never downloaded.

**Inference:**
- Frames of 512 samples (32 ms at 16 kHz).
- The LSTM state is carried from one call to the next.
- A single `InferenceSession` with one intra-op thread.

**Hysteresis** turns the speech probability into `SpeechStarted` and `SpeechEnded` events:
- speech starts when the probability stays at or above 0.5 for 64 ms;
- speech ends when it stays below 0.35 for a configurable silence, 300 ms by default;
- 200 ms of padding is kept before and after each segment, so first and last syllables are not clipped.

**Hard cap.** A segment longer than `max_segment` (about 15 s, or the value LE4
measures) is split at the quietest 100 ms inside its last 3 s, so continuous speech
still produces captions.

**Output.** Events carry sample-accurate timestamps on the capture clock, and the ASR
stage reads audio from the shared ring buffer (LE13).

**Fallback.** An energy and zero-crossing gate, used only if the model fails to load,
which is logged.

**Tests**, on the corpus:
- precision and recall of speech frames against the FLEURS segment boundaries;
- no events on 60 s of music;
- the split behaviour on a 40 s monologue;
- deterministic output for the same input.

### §LE15 Per-application capture via process loopback

Implement `ProcessLoopbackAudioSource : IAudioSource` with `ActivateAudioInterfaceAsync`
and `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`. Use
`PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE` so browsers, which play audio from
child processes, are captured whole. The interop is hand-written `[ComImport]`, as in
LE1's spike, which also showed `GetDevicePeriod` answers `E_NOTIMPL` here.

**Picker.** `AudioSessions.Enumerate()` lists the active render sessions through
`IAudioSessionManager2` and `IAudioSessionEnumerator`. Each entry carries the process
id, the exe name, the display name, the icon and whether it is playing right now.
Sessions are grouped by root process, so the list shows one row for Chrome, not one per
tab. The picker refreshes on `OnSessionCreated`.

**Exclude mode.** Offer the inverse as well: everything except the chosen apps, which is
how the user mutes Spotify while a call is being captioned.

**Lifecycle:**
- when the target process exits, the source reports `SourceEnded` and the UI offers to reselect;
- reattach to a process with the same exe name when it restarts.

**Gating:**
- the feature needs Windows 10 build 20348 or later, or Windows 11;
- on older builds the picker is hidden and a notice names the version required.

**Format.** Process loopback reports the format the caller asked for (float 48 kHz
stereo). Verify that, and normalise it through LE13.

**Tests:**
- enumeration with a fake session manager;
- the include and exclude modes;
- target exit;
- a desktop test that captures a child process playing a tone.

### §LE16 Microphone source and meeting mode

Implement `MicrophoneAudioSource : IAudioSource` on a chosen capture endpoint, or on the
default communications endpoint. It follows device changes the same way as LE12.

Two microphone-specific problems:
- **Exclusive-mode conflicts.** Use shared mode only, and report `AUDCLNT_E_DEVICE_IN_USE` clearly to the user.
- **Communications ducking.** Opening the microphone can make Windows lower other audio. Set the session category to `AudioCategory_Other` with `IAudioClient2::SetClientProperties` so it does not.

**Meeting mode** runs two sources at once, system (or one app) and the microphone, as
two independent pipelines. They share the ASR and MT sessions through the scheduler
(Block D). Each caption is tagged with its source, shown as "You" or "Others", or with
the app name.

**Echo.** The microphone also picks up loudspeaker audio, which would caption the other
side twice. A simple, deterministic rule handles it: suppress a microphone segment when
its text closely matches, by normalised edit distance, a system segment that overlaps it
in time. Full acoustic echo cancellation is out of scope, so headsets are recommended in
the UI.

**Privacy.** A visible indicator shows while the microphone is captured; Windows also
shows its own mic icon. The microphone is never opened unless the user chose it.

**Tests:**
- endpoint selection;
- the in-use error;
- echo suppression on synthetic overlapping transcripts;
- a two-source pipeline replay with the correct labels.

### §LE17 Audio level meter and no-caption diagnostics

Add an `AudioHealth` monitor that consumes frames and VAD events and publishes a state
the UI shows next to the source picker.

States:
- **Receiving speech**
- **Audio, no speech**: music or effects
- **Silence**: nothing is playing
- **Endpoint muted**, read from `IAudioEndpointVolume.GetMute` and the master volume. Shown as information only: LE1 found that endpoint loopback still captures while the speakers are muted. The source app's own session mute is read from `ISimpleAudioVolume`.
- **Possibly protected content**: the player is known to be playing, yet the capture is exactly zero for more than 5 s. This is the case LE1 documents.
- **Device lost, reconnecting**

The meter is RMS and peak in dBFS at 10 Hz, drawn as a small Fluent bar.

When no caption appears for 10 s while the meter shows audio but no speech, the overlay
shows a discreet hint, for example "Hearing audio but no speech." The hint never covers
real captions.

**Tests:**
- the state machine, fed with synthetic frames for each state;
- the protected-content heuristic, with zero frames plus a fake "session active" flag;
- no hint while captions flow.

## Block D — Local inference runtime and models

### §LE18 Provider stack ported from Revelei

Port the following from `Revelei.Inference` into `Legendario.Inference`, together with their tests:
- `GpuAdapters`: DXGI enumeration through Vortice.DXGI; skips software adapters.
- `ProviderDetector` with `IProviderProbe` and the in-memory `ProbeModel`.
- `WindowsMLProviders`: `RegisterCertifiedAsync`, `GetEpDevices` filtered by the `DxgiAdapterNumber` metadata, and `AcquireAsync` running in the background.
- `SessionOptionsFactory`.
- `MemoryFailures`: out-of-memory and device-lost classification.
- `MemoryBudget`: `QueryVideoMemoryInfo`, with unknown handled for VMware SVGA adapters.
- `SessionCache<T>`: leases and an idle release.

The package is `Microsoft.Windows.AI.MachineLearning` (Windows ML's ONNX Runtime). Use
it unpackaged, reg-free, as Revelei does.

Changes for Legendario:
- **The user's choice is kept apart from the resolved provider.** A forced `dml` or `winml` that fails is an error the UI shows, never a silent CPU fallback. This avoids Modelado's MOD120 bug.
- **Placement per stage.** VAD always runs on the CPU. The ASR and MT stages each resolve a placement from the LE6 table: provider plus adapter, or CPU with a thread cap. The UI chip shows both, e.g. "ASR: TensorRT-RTX · RTX 4060 | MT: CPU".
- **Background acquisition.** The first Windows ML acquisition can take about 57 s. It must not block captions: start on DirectML or the CPU, then upgrade the sessions the next time captions are idle.
- **Pinning.** A session that throws device-lost is recreated once. A second failure pins that stage to the next provider and records the reason.

**Tests:** probe ordering, forced-provider failure, device-lost recovery with a fake
session, and placement resolution from a hardware profile.

### §LE19 Model manifest, downloads and library

Port `ModelDownloads`, `DownloadQueue` and `ModelLibrary` from Revelei with their tests.
That brings `.part` files with resume, SHA-256 checked while streaming, a stall timeout,
a free-disk check, shared blobs and a persisted queue. Add a new manifest schema,
`legendario-models/1`, embedded in the assembly:

```json
{ "schema": 1, "sequence": 1, "models": [ {
  "id": "opus-mt-en-pt", "kind": "mt",
  "languages": { "from": ["en"], "to": ["pt"] },
  "engine": "marian",
  "variants": { "fp16": {...}, "int8": {...} },
  "files": [ { "role": "encoder", "path": "encoder.onnx",
               "url": "https://huggingface.co/.../resolve/<commit>/...",
               "bytes": 0, "sha256": "..." },
             { "role": "tokenizer", ... } ],
  "memory": { "vram_mb": 0, "ram_mb": 0 },
  "license": "CC-BY-4.0", "license_url": "...", "attribution": "...",
  "non_commercial": false } ] }
```

Rules:
- each model has several files, keyed by role, with ONNX external data allowed;
- URLs must be https and pinned to a Hugging Face commit;
- `attribution` is required for CC-BY, and the About page shows it.

**Library layout:** `%LOCALAPPDATA%\Legendario\models\<id>\<variant>\...`, with the
folder movable (port `ModelsFolderMove`).

`Library.Resolve(kind, from, to, hardware)` returns the best installed model, or the one
to offer for download. That includes a pivot route (xx→en→pt) when no direct pair
exists.

**Sizes shown** are those of the variant this machine will actually fetch, which avoids
Modelado's MOD117.

**Signed catalog.** The remote catalog is checked with an ECDSA signature and a sequence
number, as in Revelei's `ModelCatalog`. Port it, but keep it switched off until a key
exists.

**Tests:**
- schema validation and refusals (http URLs, a missing hash, an NC model in the default set);
- resolving the route;
- downloads against the loopback fixture server.

### §LE20 Model export and publishing scripts

Add these Python scripts in `scripts/`, modelled on Modelado's `export_models.py`,
`build_manifest.py`, `publish_models.py` and `verify_published.py`, and on Revelei's
`export-and-quantize.py`:

- **`export_asr.py`** exports the ASR model LE2 chose with optimum or the engine's own exporter. For Whisper that is the encoder plus a decoder with past key values merged into one graph. The script also applies ORT transformer optimisations. Variants:
  - fp16 for GPUs;
  - int8 dynamic quantisation for the CPU;
  - QDQ int8 for NPUs, only if LE6 found one worth using.
- **`export_mt.py`** does the same for each Marian pair or MT model, plus the SentencePiece `.model` and the vocabulary files.
- **`verify_export.py`** checks each export against the PyTorch reference on 50 corpus items:
  - Whisper: the token sequences must match exactly under greedy decoding;
  - MT: chrF must be within 0.5;
  - fp16 tensors must be within tolerance.

  A failed check blocks the publish.
- **`build_manifest.py`** writes the `legendario-models/1` entries with bytes and sha256. It refuses any licence outside MIT, Apache-2.0, BSD and CC-BY-4.0, and sets `non_commercial` when a model is admitted with an override.
- **`publish_models.py`** uploads to the project's Hugging Face repository and pins the commit.

**Tests:** Python unittest for the manifest builder and the licence gate, run from
`run-tests.cmd`, as in the siblings.

### §LE21 Real-time pipeline scheduler with backpressure

Build `Legendario.Pipeline` as a graph of stages connected by bounded
`System.Threading.Channels`:

```
source → convert → VAD → ASR → segmenter → MT → caption model → overlay
```

Each stage runs on its own long-running task.

**Inference workers.** One inference worker runs per device placement, as resolved in LE18. When ASR and MT share a GPU, they share one worker with a priority queue:
- ASR jobs go ahead of MT jobs;
- a job whose audio is older than the deadline (default 6 s) is coalesced with the next, not run late.

**Shedding when behind**, measured as audio time minus processed time:
1. Lengthen the ASR window.
2. Skip draft translations.
3. Switch to the smaller ASR model, if installed.
4. Show source text only, with a "translation paused, PC busy" notice.

Shedding stops, in reverse order, once the stage has kept up for 30 s. Every step is
logged.

**Metrics.** `PipelineMetrics` records, per stage:
- queue depth;
- processing time;
- RTF;
- lag;
- the latency from the end of speech to visible text (p50 and p95, 60 s window).

The diagnostics view and the operation log read these numbers.

**Lifecycle.** Start, stop and pause, plus a source swap with no restart, all
cancellable. Sessions stay warm across a pause.

**Tests** use fake stages with scripted delays on a `FakeTimeProvider`:
- the deadlines;
- the ASR-over-MT priority;
- the order in which shedding steps start and stop;
- no unbounded growth of memory or queues under a 10× overload for 10 simulated minutes.

### §LE22 Hardware fit and measured real-time factor

Port Revelei's `HardwareFit` and `MeasuredSpeed`, and add a measurement aimed at
real-time use.

**Profile.** `HardwareProfile` collects:
- the GPU adapters and their dedicated VRAM;
- whether an NPU is present (Windows ML EP devices of type NPU);
- total RAM;
- the CPU core count;
- the power source: `GetSystemPowerStatus`, plus whether battery saver is on.

**Static verdict.** From each model's `memory` field, every manifest model is rated
fits, tight or does not fit, on each available placement.

**Measured verdict.** A 20-second "calibration" runs after the models are downloaded, on
a bundled clip. It measures the RTF and the commit latency for each candidate ASR and MT
model on its placement. Results are cached per driver version and per model hash.

A model counts as **real-time** if:
- its RTF is at most 0.5, which leaves headroom for the game or video being captioned;
- its p95 latency meets the LE4 target.

**Recommendation.** Pick the largest real-time model for each stage. On battery, prefer
the smaller tier, or the CPU and NPU placement LE6 measured as efficient.

**UI.** First use (Block H) and the model manager show the verdict as "Real-time", "Near
real-time" or "Too slow", with the measured numbers available in an expander.

**Tests:**
- verdict rules on synthetic profiles;
- calibration on a fake engine;
- the cache invalidated when the driver changes.

### §LE23 Warm start: engine cache and session prewarm

**Engine cache.** Port Revelei's `EngineCache` and `EngineWarmup`. The TensorRT-RTX EP
context models (`ep.context_enable`, `ep.context_file_path`, `ep.context_embed_mode`)
are kept under `%LOCALAPPDATA%\Legendario\engines`. The cache key combines the model
hash, the provider, the driver version and the runtime version.

**Build order.** Engines are built in the background:
- right after a model is downloaded;
- after a driver update has been detected.

Until an engine is ready, the stage runs on DirectML. Whisper's dynamic shapes, meaning
the decoder sequence length, need explicit optimisation profiles. LE2 reports whether
TensorRT-RTX is worth it for the decoder at all, or only for the encoder.

**Prewarm.** When the app starts with "start captions automatically" enabled, or when the tray menu opens:
1. create the ASR and MT sessions;
2. run one dummy inference on each. On DirectML the first run compiles shaders, which can take seconds.

The warm sessions are kept through `SessionCache` leases, and an idle release (default
10 min) frees VRAM when captions are off.

**Measure** in Bench (LE10), cold and warm, for each provider:
- the time from pressing Start to the first committed caption;
- the time to the first translated caption.

The target is under 3 s with a warm cache on GPU.

**Tests:**
- cache keys change when the driver or model changes;
- a corrupt context file is rebuilt;
- prewarm is cancelled cleanly when the user quits during the build.

## Block E — Streaming speech recognition

### §LE24 IRecognizer contract and the first ASR engine

**Contract.** `Legendario.Speech` defines:

```csharp
interface IRecognizer : IAsyncDisposable {
  RecognizerInfo Info { get; }          // engine, model, languages, placement
  ValueTask<Hypothesis> DecodeAsync(AudioWindow window,
      DecodeContext context, CancellationToken ct);
}
record Hypothesis(IReadOnlyList<Word> Words, string Language,
                  float LanguageProbability, float NoSpeechProbability,
                  float AvgLogProb, float CompressionRatio);
record Word(string Text, TimeSpan Start, TimeSpan End, float Probability);
```

`DecodeContext` carries the prompt (the committed text so far), the forced language or
auto, and the task. The engine is stateless per window, so the streaming commit policy
(the next task in this block) owns the state.

**Engine.** Implement the engine LE2 picked. For Whisper on Windows ML ORT that is:
1. a log-mel front end in C#: 80 or 128 bins, a 400-sample FFT with hop 160, using `System.Numerics.Tensors`, checked against the Python reference to 1e-4;
2. the encoder run once per window;
3. a greedy decoder over the merged decoder with the KV cache, which supplies timestamp tokens and suppressed tokens;
4. word timestamps from timestamp tokens, or from cross-attention DTW if LE2 found the tokens too coarse.

The Whisper BPE tokenizer comes from `Microsoft.ML.Tokenizers` (Tiktoken or the GPT-2
BPE) and is checked against the Python output.

**Sessions.** Encoder and decoder sessions come from `SessionCache` leases, on the
placement LE18 resolved. Device-lost errors are retried through `MemoryFailures`.

**Tests:**
- mel parity;
- tokenizer round-trip;
- greedy output on the committed 3 s fixture matches the reference transcript exactly on the CPU EP;
- corpus WER within 1 point of LE2's measurement (needs the models).

### §LE25 Streaming commit policy

Implement `StreamingTranscriber` in `Legendario.Speech`. It owns the audio buffer for
the current utterance and calls `IRecognizer` with the policy LE4 chose; the default is
LocalAgreement-2 inside VAD-bounded utterances.

**Loop.** Every `step`, about 1 s, after enough new speech has arrived:
1. decode the window from the last commit point to now, with the committed text as the prompt;
2. compare the new word sequence with the previous hypothesis;
3. commit the longest common prefix;
4. move the window start to the end time of the last committed word.

On `SpeechEnded` from the VAD, flush: decode once more and commit everything.

**Output events:**
- `Partial(text)`: the uncommitted tail, which may change;
- `Committed(words)`: final text with timestamps.

**Window bounds:**
- Never let the window exceed 25 s. Force a commit at the last word boundary before that, because Whisper's limit is 30 s.
- Never decode under 1 s of audio.

**Prompt.** Keep the prompt to the last 200 tokens, and reset it on a language change or
a gap longer than 10 s.

**Language.** Pass the locked language once language detection (filed in this block) has
locked it. Until then, use auto.

**Tests**, with a scripted fake recogniser:
- prefix agreement;
- forced commit at the window limit;
- flush on the end of speech;
- prompt trimming;
- no duplicated words across commits, with timestamps that increase monotonically.

A corpus test checks that latency and flicker match the LE4 numbers within 10%.

### §LE26 Hallucination and repetition guards

Add `HypothesisFilter`, applied to every hypothesis before the commit policy sees it.
Each rule is configurable and logged with the reason it dropped text, without the text
itself unless debug logging is on.

**Rules:**
- **No-speech gate.** Drop when `NoSpeechProbability > 0.6` and `AvgLogProb < -1.0`, using Whisper's reference thresholds.
- **Compression ratio.** Drop or retry when the gzip ratio of the text is above 2.4, which means the output is looping. On retry, decode once more with temperature 0.2 and keep the better result.
- **Repetition.** Collapse an n-gram that repeats more than three times in a row. Drop a committed sentence identical to one of the last three unless the audio shows the speaker repeated it.
- **Blocklist.** Phrases Whisper learned from YouTube end screens and subtitle credits: "Thanks for watching", "Subscribe", "Legendas pela comunidade Amara.org" and others. One list per language, held in a data file. A phrase is dropped only when it is the entire segment and the VAD says the segment is mostly non-speech.
- **Low confidence.** Words below a probability threshold are not dropped but styled, which the caption model (Block G) can show as dim.

**Tests:**
- a hand-built hypothesis for each rule;
- the corpus "hard" clips (LE10): on music-only and silence clips, no caption is committed;
- WER on clean speech does not get worse by more than 0.2 points, which shows the guards do not delete real speech.

### §LE27 Source language detection and lock

Implement `LanguageTracker` in `Legendario.Speech`.

**Modes**, chosen in settings:
- **Auto** is the default.
- **Fixed** is a language the user picks; detection is skipped and decoding is cheaper.
- **Auto, limited to** a set of 2–3 languages the user expects. This suits a bilingual meeting.

**Auto.** Run the model's language-ID pass, which for Whisper is the first decoder step over the language tokens, on each VAD utterance of at least 2 s. Accumulate the probabilities with an exponential decay.
- **Lock** once one language leads with a smoothed probability of at least 0.8 over at least 6 s of speech.
- **While unlocked**, decode with auto, but tag hypotheses as provisional, so translation waits up to 1.5 s for the lock.
- **Unlock and switch** only when another language leads for two consecutive utterances, each with probability ≥ 0.9. This handles a film that switches scene language, but does not flip on a single foreign word.

**When the language changes:**
- the prompt is reset (LE25);
- the translator route is re-resolved (Block F);
- the overlay shows a small language badge, for example "EN → PT".

**Same language.** When the detected language equals the target language, translation is
skipped and the source text is shown as is.

**Tests:**
- smoothing and lock on a synthetic probability sequence;
- the switch rule;
- the limited set ignores out-of-set languages;
- a corpus test on concatenated clips in pt, en and es measures how long switching takes.

### §LE28 Sentence segmenter between ASR and MT

Implement `SentenceSegmenter` in `Legendario.Pipeline`. It consumes `Committed(words)`
events and emits `SourceSentence` records, each carrying the text, the start and end
times, the language and an id.

**Boundary rules**, in order:
1. Terminal punctuation from the ASR, `. ? ! … 。？！`, ends a sentence. Abbreviations ("Dr.", "Sr.", "e.g.") and decimal numbers are handled through a per-language exception list.
2. A pause in the audio longer than 700 ms, measured between word timestamps, ends a sentence, even without punctuation.
3. A sentence that reaches the maximum length, about 25 words or 6 s, is cut at the best clause boundary: a comma, or a conjunction from a per-language list ("and", "mas", "pero"). The cut is marked as a continuation, so MT can carry the context over.
4. A timeout: when no word has been committed for the flush interval, about 1.2 s, the pending words are emitted.

**Updates.** The segmenter also exposes `PendingText`, the committed words not yet in a
sentence. The draft-translation option (Block F) uses it.

Sentence ids are stable, so the overlay can replace a draft in place.

**Tests:**
- the rules on fixed word streams with timestamps;
- the abbreviation exceptions in pt, en and es;
- the maximum-length cut lands on a clause boundary;
- the timeout flush, on a fake clock;
- a corpus test reports the share of emitted sentences that match a reference sentence boundary.

### §LE29 Second ASR tier for low-end hardware

This is an idea, to be promoted only if LE22's calibration shows that a meaningful share
of target machines rate the default ASR "Too slow" for every model size.

Candidates, depending on what LE2 and LE7 found:
- **A native streaming model on the CPU.** The sherpa-onnx C# API with a streaming zipformer or transducer is truly incremental, with no window re-decoding, so its CPU cost is a fraction of Whisper's. Its limit is language coverage, so it is offered only for the languages it supports.
- **Parakeet-TDT-0.6B-v3 int8 on the CPU**, for its 25 European languages. It is an offline model, but fast enough to run on VAD utterances.
- **Windows Live Captions through UI Automation (LE7).** It needs no model download, and the OS pays the inference cost on Copilot+ NPUs.

Each candidate is wrapped as an `IRecognizer`, plus an `IStreamingRecognizer` extension
for engines that emit their own partials. When an engine streams natively, the
LocalAgreement step (LE25) is bypassed.

The hardware fit (LE22) then offers the tier automatically on slow machines. The engine
in use is shown in the UI chip.

If neither candidate beats Whisper-base int8 on the CPU in the bench (LE10) on both
latency and WER for pt and en, retire this idea and record the numbers here.

## Block F — Real-time translation

### §LE30 ITranslator contract and the first MT engine

**Contract.** `Legendario.Translation` defines:

```csharp
interface ITranslator : IAsyncDisposable {
  TranslatorInfo Info { get; }   // engine, model, pairs, placement
  ValueTask<Translation> TranslateAsync(TranslationRequest request,
      CancellationToken ct);
}
record TranslationRequest(string Text, string From, string To,
    IReadOnlyList<string> Context, bool Draft);
record Translation(string Text, float Confidence, TimeSpan Elapsed);
```

**Engine.** Implement the engine LE3 picked. For Marian/Opus-MT on Windows ML ORT that is:
- SentencePiece encode and decode through `Microsoft.ML.Tokenizers`, with the source and target vocabularies as exported;
- an encoder run;
- a greedy decoder over the merged decoder with past key values, capped at 1.5× the source token count plus 10;
- beam 2 behind a setting, if LE3 showed a quality gain worth its latency;
- normalisation of the output: whitespace, punctuation spacing by target language, and capitalisation carried over from the source.

**Context.** Context sentences are passed only to engines that support them, such as an
LLM tier. Marian ignores them.

**Batching.** When several sentences are queued at once, which happens after a burst,
translate them in one padded batch. Never wait to fill a batch.

**Caching.** An LRU cache of 512 entries, keyed on (from, to, normalised text), skips
repeated lines such as catchphrases, chants and song choruses.

**Tests:**
- tokenizer parity with Python on 1,000 sentences;
- greedy output equals the Python reference on 50 FLORES sentences (CPU EP);
- the cache and batching behave correctly;
- corpus chrF is within 0.5 of LE3's measurement (needs the models).

### §LE31 Pair routing, pivot through English and on-demand pairs

Implement `TranslationRouter` in `Legendario.Translation`, which resolves a `(from, to)`
pair to a route.

**Route kinds:**
- **Direct**: an installed model for the pair.
- **Pivot**: from→en, then en→to, as two translator calls in sequence. The latency is shown as it is, and the pivot is shown in the language badge, e.g. "JA → EN → PT".
- **Multilingual**: when the installed MT model covers both languages, as MADLAD or an LLM tier does.
- **Missing**: no route exists, and the UI offers to download what is needed. The size is shown, and the user gives one-click consent.

**Ranking.** Prefer a direct route, then a multilingual one, then a pivot. Between equal
candidates, prefer the model the hardware fit (LE22) rates real-time.

**Re-resolution.** The router runs again whenever the tracker (LE27) locks or switches
language, so a film that changes language mid-way keeps being translated. While a
missing pair downloads, captions continue in the source language with a notice, and
switch over as soon as the model has loaded.

**Pair packs.** The first-use flow (Block H) installs the route for the user's chosen
target, plus "common sources", which by default are en, es, fr, de, it, ja, zh and ko,
all into the target. The full size is shown before download.

**Tests:**
- route resolution over a fake library: direct, pivot, multilingual and missing;
- re-resolution when the language changes;
- the captions keep flowing while a download runs, using a fake downloader;
- the badge text.

### §LE32 Draft translation of the stable prefix

This adds the draft policy that LE4 evaluated. It is enabled by default only if LE4
measured a net gain.

**Behaviour.** While a sentence is open, the segmenter (LE28) exposes its committed words.
- When that text has grown by 4 or more words and 1 s has passed since the last draft, send a low-priority `Draft` request. The scheduler (LE21) drops it if a final sentence is queued.
- The overlay shows it in the draft style (Block G).
- When the sentence commits, the final translation replaces the draft in place, by sentence id.

**Reducing flicker:**
- Keep the longest common prefix between the previous draft and the new one, and replace only the rest.
- Never show a draft shorter than the one already on screen, unless the final translation has arrived.
- Stop drafting a sentence after 3 drafts.

**Shedding.** Drafts are the first work the scheduler drops under load (LE21, step 2).

**Measure** in Bench, against the commit-only policy:
- the time from speech to the first translated words;
- flicker (words rewritten per sentence);
- the cost in GPU time.

A setting offers "Show translation as you speak" with three values: on, off, and auto
(decided by the hardware fit).

**Tests:**
- the drafting cadence on a fake clock;
- that a draft is replaced by its final translation;
- that drafts are dropped under load;
- the prefix-retention rendering rule.

### §LE33 LLM quality tier with conversational context

This is an idea, promoted only if LE3 shows that an LLM tier (HY-MT, Qwen or Gemma,
int4) gives at least +3 chrF or a clear COMET gain on conversational clips while keeping
p95 latency per sentence under 800 ms on an 8 GB GPU, with ASR running at the same time.

**Engine.** `LlmTranslator : ITranslator` runs on onnxruntime-genai with Windows ML, or
out of process on llama.cpp Vulkan through the generation-host pattern Revelei uses
(`Generation.Host`, a line protocol, crash isolation). The choice follows what LE3
found.

**Prompt.** A fixed template:
- the source and target languages;
- the last two source sentences, and their translations, as context;
- an instruction to output only the translation.

Use stop sequences and cap the output at 2× the source tokens.

**Guards.**
- The output is checked for extra commentary and for language mismatch (with a small fastText-style language identifier, or a script check). A failure falls back to the Marian engine for that sentence.
- The KV cache is reused across turns where the runtime supports it.

**Licence.** The licence of each model is checked against the manifest gate (LE20).
Gemma's terms and Tencent's licence need review before either is shipped.

**Placement.** The LLM never runs on the CPU. On machines where the hardware fit says
ASR and the LLM do not fit together, the tier is hidden.

### §LE34 User glossary for ASR and MT

This is an idea for after the core works.

**Glossary.** The user keeps a list of entries, `source term → target term`, each with
an optional "do not translate" flag. Lists are kept per profile, for example "Work" and
"Anime".

**ASR side.** Glossary source terms are added to the decoder prompt (the Whisper initial
prompt), which biases the spelling of names. Keep the prompt within its token budget
(LE25).

**MT side.**
- **Marian:** replace matched source terms with placeholder tokens the model passes through unchanged (`⟦1⟧`; LE3 must verify that the tokenizer keeps them intact), then substitute the target terms after translation. Do-not-translate entries map a term to itself.
- **LLM tier:** add the glossary to the prompt as a short term list.

**Matching.** Case-insensitive, on word boundaries, with simple inflection tolerance for
pt and es: plural `-s` and `-es`.

**UI.** A small table editor in settings, with CSV import and export.

**Tests:**
- placeholder round-trip through the real tokenizer;
- matching edge cases;
- the prompt budget is respected.

## Block G — Caption overlay

### §LE35 Caption model and floating caption window

**Caption model.** `Legendario.Pipeline` gets a `CaptionModel`. It consumes partial, committed, draft and final-translation events and keeps a list of display lines:
- each line holds source and translated text, a state for each (partial, draft or final), the source id and timestamps;
- wrapping uses a caller-provided measurer, so tests run without WPF;
- a line rolls off after `N` lines (2 by default), or 8 s after going final with nothing new below it;
- a final line is never rewritten;
- immutable snapshots are published, at most 15 per second.

**Window.** `Legendario.Overlay` implements the floating window with the technique LE5 chose. It has:
- a rounded Fluent card with a translucent background, 70% opaque by default;
- the translated line large, with the source line smaller and dimmer above it (toggleable);
- partial and draft text in italics at reduced opacity;
- dragging and resizing, with position and size remembered per monitor;
- a hover toolbar with pause, settings, the language badge and click-through;
- click-through mode, which ignores the mouse until the global hotkey or the tray menu turns it off;
- a 120 ms fade on line changes, off when Windows animation effects are off.

**Tests:**
- caption model: roll-off, final immutability, throttling, and draft replacement by id;
- a Winwright UI case that runs a `--replay` clip and asserts the overlay text sequence through UI Automation.

### §LE36 Docked mode, multi-monitor and fullscreen behaviour

**Modes.** Add `OverlayMode`, with three values:
- **Floating** (from LE35).
- **Docked top** and **Docked bottom**: a full-width bar on the chosen monitor, registered as an AppBar with `SHAppBarMessage` (`ABM_NEW`, `ABM_QUERYPOS`, `ABM_SETPOS`), so maximised windows resize around it. Handle `ABN_POSCHANGED` and `ABN_FULLSCREENAPP`.
- **Docked overlay**: the same bar, without reserving space.

**Fullscreen.** When a fullscreen app takes over the bar's monitor
(`ABN_FULLSCREENAPP`), the docked bar becomes a floating overlay at the same edge, so
captions remain visible. It returns to docked when the fullscreen app exits.
Exclusive-fullscreen games may still hide it; the limit LE5 measured is documented.

**Monitors.**
- The user chooses the monitor, or "follow the active window's monitor".
- Moving between monitors re-registers the AppBar with that monitor's DPI.
- When a monitor is unplugged (`WM_DISPLAYCHANGE`), the bar moves to the primary monitor.

**Screen sharing.** A setting, off by default, hides the captions from screen capture
with `WDA_EXCLUDEFROMCAPTURE`, so the presenter sees them and the audience does not.

**Tests:**
- the mode state machine with fake AppBar messages;
- the fullscreen transition;
- the monitor-removal fallback;
- a Winwright UI case that docks at the bottom, maximises a window and asserts that its bottom edge sits at or above the bar's top.

### §LE37 Caption styles and Windows caption settings

**Default style.** By default, read the user's style from Settings › Accessibility ›
Captions through the WinRT `Windows.Media.ClosedCaptioning.ClosedCaptionProperties` API.
It provides the font colour, size, style and opacity, the background colour and opacity,
the region colour and the edge effect. Subscribe to its change notifications so edits in
Windows Settings apply immediately.

**App styles.** Settings offers a few presets ("Windows", "Cinema", "High legibility", "Compact") and a "Custom" editor with:
- font family, size and weight;
- text and background colour, and opacity;
- edge effect: none, outline, drop shadow or raised;
- number of lines, 1 to 4;
- source line: shown, hidden, or shown only when the language differs;
- maximum width as a percentage of the monitor.

A live preview in the settings window shows sample lines in both languages.

**Contrast.** The overlay switches to the system colours when high contrast is on
(`SystemParameters.HighContrast`). Text and background are checked to have at least a
4.5:1 ratio, and a custom style below that ratio shows a warning without being blocked.

**Scripts.** Font fallback for CJK, Arabic and Hebrew is checked, with right-to-left
alignment for Arabic and Hebrew targets.

**Tests:**
- mapping from `ClosedCaptionProperties` to the style record, with a fake provider;
- the contrast calculation;
- preset round-trip through settings;
- a Winwright UI case that changes the font size and asserts the overlay text element's font size.

### §LE38 Accessibility: UIA live region and global hotkeys

**UI Automation.** The overlay's text element exposes a UIA live region
(`AutomationProperties.LiveSetting = Polite`) and raises `LiveRegionChanged` only for
final translated lines. Partials and drafts are not announced, because announcing them
would make screen readers stutter. A setting chooses whether to announce the source
line, the translated line, or both.

All overlay controls have automation names from the `.resx` strings and are reachable by
keyboard when click-through is off. That is also what Winwright drives.

**Global hotkeys.** Register them with `RegisterHotKey`, user-configurable, and avoid Win+Ctrl+L, which is Live Captions' own hotkey. Defaults:
- Ctrl+Alt+L: show or hide the captions;
- Ctrl+Alt+P: pause or resume;
- Ctrl+Alt+T: toggle click-through;
- Ctrl+Alt+O: toggle the source line.

A registration conflict shows a notice and offers to choose another combination.

**Visual accessibility:**
- caption size follows the Windows text-scaling setting;
- the "Reduce motion" setting turns off the fades;
- focus visuals are on in the settings window.

**Tests:**
- live-region events are raised only for final lines, using a UIA event listener in a desktop test;
- hotkey conflict handling with a fake registrar;
- a Winwright case presses each hotkey and asserts the resulting state.

### §LE39 Transcript history and SRT/VTT export

**History panel.** A panel opened from the overlay toolbar or the tray shows the current session's final lines:
- a timestamp for each line, with source and translation side by side or stacked;
- search, copy of a line or of all lines, and auto-scroll that pauses while the user scrolls up.

**Privacy rules:**
- Session history lives in memory only and is discarded when captions stop, unless "Keep transcripts" is turned on. It is off by default.
- When it is on, transcripts are saved per session under `%LOCALAPPDATA%\Legendario\transcripts\` as JSONL, with a retention setting (7 days by default) and a "Delete all" button.
- Nothing is ever uploaded.

**Export** produces SRT, WebVTT, TXT or a bilingual TXT. Timing comes from the word
timestamps, translated to the session clock. Long lines are split at 42 characters per
line, two lines per cue, and each cue lasts between 1 and 7 s. Translated cues use the
timing of their source sentence.

**Tests:**
- SRT and VTT formatting, including escaping and hour rollover;
- the cue-splitting rules;
- retention cleanup on a fake clock;
- no file is written while "Keep transcripts" is off. A test watches the folder during a replayed session.

## Block H — App shell, settings and first use

### §LE40 Tray-first shell and control window

**Tray icon.** The icon reflects the state: idle, listening, translating, paused or error. Clicking it shows or hides the captions. Its menu offers:
- Start or Stop, and Pause;
- the source: System, an app, or Microphone;
- the target language: the recent ones plus "More…";
- overlay mode, click-through, history, settings, About, and Quit.

**Control window.** A WPF window using the Fluent `ThemeMode` with Mica, following Revelei's `Appearance` for the System, Light and Dark themes. Its sections:
- **Captions**: a large Start/Stop button, the source picker with app icons (LE15) and the level meter (LE17), and the source-language mode next to the target language;
- **Status**: one chip per stage, from LE18, with the hardware-fit verdict and live latency (p50) from LE21;
- **Models**: the installed models, with downloads, removals and the per-model licence;
- **Settings**: the overlay style (LE37), hotkeys (LE38), transcripts (LE39), the provider override, and "Start with Windows" through a `HKCU\…\Run` entry. For MSIX, use a `StartupTask` instead.

**Single instance.** A named mutex, with the second launch forwarding its arguments
through a named pipe, as Revelei's `CommandLine` does.

**Closing.** Closing the window keeps captions running. Quit stops the pipeline cleanly
and releases every session.

**Tests:**
- view-model tests for the state transitions;
- the single-instance forward;
- the run-key round-trip in a test registry hive;
- Winwright cases: start a replay, change the target language mid-session, stop, and assert the overlay text and the tray state.

### §LE41 First-use wizard with consent and calibration

Port the consent panel pattern from Revelei's `MainWindow.FirstUse.cs` and
`DownloadText`, and extend it into a short wizard:

1. **Language.** "I want captions in…": the target, defaulting to the Windows display language. Then "I usually listen to…": common source languages, with an Auto option.
2. **Hardware.** Show the hardware profile (LE22): GPU, VRAM and NPU, plus the recommended tier ("Fast", "Balanced" or "Light") and its trade-off.
3. **Consent.** A plan lists every file to download: the model, the variant this PC will fetch, its size, its source (a pinned Hugging Face commit), its licence and attribution, with the total size and the free disk space. Nothing downloads before explicit consent. Downloads pause, resume, and survive restarts (the `DownloadQueue` from LE19).
4. **Windows ML providers.** If they are being acquired (LE18), the wizard says so, without blocking step 5.
5. **Calibration.** About 20 s (LE22). If the chosen tier is not real-time, offer the next tier down.
6. **Try it.** Play a bundled bilingual sample through the pipeline, showing the overlay live, then "Done — press Ctrl+Alt+L anytime".

**Exit points.** The wizard can be skipped and reopened from Settings. An
offline-install path accepts a folder of pre-downloaded models through `--models`.

**Tests:**
- the plan computation for each hardware profile;
- consent gating: no network call before consent, checked with a fake HTTP handler;
- resuming after a restart;
- a Winwright case that walks the wizard against the loopback fixture server.

### §LE42 Localisation: pt-BR and en

**Resources.** All UI text lives in `Properties/Strings.resx` (en, neutral) and
`Strings.pt-BR.resx`, with the generated `Strings` class, as in Modelado. The UI culture
follows Windows, and a setting can override it.

**Gate.** Port `StringResourceTests` from Modelado's architecture tests. A string
literal assigned to UI text in code fails the build, and every key in the neutral file
must exist in pt-BR.

**Language names** are shown in the UI language through
`CultureInfo.GetCultureInfo(code).DisplayName` under that culture, never hard-coded,
with an explicit table only where .NET's names are poor.

**Error and notice text** follows the siblings' ai-writing rules. It says what happened
and what the user can do, for example: "A GPU travou e as legendas passaram para a CPU.
Reinicie o app para tentar a GPU de novo."

**Overlay hints** from LE17 and the shedding notices from LE21 are localised too.

**Documentation.** `README.md` is in en; a `README.pt-BR.md` mirrors the user-facing
sections.

**Tests:**
- the resource gate;
- key parity between en and pt-BR;
- pseudo-localisation, a test culture that expands each string by 40%, run in a Winwright case to catch clipped text in the control window and the overlay toolbar.

### §LE43 Diagnostics view and support bundle

**Diagnostics page.** A "Diagnostics" page in the control window shows live values,
refreshed once per second.

Per stage (capture, VAD, ASR, segmenter, MT, overlay):
- queue depth;
- processing time and RTF;
- lag behind the live audio;
- p50 and p95 latency from end of speech to visible text, as a small sparkline over the last 5 minutes.

For the session:
- the providers and adapters in use;
- VRAM in use and the budget (from `MemoryBudget`);
- any shedding step that is active (LE21);
- the last 20 events from the operation log: fallbacks, reconnects, model loads.

**Support bundle.** "Copy diagnostics" puts a text summary on the clipboard. "Save support bundle" writes a zip containing:
- settings, with paths redacted;
- the operation logs;
- the hardware profile;
- the model manifest status;
- driver versions.

Captured audio and caption text are never included unless the user ticks "include last
30 s of audio for this report".

**Overlay hint.** When p95 lag stays above twice the target for 30 s, the overlay shows
a non-intrusive hint linking here, for example "Captions are running behind — see why".

**Tests:**
- the metrics formatting;
- the bundle contents, with redaction and no caption text by default;
- the hint threshold on a fake clock.

### §LE44 Power-aware operation and idle release

**Power state.** Subscribe to `PowerManager` events (`PowerSourceStatusChanged`, `EnergySaverStatusChanged`) through `Windows.System.Power`. With the "Balance on battery" setting on, which is the default:
- on battery: switch to the hardware fit's battery recommendation (LE22), meaning a smaller ASR tier and the efficient placement LE6 measured, and turn drafts off (LE32);
- with energy saver on: also lengthen the ASR step to 1.5 s and mark the pipeline threads as EcoQoS with `SetThreadInformation(ThreadPowerThrottling)`, if LE6 showed that this keeps the latency target.

Switches happen between utterances, never in the middle of one. The status chip shows a
leaf icon while a power profile is in effect.

**Idle release:**
- After 2 minutes of no speech, the pipeline keeps capture and VAD running but releases the ASR and MT GPU sessions through the `SessionCache` idle policy. A VAD `SpeechStarted` triggers a warm reload; LE23's prewarm keeps that under the cold-start target.
- When captions are stopped, all sessions are released after the idle timeout (10 minutes by default), or immediately if the user chooses "Free memory now".

**Measure** in Bench, on one laptop, for one hour of mixed content:
- battery drain with the profile on and off;
- latency on battery.

**Tests:**
- power event handling with a fake power manager;
- a profile switch at an utterance boundary only;
- the idle release and reload sequence on a fake clock.

## Block I — Delivery: installers, docs and benchmarks

### §LE45 Inno Setup installer, self-updater and release workflow

Port the following from Modelado and Revelei, adapted to Legendario:
`build/installer.iss`, `build/build-installer.cmd`, `ReleaseCheck` and `ReleaseUpdate`.

**Installer:**
- per user, framework-dependent on .NET 10 (the installer checks for the Desktop Runtime and offers it), x64;
- installs to `%LOCALAPPDATA%\Programs\Legendario`;
- `onnxruntime.dll` from the Windows ML package sits beside the exe. This is Revelei's System32 lesson, and an architecture test checks the published output for it.
- creates a Start menu entry, and offers "Start with Windows";
- the uninstaller asks whether to keep the downloaded models and transcripts, and keeps them by default.

**Models are not bundled.** They are downloaded on first use (LE41), which keeps the
installer under 100 MB. Silero VAD (2 MB) is the exception and is bundled.

**Updater.** It checks GitHub Releases at most once a day, shows the release notes, and
downloads and verifies the installer against `SHA256SUMS`. It is turned off when running
packaged (`PackageIdentity`).

**Release workflow.** `.github/workflows/release.yml` runs on `windows-latest` for a `v*` tag:
1. run the test gate;
2. build the installer;
3. check that the tag matches `<Version>`;
4. publish the installer and `SHA256SUMS`.

**Tests:**
- the version-consistency architecture test;
- the update check against a fixture feed;
- installer smoke install and uninstall on the VM (`run-tests-vm.cmd`, from Modelado).

### §LE46 MSIX package and Store readiness

Port `build/build-msix.ps1` and `build/msix/AppxManifest.xml` from Revelei: full trust,
`runFullTrust`, unsigned so the Store signs it, and `-Register` for local installs.

**Capabilities.** Add `microphone`. System loopback and process loopback need no
capability, but this must be verified under package identity. Also declare a
`StartupTask` for "Start with Windows".

**Behaviour to verify under the package**, on the VM:
- `PackagedPaths`: `%LOCALAPPDATA%` is redirected to `LocalCache`, so the models and logs folders must open in the right place;
- Windows ML EP registration (`RegisterCertifiedAsync`) works inside the package. This is the open question Revelei left in its PB90 notes;
- process loopback activation works from an AppContainer-less full-trust package;
- global hotkeys and the AppBar work;
- the self-updater is off.

**Store policy:**
- Models are data, not code, so downloading them is allowed. Revelei's PB92 forbids downloading native DLLs after certification. Legendario must therefore ship every native runtime inside the package, and the Windows ML providers come from Windows Update, not from the app. Confirm this reading before submission.
- Write the privacy policy page (I-block docs): all processing is local, no audio leaves the PC, and model downloads are the only network traffic besides update checks.

**Done when** a locally registered MSIX passes a Winwright smoke case: start, caption a
replay, stop.

### §LE47 README, privacy, notices and benchmark table

Write `README.md` (en) and `README.pt-BR.md`, and run the ai-writing pass over both.

**README contents:**
- what the app does, with a screenshot and GIF of the overlay over a video;
- how it differs from Windows Live Captions: any source and target pair, per-app capture, docked or floating, history and export;
- the hardware tiers;
- installation;
- first use;
- hotkeys;
- troubleshooting, with the LE17 states and the diagnostics bundle (LE43).

**Benchmark table**, generated by `Legendario.Bench` (LE10) into `docs/BENCHMARKS.md` from a committed JSON result. Never typed by hand. It covers, per hardware tier (RTX GPU, Intel iGPU, CPU only, NPU if supported):
- the latency from speech to source text, and to translated text, at p50 and p95;
- WER for pt, en and es;
- chrF for en→pt, es→pt and ja→pt;
- RTF.

**`docs/PRIVACY.md`**, linked from About and from the Store listing:
- what is processed: audio, in memory only;
- what is stored: settings, logs without caption text, and transcripts only when opted in;
- what goes over the network: model downloads from pinned Hugging Face URLs, the update check, and Windows ML providers fetched through Windows Update by the OS.

**`NOTICE.md`**, generated from the manifest, lists every model with its licence,
authors and the attribution text CC-BY requires, plus the third-party .NET packages. The
About page renders the same data.

**Also:** `docs/COMMAND-LINE.md`, kept in sync with LE9's parser by a test that compares
the documented flags with the parser's.

### §LE48 End-to-end Winwright cases and VM run

Add `tests/Legendario.Cases` and `cases/*.cases.json` with `winwright.json`, mirroring
Modelado's and Revelei's setup, and `run-tests-vm.cmd`, which runs them in the VMware
guest. UI cases need real windows and the foreground.

**Cases**, each launching the app with `--replay` on corpus clips and `--models` pointing at a fixture models folder:
1. Start captions on an en clip with target pt; assert that the overlay shows, in order, source partials and then final pt lines. Text is matched by similarity, not exact equality.
2. Switch the target language mid-session; assert that the badge and the next line change.
3. Docked bottom: maximise a window and assert that it does not overlap the bar (LE36).
4. Click-through hotkey: clicks pass through the overlay to the window beneath.
5. Pause and resume through the tray menu.
6. Run the first-use wizard against the loopback fixture server (LE41).
7. Export SRT from history; assert that the file exists and contains the expected number of cues.

**Live audio check.** In the VM, play a WAV through the default endpoint with a helper
process, and caption it through loopback (LE12), not through replay. This proves the
real capture path end to end.

**Friction.** Gaps found in Winwright while writing these cases go to its roadmap
through the `winwright-friction` skill, not into workarounds here.

**Release gate.** The release workflow (LE45) requires a recorded pass of
`run-tests-vm.cmd` for the tagged commit, enforced as in the siblings.
