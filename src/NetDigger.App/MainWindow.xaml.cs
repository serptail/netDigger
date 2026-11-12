using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace NetDigger.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Windows 11 only: ask DWM for rounded corners and a border matching the theme. Older versions
        // reject the attributes and simply keep square corners.
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var corner = 2;          // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));
            var border = 0x322C24;   // COLORREF (BGR) of the theme's Line colour #242C32
            DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.Shutdown();
        base.OnClosing(e);
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox { SelectedItem: ResourceRow row })
            _viewModel.OpenUrl(row);
    }
}
