using System.Runtime.InteropServices;

namespace SoulsProx.Audio;

/// <summary>
/// WebRTC's audio processing module (the AEC3 echo canceller used by Chrome and Discord, plus its
/// noise suppressor and high-pass filter). Everything SoulsProx plays is fed in as the echo reference,
/// so if the friend's voice leaks from your headphones into your mic it is removed before it can be sent
/// back to them.
///
/// Thread-safe: <see cref="ProcessRender"/> runs on the audio output thread and
/// <see cref="ProcessCapture"/> on the microphone thread.
/// Native library: webrtc-apm.dll from the SoundFlow.Extensions.WebRtc.Apm package (MIT; WebRTC is BSD-3-Clause).
/// </summary>
public sealed class EchoCanceller : IDisposable
{
    /// <summary>The module works on 10 ms chunks.</summary>
    public const int ChunkSamples = VoiceFormat.SampleRate / 100;

    private readonly object _lock = new();
    private readonly float[] _renderPending = new float[ChunkSamples];
    private int _renderCount;
    private nint _apm, _config, _stream;
    private readonly nint[] _buffers = new nint[4];   // capture in/out, render in/out (float[480] each)
    private readonly nint[] _channelLists = new nint[4]; // float*[1] pointing at the buffers above
    private const int CapIn = 0, CapOut = 1, RenIn = 2, RenOut = 3;

    private EchoCanceller(bool echoCancellation, bool noiseSuppression)
    {
        _apm = Native.webrtc_apm_create();
        if (_apm == 0) throw new InvalidOperationException("webrtc_apm_create failed");

        _config = Native.webrtc_apm_config_create();
        Native.webrtc_apm_config_set_echo_canceller(_config, echoCancellation ? 1 : 0, 0);
        Native.webrtc_apm_config_set_noise_suppression(_config, noiseSuppression ? 1 : 0, 2 /* high */);
        Native.webrtc_apm_config_set_high_pass_filter(_config, 1);
        Native.webrtc_apm_config_set_gain_controller1(_config, 0, 1, 3, 9, 1);
        Native.webrtc_apm_config_set_gain_controller2(_config, 0);
        Native.webrtc_apm_config_set_pipeline(_config, VoiceFormat.SampleRate, 0, 0, 0);
        Check(Native.webrtc_apm_apply_config(_apm, _config), "apply_config");

        _stream = Native.webrtc_apm_stream_config_create(VoiceFormat.SampleRate, 1);
        Check(Native.webrtc_apm_initialize(_apm), "initialize");

        for (int i = 0; i < 4; i++)
        {
            _buffers[i] = Marshal.AllocHGlobal(ChunkSamples * sizeof(float));
            _channelLists[i] = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(_channelLists[i], _buffers[i]);
        }
        EchoEnabled = echoCancellation;
        NoiseSuppressionEnabled = noiseSuppression;
    }

    public bool EchoEnabled { get; }
    public bool NoiseSuppressionEnabled { get; }

    /// <summary>
    /// Rough delay (ms) between audio being handed to the output device and its echo arriving from the mic.
    /// Only a starting hint; AEC3 estimates the real delay itself.
    /// </summary>
    public int StreamDelayMs { get; set; } = 80;

