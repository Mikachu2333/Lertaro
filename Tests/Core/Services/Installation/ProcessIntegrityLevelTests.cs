using Lertaro.Core.Services.Installation;

namespace Lertaro.Core.Tests.Services.Installation;

// The one thing that tells a data folder refusing a write apart from a process that was never allowed to write
// there: a launcher that runs Lertaro sandboxed or reduced hands it a Lower label than the folder it must write.
[TestClass]
public sealed class ProcessIntegrityLevelTests
{
    [TestMethod]
    [DataRow(0x0000u, "Untrusted", DisplayName = "no label at all")]
    [DataRow(0x1000u, "Low", DisplayName = "a sandboxed or reduced launcher")]
    [DataRow(0x2000u, "Medium", DisplayName = "a process a user started themselves")]
    [DataRow(0x3000u, "High", DisplayName = "an elevated process")]
    [DataRow(0x4000u, "System", DisplayName = "a service running as LocalSystem")]
    [DataRow(0x1234u, "0x1234", DisplayName = "a level this build does not know")]
    public void Name_NamesEveryMandatoryLabel(uint rid, string expected) =>
        Assert.AreEqual(expected, ProcessIntegrityLevel.Name(rid));

    [TestMethod]
    public void Current_IsALevelWindowsNames()
    {
        // A normal host reads Medium and one started from a sandboxed launcher reads lower; either way the read
        // has to name a real level rather than fall back to a placeholder. Naming it is the whole point: it is
        // what turns "Access is denied" on the user's own folder into the launcher they can go and change.
        var name = ProcessIntegrityLevel.Name(ProcessIntegrityLevel.Current());

        Assert.DoesNotContain("0x", name);
    }
}
