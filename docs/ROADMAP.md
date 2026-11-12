# Roadmap (active backlog)

## Block A — Feasibility spikes

- ⏳ **LE3** (deps: —) **Spike: no MT model chosen; Opus-MT, MADLAD, HY-MT and small LLMs untested for speed, quality and licence** — Direct ja-pt and es-pt models, MADLAD-400, a Qwen3 LLM tier, beam, context and tokenizer parity are still unmeasured. → §LE3
- 📋 **LE4** (deps: LE2 ✅, LE3 ⏳) **Spike: no streaming policy; chunking, commit and re-translation trade-offs between latency and flicker unknown** — Whisper is not a streaming model, and the commit policy alone decides how long a caption takes to appear and how often it rewrites itself. → §LE4
- 📋 **LE5** (deps: —) **Spike: unknown whether a WPF Fluent window can float click-through and topmost over fullscreen video and games** — The overlay is the product's whole surface, and WPF's AllowsTransparency path is known to cost GPU and break Mica, so the window technique must be proven first. → §LE5
- 📋 **LE6** (deps: LE2 ✅, LE3 ⏳) **Spike: unknown how ASR and MT share one GPU with each other and with the game or video being captioned** — Captions are watched over GPU-heavy content, so contention can blow the latency budget or drop the user's frame rate, and that decides device placement per stage. → §LE6
- 💭 **LE7** (deps: —) **Spike: Windows Live Captions text is unused as a zero-model ASR source for low-end PCs** — Reading Live Captions through UI Automation, as LiveCaptions-Translator does, would give PCs without a capable GPU captions with no ASR model to download. → §LE7
- 📋 **LE50** (deps: LE2 ✅) **Spike: ASR untested with a static KV cache, TensorRT-RTX, streaming zipformer, real captured clips or AMD GPUs** — LE2's defaults rest on one RTX machine and read speech, and a static-cache export could make Revelei's provider stack fast enough for ASR. → §LE50

## Block B — Project foundation

- 📋 **LE8** (deps: —) **No solution exists: no projects, build props, analyzers, test runner or run-tests.cmd gate** — Every later task needs a home, and copying Revelei's layout and gates keeps the three local-AI apps maintainable with the same habits and tooling. → §LE8
- 📋 **LE9** (deps: LE8) **No settings store, operation log, packaged-path mapping or command line to configure and diagnose a run** — A real-time app is debugged from its logs, and Revelei's AppSettings, OperationLog and PackageIdentity already solve this, so porting them beats reinventing. → §LE9
- 📋 **LE10** (deps: LE8) **No licensed audio corpus, replay source or bench to measure captions deterministically** — Latency and accuracy regressions are invisible without a fixed corpus replayed in simulated real time, and the spikes and every later task need the same yardstick. → §LE10
- 📋 **LE11** (deps: LE8) **No CI runs the build and unit tests on push; only the roadkeep lint workflow exists** — Revelei has no .NET CI and Modelado compiles WPF on Linux only, so a windows-latest job is the cheapest guard for audio and interop code that cannot build elsewhere. → §LE11

## Block C — Audio capture

