using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pome.Core;

namespace Pome.Infrastructure;

public sealed class PrivilegeBroker(string helperPath) : IPrivilegeBroker
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    public async Task<OperationResult> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(helperPath)) throw new FileNotFoundException("The privileged helper is missing. Run tools/Build/Build.ps1 to assemble the app.", helperPath);
        var name = "pome-" + Guid.NewGuid().ToString("N");
        await using var pipe = UserPipe.CreateServer(name);
        var start = new ProcessStartInfo(helperPath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.Arguments = $"--pipe {name} --parent {Environment.ProcessId}";
        using var helper = Process.Start(start) ?? throw new IOException("The elevated helper did not start.");
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromMinutes(2));
        await pipe.WaitForConnectionAsync(connectTimeout.Token);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != helper.Id) throw new UnauthorizedAccessException("Unexpected IPC peer.");
        await PipeProtocol.WriteAsync(pipe, request, connectTimeout.Token);
        // Once accepted, a mutation must finish or leave its journal. UI cancellation must not kill it.
        return await PipeProtocol.ReadAsync<OperationResult>(pipe, CancellationToken.None);
    }
}
