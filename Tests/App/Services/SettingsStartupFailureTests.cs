using System.IO;
using Lertaro.App.Services;
using Lertaro.Core;

namespace Lertaro.App.Tests.Services;

[TestClass]
public sealed class SettingsStartupFailureTests
{
    // The one case that is honestly about other instances. It has to keep saying so, or the message that
    // replaced the old blanket one loses the meaning users already know.
    [TestMethod]
    public void Describe_LeaseConflict_NamesTheOtherInstancesAndTheFolder()
    {
        var text = SettingsStartupFailure.Describe(
            new SettingsLeaseConflictException(@"C:\Data", new IOException("sharing violation")));

        StringAssert.Contains(text, "其他 Lertaro 实例");
        StringAssert.Contains(text, @"C:\Data");
    }

    // The regression this whole change is about: a relocated data folder used to be reported as a settings
    // transfer in progress, which sent the user looking for instances that were never running.
    [TestMethod]
    public void Describe_SessionUnavailable_KeepsTheRealReasonAndNeverBlamesInstances()
    {
        var text = SettingsStartupFailure.Describe(new SettingsSessionUnavailableException(
            @"D:\Lertaro-Users\abc",
            new IOException("Backup paths must be ordinary directories, not links: D:\\Lertaro-Users")));

        StringAssert.Contains(text, "not links");
        StringAssert.Contains(text, @"D:\Lertaro-Users\abc");
        Assert.IsFalse(text.Contains("其他 Lertaro 实例"), text);
    }

    // Anything that never got as far as a data directory still reports the one the process resolved.
    [TestMethod]
    public void Describe_UnexpectedException_FallsBackToTheUserDataDirectory()
    {
        var text = SettingsStartupFailure.Describe(new UnauthorizedAccessException("denied"));

        StringAssert.Contains(text, "denied");
        StringAssert.Contains(text, Logger.UserDataDir);
    }
}
