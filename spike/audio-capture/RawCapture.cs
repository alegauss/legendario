using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Legendario.Spike.AudioCapture;

internal enum Source { Loopback, Mic, Process }

internal sealed record RawOptions(
    Source Source,
    int BufferMs,
    bool Convert16k,          // AUTOCONVERTPCM into 16 kHz mono float: the engine resamples
    int TargetPid,            // process loopback only
    bool ExcludeTree,         // process loopback only
    bool Mmcss,
    bool FollowDefault);      // reopen on the default device changing, not only on invalidation

/// <summary>Event-driven WASAPI capture through raw COM: endpoint loopback, microphone, process loopback.</summary>
internal static class RawCapture
{
    public static void Run(RawOptions o, Recording rec, TimeSpan duration)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        var defaultChanged = 0;
        var watcher = new DeviceWatcher(text =>
        {
            rec.Event(text);
            if (text.StartsWith("default ", StringComparison.Ordinal)
                && text.Contains(o.Source == Source.Mic ? "Capture/Console" : "Render/Console", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref defaultChanged, 1);
            }
        });
        enumerator.RegisterEndpointNotificationCallback(watcher);

        var thread = new Thread(() => Loop(o, rec, duration, enumerator, () => Interlocked.Exchange(ref defaultChanged, 0) == 1))
        {
            Name = "capture",
            IsBackground = false,
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        enumerator.UnregisterEndpointNotificationCallback(watcher);
    }

    private static void Loop(RawOptions o, Recording rec, TimeSpan duration, IMMDeviceEnumerator enumerator, Func<bool> takeDefaultChanged)
    {
        IntPtr mmcss = IntPtr.Zero;
        if (o.Mmcss)
        {
            uint index = 0;
            mmcss = Native.AvSetMmThreadCharacteristicsW("Pro Audio", ref index);
            rec.Notes.Add($"mmcss Pro Audio: {(mmcss != IntPtr.Zero ? "on" : "failed " + Marshal.GetLastWin32Error())}");
        }

        using var evt = new AutoResetEvent(false);
        var opened = Open(o, enumerator, evt, rec);
        var reopens = 0;
        var buffer = new byte[1 << 20];
        var bytesPerFrame = rec.Format.BlockAlign;
        var end = rec.NowMs + duration.TotalMilliseconds;

        try
        {
            while (rec.NowMs < end)
            {
                if (o.FollowDefault && o.Source != Source.Process && takeDefaultChanged())
                {
                    rec.Event("reopening on the new default device");
                    opened = Reopen(o, enumerator, evt, rec, opened, ref reopens);
                    continue;
                }

                if (!evt.WaitOne(200))
                {
                    rec.Timeouts++;
                    // Loopback signals nothing while the endpoint is silent; polling here is how a
                    // silent stream is told apart from a dead one.
                    if (opened.Capture.GetNextPacketSize(out _) == Hr.AUDCLNT_E_DEVICE_INVALIDATED)
                    {
                        rec.Event("device invalidated (seen while idle)");
                        opened = Reopen(o, enumerator, evt, rec, opened, ref reopens);
                    }
                    continue;
                }

                var wake = rec.NowMs;
                while (true)
                {
                    var hr = opened.Capture.GetNextPacketSize(out var next);
                    if (hr == Hr.AUDCLNT_E_DEVICE_INVALIDATED)
                    {
                        rec.Event("device invalidated");
                        opened = Reopen(o, enumerator, evt, rec, opened, ref reopens);
                        break;
                    }
                    Marshal.ThrowExceptionForHR(hr);
                    if (next == 0)
                    {
                        break;
                    }

                    hr = opened.Capture.GetBuffer(out var data, out var frames, out var flags, out var devPos, out var qpcPos);
                    var arrival = rec.NowMs;
                    if (hr == Hr.AUDCLNT_S_BUFFER_EMPTY)
                    {
                        break;
                    }
                    if (hr == Hr.AUDCLNT_E_DEVICE_INVALIDATED)
                    {
                        rec.Event("device invalidated (GetBuffer)");
                        opened = Reopen(o, enumerator, evt, rec, opened, ref reopens);
                        break;
                    }
                    Marshal.ThrowExceptionForHR(hr);

                    var bytes = (int)frames * bytesPerFrame;
                    var span = buffer.AsSpan(0, bytes);
                    if (flags.HasFlag(BufferFlags.Silent))
                    {
                        span.Clear();
                    }
                    else
                    {
                        Marshal.Copy(data, buffer, 0, bytes);
                    }
                    opened.Capture.ReleaseBuffer(frames);

                    var latency = qpcPos == 0 ? double.NaN : arrival - rec.QpcToMs(qpcPos);
                    var rms = Recording.Rms(span, rec.Format);
                    rec.Add(new Packet(wake, arrival, (int)frames, flags, devPos, latency, rms), opened.FormatMatches ? span : default);
                }
            }
        }
        finally
        {
            rec.Finish();
            opened.Client.Stop();
            Release(opened);
            rec.Notes.Add($"reopens: {reopens}");
            if (mmcss != IntPtr.Zero)
            {
                Native.AvRevertMmThreadCharacteristics(mmcss);
            }
        }
    }

    private sealed record Opened(IAudioClient Client, IAudioCaptureClient Capture, bool FormatMatches);

