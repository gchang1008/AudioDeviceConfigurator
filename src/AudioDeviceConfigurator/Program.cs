using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Cli;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator;

public static class Program
{
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("ERROR: This application only runs on Windows 10/11 x64.");
            return (int)ExitCode.SystemError;
        }

        using var cancellation = new CancellationTokenSource();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.CancelKeyPress += (_, e) =>
        {
            // Take over Ctrl+C so the original default format can still be restored.
            e.Cancel = true;
            cancellation.Cancel();
        };

        var environment = new AppEnvironment(
            Displays: new WindowsDisplayProvider(),
            Endpoints: new CoreAudioEndpointProvider(),
            Wasapi: new WasapiFormatProbe(),
            ProcessRunner: new SystemProcessRunner(),
            FileSystem: new SystemFileSystem(),
            Clock: new SystemClock(),
            Console: new SystemConsole(),
            SystemInfo: new SystemInfoProvider(),
            ApplicationDirectory: AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));

        var runner = new ValidationRunner(environment, cancellation.Token);
        return (int)runner.Run(CliOptions.Parse(args));
    }
}