- 📋 **LE12** (deps: LE1 ✅, LE10) **The app cannot hear what the PC is playing: no system loopback capture source** — Captioning whatever plays on the PC is the Live Captions behaviour users expect, and every other source builds on the same IAudioSource contract and frame clock. → §LE12
- 📋 **LE13** (deps: LE1 ✅, LE10) **Captured audio arrives at 44.1/48 kHz stereo while every ASR and VAD model expects 16 kHz mono** — A poor resampler aliases and costs WER, and a slow one adds latency on every frame, so conversion needs its own tested, allocation-free stage. → §LE13
- 📋 **LE14** (deps: LE13) **No voice activity detection: silence and music reach the ASR, wasting GPU and inviting hallucinated captions** — Whisper invents text on silence and music, and segment boundaries drive both the commit policy and the GPU duty cycle, so VAD gates everything downstream. → §LE14
- 📋 **LE15** (deps: LE12) **Cannot caption one app only; system loopback mixes in game music, notifications and other apps** — Per-app capture is a feature Live Captions lacks and cleans the ASR input, which matters most for meetings and for games with music under the dialogue. → §LE15
- 📋 **LE16** (deps: LE12) **No microphone source, so in-person speech and the user's own voice in a call cannot be captioned** — Live Captions offers microphone captions, and a mic+system "meeting" mode lets both sides of a call be captioned and translated with a speaker label. → §LE16
- 📋 **LE17** (deps: LE14) **When no captions appear, nothing tells the user whether audio is silent, muted, protected or not speech** — Silent failure is the top support question for caption tools, and a level meter with named causes turns "it doesn't work" into a fix the user can apply. → §LE17

## Block D — Local inference runtime and models

- 📋 **LE18** (deps: LE8, LE2 ✅) **No execution-provider stack: nothing picks the GPU, probes Windows ML or DirectML, or falls back to CPU** — Revelei already measured and solved provider choice on DirectX, including its 887A0004 and System32 traps, so porting it gives GPU inference without relearning them. → §LE18
- 📋 **LE19** (deps: LE18, LE3 ⏳) **No model manifest, downloader or library: ASR, MT and VAD models cannot be fetched, verified or found** — Speech and MT models are multi-file encoder-decoder sets with tokenizers and language pairs, which Modelado's one-file-per-model manifest cannot describe (its MOD124). → §LE19
- 📋 **LE20** (deps: LE19) **No reproducible pipeline exports, quantises, verifies and publishes the chosen ASR and MT models as ONNX** — Pinned, hash-checked artefacts with licence checks make the manifest trustworthy, and hand-exported files cannot be rebuilt when an upstream model updates. → §LE20
- 📋 **LE21** (deps: LE4, LE6, LE18) **No real-time scheduler: a batch job queue would let ASR and MT fall behind live audio without bound** — Revelei's single-job channel suits batch work, but captions have deadlines, so stages need bounded queues, priorities and a policy for shedding work when behind. → §LE21
- 📋 **LE22** (deps: LE19, LE21) **No hardware fit: users cannot tell which ASR and MT model sizes their PC can run in real time** — A model that cannot keep up live is worse than a smaller one that can, so the first-use flow must recommend sizes from measured speed, not only from VRAM. → §LE22
- 📋 **LE23** (deps: LE18) **First caption after start waits for session creation and, on TensorRT-RTX, a 10-17 s engine build** — Users start captions when speech is already playing, so cold-start time is lost content; Revelei's EngineCache and warmup cut builds to about 2 s. → §LE23

## Block E — Streaming speech recognition

- 📋 **LE24** (deps: LE2 ✅, LE14, LE19, LE21) **No recogniser: speech segments cannot become text, and no engine contract hides the runtime LE2 picks** — The ASR engine may change with hardware tier or model, so the pipeline must see one IRecognizer contract with partial and final results and timestamps. → §LE24
- 📋 **LE25** (deps: LE24, LE4) **No streaming commit policy: windows are decoded but nothing decides which words are stable or final** — Committing too early shows wrong words and committing too late shows nothing, so the LE4 policy must become a tested component that owns streaming state. → §LE25
- 📋 **LE26** (deps: LE24) **Whisper hallucinates on silence, music and noise ("Thanks for watching", repeated lines) and nothing filters it** — One invented subtitle over a quiet scene destroys trust faster than a missed line, and Whisper's known failure modes have cheap, measurable guards. → §LE26
- 📋 **LE27** (deps: LE25) **Source language is neither detected reliably nor locked, so short clips flip language and translate wrongly** — Per-window detection on 2 s of audio is noisy, and a wrong language makes Whisper transliterate and picks the wrong MT pair, which breaks the entire caption. → §LE27
- 📋 **LE28** (deps: LE25) **Committed words are not grouped into sentences, so MT would receive fragments or wait too long** — MT quality collapses on half-sentences while waiting for a full stop can stall on run-on speech, so a segmenter must balance completeness against a latency budget. → §LE28
- 💭 **LE29** (deps: LE24, LE7) **PCs with no usable GPU have only one slow ASR path; no lighter engine tier or system-captions source** — Many laptops cannot run Whisper live, and a second IRecognizer, such as sherpa-onnx streaming or the LE7 Live Captions source, would cover them through the same contract. → §LE29

