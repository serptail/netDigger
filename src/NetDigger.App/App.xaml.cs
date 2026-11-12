using System.Windows;
using System.Windows.Threading;

namespace NetDigger.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "netDigger", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
