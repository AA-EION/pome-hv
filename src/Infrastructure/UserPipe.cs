using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Pome.Infrastructure;

public static class UserPipe
{
    public static NamedPipeServerStream CreateServer(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("Windows user identity is unavailable.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        // CurrentUserOnly compares token owners in .NET 10; UAC changes that owner
        // to Administrators. Use the actual user SID across the elevation boundary.
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
    }

    public static void RequireCurrentUserOwner(NamedPipeClientStream pipe)
    {
        using var identity = WindowsIdentity.GetCurrent();
        RequireOwnerMatches(identity.User, pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier);
    }

    public static void RequireOwnerMatches(SecurityIdentifier? currentUser, SecurityIdentifier? pipeOwner)
    {
        if (currentUser is null || pipeOwner is null || !currentUser.Equals(pipeOwner))
            throw new UnauthorizedAccessException("The pipe is owned by a different Windows user.");
    }
}
