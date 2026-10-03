namespace SoulsProx.Audio;

public enum TalkMode
{
    VoiceActivation,
    PushToTalk,
}

/// <summary>Decides, frame by frame, whether the microphone is being transmitted.</summary>
public sealed class VoiceGate
{
    /// <summary>Keep transmitting this many frames after the voice drops below the threshold (300 ms).</summary>
    public const int HangFrames = 15;

    private int _hang;

    public TalkMode Mode { get; set; } = TalkMode.VoiceActivation;

    /// <summary>Voice-activation threshold in dBFS.</summary>
    public float ThresholdDb { get; set; } = -45f;

    /// <param name="levelDb">Level of the current frame in dBFS.</param>
    /// <param name="pushToTalkDown">Push-to-talk key held.</param>
    /// <param name="radioDown">Radio key held (always transmits).</param>
    public bool ShouldTransmit(float levelDb, bool pushToTalkDown, bool radioDown)
    {
        if (radioDown) return true;
        if (Mode == TalkMode.PushToTalk)
        {
            _hang = 0;
            return pushToTalkDown;
        }
        if (levelDb >= ThresholdDb) _hang = HangFrames;
        else if (_hang > 0) _hang--;
        return _hang > 0;
    }

    /// <summary>RMS level of a frame in dBFS (-100 for silence).</summary>
    public static float LevelDb(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        foreach (float s in frame) sum += s * s;
        double rms = Math.Sqrt(sum / Math.Max(frame.Length, 1));
        return rms < 1e-5 ? -100f : (float)(20 * Math.Log10(rms));
    }
}
