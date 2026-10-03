using Concentus;
using Concentus.Enums;

namespace SoulsProx.Audio;

public static class VoiceFormat
{
    public const int SampleRate = 48_000;
    /// <summary>20 ms of mono audio.</summary>
    public const int FrameSamples = 960;
    public const int MaxPacketBytes = 1275;
}

/// <summary>Opus encoder for one mono 48 kHz voice stream.</summary>
public sealed class VoiceEncoder : IDisposable
{
    private readonly IOpusEncoder _encoder;
    private readonly byte[] _buffer = new byte[VoiceFormat.MaxPacketBytes];

    public VoiceEncoder(int bitrate = 32_000)
    {
        _encoder = OpusCodecFactory.CreateEncoder(VoiceFormat.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP, null);
        _encoder.Bitrate = bitrate;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        _encoder.Complexity = 8;
        _encoder.UseVBR = true;
        // In-band forward error correction lets the receiver rebuild a lost packet from the next one.
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 10;
    }

    /// <summary>Encodes one 20 ms frame. The returned span is only valid until the next call.</summary>
    public ReadOnlySpan<byte> Encode(ReadOnlySpan<float> frame)
    {
        int n = _encoder.Encode(frame, VoiceFormat.FrameSamples, _buffer, _buffer.Length);
        return _buffer.AsSpan(0, n);
    }

    public void Dispose() => _encoder.Dispose();
}

/// <summary>Opus decoder for one incoming voice stream.</summary>
public sealed class VoiceDecoder : IDisposable
{
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(VoiceFormat.SampleRate, 1, null);

    /// <summary>Decodes a packet into a 960-sample frame.</summary>
    public void Decode(ReadOnlySpan<byte> packet, Span<float> frame) =>
        Fill(_decoder.Decode(packet, frame, VoiceFormat.FrameSamples, false), frame);

    /// <summary>Rebuilds a lost frame using the forward-error-correction data carried in the packet that followed it.</summary>
    public void DecodeFromNext(ReadOnlySpan<byte> nextPacket, Span<float> frame) =>
        Fill(_decoder.Decode(nextPacket, frame, VoiceFormat.FrameSamples, true), frame);

    /// <summary>Packet loss concealment: synthesizes a plausible frame when nothing arrived.</summary>
    public void Conceal(Span<float> frame) =>
        Fill(_decoder.Decode(ReadOnlySpan<byte>.Empty, frame, VoiceFormat.FrameSamples, false), frame);

    private static void Fill(int decoded, Span<float> frame)
    {
        if (decoded < frame.Length) frame[Math.Max(decoded, 0)..].Clear();
    }

    public void Dispose() => _decoder.Dispose();
}