    private static Opened Reopen(RawOptions o, IMMDeviceEnumerator enumerator, AutoResetEvent evt, Recording rec, Opened old, ref int reopens)
    {
        try
        {
            old.Client.Stop();
        }
        catch (COMException)
        {
        }
        Release(old);
        reopens++;
        var t0 = rec.NowMs;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var opened = Open(o, enumerator, evt, rec);
                rec.Event($"reopened in {rec.NowMs - t0:F0} ms after {attempt} failed attempt(s)");
                return opened;
            }
            catch (COMException e) when (attempt < 50)
            {
                rec.Event($"reopen failed 0x{e.HResult:X8}, retrying");
                Thread.Sleep(100);
            }
        }
    }

    private static void Release(Opened o)
    {
        Marshal.ReleaseComObject(o.Capture);
        Marshal.ReleaseComObject(o.Client);
    }

    /// <summary>The format a run records, decided before the first open so the WAV header holds.</summary>
    public static WaveFormat PlanFormat(RawOptions o)
    {
        if (o.Convert16k)
        {
            return WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
        }
        if (o.Source == Source.Process)
        {
            return WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        }
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        enumerator.GetDefaultAudioEndpoint(o.Source == Source.Mic ? EDataFlow.Capture : EDataFlow.Render, ERole.Console, out var device);
        var iid = Native.IID_IAudioClient;
        device.Activate(ref iid, Native.CLSCTX_ALL, IntPtr.Zero, out var obj);
        var client = (IAudioClient)obj;
        client.GetMixFormat(out var mix);
        var format = WaveFormat.MarshalFromPtr(mix);
        Marshal.FreeCoTaskMem(mix);
        Marshal.ReleaseComObject(client);
        return format;
    }

    private static Opened Open(RawOptions o, IMMDeviceEnumerator enumerator, AutoResetEvent evt, Recording rec)
    {
        IAudioClient client;
        string what;
        if (o.Source == Source.Process)
        {
            client = ActivateProcessLoopback(o.TargetPid, o.ExcludeTree);
            what = $"process loopback, {(o.ExcludeTree ? "exclude" : "include")} tree of pid {o.TargetPid}";
        }
        else
        {
            enumerator.GetDefaultAudioEndpoint(o.Source == Source.Mic ? EDataFlow.Capture : EDataFlow.Render, ERole.Console, out var device);
            device.GetId(out var id);
            var iid = Native.IID_IAudioClient;
            device.Activate(ref iid, Native.CLSCTX_ALL, IntPtr.Zero, out var obj);
            client = (IAudioClient)obj;
            what = $"{o.Source} on {id}";
        }

        var flags = StreamFlags.EventCallback;
        if (o.Source != Source.Mic)
        {
            flags |= StreamFlags.Loopback;
        }

        IntPtr format;
        bool matches;
        if (o.Convert16k || o.Source == Source.Process)
        {
            format = WaveFormat.MarshalToPtr(rec.Format);
            flags |= StreamFlags.AutoConvertPcm | StreamFlags.SrcDefaultQuality;
            matches = true;
        }
        else
        {
            client.GetMixFormat(out format);
            var mix = WaveFormat.MarshalFromPtr(format);
            matches = mix.SampleRate == rec.Format.SampleRate && mix.Channels == rec.Format.Channels && mix.BitsPerSample == rec.Format.BitsPerSample;
            if (!matches)
            {
                rec.Event($"mix format changed to {mix}; audio after this is not written to the WAV");
            }
        }

        try
        {
            var hr = client.Initialize(0, flags, o.BufferMs * 10_000L, 0, format, IntPtr.Zero);
            Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(format);
        }

        client.SetEventHandle(evt.SafeWaitHandle.DangerousGetHandle());
        var ciid = Native.IID_IAudioCaptureClient;
        client.GetService(ref ciid, out var cap);
        try
        {
            client.GetBufferSize(out var bufferFrames);
            client.GetDevicePeriod(out var defPeriod, out var minPeriod);
            client.GetStreamLatency(out var streamLatency);
            rec.Event($"opened {what}: buffer {bufferFrames} frames, device period {defPeriod / 10_000.0} ms (min {minPeriod / 10_000.0}), stream latency {streamLatency / 10_000.0} ms");
        }
        catch (Exception e) when (e is COMException or NotImplementedException)
        {
            // Process loopback answers E_NOTIMPL to GetDevicePeriod: it is a virtual device.
            client.GetBufferSize(out var bufferFrames);
            rec.Event($"opened {what}: buffer {bufferFrames} frames, period query failed 0x{e.HResult:X8}");
        }
        client.Start();
        return new Opened(client, (IAudioCaptureClient)cap, matches);
    }

    private static IAudioClient ActivateProcessLoopback(int pid, bool exclude)
    {
        var activation = new AudioClientActivationParams
        {
            ActivationType = 1,
            TargetProcessId = (uint)pid,
            ProcessLoopbackMode = exclude ? 1 : 0,
        };
        var paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        var variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activation, paramsPtr, false);
            Marshal.StructureToPtr(new PropVariantBlob
            {
                VarType = Native.VT_BLOB,
                Size = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                Data = paramsPtr,
            }, variantPtr, false);

            var handler = new ActivationHandler();
            var iid = Native.IID_IAudioClient;
            Native.ActivateAudioInterfaceAsync(Native.ProcessLoopbackDevice, ref iid, variantPtr, handler, out _);
            return handler.Task.GetAwaiter().GetResult();
        }
        finally
        {
            Marshal.FreeHGlobal(variantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }
}
