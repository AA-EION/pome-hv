using System.Security.Principal;
using Pome.Core;

namespace Pome.Infrastructure;

/// <summary>
/// Members of the built-in Hyper-V Administrators group can list, power and connect to VMs without
/// elevation. Pome uses that path for power actions only; creation, disk and file tasks stay in the
/// elevated helper.
/// </summary>
public static class HyperVAccess
{
    public static readonly SecurityIdentifier HyperVAdministrators = new("S-1-5-32-578");

    public static bool CurrentUserIsMember()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(HyperVAdministrators);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    public static bool IsLifecycle(OperationKind kind) => kind is OperationKind.Start or OperationKind.Shutdown or OperationKind.TurnOff
        or OperationKind.Reset or OperationKind.Pause or OperationKind.Resume or OperationKind.Save;

    /// <summary>Re-reads the VM, applies the same ownership/state policy as the helper, then runs the lifecycle program.</summary>
    public static async Task<OperationResult> RunLifecycleAsync(PowerShellRunner runner, OperationRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsLifecycle(request.Kind)) throw new InvalidDataException("Only power actions run without elevation.");
        if (request.VmId is null || request.VmId == Guid.Empty) throw new InvalidDataException("A VM identifier is required.");
        var manager = new HyperVManager(runner);
        var vm = (await manager.ListAsync(cancellationToken)).SingleOrDefault(x => x.Id == request.VmId)
            ?? throw new InvalidDataException("The selected VM no longer exists.");
        VmManagementPolicy.RequireAllowed(request, vm);
        var result = Json.Read<OperationResult>(await runner.RunAsync("Lifecycle", request, cancellationToken));
        try { result = result with { VirtualMachines = (await manager.ListAsync(cancellationToken)).ToArray() }; }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or System.Text.Json.JsonException) { /* The action succeeded; a listing failure must not turn it into a retry. */ }
        return result;
    }
}
