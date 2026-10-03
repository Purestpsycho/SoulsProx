using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoulsProx.Audio;

/// <summary>
/// Captures the microphone and delivers 20 ms mono 48 kHz frames on the capture thread.
/// Handles whatever format the device gives us (downmix + resample).
/// </summary>
public sealed class MicCapture : IDisposable
{
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly WasapiRecorder _recorder;
    private readonly MMDevice _device;
    private readonly int _channels;
    private readonly SampleKind _kind;
    private readonly LinearResampler? _resampler;
    private readonly float[] _frame = new float[VoiceFormat.FrameSamples];
    private float[] _mono = new float[4096];
    private int _framePos;

    private enum SampleKind { Float32, Pcm16, Pcm24, Pcm32 }

    /// <summary>Raised on the capture thread with one 960-sample frame. The array is reused; copy if you keep it.</summary>
    public event Action<float[]>? FrameCaptured;

    public string DeviceName => _device.FriendlyName;

    /// <param name="deviceId">Endpoint id, or null for the Windows default microphone.</param>
    /// <param name="communicationsMode">Ask Windows for its voice-call processing (noise suppression, echo cancellation, auto gain).</param>
    public MicCapture(string? deviceId, bool communicationsMode)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = AudioDevices.Open(enumerator, deviceId, DataFlow.Capture);

        _recorder = Build(_device, communicationsMode, requestFormat: true)
            ?? Build(_device, communicationsMode, requestFormat: false)
            ?? Build(_device, false, requestFormat: false)
            ?? throw new InvalidOperationException($"Could not open microphone '{_device.FriendlyName}'.");

        var format = _recorder.WaveFormat;
        _channels = format.Channels;
        _kind = Classify(format);
        if (format.SampleRate != VoiceFormat.SampleRate) _resampler = new LinearResampler(format.SampleRate, VoiceFormat.SampleRate);
        _recorder.DataAvailable += OnData;
    }

    private static WasapiRecorder? Build(MMDevice device, bool communicationsMode, bool requestFormat)
    {
        try
        {
            var builder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithBufferLength(40);
            if (requestFormat) builder = builder.WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(VoiceFormat.SampleRate, 1));
            if (communicationsMode) builder = builder.WithCommunicationsMode();
            return builder.Build();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static SampleKind Classify(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);
        if (isFloat && format.BitsPerSample == 32) return SampleKind.Float32;
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm
            || (format is WaveFormatExtensible ext2 && ext2.SubFormat == PcmSubFormat);
        if (isPcm) return format.BitsPerSample switch
        {
            16 => SampleKind.Pcm16,
            24 => SampleKind.Pcm24,
            32 => SampleKind.Pcm32,
            _ => throw new NotSupportedException($"Unsupported microphone format {format}"),
        };
        throw new NotSupportedException($"Unsupported microphone format {format}");
    }

    public void Start() => _recorder.StartRecording();

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        int bytesPerSample = _kind switch { SampleKind.Pcm16 => 2, SampleKind.Pcm24 => 3, _ => 4 };
        int frames = buffer.Length / (bytesPerSample * _channels);
        if (_mono.Length < frames) _mono = new float[frames * 2];
        var mono = _mono.AsSpan(0, frames);

        if (flags.HasFlag(AudioClientBufferFlags.Silent)) mono.Clear();
        else Downmix(buffer, mono, bytesPerSample);

        if (_resampler is null) Push(mono);
        else _resampler.Process(mono, Push);
    }

    private void Downmix(ReadOnlySpan<byte> buffer, Span<float> mono, int bytesPerSample)
    {
        float scale = 1f / _channels;
        int stride = bytesPerSample * _channels;
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++)
            {
                var s = buffer.Slice(i * stride + c * bytesPerSample, bytesPerSample);
                sum += _kind switch
                {
                    SampleKind.Float32 => BitConverter.ToSingle(s),
                    SampleKind.Pcm16 => BitConverter.ToInt16(s) / 32768f,
                    SampleKind.Pcm24 => ((s[2] << 24) | (s[1] << 16) | (s[0] << 8)) / 2147483648f,
                    _ => BitConverter.ToInt32(s) / 2147483648f,
                };
            }
            mono[i] = sum * scale;
        }
    }

    private void Push(ReadOnlySpan<float> samples)
    {
        while (samples.Length > 0)
        {
            int n = Math.Min(samples.Length, _frame.Length - _framePos);
            samples[..n].CopyTo(_frame.AsSpan(_framePos));
            _framePos += n;
            samples = samples[n..];
            if (_framePos == _frame.Length)
            {
                _framePos = 0;
                FrameCaptured?.Invoke(_frame);
            }
        }
    }

    public void Dispose()
    {
        _recorder.DataAvailable -= OnData;
        try { _recorder.StopRecording(); } catch (COMException) { }
        _recorder.Dispose();
        _device.Dispose();
    }
}

/// <summary>Streaming linear-interpolation resampler. Plenty for speech headed into a 48 kHz Opus encoder.</summary>
internal sealed class LinearResampler(int fromRate, int toRate)
{
    private readonly double _step = (double)fromRate / toRate;
    private double _pos;     // position in the input, relative to the current block (may be negative: -1 = _last)
    private float _last;
    private float[] _out = new float[4096];

    public void Process(ReadOnlySpan<float> input, ReadOnlySpanAction<float> output)
    {
        if (input.Length == 0) return;
        int count = 0;
        int maxOut = (int)((input.Length + 1) / _step) + 2;
        if (_out.Length < maxOut) _out = new float[maxOut * 2];

        while (_pos < input.Length - 1)
        {
            int i = (int)Math.Floor(_pos);
            float t = (float)(_pos - i);
            float a = i < 0 ? _last : input[i];
            float b = input[i + 1];
            _out[count++] = a + (b - a) * t;
            _pos += _step;
        }
        _pos -= input.Length;
        _last = input[^1];
        output(_out.AsSpan(0, count));
    }
}

public delegate void ReadOnlySpanAction<T>(ReadOnlySpan<T> span);
