using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace NotOnlyFans.Service;

/// <summary>
/// %ProgramData%\NotOnlyFans: the service's settings (fans.json, colors.json).
///
/// The service runs as Local System, and by default any user may create folders and files in ProgramData.
/// So before the first write the folder is made ours: if it is a link (junction) or was created by someone
/// who is not an administrator, it is moved aside; then only Local System and administrators may change it
/// (users may read). Otherwise a user could point it somewhere else and have the service overwrite files there,
/// or edit the fan settings behind the service's back.
/// </summary>
internal static class DataDirectory
{
    public static string Root { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NotOnlyFans");

    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private static bool _secured;

    /// <summary>For tests: use another folder.</summary>
    internal static void UseRoot(string root)
    {
        Root = root;
        _secured = false;
    }

    /// <summary>Makes sure the folder exists, is a real folder and only Local System / administrators can change it.</summary>
    public static void Secure(ILogger? log)
    {
        if (_secured)
        {
            return;
        }

        var dir = new DirectoryInfo(Root);
        if (dir.Exists && !IsTrusted(dir.FullName, dir.Attributes))
        {
            string aside = $"{Root}.untrusted-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                dir.Delete();   // removes the link only, never what it points to
                log?.LogWarning("{Folder} was a link to another place - removed it.", Root);
            }
            else
            {
                dir.MoveTo(aside);
                log?.LogWarning("{Folder} was not created by an administrator - moved it to {Aside} and started fresh.", Root, aside);
            }
            dir = new DirectoryInfo(Root);
        }

        if (!dir.Exists)
        {
            dir.Create(Protected());
        }
        else
        {
            dir.SetAccessControl(Protected());
            // Files someone else created in the folder (e.g. a planted fans.json.tmp) can't be trusted either.
            foreach (var file in dir.EnumerateFileSystemInfos())
            {
                if (!IsTrusted(file.FullName, file.Attributes))
                {
                    if (file is DirectoryInfo sub && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        sub.Delete(recursive: true);
                    }
                    else
                    {
                        file.Delete();
                    }
                    log?.LogWarning("Removed {File}: not created by an administrator.", file.FullName);
                }
            }
        }
        _secured = true;
    }

    /// <summary>Like <see cref="Secure"/>, but a failure is only logged.</summary>
    public static void TrySecure(ILogger? log)
    {
        try
        {
            Secure(log);
        }
        catch (Exception ex)
        {
            log?.LogError(ex, "Could not protect {Folder}.", Root);
        }
    }

    /// <summary>Reads a settings file; null if it is missing or unreadable (the caller then uses defaults).</summary>
    public static T? Load<T>(string fileName, JsonTypeInfo<T> type, ILogger? log) where T : class
    {
        string path = Path.Combine(Root, fileName);
        TrySecure(log);   // if that fails the file is still read: losing the user's settings would be worse
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            log?.LogWarning(ex, "{File} could not be read - using defaults.", path);
            return null;
        }
    }

    /// <summary>Writes a settings file in one step (a crash mid-write never leaves half a file). False if it failed.</summary>
    public static bool Save<T>(string fileName, T value, JsonTypeInfo<T> type, ILogger? log)
    {
        string path = Path.Combine(Root, fileName);
        TrySecure(log);
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, type));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _secured = false;   // try securing again next time (the folder may have been changed)
            log?.LogError(ex, "Saving {File} failed.", path);
            return false;
        }
    }

    /// <summary>Owned by Local System or the administrators, and not a link.</summary>
    private static bool IsTrusted(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }
        FileSystemSecurity security = attributes.HasFlag(FileAttributes.Directory)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        return owner == System || owner == Administrators || IsOwnedByMe(owner);
    }

    /// <summary>
    /// Started by hand as an administrator, files may be owned by that administrator account itself
    /// (depends on a Windows policy) - they are still ours.
    /// </summary>
    private static bool IsOwnedByMe(IdentityReference? owner)
    {
        using var me = WindowsIdentity.GetCurrent();
        return owner is not null && owner == me.User && new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Local System and administrators: full control; users: read. Not inherited from ProgramData.</summary>
    private static DirectorySecurity Protected()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
