using System.Collections.Concurrent;
using System.Net;
using System.Numerics;
using SoulsProx.Audio;
using SoulsProx.Games;
using SoulsProx.Input;
using SoulsProx.Net;
using SoulsProx.Proximity;
using SoulsProx.Settings;

namespace SoulsProx;

/// <summary>Snapshot of the friend's status for the UI.</summary>
public sealed record FriendStatus(
    LinkState Link,
    IPEndPoint? Endpoint,
    PeerReport? Report,
    SpatialResult Spatial,
    bool Speaking,
    bool Radio);

/// <summary>
/// Wires everything together: game reading → proximity → per-voice gain/pan, mic → Opus → network,
/// network → jitter buffer → speakers.
/// </summary>
public sealed class ProxChatEngine : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ReportStaleAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BeaconDelay = TimeSpan.FromMilliseconds(500);
    private const int PreRollFrames = 2;

    private readonly string _settingsPath;
    private readonly PeerVoice _friendVoice = new();
    private readonly PeerVoice _beaconVoice = new(targetFrames: 4);
    private readonly VoiceEncoder _encoder;
    private readonly VoiceGate _gate = new();
    private readonly HotkeyPoller _hotkeys = new();
    private readonly SpatialHold _hold = new(TimeSpan.FromSeconds(10));
    private readonly Queue<(uint Seq, byte[] Data)> _preRoll = new();
    private readonly ConcurrentQueue<(long Due, uint Seq, byte[] Data, bool Radio)> _beaconQueue = new();
    private readonly object _audioLock = new();
    private readonly Timer _tick;

    private MicCapture? _mic;
    private AudioOutput? _output;
    private ProximitySettings _proximity;
    private volatile PeerReport? _friendReport;
    private long _friendReportTicks;
    private DateTime _lastStateSent;
    private uint _seq;
    private bool _wasTransmitting;
    private Vector3? _beaconPosition;
    private FriendStatus _friendStatus;
    private SpatialResult? _beaconStatus;

    public ProxChatEngine(AppSettings settings, string settingsPath, int? portOverride = null)
    {
        Settings = settings;
        _settingsPath = settingsPath;
        _proximity = settings.ToProximity();
        _encoder = new VoiceEncoder(settings.Bitrate);
        ApplyToComponents(settings);

        Game = new GameMonitor();
        Link = new PeerLink(portOverride ?? settings.Port, settings.SecretBytes);
        Link.VoiceReceived += (seq, opus, radio) => _friendVoice.Enqueue(seq, opus.Span, radio);
        Link.ReportReceived += r =>
        {
            _friendReport = r;
            Interlocked.Exchange(ref _friendReportTicks, Environment.TickCount64);
        };
        Link.StateChanged += s =>
        {
            Log.Info($"Link: {s} {Link.RemoteEndpoint}");
            if (s != LinkState.Connected) _friendVoice.Reset();
        };

        _friendStatus = new FriendStatus(LinkState.Idle, null, null, default, false, false);
        _tick = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public AppSettings Settings { get; private set; }
    public GameMonitor Game { get; }
    public PeerLink Link { get; }

    public float MicLevelDb { get; private set; } = -100f;
    public bool Transmitting { get; private set; }
    public string MicStatus { get; private set; } = "";
    public string OutputStatus { get; private set; } = "";
    public FriendStatus Friend => Volatile.Read(ref _friendStatus);
    public SpatialResult? Beacon => _beaconStatus;
    public bool BeaconActive => _beaconPosition is not null;
    public HotkeyPoller Hotkeys => _hotkeys;

    public async Task StartAsync()
    {
        Log.Info($"Starting. UDP port {Link.LocalPort}");
        Game.Start();
        RestartAudio();
        _tick.Change(TickInterval, TickInterval);
        await Link.DiscoverPublicEndpointAsync();
        Log.Info($"Public endpoint {Link.PublicEndpoint?.ToString() ?? "unknown"}; LAN {Link.LanEndpoint}. {Link.NatNote}");

        if (ConnectionCode.TryParse(Settings.FriendCode, out var code, out _))
        {
            try { Link.Connect(code!, ParseEndpoint(Settings.FriendAddressOverride)); }
            catch (ArgumentException ex) { Log.Error("Saved friend code rejected", ex); }
        }
    }

    /// <summary>Connects to a friend's code. Returns an error message, or null on success.</summary>
    public string? Connect(string friendCode, string? addressOverride)
    {
        if (!ConnectionCode.TryParse(friendCode, out var code, out var error)) return error;
        IPEndPoint? overrideEp = null;
        if (!string.IsNullOrWhiteSpace(addressOverride))
        {
            overrideEp = ParseEndpoint(addressOverride);
            if (overrideEp is null) return "The friend address should look like 100.64.1.2:47800.";
        }
        try { Link.Connect(code!, overrideEp); }
        catch (ArgumentException ex) { return ex.Message; }

        UpdateSettings(Settings with { FriendCode = friendCode.Trim(), FriendAddressOverride = addressOverride?.Trim() });
        return null;
    }

    public void Disconnect()
    {
        Link.Disconnect();
        UpdateSettings(Settings with { FriendCode = null });
    }

    /// <summary>Saves new settings and applies them, reopening audio devices only if they changed.</summary>
    public void UpdateSettings(AppSettings updated)
    {
        var old = Settings;
        Settings = updated;
        updated.Save(_settingsPath);
        _proximity = updated.ToProximity();
        ApplyToComponents(updated);
        if (old.MicDeviceId != updated.MicDeviceId || old.OutputDeviceId != updated.OutputDeviceId || old.NoiseSuppression != updated.NoiseSuppression)
            RestartAudio();
    }

    private void ApplyToComponents(AppSettings s)
    {
        _gate.Mode = s.TalkMode;
        _gate.ThresholdDb = s.VoiceThresholdDb;
        _hotkeys.PushToTalk = s.PushToTalkKey;
        _hotkeys.Radio = s.RadioKey;
        _friendVoice.UserVolume = s.FriendVolume;
        _friendVoice.RadioEffect = s.RadioEffect;
        _beaconVoice.UserVolume = 1f;
        _beaconVoice.RadioEffect = s.RadioEffect;
    }

    public void RestartAudio()
    {
        lock (_audioLock)
        {
            _mic?.Dispose();
            _mic = null;
            _output?.Dispose();
            _output = null;

            try
            {
                _output = new AudioOutput(Settings.OutputDeviceId);
                _output.AddInput(_friendVoice);
                _output.AddInput(_beaconVoice);
                OutputStatus = _output.DeviceName;
            }
            catch (Exception ex)
            {
                OutputStatus = $"Couldn't open speakers/headphones: {ex.Message}";
                Log.Error("Output device", ex);
            }

            try
            {
                _mic = new MicCapture(Settings.MicDeviceId, Settings.NoiseSuppression);
                _mic.FrameCaptured += OnMicFrame;
                _mic.Start();
                MicStatus = _mic.DeviceName;
            }
            catch (Exception ex)
            {
                MicStatus = $"Couldn't open microphone: {ex.Message}";
                Log.Error("Microphone", ex);
            }
            Log.Info($"Audio: mic '{MicStatus}', output '{OutputStatus}'");
        }
    }

    /// <summary>Runs on the audio capture thread for every 20 ms of microphone audio.</summary>
    private void OnMicFrame(float[] frame)
    {
        float level = VoiceGate.LevelDb(frame);
        MicLevelDb = level;
        bool radio = _hotkeys.RadioDown;
        bool transmit = _gate.ShouldTransmit(level, _hotkeys.PushToTalkDown, radio);

        // Always encode so the encoder state is continuous and we can send a little pre-roll
        // when the voice gate opens (so the first syllable isn't clipped).
        byte[] packet = _encoder.Encode(frame).ToArray();
        uint seq = _seq++;

        if (transmit)
        {
            if (!_wasTransmitting)
            {
                foreach (var (preSeq, preData) in _preRoll) Send(preSeq, preData, radio);
                _preRoll.Clear();
            }
            Send(seq, packet, radio);
        }
        else
        {
            _preRoll.Enqueue((seq, packet));
            while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
        }
        _wasTransmitting = transmit;
        Transmitting = transmit;
    }

    private void Send(uint seq, byte[] packet, bool radio)
    {
        Link.SendVoice(seq, packet, radio);
        if (_beaconPosition is not null)
            _beaconQueue.Enqueue((Environment.TickCount64 + (long)BeaconDelay.TotalMilliseconds, seq, packet, radio));
    }

    private int _ticking;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            var now = DateTime.UtcNow;
            var snap = Game.Latest;

            if (now - _lastStateSent >= StateInterval)
            {
                _lastStateSent = now;
                Link.SendState(ToReport(snap));
            }

            // Friend
            var report = Environment.TickCount64 - Interlocked.Read(ref _friendReportTicks) < ReportStaleAfter.TotalMilliseconds ? _friendReport : null;
            var spatial = _hold.Apply(ProximityModel.Compute(snap, report, _proximity), now);
            bool speaking = _friendVoice.IsSpeaking;
            bool radio = speaking && _friendVoice.IsRadio;
            if (radio) spatial = spatial with { Gain = 1f, Pan = 0f, Source = SpatialSource.Radio };
            _friendVoice.SetSpatial(spatial.Gain, spatial.Pan);
            Volatile.Write(ref _friendStatus, new FriendStatus(Link.State, Link.RemoteEndpoint, report, spatial, speaking, radio));

            // Test beacon: our own voice, delayed, coming from where we dropped it.
            if (_beaconPosition is { } beacon && snap is { InWorld: true })
            {
                var b = ProximityModel.ForPosition(snap, beacon, _proximity, SpatialSource.Beacon);
                _beaconVoice.SetSpatial(b.Gain, b.Pan);
                _beaconStatus = b;
            }
            else
            {
                _beaconVoice.SetSpatial(0f, 0f);
                _beaconStatus = null;
            }
            long nowTicks = Environment.TickCount64;
            while (_beaconQueue.TryPeek(out var item) && item.Due <= nowTicks && _beaconQueue.TryDequeue(out item))
                if (_beaconPosition is not null) _beaconVoice.Enqueue(item.Seq, item.Data, item.Radio);
        }
        catch (Exception ex)
        {
            Log.Error("Tick", ex);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>Drops a test beacon at the current position. Returns false if not in the game world.</summary>
    public bool DropBeacon()
    {
        if (Game.Latest is not { InWorld: true } snap) return false;
        _beaconPosition = snap.LocalPosition;
        return true;
    }

    public void ClearBeacon()
    {
        _beaconPosition = null;
        _beaconVoice.Reset();
    }

    private static PeerReport ToReport(GameSnapshot? snap) => snap is { InWorld: true }
        ? new PeerReport(snap.Game, true, snap.LocalName, snap.LocalPosition, snap.Frame)
        : new PeerReport(snap?.Game ?? GameId.None, false, "", Vector3.Zero, 0);

    private static IPEndPoint? ParseEndpoint(string? text) =>
        !string.IsNullOrWhiteSpace(text) && IPEndPoint.TryParse(text.Trim(), out var ep) && ep.Port != 0 ? ep : null;

    public void Dispose()
    {
        _tick.Dispose();
        lock (_audioLock)
        {
            _mic?.Dispose();
            _output?.Dispose();
        }
        Link.Dispose();
        Game.Dispose();
        _hotkeys.Dispose();
        _encoder.Dispose();
        _friendVoice.Dispose();
        _beaconVoice.Dispose();
    }
}
