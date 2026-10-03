using NAudio.Wave;

namespace SoulsProx.Audio;

/// <summary>
/// One incoming voice: jitter buffer → Opus decode → distance gain and stereo pan → 48 kHz stereo float.
/// Packets are added from the network thread; <see cref="Read"/> runs on the audio thread.
/// </summary>
public sealed class PeerVoice : ISampleProvider, IDisposable
{
    private enum State { Idle, Buffering, Playing }
    private enum FrameKind { Silence, Normal, FromNext, Conceal }
    private readonly record struct Packet(byte[] Data, bool Radio);

    /// <summary>Never hold more than a second of audio.</summary>
    private const int MaxQueued = 50;
    /// <summary>Frames of concealment to play after the stream dries up before going quiet.</summary>
    private const int MaxConcealFrames = 3;
    /// <summary>Gain/pan smoothing time constant (~50 ms) as a per-sample coefficient.</summary>
    private static readonly float Smoothing = 1f - MathF.Exp(-1f / (0.05f * VoiceFormat.SampleRate));

    private readonly object _lock = new();
    private readonly Dictionary<uint, Packet> _queue = [];
    private readonly int _targetFrames;
    private readonly int _maxLatencyFrames;
    private readonly VoiceDecoder _decoder = new();
    private readonly RadioFilter _radio = new();
    private readonly float[] _frame = new float[VoiceFormat.FrameSamples];

    private State _state = State.Idle;
    private uint _nextSeq;
    private int _bufferingFrames;
    private int _lost;
    private int _pos = VoiceFormat.FrameSamples;
    private bool _frameIsRadio;

    private float _targetGain, _targetPan;
    private float _gain, _pan;
    private long _lastVoiceTick = long.MinValue / 2;

    /// <param name="targetFrames">How many 20 ms frames to buffer before starting playback.</param>
    public PeerVoice(int targetFrames = 3)
    {
        _targetFrames = targetFrames;
        _maxLatencyFrames = targetFrames + 6;
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(VoiceFormat.SampleRate, 2);

    /// <summary>User volume multiplier for this voice (1 = 100%).</summary>
    public float UserVolume { get; set; } = 1f;

    /// <summary>Apply the walkie-talkie sound to frames sent with the radio key.</summary>
    public bool RadioEffect { get; set; } = true;

    /// <summary>True while voice packets are actively being played.</summary>
    public bool IsSpeaking => Environment.TickCount64 - Interlocked.Read(ref _lastVoiceTick) < 250;

    /// <summary>True if the most recent voice packet was sent with the radio key.</summary>
    public bool IsRadio { get; private set; }

    /// <summary>Sets where the voice should sound like it comes from. Changes are smoothed.</summary>
    public void SetSpatial(float gain, float pan)
    {
        Volatile.Write(ref _targetGain, gain);
        Volatile.Write(ref _targetPan, Math.Clamp(pan, -1f, 1f));
    }

    public void Enqueue(uint seq, ReadOnlySpan<byte> opus, bool radio)
    {
        lock (_lock)
        {
            if (_state == State.Playing && (int)(seq - _nextSeq) < 0) return; // arrived too late
            if (_queue.ContainsKey(seq)) return;
            if (_queue.Count >= MaxQueued) _queue.Remove(MinSeq());
            _queue[seq] = new Packet(opus.ToArray(), radio);
            if (_state == State.Idle)
            {
                _state = State.Buffering;
                _bufferingFrames = 0;
            }
        }
        IsRadio = radio;
    }

    /// <summary>Drops everything queued (e.g. after a disconnect).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _queue.Clear();
            _state = State.Idle;
        }
    }

    public int Read(Span<float> buffer)
    {
        int frames = buffer.Length / 2;
        float targetGain = Volatile.Read(ref _targetGain) * UserVolume;
        float targetPan = Volatile.Read(ref _targetPan);
        for (int i = 0; i < frames; i++)
        {
            if (_pos >= _frame.Length)
            {
                NextFrame();
                _pos = 0;
            }
            float s = _frame[_pos++];
            _gain += (targetGain - _gain) * Smoothing;
            _pan += (targetPan - _pan) * Smoothing;
            buffer[2 * i] = s * _gain * MathF.Min(1f, 1f - _pan);
            buffer[2 * i + 1] = s * _gain * MathF.Min(1f, 1f + _pan);
        }
        return frames * 2;
    }

    private void NextFrame()
    {
        FrameKind kind;
        Packet packet = default;
        lock (_lock)
        {
            kind = Dequeue(out packet);
        }

        switch (kind)
        {
            case FrameKind.Normal:
                _decoder.Decode(packet.Data, _frame);
                _frameIsRadio = packet.Radio;
                Interlocked.Exchange(ref _lastVoiceTick, Environment.TickCount64);
                break;
            case FrameKind.FromNext:
                _decoder.DecodeFromNext(packet.Data, _frame);
                break;
            case FrameKind.Conceal:
                _decoder.Conceal(_frame);
                break;
            default:
                Array.Clear(_frame);
                _frameIsRadio = false;
                return;
        }

        if (_frameIsRadio && RadioEffect) _radio.Process(_frame);
    }

    /// <summary>Jitter-buffer logic. Must be called under the lock.</summary>
    private FrameKind Dequeue(out Packet packet)
    {
        packet = default;
        if (_state == State.Idle) return FrameKind.Silence;

        if (_state == State.Buffering)
        {
            // Start once enough audio is queued, or once we've waited that long anyway (short utterances).
            if (_queue.Count < _targetFrames && ++_bufferingFrames < _targetFrames) return FrameKind.Silence;
            if (_queue.Count == 0)
            {
                _state = State.Idle;
                return FrameKind.Silence;
            }
            _state = State.Playing;
            _nextSeq = MinSeq();
            _lost = 0;
        }

        // Bound the latency if packets piled up (e.g. after a network hiccup).
        if (_queue.Count > _maxLatencyFrames)
        {
            while (_queue.Count > _targetFrames) _queue.Remove(MinSeq());
            _nextSeq = MinSeq();
        }

        FrameKind kind;
        if (_queue.Remove(_nextSeq, out packet))
        {
            kind = FrameKind.Normal;
            _lost = 0;
        }
        else if (_queue.TryGetValue(_nextSeq + 1, out packet))
        {
            kind = FrameKind.FromNext;
            _lost++;
        }
        else if (_queue.Count == 0)
        {
            if (++_lost > MaxConcealFrames)
            {
                _state = State.Idle;
                return FrameKind.Silence;
            }
            kind = FrameKind.Conceal;
        }
        else if (++_lost > MaxConcealFrames)
        {
            // A gap we won't recover from; skip ahead to what we have.
            _nextSeq = MinSeq();
            _queue.Remove(_nextSeq, out packet);
            kind = FrameKind.Normal;
            _lost = 0;
        }
        else
        {
            kind = FrameKind.Conceal;
        }

        _nextSeq++;
        return kind;
    }

    private uint MinSeq()
    {
        bool first = true;
        uint min = 0;
        foreach (uint seq in _queue.Keys)
        {
            if (first || (int)(seq - min) < 0) min = seq;
            first = false;
        }
        return min;
    }

    public void Dispose() => _decoder.Dispose();
}

