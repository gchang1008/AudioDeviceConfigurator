using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using AudioDeviceConfigurator.Abstractions;

namespace AudioDeviceConfigurator.Windows;

/// <summary>
/// Reads GPU and HDMI audio driver metadata by running an embedded PowerShell script. The
/// script is written to a temp file because <c>powershell -Command -</c> requires stdin
/// support, which the existing <see cref="IProcessRunner"/> boundary does not expose.
/// PowerShell ships with Windows 10/11, so this does not introduce a third-party NuGet
/// dependency (story 58). The temp file is best-effort cleaned up afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerShellDriverMetadataProvider : IDriverMetadataProvider
{
    private const string ResourceName = "AudioDeviceConfigurator.scripts.Get-DriverInfo.ps1";

    private static readonly TimeSpan PowerShellTimeout = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fileSystem;
    private readonly string _script;

    public PowerShellDriverMetadataProvider(IProcessRunner processRunner, IFileSystem fileSystem)
    {
        _processRunner = processRunner;
        _fileSystem = fileSystem;
        _script = LoadEmbeddedScript();
    }

    public DriverMetadata GetDriverMetadata()
    {
        var scriptPath = _fileSystem.GetTempFilePath(".ps1");
        try
        {
            _fileSystem.WriteAllText(scriptPath, _script);
            var result = _processRunner.Run(
                "powershell.exe",
                new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath },
                PowerShellTimeout);

            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return Empty();
            }

            try
            {
                return Parse(result.StandardOutput);
            }
            catch (JsonException)
            {
                return Empty();
            }
        }
        finally
        {
            _fileSystem.DeleteFile(scriptPath);
        }
    }

    private static string LoadEmbeddedScript()
    {
        var assembly = typeof(PowerShellDriverMetadataProvider).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static DriverMetadata Empty() =>
        new(GpuName: null, GpuDriverVersion: null, GpuDriverProvider: null, AudioHdmi: []);

    private static DriverMetadata Parse(string stdout)
    {
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        string? gpuName = null;
        string? gpuVersion = null;
        string? gpuProvider = null;

        if (root.TryGetProperty("Gpu", out var gpu) && gpu.ValueKind == JsonValueKind.Object)
        {
            gpuName = gpu.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            gpuVersion = gpu.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            gpuProvider = gpu.TryGetProperty("Provider", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }

        var audio = new List<AudioHdmiDriver>();
        if (root.TryGetProperty("AudioHdmi", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = item.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (name is null) continue;
                var ver = item.TryGetProperty("Version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var prov = item.TryGetProperty("Provider", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                audio.Add(new AudioHdmiDriver(name, ver, prov));
            }
        }

        return new DriverMetadata(gpuName, gpuVersion, gpuProvider, audio);
    }
}