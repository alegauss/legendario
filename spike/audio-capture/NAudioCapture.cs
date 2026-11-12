using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Legendario.Spike.AudioCapture;

/// <summary>The same sources through NAudio's wrappers, which expose bytes and nothing else.</summary>
internal static class NAudioCapture
{
    public static WaveFormat PlanFormat(Source source)
    {
        using var e = new MMDeviceEnumerator();
        using var d = e.GetDefaultAudioEndpoint(source == Source.Mic ? DataFlow.Capture : DataFlow.Render, Role.Console);
        return d.AudioClient.MixFormat;
    }

    public static void Run(Source source, int bufferMs, Recording rec, TimeSpan duration)
    {
        using var e = new MMDeviceEnumerator();
        var device = e.GetDefaultAudioEndpoint(source == Source.Mic ? DataFlow.Capture : DataFlow.Render, Role.Console);
        using WasapiCapture capture = source == Source.Mic
            ? new WasapiCapture(device, true, bufferMs)
            : new WasapiLoopbackCapture(device);
        rec.Event($"NAudio {capture.GetType().Name} on {device.FriendlyName}, format {capture.WaveFormat}");

        capture.DataAvailable += (_, a) =>
        {
            var now = rec.NowMs;
            var span = a.Buffer.AsSpan(0, a.BytesRecorded);
            rec.Add(new Packet(now, now, a.BytesRecorded / rec.Format.BlockAlign, BufferFlags.None, 0, double.NaN, Recording.Rms(span, rec.Format)), span);
        };
        var stopped = new ManualResetEventSlim();
        capture.RecordingStopped += (_, a) =>
        {
            if (a.Exception != null)
            {
                rec.Event("NAudio stopped with " + a.Exception.Message);
            }
            stopped.Set();
        };
        capture.StartRecording();
        if (stopped.Wait(duration))
        {
            rec.Event("NAudio stopped before the run ended");
        }
        capture.StopRecording();
        stopped.Wait(2000);
        rec.Finish();
        device.Dispose();
    }
}

/// <summary>Plays a quiet sine through the default render endpoint: the signal loopback runs measure.</summary>
internal static class Tone
{
    public static void Play(double seconds, double frequency, float gain)
    {
        using var output = new WasapiOut(AudioClientShareMode.Shared, true, 20);
        var signal = new SignalGenerator(48000, 2) { Frequency = frequency, Gain = gain, Type = SignalGeneratorType.Sin };
        output.Init(signal.Take(TimeSpan.FromSeconds(seconds)));
        output.Play();
        while (output.PlaybackState == PlaybackState.Playing)
        {
            Thread.Sleep(50);
        }
    }
}
