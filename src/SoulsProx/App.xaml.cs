using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using SoulsProx.Settings;

namespace SoulsProx.App;

/// <summary>
/// Command line (for testing two copies on one PC):
///   --profile NAME   use a separate settings file (settings-NAME.json)
///   --port N         use a different UDP port
/// </summary>
public partial class App : Application
{
    private ProxChatEngine? _engine;
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? profile = null;
        int? port = null;
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--profile") profile = e.Args[i + 1];
            if (e.Args[i] == "--port") port = int.Parse(e.Args[i + 1], CultureInfo.InvariantCulture);
        }
        string title = string.IsNullOrEmpty(profile) ? "SoulsProx" : $"SoulsProx ({profile})";

        // Two copies sharing one identity confuse the friend's side (they'd bounce between both),
        // so a second launch just brings the existing window forward.
        _singleInstance = new Mutex(true, $@"Local\SoulsProx-{profile ?? "default"}", out bool firstInstance);
        if (!firstInstance)
        {
            BringExistingWindowForward(title);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        Log.Init(AppSettings.Directory, string.IsNullOrEmpty(profile) ? "log.txt" : $"log-{profile}.txt");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("UI thread", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled", args.ExceptionObject as Exception);

        var settingsPath = AppSettings.FilePath(profile);
        _engine = new ProxChatEngine(AppSettings.Load(settingsPath), settingsPath, port);

        var window = new MainWindow(new MainViewModel(_engine)) { Title = title };
        MainWindow = window;
        window.Show();
        _ = _engine.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _engine?.Dispose();
        if (_singleInstance is not null)
        {
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    private static void BringExistingWindowForward(string title)
    {
        int me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("SoulsProx"))
        {
            using (p)
            {
                if (p.Id == me || p.MainWindowHandle == 0 || p.MainWindowTitle != title) continue;
                if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9 /* SW_RESTORE */);
                SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
}
