using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SoulsProx.Audio;
using SoulsProx.Games;
using SoulsProx.Input;
using SoulsProx.Proximity;
using SoulsProx.Settings;
using InputBinding = SoulsProx.Input.InputBinding;
using LinkState = SoulsProx.Net.LinkState;

namespace SoulsProx.App;

public sealed record DeviceItem(string? Id, string Name);

public sealed class MainViewModel : ObservableObject
{
    private const double FriendBarMax = 120;
    private static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Avatar colours (a nod to CrewLink's crewmates).</summary>
    private static readonly Brush[] AvatarColors = new[]
    {
        "#C51111", "#132ED1", "#117F2D", "#ED54BA", "#EF7D0D", "#F5F557",
        "#6B2FBB", "#71491E", "#38FEDC", "#50EF39", "#D6E0F0", "#3F474E",
    }.Select(MakeBrush).ToArray();

    private static readonly Brush TalkBrush = MakeBrush("#2ECC71");
    private static readonly Brush RadioBrush = MakeBrush("#BA68C8");
    private static readonly Brush WarnBrush = MakeBrush("#E67E22");
    private static readonly Brush ErrorBrush = MakeBrush("#EA3C2A");
    private static readonly Brush MutedBrush = MakeBrush("#777777");
    private static readonly Brush SubtleBrush = MakeBrush("#A9A4AE");
    private static readonly Brush GreyFill = MakeBrush("#4A4552");

    private readonly ProxChatEngine _engine;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private string _lastMyName = "";
    private string _lastFriendName = "";

    // Key binding in progress
    private Action<InputBinding?>? _bindTarget;
    private DateTime _bindDeadline;
    private bool _bindWaitRelease;
    private string? _bindingWhich;

    public MainViewModel(ProxChatEngine engine)
    {
        _engine = engine;
        Microphones = [new DeviceItem(null, "Windows default"), .. AudioDevices.Microphones().Select(d => new DeviceItem(d.Id, d.Name))];
        Outputs = [new DeviceItem(null, "Windows default"), .. AudioDevices.Outputs().Select(d => new DeviceItem(d.Id, d.Name))];
        _friendCodeInput = engine.Settings.FriendCode ?? "";
        _friendAddressInput = engine.Settings.FriendAddressOverride ?? "";

        ToggleSettingsCommand = new RelayCommand(() => { ConnectOpen = false; SettingsOpen = !SettingsOpen; });
        OpenConnectCommand = new RelayCommand(() => { SettingsOpen = false; ConnectOpen = true; });
        CloseOverlayCommand = new RelayCommand(CloseOverlays);
        CopyCodeCommand = new RelayCommand(CopyCode);
        ConnectCommand = new RelayCommand(Connect);
        DisconnectCommand = new RelayCommand(() => { _engine.Disconnect(); ConnectError = ""; });
        BindPushToTalkCommand = new RelayCommand(() => BeginBinding(nameof(PushToTalkKeyText), b => Update(s => s with { PushToTalkKey = b }, nameof(PushToTalkKeyText))));
        BindRadioCommand = new RelayCommand(() => BeginBinding(nameof(RadioKeyText), b => Update(s => s with { RadioKey = b }, nameof(RadioKeyText))));
        DropBeaconCommand = new RelayCommand(() => BeaconMessage = _engine.DropBeacon() ? "" : "Load into the game world first.");
        ClearBeaconCommand = new RelayCommand(() => { _engine.ClearBeacon(); BeaconMessage = ""; });
        OpenLogsCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo { FileName = AppSettings.Directory, UseShellExecute = true }));

        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private AppSettings S => _engine.Settings;

    // ---------------- Commands ----------------

    public ICommand ToggleSettingsCommand { get; }
    public ICommand OpenConnectCommand { get; }
    public ICommand CloseOverlayCommand { get; }
    public ICommand CopyCodeCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand BindPushToTalkCommand { get; }
    public ICommand BindRadioCommand { get; }
    public ICommand DropBeaconCommand { get; }
    public ICommand ClearBeaconCommand { get; }
    public ICommand OpenLogsCommand { get; }

    // ---------------- Overlays ----------------

    private bool _settingsOpen, _connectOpen;

    public bool SettingsOpen
    {
        get => _settingsOpen;
        set { if (Set(ref _settingsOpen, value)) OnPropertyChanged(nameof(AnyOverlayOpen)); }
    }

