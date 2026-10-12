using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lertaro.Core.Services.Installation;

/// <summary>
/// The mandatory label this process itself runs at, and the names Windows gives those levels.
/// </summary>
/// <remarks>
/// Split out of <see cref="DirectoryLockNativeMethods"/>, which keeps the directory-handle interop: this is a
/// question about the process's own token, with no handle walk attached to it, and it is what tells a data
/// folder that refuses a write apart from a process that was never allowed to write there to begin with.
/// </remarks>
internal static class ProcessIntegrityLevel
{
    internal const uint UntrustedLevel = 0x0000, LowLevel = 0x1000, MediumLevel = 0x2000,
        HighLevel = 0x3000, SystemLevel = 0x4000;

    /// <summary>
    /// The label this process runs at, or <see cref="MediumLevel"/> when it cannot be read.
    /// </summary>
    /// <remarks>
    /// A launcher that runs Lertaro sandboxed or reduced hands its process a Low label, and no ACL on a data
    /// folder answers for that: Windows refuses the write itself, because the file a Low process creates would
    /// inherit the folder's own (Medium) label. That is how a folder which has the right owner, is not a link,
    /// and is writable by every other process running as this account still refuses this one. Medium is the
    /// fallback because assuming a normal level only means the caller tries the repair it would have tried.
    /// </remarks>
    internal static uint Current()
    {
        const uint query = 0x0008;
        const int integrityLevel = 25; // TokenIntegrityLevel
        if (!OpenProcessToken(Win32Api.GetCurrentProcess(), query, out var token))
            return MediumLevel;
        using (token)
        {
            // The first call sizes the buffer, the second fills it: TOKEN_MANDATORY_LABEL is a SID and attributes.
            GetTokenInformation(token, integrityLevel, IntPtr.Zero, 0, out var size);
            if (size <= 0) return MediumLevel;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, integrityLevel, buffer, size, out _)) return MediumLevel;
                return LastSubAuthority(Marshal.ReadIntPtr(buffer));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    /// <summary>The label's RID as Windows names it, for a message a user can act on.</summary>
    internal static string Name(uint rid) => rid switch
    {
        UntrustedLevel => "Untrusted",
        LowLevel => "Low",
        MediumLevel => "Medium",
        HighLevel => "High",
        SystemLevel => "System",
        _ => $"0x{rid:X4}",
    };

    // SID: revision, sub-authority count, six identifier-authority bytes, then four bytes per sub-authority.
    // The integrity RID is the last one ("S-1-16-4096" is Low).
    private static uint LastSubAuthority(IntPtr sid)
    {
        var count = Marshal.ReadByte(sid, 1);
        return count == 0 ? 0 : (uint)Marshal.ReadInt32(sid, 8 + (4 * (count - 1)));
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        IntPtr information, int informationLength, out int returnLength);
}
