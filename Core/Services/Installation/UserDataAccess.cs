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
    /// Why asking for an elevated repair would be pointless, or null when it may still be tried.
    /// </summary>
    /// <remarks>
    /// A launcher that runs Lertaro sandboxed or reduced hands its process a Low mandatory label, and a Low
    /// process cannot write anywhere in the user's own profile whatever the ACLs say -- the file it creates
    /// inherits the folder's Medium label and its own write-up policy refuses the write. Elevation cannot change
    /// that: the repair runs with its own token and exits, leaving the reduced one to try again, so the only
    /// answer worth giving is the one the user can act on. The elevated attempt is still offered for everything
    /// a repair can genuinely fix, which is why this is only about this process's own level.
    /// </remarks>
    internal static string? RepairCannotHelpReason(string directory) =>
        ReducedIntegrityReason(directory, ProcessIntegrityLevel.Current());

    /// <summary>What <see cref="RepairCannotHelpReason"/> says at a given level. Pure, so it can be asserted.</summary>
    internal static string? ReducedIntegrityReason(string directory, uint integrityRid) =>
        integrityRid < ProcessIntegrityLevel.MediumLevel
            ? $"{directory} is not writable by this process: it runs at "
              + $"{ProcessIntegrityLevel.Name(integrityRid)} integrity, and an elevated repair does "
              + "not change the token this process would still be using. Start Lertaro directly (Explorer, or a "
              + "normal command prompt), not from a sandboxed or reduced-privilege launcher."
            : null;

    private static byte[] DescribeUserDirectory(SecurityIdentifier user)
    {
        var descriptor = InstallDirectoryLock.Describe(PortableDirectoryLock.OwnedBy(user), true, true);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }
}
