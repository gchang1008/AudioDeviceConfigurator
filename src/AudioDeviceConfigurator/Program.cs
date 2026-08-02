using AudioDeviceConfigurator.Application;
using AudioDeviceConfigurator.Audio;
using AudioDeviceConfigurator.Cli;
using AudioDeviceConfigurator.Domain;
using AudioDeviceConfigurator.Gui;
using AudioDeviceConfigurator.Svcl;
using AudioDeviceConfigurator.Windows;

namespace AudioDeviceConfigurator;

public static class Program
{
    [STAThread]
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

        if (ShouldLaunchGui(args))
        {
            return LaunchGui(args);
        }

        return LaunchCli(args);
    }

    private static bool ShouldLaunchGui(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--cli", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-?", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--list", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--device-id", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--monitor-id", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static int LaunchCli(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.CancelKeyPress += (_, e) =>
        {
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

    private static int LaunchGui(string[] args)
    {
        var appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var fileSystem = new SystemFileSystem();
        var endpointProvider = new CoreAudioEndpointProvider();
        var controlPanel = new ControlPanelFormatProvider();
        var svcl = new SvclClient(
            new SystemProcessRunner(),
            fileSystem,
            Path.Combine(appDirectory, "svcl.exe"));
        var service = new DeviceConfigurationService(endpointProvider, controlPanel, svcl);
        var playback = new WasapiAudioPlaybackService();
        var viewModel = new MainViewModel(
            service,
            playback,
            resolveWaveSource: _ => WaveSource.Parse(File.ReadAllBytes(Path.Combine(appDirectory, "test_audio.wav"))));
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow(viewModel);
        app.MainWindow = window;
        return app.Run(window);
    }
}