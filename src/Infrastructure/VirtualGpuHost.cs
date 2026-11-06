using System.Diagnostics;
using Microsoft.Win32;
using Pome.Core;

namespace Pome.Infrastructure;

/// <summary>The Hyper-V socket service registration pvg-host needs (administrator, once per computer).</summary>
public static class VirtualGpuService
{
    private static string KeyPath => VirtualGpuPolicy.ServicesKey + @"\" + VirtualGpuPolicy.ServiceId.ToString("D");

    public static bool IsRegistered()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key?.GetValue("ElementName") is string;
    }

    /// <summary>Hyper-V accepts a host connection to a guest service only when its GUID is listed here.</summary>
    public static void Register()
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath, true) ?? throw new IOException("The Hyper-V guest service could not be registered.");
        key.SetValue("ElementName", VirtualGpuPolicy.ServiceName, RegistryValueKind.String);
    }
}

/// <summary>
/// Runs one pvg-host per VM whose virtual GPU is on, as the signed-in user (a Hyper-V Administrators
/// member). pvg-host serves one guest session and exits when the guest disconnects (shutdown,
/// restart), so the launcher starts it again until the VM stops.
/// </summary>
public sealed class VirtualGpuHost : IDisposable
{
    private readonly Dictionary<Guid, Process> running = [];
    private readonly HashSet<Guid> wanted = [];
    private readonly object gate = new();

    /// <summary>pvg-host ships in a "pvg-host" folder next to pome (gpu/host/build-windows.ps1 output).</summary>
    public static string? Locate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "pvg-host", "pvg-host.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>The guest driver package folder (install.sh and its payload) pome copies onto POME-BOOT, if bundled.</summary>
    public static string? LocateGuestPackage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "pvg-guest");
        return File.Exists(Path.Combine(path, "install.sh")) ? path : null;
    }

    public static string LogPath(Guid vmId) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pome", "logs", $"pvg-host-{vmId:N}.log");

    public bool IsRunning(Guid vmId) { lock (gate) return running.TryGetValue(vmId, out var p) && !p.HasExited; }

    /// <summary>Starts serving the VM. Returns a reason when pvg-host is not installed.</summary>
    public string? Ensure(Guid vmId)
    {
        var exe = Locate();
        if (exe is null) return "pvg-host is not installed next to pome; the guest will keep its basic display.";
        lock (gate)
        {
            wanted.Add(vmId);
            if (running.TryGetValue(vmId, out var existing) && !existing.HasExited) return null;
            Launch(exe, vmId);
        }
        return null;
    }

    public void Stop(Guid vmId)
    {
        Process? process;
        lock (gate)
        {
            wanted.Remove(vmId);
            if (!running.Remove(vmId, out process)) return;
        }
        try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
        process.Dispose();
    }

    private void Launch(string exe, Guid vmId)
    {
        var log = LogPath(vmId);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        info.ArgumentList.Add("--vm"); info.ArgumentList.Add(vmId.ToString("D")); info.ArgumentList.Add("-v");
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var writer = new StreamWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        void Write(string? line) { if (line is null) return; lock (writer) writer.WriteLine($"{DateTimeOffset.Now:O} {line}"); }
        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.Exited += (_, _) =>
        {
            Write($"pvg-host exited ({SafeExitCode(process)})");
            writer.Dispose();
            bool again;
            lock (gate) again = wanted.Contains(vmId) && running.TryGetValue(vmId, out var current) && current == process;
            if (!again) return;
            // The guest restarted or shut down. Wait briefly so a crash loop cannot spin.
            Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
            {
                lock (gate) if (wanted.Contains(vmId) && running.TryGetValue(vmId, out var current) && current == process) Launch(exe, vmId);
            });
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        running[vmId] = process;
        Write($"pvg-host started for VM {vmId:D}");
    }

    private static string SafeExitCode(Process p) { try { return p.ExitCode.ToString(); } catch (InvalidOperationException) { return "unknown"; } }

    public void Dispose()
    {
        Guid[] ids;
        lock (gate) ids = [.. running.Keys];
        foreach (var id in ids) Stop(id);
    }
}
