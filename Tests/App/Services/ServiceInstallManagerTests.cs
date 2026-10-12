using Lertaro.App.Services;

namespace Lertaro.App.Tests.Services;

// What the user is told when the elevated repair does not succeed. Issue #342 is the reason this is a function
// at all: one sentence covered a timeout, a refused lock walk, an unknown profile and a thrown exception, and
// the reporter had no way to tell which.
[TestClass]
public sealed class ServiceInstallManagerTests
{
    [TestMethod]
    public void DescribeRepairFailure_NonZeroExit_QuotesTheRepairsOwnDiagnostic()
    {
        var text = ServiceInstallManager.DescribeRepairFailure(
            exited: true, exitCode: 1,
            report: "repair failed: C:\\Users\\x\\AppData\\Local\\Lertaro: owner is still S-1-5-32-544, expected S-1-5-21-1",
            sid: "S-1-5-21-1");

        StringAssert.Contains(text, "exit code 1");
        StringAssert.Contains(text, "owner is still S-1-5-32-544");
        StringAssert.Contains(text, "--repair-permissions S-1-5-21-1");
    }

    [TestMethod]
    public void DescribeRepairFailure_Timeout_NamesTheStepItWasStuckOn()
    {
        var text = ServiceInstallManager.DescribeRepairFailure(
            exited: false, exitCode: 0,
            report: "[2026-10-07 21:59:30.000] [1234] locking the shared data and application directories",
            sid: "S-1-5-21-1");

        StringAssert.Contains(text, "did not finish within 120 seconds");
        StringAssert.Contains(text, "locking the shared data and application directories");
    }

    // The repair can die before writing anything at all -- then saying so is the honest answer, not silence.
    [TestMethod]
    public void DescribeRepairFailure_WithoutAnyTrace_SaysSoRatherThanLeavingItBlank()
    {
        var withoutTrace = ServiceInstallManager.DescribeRepairFailure(exited: true, exitCode: 2, report: null, sid: "S-1-5-21-1");
        var timedOut = ServiceInstallManager.DescribeRepairFailure(exited: false, exitCode: 0, report: null, sid: "S-1-5-21-1");

        StringAssert.Contains(withoutTrace, "no diagnostic of its own");
        StringAssert.Contains(timedOut, "unrecorded step");
    }
}
