using System.Security.AccessControl;
using System.Security.Principal;

namespace NaxEventViewer.Core;

/// <summary>
/// Whether only administrators (and the system) can change a program file. A machine-wide setting that starts a
/// program, such as the Event Viewer replacement, must only point at such a file: if a standard user can replace it,
/// an administrator who opens the Event Viewer would run the replaced file with full rights.
/// </summary>
public static class ProtectedLocation
{
    private static readonly SecurityIdentifier[] Trusted =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        // NT SERVICE\TrustedInstaller
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"),
    ];

    // Rights that let someone replace, rename or re-permission the file, its folder, or a folder above it.
    private const FileSystemRights GenericWriteRights = (FileSystemRights)0x50000000; // GENERIC_WRITE | GENERIC_ALL
    private const FileSystemRights FileRights = GenericWriteRights | FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.WriteAttributes | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    private const FileSystemRights FolderRights = GenericWriteRights | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | FileSystemRights.WriteAttributes
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
    private const FileSystemRights AncestorRights = GenericWriteRights | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
        | FileSystemRights.WriteAttributes | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>
    /// True when no account other than the system, administrators and TrustedInstaller may change the file, the folder
    /// it lies in, or rename a folder above it. False for anything under a user profile, and when the rights cannot be read.
    /// </summary>
    public static bool IsAdminOnly(string filePath)
    {
        try
        {
            var file = new FileInfo(Path.GetFullPath(filePath));
            string? root = Path.GetPathRoot(file.FullName);
            if (root is null || root.Length != 3 || root[1] != ':' || new DriveInfo(root).DriveType != DriveType.Fixed) return false;
            if (!file.Exists || IsReparsePoint(file) || !OnlyTrustedMayHave(file.GetAccessControl(), FileRights)) return false;
            DirectoryInfo? folder = file.Directory;
            if (folder is null || !IsProtectedDirectory(folder, FolderRights)) return false;
            // Renaming a folder above needs Delete on that folder or DeleteSubdirectoriesAndFiles on its parent.
            for (DirectoryInfo? ancestor = folder.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (!IsProtectedDirectory(ancestor, AncestorRights)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsProtectedDirectory(DirectoryInfo directory, FileSystemRights rights) =>
        directory.Exists && !IsReparsePoint(directory) && OnlyTrustedMayHave(directory.GetAccessControl(), rights);

    private static bool IsReparsePoint(FileSystemInfo entry) => (entry.Attributes & FileAttributes.ReparsePoint) != 0;

    private static bool OnlyTrustedMayHave(FileSystemSecurity security, FileSystemRights rights)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null || descriptor.Owner is not SecurityIdentifier owner || !IsTrusted(owner)) return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            // An inherit-only entry applies to children, not to this object.
            if (rule.AccessControlType != AccessControlType.Allow || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            if ((rule.FileSystemRights & rights) != 0 && rule.IdentityReference is SecurityIdentifier sid && !IsTrusted(sid)) return false;
        }
        return true;
    }

    private static bool IsTrusted(SecurityIdentifier sid) => Trusted.Any(trusted => trusted.Equals(sid));
}
