using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace SoulsProx.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        SourceInitialized += (_, _) => RoundCorners();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc backs out of a panel (unless it's being bound as a shortcut, where Esc means "clear").
        if (e.Key == Key.Escape && !_vm.IsBindingKey && _vm.AnyOverlayOpen)
        {
            _vm.CloseOverlays();
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Ask Windows 11 for rounded corners on our borderless window (ignored on Windows 10).</summary>
    private void RoundCorners()
    {
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        int round = 2; // DWMWCP_ROUND
        var hwnd = new WindowInteropHelper(this).Handle;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
