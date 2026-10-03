using System.Globalization;
using SoulsProx.Settings;

namespace SoulsProx.App;

internal static class Program
{
    /// <summary>
    /// Command line (for testing two copies on one PC):
    ///   --profile NAME   use a separate settings file (settings-NAME.json)
    ///   --port N         use a different UDP port
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        string? profile = null;
        int? port = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--profile") profile = args[i + 1];
            if (args[i] == "--port") port = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
        }

        Log.Init(AppSettings.Directory, string.IsNullOrEmpty(profile) ? "log.txt" : $"log-{profile}.txt");
        Application.ThreadException += (_, e) => Log.Error("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // Follow the Windows light/dark setting.
        Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001

        var settingsPath = AppSettings.FilePath(profile);
        var settings = AppSettings.Load(settingsPath);
        using var engine = new ProxChatEngine(settings, settingsPath, port);
        _ = engine.StartAsync();
        Application.Run(new MainForm(engine));
    }
}