    public bool ConnectOpen
    {
        get => _connectOpen;
        set { if (Set(ref _connectOpen, value)) OnPropertyChanged(nameof(AnyOverlayOpen)); }
    }

    public bool AnyOverlayOpen => SettingsOpen || ConnectOpen;

    public void CloseOverlays()
    {
        SettingsOpen = false;
        ConnectOpen = false;
    }

    public bool IsBindingKey => _bindTarget is not null;

    // ---------------- Me ----------------

    private string _myName = "", _myInitial = "", _gameText = "";
    private Brush _myFill = GreyFill;
    private bool _myTalking, _gameWaiting;

    public string MyName { get => _myName; private set => Set(ref _myName, value); }
    public string MyInitial { get => _myInitial; private set => Set(ref _myInitial, value); }
    public Brush MyFill { get => _myFill; private set => Set(ref _myFill, value); }
    public bool MyTalking { get => _myTalking; private set => Set(ref _myTalking, value); }
    public string GameText { get => _gameText; private set => Set(ref _gameText, value); }
    public bool GameWaiting { get => _gameWaiting; private set => Set(ref _gameWaiting, value); }

    // ---------------- Friend ----------------

    private bool _showFriendSlot, _showFriend, _friendConnected, _friendTalking, _friendBadge;
    private string _friendName = "", _friendInitial = "", _friendDetail = "", _friendBadgeGlyph = "";
    private Brush _friendFill = GreyFill, _friendRing = TalkBrush, _friendBadgeBrush = WarnBrush;
    private double _friendBar;

    public bool ShowFriendSlot { get => _showFriendSlot; private set => Set(ref _showFriendSlot, value); }
    public bool ShowFriend { get => _showFriend; private set => Set(ref _showFriend, value); }
    public bool FriendConnected { get => _friendConnected; private set => Set(ref _friendConnected, value); }
    public string FriendName { get => _friendName; private set => Set(ref _friendName, value); }
    public string FriendInitial { get => _friendInitial; private set => Set(ref _friendInitial, value); }
    public Brush FriendFill { get => _friendFill; private set => Set(ref _friendFill, value); }
    public bool FriendTalking { get => _friendTalking; private set => Set(ref _friendTalking, value); }
    public Brush FriendRing { get => _friendRing; private set => Set(ref _friendRing, value); }
    public string FriendDetail { get => _friendDetail; private set => Set(ref _friendDetail, value); }
    public double FriendBarWidth { get => _friendBar; private set => Set(ref _friendBar, value); }
    public bool FriendBadge { get => _friendBadge; private set => Set(ref _friendBadge, value); }
    public string FriendBadgeGlyph { get => _friendBadgeGlyph; private set => Set(ref _friendBadgeGlyph, value); }
    public Brush FriendBadgeBrush { get => _friendBadgeBrush; private set => Set(ref _friendBadgeBrush, value); }

    // ---------------- Mic bar ----------------

    private double _micLevel = -100;
    private bool _transmitting;
    private string _talkHint = "";
    private Brush _talkHintBrush = MutedBrush;

    public double MicLevelDb { get => _micLevel; private set => Set(ref _micLevel, value); }
    public bool Transmitting { get => _transmitting; private set => Set(ref _transmitting, value); }
    public string TalkHint { get => _talkHint; private set => Set(ref _talkHint, value); }
    public Brush TalkHintBrush { get => _talkHintBrush; private set => Set(ref _talkHintBrush, value); }

    // ---------------- Connect panel ----------------

    private string _myCode = "", _friendCodeInput, _friendAddressInput, _connectError = "", _linkStatus = "", _copyText = "Copy code";
    private Brush _linkStatusBrush = SubtleBrush;
    private bool _canDisconnect;

    public string MyCode { get => _myCode; private set => Set(ref _myCode, value); }
    public string FriendCodeInput { get => _friendCodeInput; set => Set(ref _friendCodeInput, value); }
    public string FriendAddressInput { get => _friendAddressInput; set => Set(ref _friendAddressInput, value); }
    public string ConnectError { get => _connectError; private set { if (Set(ref _connectError, value)) OnPropertyChanged(nameof(HasConnectError)); } }
    public bool HasConnectError => ConnectError.Length > 0;
    public string LinkStatus { get => _linkStatus; private set => Set(ref _linkStatus, value); }
    public Brush LinkStatusBrush { get => _linkStatusBrush; private set => Set(ref _linkStatusBrush, value); }
    public bool CanDisconnect { get => _canDisconnect; private set => Set(ref _canDisconnect, value); }
    public string CopyText { get => _copyText; private set => Set(ref _copyText, value); }

