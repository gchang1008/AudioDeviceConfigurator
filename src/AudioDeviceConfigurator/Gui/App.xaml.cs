using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AudioDeviceConfigurator.Gui;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "audio-device-configurator.crash.log");
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:O}] unhandled {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}\n\n");
        }
        catch
        {
        }

        MessageBox.Show(
            $"An unexpected error occurred and was logged:\n\n{e.Exception.Message}",
            "AudioDeviceConfigurator",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}