using System.Diagnostics;

namespace GitPa;

/// <summary>Runs external build and deployment tools; replaced by a fake in tests.</summary>
public interface IToolRunner
{
    /// <summary>Runs the tool attached to the console and returns its exit code.</summary>
    int Run(string fileName, IReadOnlyList<string> args, string workingDirectory);
}

public sealed class ProcessToolRunner : IToolRunner
{
    public int Run(string fileName, IReadOnlyList<string> args, string workingDirectory)
    {
        var info = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory, UseShellExecute = false };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        process.WaitForExit();
        return process.ExitCode;
    }
}

/// <summary>The external tools deploy needs; null when not found.</summary>
public sealed record DeployTools(string? MSBuild, string? Pac, string? NuGet)
{
    public static DeployTools Find(DeployConfig deploy)
    {
        var pac = FindOnPath("pac.cmd", "pac.exe", "pac");
        // The Power Platform CLI ships a nuget.exe next to pac.
        var nuget = FindOnPath("nuget.exe", "nuget")
            ?? (pac is null ? null : Existing(Path.Combine(Path.GetDirectoryName(pac)!, "nuget.exe")));
        return new DeployTools(FindMSBuild(deploy.MSBuild), pac, nuget);
    }

    /// <summary>Visual Studio's MSBuild: the configured path, else via vswhere, else from PATH.</summary>
    static string? FindMSBuild(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Existing(configured);

        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            try
            {
                var info = new ProcessStartInfo(vswhere) { RedirectStandardOutput = true, UseShellExecute = false };
                foreach (var arg in new[] { "-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild",
                             "-find", @"MSBuild\**\Bin\MSBuild.exe" })
                    info.ArgumentList.Add(arg);
                using var process = Process.Start(info)!;
                var found = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries)
                    .FirstOrDefault(line => line.Length > 0);
                process.WaitForExit();
                if (found is not null && File.Exists(found))
                    return found;
            }
            catch (Exception)
            {
                // fall back to PATH
            }
        }
        return FindOnPath("MSBuild.exe", "msbuild");
    }

    static string? FindOnPath(params string[] names) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(dir => names.Select(name => Path.Combine(dir.Trim('"'), name)))
        .FirstOrDefault(File.Exists);

    static string? Existing(string path) => File.Exists(path) ? path : null;
}