    private void CopyCode()
    {
        try
        {
            Clipboard.SetText(MyCode);
            CopyText = "Copied!";
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            reset.Tick += (_, _) => { CopyText = "Copy code"; reset.Stop(); };
            reset.Start();
        }
        catch (COMException)
        {
            CopyText = "Clipboard busy, try again";
        }
    }

    private void Connect()
    {
        ConnectError = _engine.Connect(FriendCodeInput, FriendAddressInput) ?? "";
    }

    // ---------------- Settings ----------------

    public IReadOnlyList<DeviceItem> Microphones { get; }
    public IReadOnlyList<DeviceItem> Outputs { get; }

    private void Update(Func<AppSettings, AppSettings> change, params string[] alsoChanged)
    {
        _engine.UpdateSettings(change(S));
        foreach (var name in SettingsProperties.Concat(alsoChanged)) OnPropertyChanged(name);
    }

    private static readonly string[] SettingsProperties =
    [
        nameof(VoiceActivation), nameof(PushToTalkMode), nameof(Threshold), nameof(ThresholdText),
        nameof(FriendVolume), nameof(FriendVolumeText), nameof(Near), nameof(NearText), nameof(Far), nameof(FarText),
        nameof(Floor), nameof(FloorText),
    ];

    public DeviceItem SelectedMic
    {
        get => Microphones.FirstOrDefault(d => d.Id == S.MicDeviceId) ?? Microphones[0];
        set { if (value is not null && value.Id != S.MicDeviceId) Update(s => s with { MicDeviceId = value.Id }); }
    }

    public DeviceItem SelectedOutput
    {
        get => Outputs.FirstOrDefault(d => d.Id == S.OutputDeviceId) ?? Outputs[0];
        set { if (value is not null && value.Id != S.OutputDeviceId) Update(s => s with { OutputDeviceId = value.Id }); }
    }

    public bool VoiceActivation
    {
        get => S.TalkMode == TalkMode.VoiceActivation;
        set { if (value) Update(s => s with { TalkMode = TalkMode.VoiceActivation }); }
    }

    public bool PushToTalkMode
    {
        get => S.TalkMode == TalkMode.PushToTalk;
        set { if (value) Update(s => s with { TalkMode = TalkMode.PushToTalk }); }
    }

    public double Threshold
    {
        get => S.VoiceThresholdDb;
        set => Update(s => s with { VoiceThresholdDb = (float)value });
    }
    public string ThresholdText => $"{S.VoiceThresholdDb:F0} dB";

    public bool NoiseSuppression
    {
        get => S.NoiseSuppression;
        set => Update(s => s with { NoiseSuppression = value }, nameof(NoiseSuppression));
    }

    public double FriendVolume
    {
        get => S.FriendVolume * 100;
        set => Update(s => s with { FriendVolume = (float)(value / 100) });
    }
    public string FriendVolumeText => $"{S.FriendVolume * 100:F0}%";

    public double Near
    {
        get => S.NearDistance;
        set => Update(s => s with { NearDistance = (float)value, FarDistance = MathF.Max(s.FarDistance, (float)value + 5) });
    }
    public string NearText => $"{S.NearDistance:F0} m";

    public double Far
    {
        get => S.FarDistance;
        set => Update(s => s with { FarDistance = (float)value, NearDistance = MathF.Max(1, MathF.Min(s.NearDistance, (float)value - 5)) });
    }
    public string FarText => $"{S.FarDistance:F0} m";

    public double Floor
    {
        get => S.OutOfRangeVolume * 100;
        set => Update(s => s with { OutOfRangeVolume = (float)(value / 100) });
    }
    public string FloorText => S.OutOfRangeVolume <= 0 ? "silent" : $"{S.OutOfRangeVolume * 100:F0}%";

    public bool RadioEffect
    {
        get => S.RadioEffect;
        set => Update(s => s with { RadioEffect = value }, nameof(RadioEffect));
    }

    public bool FullVolumeOutsideWorld
    {
        get => S.FullVolumeOutsideWorld;
        set => Update(s => s with { FullVolumeOutsideWorld = value }, nameof(FullVolumeOutsideWorld));
    }