    /// <summary>Creates the processor, or returns null (with a reason) if the native library can't be loaded.</summary>
    public static EchoCanceller? TryCreate(bool echoCancellation, bool noiseSuppression, out string? error)
    {
        error = null;
        if (!echoCancellation && !noiseSuppression) return null;
        try
        {
            return new EchoCanceller(echoCancellation, noiseSuppression);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Feeds audio that is about to be played (mono, 48 kHz). Any length.</summary>
    public void ProcessRender(ReadOnlySpan<float> played)
    {
        lock (_lock)
        {
            if (_apm == 0) return;
            while (played.Length > 0)
            {
                int n = Math.Min(played.Length, ChunkSamples - _renderCount);
                played[..n].CopyTo(_renderPending.AsSpan(_renderCount));
                _renderCount += n;
                played = played[n..];
                if (_renderCount < ChunkSamples) break;

                _renderCount = 0;
                Marshal.Copy(_renderPending, 0, _buffers[RenIn], ChunkSamples);
                Native.webrtc_apm_process_reverse_stream(_apm, _channelLists[RenIn], _stream, _stream, _channelLists[RenOut]);
            }
        }
    }

    /// <summary>Cleans microphone audio in place (mono, 48 kHz, a multiple of 10 ms).</summary>
    public void ProcessCapture(float[] samples)
    {
        lock (_lock)
        {
            if (_apm == 0) return;
            for (int offset = 0; offset + ChunkSamples <= samples.Length; offset += ChunkSamples)
            {
                Marshal.Copy(samples, offset, _buffers[CapIn], ChunkSamples);
                Native.webrtc_apm_set_stream_delay_ms(_apm, StreamDelayMs);
                int err = Native.webrtc_apm_process_stream(_apm, _channelLists[CapIn], _stream, _stream, _channelLists[CapOut]);
                if (err == 0) Marshal.Copy(_buffers[CapOut], samples, offset, ChunkSamples);
            }
        }
    }

    private static void Check(int error, string what)
    {
        if (error != 0) throw new InvalidOperationException($"WebRTC audio processing: {what} failed ({error})");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_apm == 0) return;
            Native.webrtc_apm_destroy(_apm);
            _apm = 0;
            if (_config != 0) Native.webrtc_apm_config_destroy(_config);
            if (_stream != 0) Native.webrtc_apm_stream_config_destroy(_stream);
            _config = _stream = 0;
            for (int i = 0; i < 4; i++)
            {
                Marshal.FreeHGlobal(_channelLists[i]);
                Marshal.FreeHGlobal(_buffers[i]);
            }
        }
    }

    private static class Native
    {
        private const string Lib = "webrtc-apm";

        [DllImport(Lib)] public static extern nint webrtc_apm_create();
        [DllImport(Lib)] public static extern void webrtc_apm_destroy(nint apm);
        [DllImport(Lib)] public static extern nint webrtc_apm_config_create();
        [DllImport(Lib)] public static extern void webrtc_apm_config_destroy(nint config);
        [DllImport(Lib)] public static extern void webrtc_apm_config_set_echo_canceller(nint config, int enabled, int mobileMode);
        [DllImport(Lib)] public static extern void webrtc_apm_config_set_noise_suppression(nint config, int enabled, int level);
        [DllImport(Lib)] public static extern void webrtc_apm_config_set_gain_controller1(nint config, int enabled, int mode, int targetLevelDbfs, int compressionGainDb, int enableLimiter);
        [DllImport(Lib)] public static extern void webrtc_apm_config_set_gain_controller2(nint config, int enabled);
        [DllImport(Lib)] public static extern void webrtc_apm_config_set_high_pass_filter(nint config, int enabled);
        [DllImport(Lib)] public static extern void webrtc_apm_config_set_pipeline(nint config, int maxInternalRate, int multiChannelRender, int multiChannelCapture, int downmixMethod);
        [DllImport(Lib)] public static extern int webrtc_apm_apply_config(nint apm, nint config);
        [DllImport(Lib)] public static extern nint webrtc_apm_stream_config_create(int sampleRateHz, nuint numChannels);
        [DllImport(Lib)] public static extern void webrtc_apm_stream_config_destroy(nint config);
        [DllImport(Lib)] public static extern int webrtc_apm_initialize(nint apm);
        [DllImport(Lib)] public static extern int webrtc_apm_process_stream(nint apm, nint src, nint inputConfig, nint outputConfig, nint dest);
        [DllImport(Lib)] public static extern int webrtc_apm_process_reverse_stream(nint apm, nint src, nint inputConfig, nint outputConfig, nint dest);
        [DllImport(Lib)] public static extern void webrtc_apm_set_stream_delay_ms(nint apm, int delay);
    }
}
