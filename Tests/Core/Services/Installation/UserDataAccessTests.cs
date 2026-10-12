using System.Diagnostics;
using System.Security.Principal;
using Lertaro.Core.Services.Installation;

namespace Lertaro.Core.Tests.Services.Installation;

// The data root has to be writable by the token this App actually got. Its DACL already grants this account
// FullControl, so what is left is the mandatory integrity label: a Low token may not write an unlabelled
// (Medium) folder however the DACL reads, which is the difference between a reduced start that works and one
// refused a folder it owns.
//
// Whether the label can be written is the host's decision, not this type's: a directory owner may set it, and
// a sandbox that withholds that right (ERROR_ACCESS_DENIED / ERROR_PRIVILEGE_NOT_HELD) must not turn into a
// refused start. So these tests pin the contract that is the same either way -- it never throws, and it never
// takes away the access Verify's own probe needs.
[TestClass]
public sealed class UserDataAccessTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LertaroUserDataAccess_" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void SetLowIntegrityLabel_OnADirectoryThatCannotBeLabelled_DoesNotThrow()
    {
        var directory = Directory.CreateDirectory(_root);

        // No assertion on the resulting label: this machine may or may not allow the write. What must hold is
        // that a refusal is swallowed -- a start the probe would allow must not die here instead.
        UserDataAccess.SetLowIntegrityLabel(directory);

        Assert.IsTrue(directory.Exists, "labelling must leave the directory in place");
    }

    [TestMethod]
    public void SetLowIntegrityLabel_LeavesTheOwnerAlone()
    {
        var directory = Directory.CreateDirectory(_root);
        var before = directory.GetAccessControl(System.Security.AccessControl.AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier));

        UserDataAccess.SetLowIntegrityLabel(directory);

        Assert.AreEqual(before, directory.GetAccessControl(System.Security.AccessControl.AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier)), "labelling must not move the owner");
    }

    [TestMethod]
    public void Verify_LeavesAUsableDirectoryUsable()
    {
        Directory.CreateDirectory(_root);

        UserDataAccess.Verify(_root);

        // The probe file Verify creates is DeleteOnClose, so a successful call leaves nothing behind.
        Assert.IsEmpty(Directory.GetFiles(_root), "Verify must not leave its probe file behind");
    }

    [TestMethod]
    public void Verify_ADataRootThatIsALink_IsRefused()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var link = Path.Combine(_root, "Lertaro");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe",
            $"/d /c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            process.WaitForExit();

        try
        {
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => UserDataAccess.Verify(link));
        }
        finally { Directory.Delete(link); }
    }
}