    public bool SwapLeftRight
    {
        get => S.SwapLeftRight;
        set => Update(s => s with { SwapLeftRight = value }, nameof(SwapLeftRight));
    }

    public string PushToTalkKeyText => _bindingWhich == nameof(PushToTalkKeyText) ? "Press a key…" : Describe(S.PushToTalkKey);
    public string RadioKeyText => _bindingWhich == nameof(RadioKeyText) ? "Press a key…" : Describe(S.RadioKey);

    private static string Describe(InputBinding? b) =>
        b?.Describe(vk => KeyInterop.KeyFromVirtualKey(vk).ToString()) ?? "Not set";

    private void BeginBinding(string which, Action<InputBinding?> target)
    {
        _bindingWhich = which;
        _bindTarget = target;
        _bindDeadline = DateTime.UtcNow + BindTimeout;
        _bindWaitRelease = true;
        OnPropertyChanged(which);
    }

    private void PollBinding()
    {
        if (_bindTarget is null) return;
        var pressed = _engine.Hotkeys.DetectPressed();
        if (_bindWaitRelease)
        {
            // Wait until everything is released so whatever was already held isn't bound.
            if (pressed is null) _bindWaitRelease = false;
        }
        else if (pressed is not null)
        {
            var target = _bindTarget;
            EndBinding();
            target(pressed is { Kind: InputKind.Key, Code: 0x1B } ? null : pressed); // Esc clears
            return;
        }
        if (DateTime.UtcNow > _bindDeadline) EndBinding();
    }

    private void EndBinding()
    {
        var which = _bindingWhich;
        _bindTarget = null;
        _bindingWhich = null;
        if (which is not null) OnPropertyChanged(which);
    }

    // ---------------- Test & debug ----------------

    private string _beaconMessage = "", _beaconText = "", _debugText = "";
    private bool _showDebug;

    private string BeaconMessage { get => _beaconMessage; set { _beaconMessage = value; Refresh(); } }
    public string BeaconText { get => _beaconText; private set => Set(ref _beaconText, value); }
    public bool ShowDebug { get => _showDebug; set => Set(ref _showDebug, value); }
    public string DebugText { get => _debugText; private set => Set(ref _debugText, value); }
    public string Version => $"SoulsProx v{typeof(ProxChatEngine).Assembly.GetName().Version?.ToString(3)}";

    // ---------------- Live refresh ----------------

    private void Refresh()
    {
        PollBinding();
        var snap = _engine.Game.Latest;
        RefreshMe(snap);
        var friend = _engine.Friend;
        RefreshFriend(friend);
        RefreshMic();
        RefreshConnect(friend);

        BeaconText = _beaconMessage.Length > 0 ? _beaconMessage
            : _engine.Beacon is { } b ? $"Beacon {b.Distance:F1} m away, {b.Gain * 100:F0}% volume"
            : _engine.BeaconActive ? "Beacon placed (not in game world)" : "";

        if (ShowDebug) DebugText = BuildDebug(snap, friend);
    }

    private void RefreshMe(GameSnapshot? snap)
    {
        if (snap is { InWorld: true } && snap.LocalName.Length > 0) _lastMyName = snap.LocalName;
        string name = _lastMyName.Length > 0 ? _lastMyName : "You";
        MyName = name;
        MyInitial = Initial(name);
        MyFill = _lastMyName.Length > 0 ? ColorFor(name) : GreyFill;
        MyTalking = _engine.Transmitting;

        (GameText, GameWaiting) = snap switch
        {
            null => ("Waiting for game", true),
            { InWorld: false } => (Short(snap.Game) + " · menu", false),
            _ => (Short(snap.Game), false),
        };
    }

