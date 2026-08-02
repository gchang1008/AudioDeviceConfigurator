using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Cli;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Svcl;
using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 2 && string.Equals(args[0], "--control-panel-worker", StringComparison.Ordinal))
        {
            return ControlPanelFormatProvider.RunWorker(args[1]);
        }

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
        var fileSystem = new SystemFileSystem();
        var endpointProvider = new CoreAudioEndpointProvider();
        var environment = new AppEnvironment(
            Endpoints: endpointProvider,
            FileSystem: fileSystem,
            Clock: new SystemClock(),
            Console: new SystemConsole(),
            ControlPanelFormats: new ControlPanelFormatProvider(),
            Svcl: new SvclClient(
                new SystemProcessRunner(),
                fileSystem,
                Path.Combine(appDirectory, "svcl.exe")));

        var runner = new ValidationRunner(environment, cancellation.Token);
        return (int)runner.Run(CliOptions.Parse(args));
    }
}