/// <summary>Band-limited, slightly crunchy "walkie-talkie" voice.</summary>
internal sealed class RadioFilter
{
    private readonly Biquad _highPass = Biquad.HighPass(VoiceFormat.SampleRate, 400f, 0.707f);
    private readonly Biquad _lowPass = Biquad.LowPass(VoiceFormat.SampleRate, 2800f, 0.707f);

    public void Process(Span<float> frame)
    {
        const float drive = 2.5f;
        float norm = 1f / MathF.Tanh(drive);
        for (int i = 0; i < frame.Length; i++)
        {
            float s = _lowPass.Process(_highPass.Process(frame[i]));
            frame[i] = MathF.Tanh(s * drive) * norm * 0.8f;
        }
    }
}

/// <summary>RBJ cookbook biquad, transposed direct form II.</summary>
internal sealed class Biquad
{
    private readonly float _b0, _b1, _b2, _a1, _a2;
    private float _z1, _z2;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = (float)(b0 / a0); _b1 = (float)(b1 / a0); _b2 = (float)(b2 / a0);
        _a1 = (float)(a1 / a0); _a2 = (float)(a2 / a0);
    }

    public static Biquad LowPass(int sampleRate, float cutoff, float q)
    {
        double w0 = 2 * Math.PI * cutoff / sampleRate, cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
        return new Biquad((1 - cos) / 2, 1 - cos, (1 - cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public static Biquad HighPass(int sampleRate, float cutoff, float q)
    {
        double w0 = 2 * Math.PI * cutoff / sampleRate, cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
        return new Biquad((1 + cos) / 2, -(1 + cos), (1 + cos) / 2, 1 + alpha, -2 * cos, 1 - alpha);
    }

    public float Process(float x)
    {
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }
}
