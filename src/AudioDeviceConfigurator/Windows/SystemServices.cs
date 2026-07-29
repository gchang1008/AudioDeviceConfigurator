using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AudioDeviceConfigurator.Abstractions;

namespace AudioDeviceConfigurator.Windows;

public sealed class SystemFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void WriteAllText(string path, string contents) =>
        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing the run over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public string? GetFileVersion(string path)
    {
        // NirSoft's VERSION_INFO stores both FileVersion and ProductVersion as the literal string
        // "1.28"; the structured fields are filled as (1,2,8,0) instead. The product string is what
        // the spec asks for ("SVCL 1.28 or newer"), so prefer it when present.
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!string.IsNullOrEmpty(info.ProductVersion))
        {
            return info.ProductVersion;
        }

        if (info.FileMajorPart == 0 && info.FileMinorPart == 0 && info.FileBuildPart == 0)
        {
            return string.IsNullOrEmpty(info.FileVersion) ? null : info.FileVersion;
        }

        return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
    }

    public string GetTempFilePath(string suffix) =>
        Path.Combine(Path.GetTempPath(), $"adc-format-{Guid.NewGuid():N}{suffix}");
}

public sealed class SystemClock : IClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public DateTimeOffset LocalNow => DateTimeOffset.Now;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
}

public sealed class SystemConsole : IConsole
{
    public void WriteLine(string text = "") => Console.WriteLine(text);

    public void WriteError(string text) => Console.Error.WriteLine(text);

    public string? ReadLine() => Console.ReadLine();
}

public sealed class SystemInfoProvider : ISystemInfoProvider
{
    public SystemInfo GetSystemInfo() => new(
        MachineName: Environment.MachineName,
        OsDescription: RuntimeInformation.OSDescription,
        OsVersion: Environment.OSVersion.Version.ToString(),
        Architecture: RuntimeInformation.OSArchitecture.ToString(),
        UserName: Environment.UserName,
        ApplicationVersion: typeof(SystemInfoProvider).Assembly.GetName().Version?.ToString() ?? "1.0.0");
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public ProcessResult Run(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start '{executablePath}'.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return new ProcessResult(-1, "", $"The process did not exit within {timeout.TotalSeconds:F0} seconds.");
        }

        return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
