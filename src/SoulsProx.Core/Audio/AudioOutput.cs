using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoulsProx.Audio;

public sealed record AudioDeviceInfo(string Id, string Name);

public static class AudioDevices
{
    public static IReadOnlyList<AudioDeviceInfo> List(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var list = new List<AudioDeviceInfo>();
        foreach (var d in devices)
        {
            list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName));
            d.Dispose();
        }
        return list;
    }

    public static IReadOnlyList<AudioDeviceInfo> Microphones() => List(DataFlow.Capture);
    public static IReadOnlyList<AudioDeviceInfo> Outputs() => List(DataFlow.Render);

    /// <summary>Opens a device by id, falling back to the Windows default if it's missing (unplugged headset etc).</summary>
    internal static MMDevice Open(MMDeviceEnumerator enumerator, string? id, DataFlow flow)
    {
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                var device = enumerator.GetDevice(id);
                if (device.State == DeviceState.Active) return device;
                device.Dispose();
            }
            catch (Exception) { /* fall through to default */ }
        }
        return enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
    }
}

/// <summary>Mixes all incoming voices and plays them on one output device.</summary>
public sealed class AudioOutput : IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiPlayer _player;
    private readonly MixingSampleProvider _mixer = new(WaveFormat.CreateIeeeFloatWaveFormat(VoiceFormat.SampleRate, 2)) { ReadFully = true };

    public string DeviceName => _device.FriendlyName;

    /// <param name="deviceId">Endpoint id, or null for the Windows default output.</param>
    public AudioOutput(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = AudioDevices.Open(enumerator, deviceId, DataFlow.Render);
        _player = new WasapiPlayerBuilder().WithDevice(_device).WithSharedMode().WithLatency(60).Build();

        ISampleProvider source = new SoftLimiter(_mixer);
        int deviceRate = _player.DeviceMixFormat.SampleRate;
        if (deviceRate != VoiceFormat.SampleRate) source = new WdlResamplingSampleProvider(source, deviceRate);
        _player.Init(new SampleToWaveProvider(source));
        _player.Play();
    }

    public void AddInput(ISampleProvider input) => _mixer.AddMixerInput(input);

    public void RemoveInput(ISampleProvider input) => _mixer.RemoveMixerInput(input);

    public void Dispose()
    {
        try { _player.Stop(); } catch (Exception) { }
        _player.Dispose();
        _device.Dispose();
    }
}

/// <summary>Keeps the mix out of hard clipping when voices are boosted above 100%.</summary>
internal sealed class SoftLimiter(ISampleProvider source) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        int n = source.Read(buffer);
        for (int i = 0; i < n; i++)
        {
            float x = buffer[i];
            // Linear below 0.8, smoothly approaching 1.0 above it.
            if (x > 0.8f) buffer[i] = 0.8f + 0.2f * MathF.Tanh((x - 0.8f) / 0.2f);
            else if (x < -0.8f) buffer[i] = -0.8f - 0.2f * MathF.Tanh((-x - 0.8f) / 0.2f);
        }
        return n;
    }
}
