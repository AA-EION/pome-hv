using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pome.Core;

namespace Pome.Infrastructure;

public sealed class PrivilegeBroker(string helperPath) : IPrivilegeBroker
{
    // The UI supplies its own HWND so Windows can associate elevation with the
    // visible app. CLI callers may leave this zero; no foreign window is used.
    public nint OwnerWindow { get; set; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    public async Task<OperationResult> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(helperPath)) throw new FileNotFoundException("The privileged helper is missing. Run tools/Build/Build.ps1 to assemble the app.", helperPath);
        var name = "pome-" + Guid.NewGuid().ToString("N");
        await using var pipe = UserPipe.CreateServer(name);
        var start = new ProcessStartInfo(helperPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, ErrorDialogParentHandle = OwnerWindow };
        start.Arguments = $"--pipe {name} --parent {Environment.ProcessId}";
        // ShellExecute can wait for UAC. Keep that native wait off the WinUI
        // thread, without abandoning a helper that may already be launching.
        using var helper = await Task.Run(() => Process.Start(start), CancellationToken.None)
            ?? throw new IOException("The elevated helper did not start.");
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromMinutes(2));
        using var exitWatch = new CancellationTokenSource();
        var connection = pipe.WaitForConnectionAsync(connectTimeout.Token);
        var exited = helper.WaitForExitAsync(exitWatch.Token);
        if (await Task.WhenAny(connection, exited) == exited && !connection.IsCompletedSuccessfully)
        {
            connectTimeout.Cancel();
            try { await connection; } catch (OperationCanceledException) { }
            throw new IOException($"The administrator helper exited before connecting (code {helper.ExitCode}). Check that the app and helper came from the same build.");
        }
        exitWatch.Cancel();
        try { await exited; } catch (OperationCanceledException) { }
        try { await connection; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException("Windows did not connect the administrator helper within two minutes. Check for a pending UAC prompt, then retry."); }
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != helper.Id) throw new UnauthorizedAccessException("Unexpected IPC peer.");
        await PipeProtocol.WriteAsync(pipe, request, connectTimeout.Token);
        // Once accepted, a mutation must finish or leave its journal. UI cancellation must not kill it.
        return await PipeProtocol.ReadAsync<OperationResult>(pipe, CancellationToken.None);
    }
}