## Block F — Real-time translation

- 📋 **LE30** (deps: LE3 ⏳, LE19, LE21, LE28) **No translator: committed sentences cannot be translated, and no contract hides the MT engine LE3 picks** — MT engines differ by pair, size and runtime, so the pipeline needs one ITranslator contract, with the first engine running on the placement LE6 chose. → §LE30
- 📋 **LE31** (deps: LE30, LE27) **No pair routing: a source language with no direct model to the target has no translation path** — Opus-MT ships one model per direction and many pairs into Portuguese are missing, so an English pivot and on-demand pair download are what make auto-detect usable. → §LE31
- 📋 **LE32** (deps: LE30, LE4) **Translation appears only after a sentence commits, so long sentences leave the target line blank for seconds** — Viewers need words early, and translating the stable prefix as a replaceable draft can halve perceived delay if flicker stays under control. → §LE32
- 💭 **LE33** (deps: LE30, LE3 ⏳) **Sentence-by-sentence MT loses context: pronouns, gender and register drift across lines** — A small LLM fed the previous lines translates conversation more naturally, and LE3 measures whether a GPU can afford it as an optional quality tier. → §LE33
- 💭 **LE34** (deps: LE30, LE24) **Names, jargon and product terms are mistranslated, and the user cannot pin how a term is rendered** — Recurring names in a show or meeting get mangled on every line, and a user glossary fixes them for both ASR spelling and MT output. → §LE34

## Block G — Caption overlay

- 📋 **LE35** (deps: LE5, LE21) **No caption window: text from the pipeline has nowhere to appear on screen** — The overlay is what users judge the app by, and keeping line layout in a UI-free caption model lets it be tested apart from the window technique LE5 chose. → §LE35
- 📋 **LE36** (deps: LE35) **No docked mode: captions cannot reserve a screen edge, so they cover the video or window being watched** — Live Captions users expect a docked bar that pushes other windows aside, and a floating card over fullscreen video hides exactly the burned-in subtitles and faces. → §LE36
- 📋 **LE37** (deps: LE35) **Caption look is fixed, ignoring the user's Windows caption style and readability needs** — Caption readers have strong, often accessibility-driven preferences, and Windows already stores a caption style that the app should follow by default. → §LE37
- 📋 **LE38** (deps: LE35) **Screen readers cannot read captions, and there is no global hotkey to show, pause or make them click-through** — A caption tool is an accessibility tool first, so live-region announcements and keyboard-only control are part of the product, not polish. → §LE38
- 📋 **LE39** (deps: LE35, LE9) **Captions vanish once they roll off; no history to scroll back, copy or export as subtitles** — Missing a line in a meeting or lecture is common, and an opt-in transcript with SRT/VTT export turns the app into a note-taking tool without breaking privacy. → §LE39

## Block H — App shell, settings and first use