    private void RefreshFriend(FriendStatus f)
    {
        if (f.Report is { Name.Length: > 0 } r) _lastFriendName = r.Name;
        ShowFriendSlot = f.Link == LinkState.Idle;
        ShowFriend = f.Link != LinkState.Idle;
        FriendConnected = f.Link == LinkState.Connected;

        if (f.Link == LinkState.Connecting)
        {
            FriendName = _lastFriendName.Length > 0 ? _lastFriendName : "Friend";
            FriendInitial = Initial(FriendName);
            FriendFill = GreyFill;
            FriendTalking = false;
            FriendDetail = "Connecting…";
            FriendBarWidth = 0;
            SetBadge("", WarnBrush);
            return;
        }

        string name = _lastFriendName.Length > 0 ? _lastFriendName : "Friend";
        FriendName = name;
        FriendInitial = Initial(name);
        FriendFill = _lastFriendName.Length > 0 ? ColorFor(name) : GreyFill;
        FriendTalking = f.Speaking;
        FriendRing = f.Radio ? RadioBrush : TalkBrush;
        FriendDetail = f.Spatial.Source switch
        {
            SpatialSource.Radio => "On the radio",
            SpatialSource.OutsideWorld => "Not both in game · normal chat",
            SpatialSource.OutOfRange => "Out of range",
            _ => $"{f.Spatial.Distance:F0} m away",
        };
        FriendBarWidth = FriendBarMax * Math.Clamp(f.Spatial.Gain, 0, 1);
        if (f.Radio) SetBadge("", RadioBrush);
        else ClearBadge();
    }

    private void SetBadge(string glyph, Brush brush)
    {
        FriendBadgeGlyph = glyph;
        FriendBadgeBrush = brush;
        FriendBadge = true;
    }

    private void ClearBadge() => FriendBadge = false;

    private void RefreshMic()
    {
        MicLevelDb = _engine.MicLevelDb;
        Transmitting = _engine.Transmitting;
        if (!_engine.MicOk && _engine.MicStatus.Length > 0)
        {
            TalkHint = "Mic unavailable";
            TalkHintBrush = ErrorBrush;
        }
        else if (S.TalkMode == TalkMode.PushToTalk)
        {
            TalkHint = S.PushToTalkKey is null ? "Set a push-to-talk key" : $"Hold {Describe(S.PushToTalkKey)}";
            TalkHintBrush = S.PushToTalkKey is null ? WarnBrush : MutedBrush;
        }
        else
        {
            TalkHint = "Voice activity";
            TalkHintBrush = MutedBrush;
        }
    }

    private void RefreshConnect(FriendStatus f)
    {
        MyCode = _engine.Link.MyCode.ToString();
        CanDisconnect = f.Link != LinkState.Idle;
        (LinkStatus, LinkStatusBrush) = f.Link switch
        {
            LinkState.Connected => ($"Connected to {f.Endpoint}", TalkBrush),
            LinkState.Connecting => ("Waiting for your friend… they need to paste your code too.", WarnBrush),
            _ => (_engine.Link.NatNote.Length > 0 ? _engine.Link.NatNote : "Not connected", SubtleBrush),
        };
    }

    private string BuildDebug(GameSnapshot? snap, FriendStatus f)
    {
        var lines = new List<string>
        {
            $"Monitor: {_engine.Game.Status}",
            $"Mic: {_engine.MicStatus}",
            $"Output: {_engine.OutputStatus}",
            $"UDP {_engine.Link.LocalPort}  LAN {_engine.Link.LanEndpoint}  public {_engine.Link.PublicEndpoint?.ToString() ?? "?"}",
            $"Link: {f.Link} {f.Endpoint}",
            $"Friend: {f.Report}",
            $"Spatial: {f.Spatial}",
        };
        if (snap is { InWorld: true })
        {
            lines.Add($"Me: '{snap.LocalName}' {snap.LocalPosition} fwd {snap.ListenerForward}");
            foreach (var r in snap.Remotes) lines.Add($"In world: '{r.Name}' {r.Position}");
        }
        foreach (var (k, v) in _engine.Game.Diagnostics()) lines.Add($"{k}: {v}");
        return string.Join(Environment.NewLine, lines);
    }

    // ---------------- Helpers ----------------

    private static string Short(GameId game) => game switch
    {
        GameId.DarkSoulsRemastered => "DARK SOULS R",
        GameId.DarkSouls2 => "DARK SOULS II",
        GameId.DarkSouls3 => "DARK SOULS III",
        GameId.EldenRing => "ELDEN RING",
        _ => "NO GAME",
    };

    private static string Initial(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? "?" : char.ToUpperInvariant(trimmed[0]).ToString();
    }

    private static Brush ColorFor(string name)
    {
        // Stable across runs (string.GetHashCode isn't).
        uint hash = 2166136261;
        foreach (char c in name) hash = (hash ^ c) * 16777619;
        return AvatarColors[hash % (uint)AvatarColors.Length];
    }

    private static Brush MakeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
