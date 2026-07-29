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

        var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var processRunner = new SystemProcessRunner();
        var fileSystem = new SystemFileSystem();
        var driverMetadata = new PowerShellDriverMetadataProvider(processRunner, fileSystem);

        var environment = new AppEnvironment(
            Displays: new WindowsDisplayProvider(driverMetadata),
            Endpoints: new CoreAudioEndpointProvider(driverMetadata),
            Wasapi: new WasapiFormatProbe(),
            ProcessRunner: processRunner,
            FileSystem: fileSystem,
            Clock: new SystemClock(),
            Console: new SystemConsole(),
            SystemInfo: new SystemInfoProvider(),
            DriverMetadata: driverMetadata,
            ApplicationDirectory: appDirectory);

        var runner = new ValidationRunner(environment, cancellation.Token);
        return (int)runner.Run(CliOptions.Parse(args));
    }
}
