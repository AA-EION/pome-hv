namespace Pome.Infrastructure;

public static class PythonLocator
{
    public static IReadOnlyList<string> ManagedCandidates(string? cacheRoot = null)
    {
        var localRoot = cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pome");
        return
        [
            Path.Combine(AppContext.BaseDirectory, "python", "venv", "Scripts", "python.exe"),
            Path.Combine(AppContext.BaseDirectory, "python", "python.exe"),
            Path.Combine(localRoot, "python", "venv", "Scripts", "python.exe"),
            Path.Combine(localRoot, "python", "runtime", "python.exe")
        ];
    }

    public static string Find()
    {
        foreach (var candidate in ManagedCandidates())
        {
            try
            {
                if (File.Exists(candidate)) return SafePaths.RequireLocal(candidate);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var path = Path.Combine(directory.Trim('"'), "python.exe");
                if (!path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    return SafePaths.RequireLocal(path);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
        }
        return "";
    }

    public static async Task<string> EnsureManagedAsync(string cacheRoot, CancellationToken ct = default, IProgress<string>? progress = null)
    {
        foreach (var candidate in ManagedCandidates(cacheRoot))
        {
            if (File.Exists(candidate)) return SafePaths.RequireLocal(candidate);
        }
        var pythonRoot = Path.Combine(cacheRoot, "python");
        Directory.CreateDirectory(pythonRoot);
        var archive = Path.Combine(pythonRoot, "python-3.12.10-embed-amd64.zip");
        progress?.Report("Provisioning Pome's isolated Python 3.12 runtime and virtual environment…");
        using (var client = VerifiedDownloader.CreateClient())
        {
            await new VerifiedDownloader(client).DownloadAsync(UpstreamAssets.PythonEmbed, archive, ct);
        }
        var runtimeDir = Path.Combine(pythonRoot, "runtime-" + Guid.NewGuid().ToString("N"));
        SafeArchive.Extract(archive, runtimeDir);
        var venvDir = Path.Combine(pythonRoot, "venv");
        var scriptsDir = Path.Combine(venvDir, "Scripts");
        Directory.CreateDirectory(scriptsDir);
        foreach (var file in Directory.GetFiles(runtimeDir))
        {
            File.Copy(file, Path.Combine(scriptsDir, Path.GetFileName(file)), true);
        }
        await File.WriteAllTextAsync(
            Path.Combine(venvDir, "pyvenv.cfg"),
            $"home = {scriptsDir}\ninclude-system-site-packages = false\nversion = 3.12.10\nexecutable = {Path.Combine(scriptsDir, "python.exe")}\n",
            ct);
        return SafePaths.RequireLocal(Path.Combine(scriptsDir, "python.exe"));
    }
}
