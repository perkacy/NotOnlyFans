using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NotOnlyFans.Services;

/// <summary>
/// The service's pipes are created by Local System (or an administrator, when it runs by hand).
/// Any program could create a pipe with the same name while the service is not running; such a pipe
/// would be owned by a normal user, so the window app does not talk to it (no fake readings, and fan
/// settings never go to it).
/// </summary>
internal static class PipeCheck
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>Throws <see cref="UnauthorizedAccessException"/> when the pipe is not the service's.</summary>
    public static void EnsureService(PipeStream pipe)
    {
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (owner != System && owner != Administrators)
        {
            throw new UnauthorizedAccessException($"The pipe is not NotOnlyFans.Service's (owner {owner}).");
        }
    }
}
