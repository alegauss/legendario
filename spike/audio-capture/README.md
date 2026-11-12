# Audio capture spike (LE1)

Which Windows audio API Legendario captures with, at what buffer size, and how the
captured audio gets to 16 kHz mono. This folder is a record of the answer, not live
code: it is outside any solution and no gate builds it.

## How it was measured

`AudioCaptureSpike` captures from one source for 60 s. It writes a WAV file, a CSV with
one row per packet, and a summary to `out\` (gitignored).

```
dotnet build -c Release
bin\Release\net10.0-windows\win-x64\AudioCaptureSpike.exe capture raw loopback --tone 440
bin\Release\net10.0-windows\win-x64\AudioCaptureSpike.exe capture raw process --tone 440 [--exclude]
bin\Release\net10.0-windows\win-x64\AudioCaptureSpike.exe capture raw mic
bin\Release\net10.0-windows\win-x64\AudioCaptureSpike.exe capture naudio loopback|mic
bin\Release\net10.0-windows\win-x64\AudioCaptureSpike.exe resample
```

- **Raw sources.** `raw` is event-driven WASAPI through hand-written `[ComImport]` interop
  (`Interop.cs`, `RawCapture.cs`): one capture loop serves endpoint loopback, process
  loopback and the microphone. The capture thread is registered with MMCSS "Pro Audio".
- **NAudio sources.** `naudio` uses NAudio 2.2.1's `WasapiLoopbackCapture` and
  `WasapiCapture` with their defaults.
- **Test signal.** `--tone` plays a 0.05-amplitude sine through the default endpoint
  from a child process, which is also the process-loopback target.
- **Options.** `--16k` asks the audio engine itself for 16 kHz mono float
  (`AUTOCONVERTPCM | SRC_DEFAULT_QUALITY`). `--follow-default` reopens the stream when
  the default device changes.

**Capture latency** is when `GetBuffer` returned a packet minus the packet's QPC position.
**Jitter** is the spread of the interval between the capture thread's wake-ups. **CPU** is
the process's total processor time over the run, including writing the WAV file.

Machine: Windows 11 Pro 26300, Realtek(R) Audio speakers (48 kHz stereo float mix
format), HyperX QuadCast USB microphone, idle desktop. Measured 2026-11-12.

## Results

### Capture

| Run | Packets | Wake interval p50 / p99 / max | Capture latency p50 / max | Flags | CPU, one core |
| --- | --- | --- | --- | --- | --- |
| Endpoint loopback, raw, 10 ms buffer | 480 frames every 10 ms | 10.00 / 10.58 / 19.9 ms | −10.0 / −8.2 ms | 1 discontinuity (first packet) | 0.26 % |
| Endpoint loopback, raw, 20 ms buffer | same | 10.00 / 10.31 / 20.0 ms | −10.5 / −9.3 ms | same | 1.15 % |
| Endpoint loopback, raw, no MMCSS | same | 10.00 / 10.31 / 20.0 ms | −10.5 / −4.8 ms | same | 1.43 % |
| Endpoint loopback, NAudio | 2,880 frames every 60 ms | 60.4 / 62.3 / 63.5 ms | not exposed | not exposed | 0.34 % |
| Process loopback, raw, include tree | 480 frames every 10 ms | 10.00 / 11.19 / 20.5 ms | +10.6 / +13.7 ms | none | 0.47 % |
| Process loopback, raw, exclude tree | same, all digital zero | 10.00 / 11.11 / 31.2 ms | +10.3 / +12.9 ms | none | 0.68 % |
| Process loopback, raw, 16 kHz mono | 160 frames every 10 ms | 10.00 / 11.24 / 11.8 ms | +9.6 / +11.1 ms | none | 0.47 % |
| Microphone, raw, 10 ms buffer | 480 frames every 10 ms | 10.00 / 10.95 / 19.3 ms | +19.2 / +22.1 ms | 1 discontinuity (first packet) | 0.42 % |
| Microphone, NAudio, 20 ms, event | 480 frames every 10 ms | 10.00 / 11.02 / 11.9 ms | not exposed | not exposed | 1.09 % |

What the runs showed:

- **The buffer size does not change the cadence.** In shared mode the engine period
  (10 ms here, 3 ms minimum) sets the packet size. Asking for a 10 ms or a 20 ms buffer
  both gave a 1,056-frame (22 ms) endpoint buffer and 10 ms packets. Process loopback
  gives exactly the buffer asked for (480 frames for 10 ms).
- **MMCSS made no measurable difference** on an idle desktop. Keep it anyway: it costs
  nothing, and its purpose is to protect the thread under load, which this run did not
  create.
- **CPU is negligible.** Every raw run used under 1.5 % of one core, or 9 to 52 s of
  CPU per hour, WAV writing included. The spread between runs is noise, not a
  difference between methods.
- **Endpoint loopback hands audio over before it is heard.** The QPC position of a
  loopback packet lies about 10 ms after its arrival, so the stamp marks when the
  speaker will play it. Process loopback stamps the packet's start, which arrives one
  packet later (+10 ms). The microphone arrives 19 to 22 ms after its first sample was
  captured. Speech-to-capture latency is therefore at most about 22 ms, a rounding
  error against the 1.5 s budget.
- **Endpoint loopback goes silent when nothing plays.** In the run without a tone it
  delivered a few `SILENT`-flagged packets, then no packets at all for 27.6 s; the
  event never fired, and the loop timed out 177 times. When playback resumed, the device
  position had jumped by the length of the gap and the packet carried
  `DATA_DISCONTINUITY`. The position jump is the exact length of silence to synthesise,
  so LE12's silent frames can be counted from it rather than from the wall clock.
- **Process loopback never goes silent.** In exclude mode, with nothing else playing,
  it delivered a packet of digital zeros every 10 ms. No synthesis is needed there.
- **Endpoint loopback carries the endpoint's effects; process loopback does not.** With a
  0.05-amplitude tone averaged to mono, process loopback measured exactly −26.0 dBFS.
  Endpoint loopback measured +1.7 dB at 1 kHz and +6.8 dB at 7 kHz, which is the Realtek
  driver's enhancements (APO) applied before the loopback tap.
- **Muting the speakers does not silence loopback.** The endpoint was muted (volume
  0.85) during the later runs, and the tone was captured at the same level. So "muted"
  is not a reason for missing captions, and LE17 should not treat it as one.
- **First-packet discontinuity is normal.** Every endpoint stream flags its first
  packet. Only a later flag means a real gap.
- **Process loopback answers `E_NOTIMPL` (0x80004001) to `GetDevicePeriod`.** It is a
  virtual device. `GetBufferSize`, `GetBuffer` and the QPC positions all work.
- **`AUTOCONVERTPCM` on endpoint loopback reports the device position in endpoint
  frames.** At 16 kHz the position advanced 480 per 160-frame packet. Process loopback
  counts in the frames it delivers. The frame clock should come from the QPC position,
  which is correct in both cases.

### NAudio

NAudio 2.2.1 is not enough for this app:

- `WasapiLoopbackCapture` polls with a 100 ms buffer and delivers 60 ms packets. That is
  up to 60 ms of added latency, and nothing in its constructor changes it.
- `DataAvailable` passes bytes only. The QPC position, the device position and
  `DATA_DISCONTINUITY` are dropped, so the frame clock and the discontinuity flag that
  LE12's `AudioFrame` carries cannot be built on it.
- NAudio declares `AUDIOCLIENT_ACTIVATION_PARAMS` and `ActivateAudioInterfaceAsync`, but
  both are internal, and the public `AudioClient.ActivateAsync` passes no activation
  parameters. Process loopback needs its own interop either way.

The hand-written interop for all three sources (the Core Audio interfaces, the
activation handler and the device watcher) is about 200 lines. CsWin32 was not tried:
the runtime numbers would be identical, and the hand-written interfaces are already
known to work.

### Getting to 16 kHz mono

`AudioCaptureSpike resample` feeds synthetic 0.5-amplitude stereo sines in 10 ms chunks.
It reports the gain at 1 kHz and 7 kHz (passband), the level of 9 kHz and 12 kHz input
after conversion (aliasing, against the sine's level), the delay of an impulse, and the
time to convert 60 s.

| Input | Method | 1 kHz | 7 kHz | 9 kHz alias | 12 kHz alias | Delay | CPU, one core |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 48 kHz | Media Foundation resampler DSP, quality 60 | 0.00 dB | 0.00 dB | −107.2 dB | −123.7 dB | 0 ms (compensated) | 0.10 % |
| 48 kHz | NAudio WDL (managed) | −0.01 dB | −11.7 dB | −20.5 dB | −33.8 dB | 0.06 ms | 0.09 % |
| 48 kHz | Own polyphase FIR, 128 taps per phase, Kaiser β=8 | 0.00 dB | −2.9 dB | −85.9 dB | −108.4 dB | 1.31 ms | 0.12 % |
| 44.1 kHz | Media Foundation resampler DSP, quality 60 | 0.00 dB | 0.00 dB | −107.4 dB | −127.3 dB | 0 ms (compensated) | 0.11 % |
| 44.1 kHz | NAudio WDL (managed) | −0.02 dB | −12.6 dB | −22.2 dB | −37.5 dB | 0.06 ms | 0.08 % |
| 44.1 kHz | Own polyphase FIR, 128 taps per phase, Kaiser β=8 | 0.00 dB | −2.6 dB | −113.6 dB | −102.5 dB | 1.44 ms | 0.12 % |

The audio engine's own converter (`--16k`) was measured on live endpoint loopback with
the FFT of the capture. A 12 kHz tone left −132.6 dBFS at its 4 kHz alias, below the
noise floor. A 7 kHz tone passed. Process loopback at 16 kHz delivered a 440 Hz tone at
exactly the expected −26.0 dBFS.

- **Every option is cheap.** CPU is not what decides between them.
- **WDL is unusable for ASR.** It attenuates 7 kHz by 12 dB and lets a 9 kHz tone alias
  back at −20 dB.
- **The managed polyphase filter works** but needs 128 taps per phase to reach −85 dB,
  and still rolls off 3 dB at 7 kHz. LE13's draft of 64 taps with a 7.6 kHz cutoff and
  −80 dB of rejection cannot have all three: at 48 kHz, 64 Kaiser taps give a transition
  band about 3.7 kHz wide.
- **Media Foundation and the engine converter are both effectively transparent** up to
  7 kHz, with over 100 dB of alias rejection.

## Decision

- **API.** Shared-mode, event-driven WASAPI through hand-written `[ComImport]` interop,
  one capture loop for all three sources. NAudio is not used for capture. DirectSound and
  XAudio2 are not candidates: the first is an emulation layer over WASAPI with no
  loopback, and the second only plays audio.
- **Buffer.** Ask for 10 ms. The engine period decides the cadence anyway, so 20 ms buys
  nothing. Keep MMCSS "Pro Audio" on the capture thread.
- **Format.** Ask the engine for 16 kHz mono float (`AUTOCONVERTPCM |
  SRC_DEFAULT_QUALITY`) on every WASAPI source. The engine's converter measured as clean
  as Media Foundation's, it runs outside the process, and it removes a stage from the hot
  path. LE13 keeps its stage only for sources that do not come from WASAPI (the LE10
  replay corpus, files), and there it uses the Media Foundation resampler at quality 60,
  not a hand-written filter. The cost is that the engine averages the channels, so 5.1
  content gets no extra centre weighting.
- **Clock.** Timestamp frames from the packet's QPC position, never from the device
  position, which `AUTOCONVERTPCM` reports in the wrong unit. Ignore the first packet's
  discontinuity flag. On endpoint loopback, synthesise silence for the gap that the
  position jump shows.
- **Reconnect.** `IMMNotificationClient.OnDefaultDeviceChanged`, together with
  `AUDCLNT_E_DEVICE_INVALIDATED` from `GetNextPacketSize` or `GetBuffer` (including a
  check while idle, because a silent loopback gets no events), triggers a reopen on the
  new default with retries every 100 ms. The spike implements this (`--follow-default`),
  but it has **not been exercised here**. Unplugging a headset, Bluetooth switching from
  A2DP to HFP, and DRM playback need a person at the machine, and that run is its own
  roadmap line.
- **Minimum build for per-app capture.** Windows 10 build 20348 is the documented
  minimum, which in practice means Windows 11 (consumer Windows 10 stops at 19045).
  It works on 26300 here; older builds were not available to test. Gate on the build
  and also treat a failed activation as "unsupported", so a build that answers
  differently from the documentation falls back cleanly.
