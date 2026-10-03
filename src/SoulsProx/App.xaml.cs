using System.Globalization;
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

        Log.Init(AppSettings.Directory, string.IsNullOrEmpty(profile) ? "log.txt" : $"log-{profile}.txt");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("UI thread", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled", args.ExceptionObject as Exception);

        var settingsPath = AppSettings.FilePath(profile);
        _engine = new ProxChatEngine(AppSettings.Load(settingsPath), settingsPath, port);

        var window = new MainWindow(new MainViewModel(_engine));
        if (!string.IsNullOrEmpty(profile)) window.Title = $"SoulsProx ({profile})";
        MainWindow = window;
        window.Show();
        _ = _engine.StartAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _engine?.Dispose();
        base.OnExit(e);
    }
}
