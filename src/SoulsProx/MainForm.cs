using System.ComponentModel;
using System.Diagnostics;
using SoulsProx.Audio;
using SoulsProx.Games;
using SoulsProx.Input;
using SoulsProx.Net;
using SoulsProx.Proximity;
using SoulsProx.Settings;
using LinkState = SoulsProx.Net.LinkState;

namespace SoulsProx.App;

internal sealed class MainForm : Form
{
    private const int ContentWidth = 600;

    private readonly ProxChatEngine _engine;
    private readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 100 };
    private bool _populating;

    // Status
    private readonly Label _gameStatus = ValueLabel();
    private readonly Label _friendStatus = ValueLabel();

    // Connection
    private readonly TextBox _myCode = new() { ReadOnly = true, Width = 360 };
    private readonly TextBox _friendCode = new() { Width = 360, PlaceholderText = "Paste your friend's SPX-… code here" };
    private readonly TextBox _friendAddress = new() { Width = 220, PlaceholderText = "optional, e.g. 100.64.1.2:47800" };
    private readonly Label _linkStatus = ValueLabel();

    // Microphone
    private readonly ComboBox _mic = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    private readonly LevelMeter _meter = new() { Width = 340, Height = 18 };
    private readonly RadioButton _voiceActivation = new() { Text = "Voice activation", AutoSize = true };
    private readonly RadioButton _pushToTalk = new() { Text = "Push to talk", AutoSize = true };
    private readonly TrackBar _threshold = Slider(-70, -10);
    private readonly Label _thresholdValue = ValueLabel();
    private readonly Button _pttKey = new() { AutoSize = true };
    private readonly Button _radioKey = new() { AutoSize = true };
    private readonly CheckBox _noiseSuppression = new() { Text = "Noise suppression (Windows voice processing)", AutoSize = true };

    // Hearing
    private readonly ComboBox _output = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    private readonly TrackBar _volume = Slider(0, 200);
    private readonly Label _volumeValue = ValueLabel();
    private readonly TrackBar _near = Slider(1, 30);
    private readonly Label _nearValue = ValueLabel();
    private readonly TrackBar _far = Slider(5, 150);
    private readonly Label _farValue = ValueLabel();
    private readonly TrackBar _floor = Slider(0, 50);
    private readonly Label _floorValue = ValueLabel();
    private readonly CheckBox _radioEffect = new() { Text = "Walkie-talkie sound on radio", AutoSize = true };
    private readonly CheckBox _swap = new() { Text = "Swap left/right", AutoSize = true };
    private readonly CheckBox _outsideWorld = new() { Text = "Normal voice chat in menus / loading screens", AutoSize = true };

    // Test + debug
    private readonly Button _dropBeacon = new() { Text = "Drop test beacon here", AutoSize = true };
    private readonly Button _clearBeacon = new() { Text = "Remove beacon", AutoSize = true };
    private readonly Label _beaconStatus = ValueLabel();
    private readonly CheckBox _showDebug = new() { Text = "Show debug info", AutoSize = true };
    private readonly TextBox _debug = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = ContentWidth - 30, Height = 220, Visible = false, Font = new Font(FontFamily.GenericMonospace, 8.5f) };

    private Button? _bindingButton;
    private Action<InputBinding?>? _bindingTarget;
    private long _bindingDeadline;
    private bool _bindingWaitRelease;

    public MainForm(ProxChatEngine engine)
    {
        _engine = engine;
        Text = "SoulsProx — proximity voice chat";
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(ContentWidth + 40, 860);
        MinimumSize = new Size(ContentWidth + 60, 400);

        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(10),
        };
        root.Controls.Add(BuildStatus());
        root.Controls.Add(BuildConnection());
        root.Controls.Add(BuildMicrophone());
        root.Controls.Add(BuildHearing());
        root.Controls.Add(BuildTest());
        Controls.Add(root);

        PopulateFromSettings();
        WireEvents();
        _uiTimer.Tick += (_, _) => RefreshStatus();
        _uiTimer.Start();
    }

    // ---------- Layout ----------

    private GroupBox BuildStatus()
    {
        var grid = Grid();
        AddRow(grid, "Game", _gameStatus);
        AddRow(grid, "Friend", _friendStatus);
        return Group("Status", grid);
    }

    private GroupBox BuildConnection()
    {
        var grid = Grid();
        var copy = new Button { Text = "Copy", AutoSize = true };
        copy.Click += (_, _) =>
        {
            if (_myCode.Text.Length > 0) Clipboard.SetText(_myCode.Text);
        };
        AddRow(grid, "Your code", Row(_myCode, copy));

        var connect = new Button { Text = "Connect", AutoSize = true };
        var disconnect = new Button { Text = "Disconnect", AutoSize = true };
        connect.Click += (_, _) => OnConnect();
        disconnect.Click += (_, _) => _engine.Disconnect();
        AddRow(grid, "Friend's code", _friendCode);
        AddRow(grid, "", Row(connect, disconnect));
        AddRow(grid, "Friend address", Row(_friendAddress, Hint("only if codes don't connect")));
        AddRow(grid, "", _linkStatus);
        return Group("Connect (swap codes with your friend, e.g. over Discord)", grid);
    }

    private GroupBox BuildMicrophone()
    {
        var grid = Grid();
        AddRow(grid, "Microphone", _mic);
        AddRow(grid, "Level", _meter);
        var modes = Row(_voiceActivation, _pushToTalk);
        AddRow(grid, "Talk with", modes);
        AddRow(grid, "Sensitivity", Row(_threshold, _thresholdValue));
        AddRow(grid, "Push-to-talk key", _pttKey);
        AddRow(grid, "Radio key", Row(_radioKey, Hint("hold to be heard anywhere")));
        AddRow(grid, "", _noiseSuppression);
        return Group("Microphone", grid);
    }

    private GroupBox BuildHearing()
    {
        var grid = Grid();
        AddRow(grid, "Output", _output);
        AddRow(grid, "Friend volume", Row(_volume, _volumeValue));
        AddRow(grid, "Full volume within", Row(_near, _nearValue));
        AddRow(grid, "Silent beyond", Row(_far, _farValue));
        AddRow(grid, "Far-away volume", Row(_floor, _floorValue));
        AddRow(grid, "", _radioEffect);
        AddRow(grid, "", _outsideWorld);
        AddRow(grid, "", _swap);
        return Group("Hearing", grid);
    }

    private GroupBox BuildTest()
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        panel.Controls.Add(Hint("Solo test: drop a beacon, then talk. You'll hear yourself from that spot. Walk away and turn the camera."));
        panel.Controls.Add(Row(_dropBeacon, _clearBeacon, _beaconStatus));
        var openLog = new LinkLabel { Text = "Open log folder", AutoSize = true };
        openLog.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo { FileName = AppSettings.Directory, UseShellExecute = true });
        panel.Controls.Add(Row(_showDebug, openLog));
        panel.Controls.Add(_debug);
        return Group("Test && debug", panel);
    }

    // ---------- Settings <-> UI ----------

    private void PopulateFromSettings()
    {
        _populating = true;
        var s = _engine.Settings;

        FillDevices(_mic, AudioDevices.Microphones(), s.MicDeviceId);
        FillDevices(_output, AudioDevices.Outputs(), s.OutputDeviceId);
        _voiceActivation.Checked = s.TalkMode == TalkMode.VoiceActivation;
        _pushToTalk.Checked = s.TalkMode == TalkMode.PushToTalk;
        _threshold.Value = Math.Clamp((int)s.VoiceThresholdDb, _threshold.Minimum, _threshold.Maximum);
        _noiseSuppression.Checked = s.NoiseSuppression;
        _volume.Value = Math.Clamp((int)(s.FriendVolume * 100), _volume.Minimum, _volume.Maximum);
        _near.Value = Math.Clamp((int)s.NearDistance, _near.Minimum, _near.Maximum);
        _far.Value = Math.Clamp((int)s.FarDistance, _far.Minimum, _far.Maximum);
        _floor.Value = Math.Clamp((int)(s.OutOfRangeVolume * 100), _floor.Minimum, _floor.Maximum);
        _radioEffect.Checked = s.RadioEffect;
        _swap.Checked = s.SwapLeftRight;
        _outsideWorld.Checked = s.FullVolumeOutsideWorld;
        _friendCode.Text = s.FriendCode ?? "";
        _friendAddress.Text = s.FriendAddressOverride ?? "";
        UpdateValueLabels();
        UpdateBindingButtons();
        _populating = false;
    }

    private static void FillDevices(ComboBox box, IReadOnlyList<AudioDeviceInfo> devices, string? selectedId)
    {
        box.Items.Clear();
        box.Items.Add(new DeviceItem(null, "Windows default"));
        foreach (var d in devices) box.Items.Add(new DeviceItem(d.Id, d.Name));
        box.SelectedIndex = Math.Max(0, box.Items.Cast<DeviceItem>().ToList().FindIndex(i => i.Id == selectedId));
    }

    private void WireEvents()
    {
        _mic.SelectedIndexChanged += (_, _) => Save(s => s with { MicDeviceId = ((DeviceItem)_mic.SelectedItem!).Id });
        _output.SelectedIndexChanged += (_, _) => Save(s => s with { OutputDeviceId = ((DeviceItem)_output.SelectedItem!).Id });
        _voiceActivation.CheckedChanged += (_, _) => Save(s => s with { TalkMode = _pushToTalk.Checked ? TalkMode.PushToTalk : TalkMode.VoiceActivation });
        _threshold.ValueChanged += (_, _) => Save(s => s with { VoiceThresholdDb = _threshold.Value });
        _noiseSuppression.CheckedChanged += (_, _) => Save(s => s with { NoiseSuppression = _noiseSuppression.Checked });
        _volume.ValueChanged += (_, _) => Save(s => s with { FriendVolume = _volume.Value / 100f });
        _near.ValueChanged += (_, _) =>
        {
            if (_far.Value <= _near.Value) _far.Value = Math.Min(_far.Maximum, _near.Value + 5);
            Save(s => s with { NearDistance = _near.Value, FarDistance = _far.Value });
        };
        _far.ValueChanged += (_, _) =>
        {
            if (_near.Value >= _far.Value) _near.Value = Math.Max(_near.Minimum, _far.Value - 5);
            Save(s => s with { NearDistance = _near.Value, FarDistance = _far.Value });
        };
        _floor.ValueChanged += (_, _) => Save(s => s with { OutOfRangeVolume = _floor.Value / 100f });
        _radioEffect.CheckedChanged += (_, _) => Save(s => s with { RadioEffect = _radioEffect.Checked });
        _swap.CheckedChanged += (_, _) => Save(s => s with { SwapLeftRight = _swap.Checked });
        _outsideWorld.CheckedChanged += (_, _) => Save(s => s with { FullVolumeOutsideWorld = _outsideWorld.Checked });

        _pttKey.Click += (_, _) => BeginBinding(_pttKey, b => Save(s => s with { PushToTalkKey = b }));
        _radioKey.Click += (_, _) => BeginBinding(_radioKey, b => Save(s => s with { RadioKey = b }));

        _dropBeacon.Click += (_, _) =>
        {
            if (!_engine.DropBeacon())
                MessageBox.Show(this, "Load into the game world first. The beacon is placed where your character is standing.", "SoulsProx");
        };
        _clearBeacon.Click += (_, _) => _engine.ClearBeacon();
        _showDebug.CheckedChanged += (_, _) => _debug.Visible = _showDebug.Checked;
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        UpdateValueLabels();
        if (_populating) return;
        _engine.UpdateSettings(change(_engine.Settings));
        UpdateBindingButtons();
    }

    private void UpdateValueLabels()
    {
        _thresholdValue.Text = $"{_threshold.Value} dB";
        _volumeValue.Text = $"{_volume.Value}%";
        _nearValue.Text = $"{_near.Value} m";
        _farValue.Text = $"{_far.Value} m";
        _floorValue.Text = _floor.Value == 0 ? "silent" : $"{_floor.Value}%";
        _meter.ThresholdDb = _threshold.Value;
        _meter.ShowThreshold = _voiceActivation.Checked;
    }

    private void UpdateBindingButtons()
    {
        if (_bindingButton is not null) return;
        _pttKey.Text = Describe(_engine.Settings.PushToTalkKey) + "  (click to change)";
        _radioKey.Text = Describe(_engine.Settings.RadioKey) + "  (click to change)";
    }

    private static string Describe(InputBinding? b) => b?.Describe(vk => ((Keys)vk).ToString()) ?? "Not set";

    // ---------- Key binding ----------

    private void BeginBinding(Button button, Action<InputBinding?> target)
    {
        _bindingButton = button;
        _bindingTarget = target;
        _bindingDeadline = Environment.TickCount64 + 6000;
        _bindingWaitRelease = true;
        button.Text = "Press a key, mouse button or controller button… (Esc clears)";
    }

    private void PollBinding()
    {
        if (_bindingButton is null) return;
        var pressed = _engine.Hotkeys.DetectPressed();
        if (_bindingWaitRelease)
        {
            // Wait until everything is released so we don't bind whatever was already held.
            if (pressed is null) _bindingWaitRelease = false;
        }
        else if (pressed is not null)
        {
            var target = _bindingTarget!;
            EndBinding();
            target(pressed is { Kind: InputKind.Key, Code: 0x1B } ? null : pressed);
            return;
        }
        if (Environment.TickCount64 > _bindingDeadline) EndBinding();
    }

    private void EndBinding()
    {
        _bindingButton = null;
        _bindingTarget = null;
        UpdateBindingButtons();
    }

    // ---------- Actions ----------

    private void OnConnect()
    {
        var error = _engine.Connect(_friendCode.Text, _friendAddress.Text);
        if (error is not null) MessageBox.Show(this, error, "SoulsProx", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ---------- Live status ----------

    private void RefreshStatus()
    {
        PollBinding();

        var code = _engine.Link.MyCode.ToString();
        if (_myCode.Text != code) _myCode.Text = code;

        _meter.LevelDb = _engine.MicLevelDb;
        _meter.Transmitting = _engine.Transmitting;
        _meter.Invalidate();

        var snap = _engine.Game.Latest;
        _gameStatus.Text = snap switch
        {
            null => _engine.Game.Status,
            { InWorld: false } => $"{snap.Game.DisplayName()}: in a menu or loading screen",
            _ => $"{snap.Game.DisplayName()}: playing as \"{snap.LocalName}\"",
        };

        var f = _engine.Friend;
        _friendStatus.Text = DescribeFriend(f);
        _linkStatus.Text = DescribeLink(f);

        _beaconStatus.Text = _engine.Beacon is { } b
            ? $"{b.Distance:F1} m away, {b.Gain * 100:F0}% volume"
            : _engine.BeaconActive ? "(not in game world)" : "";

        if (_debug.Visible) RefreshDebug(snap, f);
    }

    private static string DescribeFriend(FriendStatus f)
    {
        if (f.Link != LinkState.Connected) return "Not connected";
        var name = f.Report is { Name.Length: > 0 } r ? $"\"{r.Name}\"" : "Friend";
        var talking = f.Radio ? " (radio)" : f.Speaking ? " (talking)" : "";
        var where = f.Spatial.Source switch
        {
            SpatialSource.OutsideWorld => "not both in the game world, normal chat",
            SpatialSource.OutOfRange => "out of range",
            SpatialSource.Radio => "on the radio",
            _ => $"{f.Spatial.Distance:F0} m away",
        };
        return $"{name}: {where}, {f.Spatial.Gain * 100:F0}% volume{talking}";
    }

    private string DescribeLink(FriendStatus f) => f.Link switch
    {
        LinkState.Connected => $"Connected to {f.Endpoint}",
        LinkState.Connecting => "Trying to reach your friend… (they need to paste your code too)",
        _ => string.IsNullOrEmpty(_engine.Link.NatNote) ? "Not connected" : _engine.Link.NatNote,
    };

    private void RefreshDebug(GameSnapshot? snap, FriendStatus f)
    {
        var lines = new List<string>
        {
            $"Monitor: {_engine.Game.Status}",
            $"Mic: {_engine.MicStatus}",
            $"Output: {_engine.OutputStatus}",
            $"UDP port {_engine.Link.LocalPort}, LAN {_engine.Link.LanEndpoint}, public {_engine.Link.PublicEndpoint?.ToString() ?? "?"}",
            $"Link: {f.Link} {f.Endpoint}",
            $"Friend report: {f.Report}",
            $"Spatial: {f.Spatial}",
        };
        if (snap is { InWorld: true })
        {
            lines.Add($"Me: '{snap.LocalName}' {snap.LocalPosition} forward {snap.ListenerForward}");
            foreach (var r in snap.Remotes) lines.Add($"In world: '{r.Name}' {r.Position}");
        }
        foreach (var (k, v) in _engine.Game.Diagnostics()) lines.Add($"{k}: {v}");
        var text = string.Join(Environment.NewLine, lines);
        if (_debug.Text != text) _debug.Text = text;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _uiTimer.Stop();
        base.OnFormClosed(e);
    }

    // ---------- Small helpers ----------

    private sealed record DeviceItem(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private static Label ValueLabel() => new() { AutoSize = true, Anchor = AnchorStyles.Left };

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, MaximumSize = new Size(ContentWidth - 30, 0) };

    private static TrackBar Slider(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = 260,
        TickStyle = TickStyle.None,
        AutoSize = false,
        Height = 28,
    };

    private static TableLayoutPanel Grid()
    {
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return grid;
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) }, 0, row);
        control.Anchor = AnchorStyles.Left;
        grid.Controls.Add(control, 1, row);
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        foreach (var c in controls)
        {
            c.Anchor = AnchorStyles.Left;
            row.Controls.Add(c);
        }
        return row;
    }

    private static GroupBox Group(string title, Control content)
    {
        var box = new GroupBox
        {
            Text = title,
            AutoSize = true,
            MinimumSize = new Size(ContentWidth, 0),
            MaximumSize = new Size(ContentWidth, 0),
            Padding = new Padding(8),
        };
        content.Dock = DockStyle.Fill;
        box.Controls.Add(content);
        return box;
    }
}

/// <summary>Mic level bar with the voice-activation threshold marked.</summary>
internal sealed class LevelMeter : Control
{
    private const float MinDb = -70f, MaxDb = 0f;

    public LevelMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float LevelDb { get; set; } = -100f;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float ThresholdDb { get; set; } = -45f;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowThreshold { get; set; } = true;
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Transmitting { get; set; }

    private float ToX(float db) => (Math.Clamp(db, MinDb, MaxDb) - MinDb) / (MaxDb - MinDb) * (Width - 1);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(SystemColors.ControlDark);
        using var fill = new SolidBrush(Transmitting ? Color.FromArgb(76, 175, 80) : Color.FromArgb(120, 144, 156));
        g.FillRectangle(fill, 0, 0, ToX(LevelDb), Height);
        if (ShowThreshold)
        {
            float x = ToX(ThresholdDb);
            using var pen = new Pen(Color.FromArgb(255, 193, 7), 2);
            g.DrawLine(pen, x, 0, x, Height);
        }
        g.DrawRectangle(SystemPens.ControlDarkDark, 0, 0, Width - 1, Height - 1);
    }
}
