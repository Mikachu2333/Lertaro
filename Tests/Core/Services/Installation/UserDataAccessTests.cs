using Lertaro.Core.Services.Installation;

namespace Lertaro.Core.Tests.Services.Installation;

// What the App is told when its own data folder refuses a write. By that point the folder's owner is known to be
// this account and the folder is known not to be a link, so what is left is the process's own right to write
// there -- which is a launcher's decision, not a folder's, and elevation cannot undo it.
[TestClass]
public sealed class UserDataAccessTests
{
    [TestMethod]
    public void ReducedIntegrityReason_OnlyBlamesAProcessBelowMedium()
    {
        Assert.IsNull(UserDataAccess.ReducedIntegrityReason(@"C:\Data", ProcessIntegrityLevel.MediumLevel));
        Assert.IsNull(UserDataAccess.ReducedIntegrityReason(@"C:\Data", ProcessIntegrityLevel.HighLevel));

        var low = UserDataAccess.ReducedIntegrityReason(@"C:\Data", ProcessIntegrityLevel.LowLevel);
        Assert.IsNotNull(low);
        StringAssert.Contains(low, @"C:\Data");
        StringAssert.Contains(low, "Low integrity");
        StringAssert.Contains(low, "Start Lertaro directly");
    }

    [TestMethod]
    public void RepairCannotHelpReason_ReadsTheLevelThisHostRunsAt()
    {
        // Either answer is legitimate -- a normal host is Medium, and a test run started from a sandboxed
        // launcher would not be -- so this asserts that the wrapper agrees with the level it read rather than
        // with a constant. What the message says is asserted above, where the level can be named.
        var level = ProcessIntegrityLevel.Current();
        var reason = UserDataAccess.RepairCannotHelpReason(@"C:\Data");
        var described = $"this host runs at {ProcessIntegrityLevel.Name(level)} integrity";

        if (level < ProcessIntegrityLevel.MediumLevel) Assert.IsNotNull(reason, described);
        else Assert.IsNull(reason, described);
    }
}