- 📋 **LE40** (deps: LE9, LE21, LE35) **No app shell: nothing starts or stops captions or picks the source and languages** — Captions run in the background for hours, so a tray-first shell with a compact control window is the right home, as in the sibling claude-tray and Revelei apps. → §LE40
- 📋 **LE41** (deps: LE40, LE22, LE31) **First launch has no guided setup: no languages, no consented model download, no hardware check** — A local-AI app is useless until gigabytes of models are chosen and fetched, so the first minutes decide whether users stay, and consent must name sizes and licences. → §LE41
- 📋 **LE42** (deps: LE8) **UI text is not localised; a pt-BR-first app would ship hard-coded strings in one language** — The audience is Portuguese speakers translating foreign content, so the UI must be pt-BR and en from day one, with the siblings' string-resource gate stopping regressions. → §LE42
- 📋 **LE43** (deps: LE40, LE21) **When captions lag or stop, there is no diagnostics view of per-stage latency, provider, VRAM or fallbacks** — Real-time problems are invisible without numbers, and one screen with copyable stats turns a vague "it's slow" report into an actionable one. → §LE43
- 📋 **LE44** (deps: LE22, LE23) **The pipeline draws the same power on battery and keeps GPU sessions loaded while idle** — Hours of captions on a laptop drain battery and warm the GPU, so power state and idle time must drive model tier, placement and session release. → §LE44

## Block I — Delivery: installers, docs and benchmarks

- 📋 **LE45** (deps: LE40) **No installer or updater: the app cannot be installed per user or updated outside a dev build** — Both siblings ship an Inno Setup per-user installer with a self-updater, and reusing that path is the shortest route to real users without waiting on the Store. → §LE45
- 📋 **LE46** (deps: LE45) **No MSIX/Store package, and unchecked whether audio capture and Windows ML providers work packaged** — The Store is where caption tools are found, but Revelei hit Store blockers (PB92) and packaged-path issues, so packaged capture must be proven before submission. → §LE46
- 📋 **LE47** (deps: LE41, LE10) **No README, privacy statement, model attribution notice or published benchmark numbers** — CC-BY models legally require attribution, "100% local" is the product's main promise and must be stated precisely, and measured latency is what convinces users. → §LE47
- 📋 **LE48** (deps: LE40, LE36, LE41) **No end-to-end UI evidence: nothing drives the real app from capture to translated overlay text** — Unit tests cover stages in isolation, while the bugs users see live between them, and the siblings already gate releases on Winwright cases run in a VM. → §LE48

## Done when — Block A

- **Every spike has a README with numbers and a decision** Each spike/<name>/README.md
  states the hardware, the measured table, the chosen option and what was rejected and
  why; the tasks that depend on it cite that decision instead of re-deciding.

## Done when — LE3

- **Direct es→pt and ja→pt models and a Qwen3 LLM tier are measured**
  spike/mt-engines/README.md has rows for opus-mt-ja-pt, opus-mt-tc-big-itc-itc and
  Qwen3 1.7B/4B on the same 100 FLORES sentences, so es→pt and ja→pt get a default and
  the quality tier is decided.

## Non-goals

- **Cloud speech or translation services** The promise is that audio and captions never
  leave the PC; a cloud fallback would make that promise conditional and add accounts,
  keys and costs.
- **WinUI 3 instead of WPF with the Fluent ThemeMode** Revelei, Modelado, claude-tray
  and winwright are WPF + Fluent; staying there reuses their code, Winwright cases and
  build habits, and WPF gives easier transparent topmost windows.
- **Non-commercial models (NLLB, SeamlessM4T) in the shipped catalog** CC-BY-NC weights
  cannot be redistributed in a product that may be sold; the manifest gate admits MIT,
  Apache-2.0, BSD and CC-BY-4.0 only, and NC models serve as spike references.
- **Speech synthesis or dubbing of the translation** The app shows text; reading the
  translation aloud is a different product with its own latency, audio routing and
  voice-licensing problems.
- **DirectSound or XAudio2 for capture** DirectSound is a legacy layer over WASAPI
  without loopback and XAudio2 only plays audio; WASAPI shared-mode and process loopback
  are the low-latency capture APIs (LE1).
- **Speaker diarization beyond the You/Others meeting label** Telling individual
  speakers apart needs another model and adds latency and errors; the mic-versus-system
  label (LE16) covers the main call use case.
