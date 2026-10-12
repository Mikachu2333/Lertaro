using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Lertaro.Core.Services.Installation;

public static class UserDataAccess
{
    public static void Verify(string directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("Cannot identify the settings owner.");
        if (!Directory.Exists(directory))
        {
            var acl = new DirectorySecurity();
            acl.SetSecurityDescriptorBinaryForm(DescribeUserDirectory(user), AccessControlSections.Access | AccessControlSections.Owner);
            new DirectoryInfo(directory).Create(acl);
        }
        var info = new DirectoryInfo(directory);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException($"The user data root is a link: {directory}");
        var owner = info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
        if (owner != user)
            // Both SIDs, because "belongs to another security identity" on its own is not something a user can
            // act on, and the pair is the whole diagnosis: a DACL that already grants this user FullControl
            // beside an Administrator owner is a legacy artifact, not tampering.
            throw new UnauthorizedAccessException(
                $"The user data directory belongs to another security identity: {directory} (owner: {owner?.Value ?? "unknown"}, expected: {user.Value})");

        // A DACL is only half of "may this process write here". Windows also compares the process's mandatory
        // integrity level against the folder's: a Low token may not write an unlabelled (Medium) folder, and no
        // DACL changes that. %LocalAppData%\Lertaro carries no label of its own, so a launch that runs reduced
        // -- a sandbox, or any launcher that drops the token -- is refused a folder it demonstrably owns. The
        // label is what makes the two agree, and it grants nothing: NW is no-write-up, so a Low process may
        // write and every higher level may still do anything it could before.
        SetLowIntegrityLabel(info);

        // An actual read/create/delete, not File.Exists or an Allow-ACE heuristic. Never truncate settings.
        _ = SettingsFileReader.ReadIfPresent(Path.Combine(directory, "user-settings.json"));
        using var probe = new FileStream(Path.Combine(directory, $".access-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        probe.WriteByte(1);
    }

    /// <summary>
    /// The owner SID of <paramref name="directory"/>, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// For checking a repair's own result: the elevated repair has to be able to say whether the owner it
    /// claims to have set is the owner that is actually there. On the machine in issue #342 the walk reported
    /// success while every later start still refused, and nothing could tell those two apart.
    /// </remarks>
    internal static string? TryGetOwner(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner)
                .GetOwner(typeof(SecurityIdentifier))?.Value;
        }
        catch { return null; }
    }

    /// <summary>
    /// Labels <paramref name="directory"/> Low so a reduced token may write it. Best effort: a host that
    /// refuses the SACL write (no <c>SeSecurityPrivilege</c>) simply keeps the label it had, and the write
    /// probe below is what decides.
    /// </summary>
    /// <remarks>
    /// Low plus <c>NW</c> is no-write-up, not no-read-down: the folder stays readable and writable by every
    /// level at or above Low, which is every level this App runs at. Only the mandatory label is touched --
    /// the DACL and owner this type just established are left exactly as they are.
    ///
    /// The label lives in the SACL, and .NET has no managed accessor for it (<c>AccessControlSections</c> has
    /// no <c>Label</c> member), so this is the one place that goes to advapi32 directly. A caller who owns the
    /// directory is allowed to set its label without elevation.
    /// </remarks>
    internal static void SetLowIntegrityLabel(DirectoryInfo directory)
    {
        const uint SaclSecurityInformation = 0x00000008;
        const uint SddlRevision1 = 1;
        var descriptor = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("S:(ML;;NW;;;LW)", SddlRevision1,
                    out descriptor, out _))
                return;
            SetFileSecurityW(directory.FullName, SaclSecurityInformation, descriptor);
        }
        catch
        {
            // A folder that cannot be labelled is not a folder that cannot be used: Verify's own probe is the
            // verdict, and failing here would refuse a start the probe would have allowed.
        }
        finally
        {
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string stringSecurityDescriptor,
        uint stringSdRevision, out IntPtr securityDescriptor, out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetFileSecurityW(string fileName, uint securityInformation, IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static byte[] DescribeUserDirectory(SecurityIdentifier user)
    {
        var descriptor = InstallDirectoryLock.Describe(PortableDirectoryLock.OwnedBy(user), true, true);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }
}
