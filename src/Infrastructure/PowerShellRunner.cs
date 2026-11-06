using System.Diagnostics;
using System.Text;
using Pome.Core;

namespace Pome.Infrastructure;

/// <summary>Only embedded, allowlisted programs are executable. Data is UTF-8 JSON inside base64, never shell text.</summary>
public sealed class PowerShellRunner
{
    public async Task<string> RunAsync(string script, object? data = null, CancellationToken cancellationToken = default)
    {
        if (script is not ("Discover" or "ListVms" or "CreateVm" or "Lifecycle" or "EnableHyperV" or "ManageVm" or "CollectBootLogs" or "GrantHyperVAccess" or "VirtualGpuMedia")) throw new ArgumentException("Unknown operation.");
        using var resource = typeof(PowerShellRunner).Assembly.GetManifestResourceStream($"Pome.Infrastructure.Scripts.{script}.ps1")
            ?? throw new InvalidOperationException("Missing embedded program.");
        using var reader = new StreamReader(resource);
        var program = await reader.ReadToEndAsync(cancellationToken);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Write(data)));
        var command = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; "
            + "$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + payload + "')) | ConvertFrom-Json; try { " + program
            + " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Windows PowerShell did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        var output = await stdout; var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{script} failed: {Redaction.Clean(error.Trim())}");
        return output.Trim().TrimStart('\uFEFF');
    }
}

public sealed class WindowsHostDiscovery(PowerShellRunner runner) : IHostDiscovery
{
    public async Task<HostInfo> DiscoverAsync(CancellationToken cancellationToken = default) => Json.Read<HostInfo>(await runner.RunAsync("Discover", cancellationToken: cancellationToken));
}
public sealed class HyperVManager(PowerShellRunner runner) : IVirtualMachineManager
{
    public async Task<IReadOnlyList<VmInfo>> ListAsync(CancellationToken cancellationToken = default) => Json.Read<VmInfo[]>(await runner.RunAsync("ListVms", cancellationToken: cancellationToken));
}
