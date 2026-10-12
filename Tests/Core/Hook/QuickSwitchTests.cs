using Lertaro.Core.Hook;
using Lertaro.Core.Hook.Commands;
using Lertaro.Core.Hook.InlineSearch;
using Lertaro.PluginSdk.Abstractions.Plugins.WindowAdapters;

namespace Lertaro.Core.Tests.Hook;

/// <summary>
/// Ctrl+G end to end, minus the two native edges: which folder it picks, when it declines, and -- the part
/// that used to fail -- that recognizing the gesture never waits on the tracker lock.
/// </summary>
[TestClass]
public sealed class QuickSwitchTests
{
    private static readonly IntPtr Dialog = new(0x1000);
    private static readonly IntPtr ExplorerA = new(0x2000);
    private static readonly IntPtr ExplorerB = new(0x3000);
    private static readonly IntPtr Gone = new(0x4000);

    private sealed class RecordingAdapter(bool navigate = true) : IFileDialogAdapter
    {
        internal List<(IntPtr Hwnd, string Path)> Navigations { get; } = [];
        internal bool Throw { get; init; }

        public bool CanHandle(IntPtr hwnd, string className, string processName) => true;
        public string? GetCurrentPath(IntPtr hwnd) => null;
        public bool GetDockBounds(IntPtr hwnd, out AdapterRect rect) { rect = default; return false; }
        public bool RestoreFocus(IntPtr hwnd) => true;

        public bool NavigateTo(IntPtr hwnd, string targetPath)
        {
            Navigations.Add((hwnd, targetPath));
            if (Throw) throw new InvalidOperationException("adapter fault");
            return navigate;
        }
    }

    private sealed class ProviderAdapter : IInlineSearchAdapter
    {
        public bool IsFileExplorer => true;
        public bool CanHandle(IntPtr hwnd, string className, string processName) => false;
        public bool CanTrigger(IntPtr hwnd, string className) => false;
        public string? GetSearchScope(IntPtr hwnd) => null;
        public bool ExecuteItem(IntPtr hwnd, string path, string searchInput) => false;
        public bool GetDockBounds(IntPtr hwnd, out AdapterRect rect) { rect = default; return false; }
        public bool CanEnterActionsMode(IntPtr hwnd) => false;
    }

    // A tracker whose window facts are the test's own, so a dialog and two file managers can exist without a
    // desktop. Handles are otherwise inert.
    private static ExplorerTracker Tracker(params (IntPtr Hwnd, uint ProcessId, bool Alive)[] windows)
    {
        var tracker = new ExplorerTracker();
        tracker.WindowIsAlive = hwnd => Array.Exists(windows, window => window.Hwnd == hwnd && window.Alive) || windows.Length == 0;
        tracker.WindowProcessId = hwnd => Array.Find(windows, window => window.Hwnd == hwnd).ProcessId;
        return tracker;
    }

    // The dialog is active while the browsed folder is the file manager's -- that is the whole case -- so the
    // path goes in flagged as the non-dialog one it came from, exactly as ExplorerWindowClassifier publishes it.
    private static ExplorerTracker DialogTracker(bool withAdapter = true, string? browsedFolder = null)
    {
        var tracker = Tracker((Dialog, 42, true));
        tracker.ActiveHwnd = Dialog;
        tracker.IsActiveWindowDialog = true;
        tracker.ActiveAdapter = withAdapter ? new RecordingAdapter() : null;
        if (browsedFolder != null) tracker.UpdatePath(browsedFolder, false, isDialog: false);
        return tracker;
    }

    [TestMethod]
    public void Execute_KnownFolder_NavigatesOnceWithATrailingSeparator()
    {
        using var tracker = DialogTracker(browsedFolder: @"D:\Work");
        var adapter = (RecordingAdapter)tracker.ActiveAdapter!;

        // Synchronously, where production queues the work item: what this asserts is the path handed over.
        Assert.IsTrue(QuickSwitchNavigator.Execute(tracker, Dialog, (a, h, p) => a.NavigateTo(h, p)));

        Assert.AreEqual((Dialog, @"D:\Work\"), Assert.ContainsSingle(adapter.Navigations));
    }

    [TestMethod]
    public void Execute_WindowThatIsNoLongerTheDialog_DoesNotNavigate()
    {
        using var tracker = DialogTracker(browsedFolder: @"D:\Work");
        tracker.IsActiveWindowDialog = false;
        var adapter = (RecordingAdapter)tracker.ActiveAdapter!;

        Assert.IsFalse(QuickSwitchNavigator.Execute(tracker, Dialog));
        Assert.IsEmpty(adapter.Navigations);
    }

    [TestMethod]
    public void Execute_DialogTheTrackerHasLetGoOf_DoesNotNavigate()
    {
        using var tracker = DialogTracker(browsedFolder: @"D:\Work");
        // Held before the tracker is told the dialog is gone: the point is that the adapter it kept is not
        // used once the window it was matched for is no longer the tracked one.
        var adapter = (RecordingAdapter)tracker.ActiveAdapter!;
        tracker.ActiveHwnd = new IntPtr(0x9999);

        Assert.IsFalse(QuickSwitchNavigator.Execute(tracker, Dialog));
        Assert.IsEmpty(adapter.Navigations);
    }

    [TestMethod]
    public void Execute_NoAdapterForTheDialog_DoesNotNavigate()
    {
        using var tracker = DialogTracker(withAdapter: false, browsedFolder: @"D:\Work");

        Assert.IsFalse(QuickSwitchNavigator.Execute(tracker, Dialog));
    }

    [TestMethod]
    public void Execute_NothingBrowsedYet_DoesNotNavigate()
    {
        using var tracker = DialogTracker();
        var adapter = (RecordingAdapter)tracker.ActiveAdapter!;

        Assert.IsFalse(QuickSwitchNavigator.Execute(tracker, Dialog));
        Assert.IsEmpty(adapter.Navigations);
    }

    [TestMethod]
    public void Execute_WindowTheDesktopNoLongerHas_DoesNotNavigate()
    {
        using var tracker = DialogTracker(browsedFolder: @"D:\Work");
        tracker.WindowIsAlive = _ => false;
        var adapter = (RecordingAdapter)tracker.ActiveAdapter!;

        Assert.IsFalse(QuickSwitchNavigator.Execute(tracker, Dialog));
        Assert.IsEmpty(adapter.Navigations);
    }

    [TestMethod]
    public void Execute_AdapterFault_IsHandedToTheWorkItemThatCannotLetItEscape()
    {
        using var tracker = DialogTracker(browsedFolder: @"D:\Work");
        var adapter = new RecordingAdapter { Throw = true };

        // The hand-off itself succeeds; the fault happens on the pool thread, which is why the body swallows.
        Assert.IsTrue(QuickSwitchNavigator.Execute(tracker, Dialog, (a, h, p) => FileDialogCommandHandler.NavigateLoggingFailures(a, h, p)));
        FileDialogCommandHandler.NavigateLoggingFailures(adapter, Dialog, @"D:\Work\");
        Assert.AreEqual((Dialog, @"D:\Work\"), Assert.ContainsSingle(adapter.Navigations));
    }

    [TestMethod]
    public void Execute_DecliningAdapter_IsStillNotAnEscape()
    {
        var adapter = new RecordingAdapter(navigate: false);
        FileDialogCommandHandler.NavigateLoggingFailures(adapter, Dialog, @"D:\Work\");
        Assert.ContainsSingle(adapter.Navigations);
    }

    // The two-file-managers report: one global path could only ever answer with whichever window was
    // classified last.
    [TestMethod]
    public void SelectQuickSwitchPath_ProviderInTheDialogsProcess_WinsOverTheMoreRecentlyUsedOne()
    {
        using var tracker = Tracker((Dialog, 42, true), (ExplorerA, 42, true), (ExplorerB, 7, true));
        var provider = new ProviderAdapter();
        tracker.RememberPathProvider(provider, ExplorerA, @"D:\SameProcess");
        tracker.RememberPathProvider(provider, ExplorerB, @"C:\OtherProcess");

        Assert.AreEqual(@"D:\SameProcess", tracker.SelectQuickSwitchPath(Dialog));
    }

    [TestMethod]
    public void SelectQuickSwitchPath_NoProviderInTheDialogsProcess_UsesTheMostRecentOne()
    {
        using var tracker = Tracker((Dialog, 42, true), (ExplorerA, 7, true), (ExplorerB, 7, true));
        var provider = new ProviderAdapter();
        tracker.RememberPathProvider(provider, ExplorerA, @"D:\First");
        tracker.RememberPathProvider(provider, ExplorerB, @"C:\MostRecent");

        Assert.AreEqual(@"C:\MostRecent", tracker.SelectQuickSwitchPath(Dialog));
    }

    [TestMethod]
    public void SelectQuickSwitchPath_ProviderWhoseFolderWasNeverRead_IsSkipped()
    {
        using var tracker = Tracker((Dialog, 42, true), (ExplorerA, 42, true), (ExplorerB, 7, true));
        var provider = new ProviderAdapter();
        tracker.RememberPathProvider(provider, ExplorerA, @"D:\Remembered");
        tracker.RememberPathProvider(provider, ExplorerB);

        Assert.AreEqual(@"D:\Remembered", tracker.SelectQuickSwitchPath(Dialog));
    }

    [TestMethod]
    public void SelectQuickSwitchPath_ClosedProvider_IsDroppedRatherThanAnswered()
    {
        using var tracker = Tracker((Dialog, 7, true), (ExplorerA, 7, false), (ExplorerB, 7, true));
        var provider = new ProviderAdapter();
        tracker.RememberPathProvider(provider, ExplorerA, @"D:\Closed");
        tracker.RememberPathProvider(provider, ExplorerB, @"C:\Open");

        Assert.AreEqual(@"C:\Open", tracker.SelectQuickSwitchPath(Dialog));
        // And once, not on every read: the dead entry is gone from the list.
        Assert.AreEqual(@"C:\Open", tracker.SelectQuickSwitchPath(Dialog));
    }

    [TestMethod]
    public void SelectQuickSwitchPath_NoProviderAtAll_FallsBackToTheLastBrowsedFolder()
    {
        using var tracker = Tracker((Dialog, 42, true));
        tracker.UpdatePath(@"D:\Browsed", false);

        Assert.AreEqual(@"D:\Browsed", tracker.SelectQuickSwitchPath(Dialog));
    }

    [TestMethod]
    public void SelectQuickSwitchPath_FolderChange_KeepsTheProviderEntryCurrent()
    {
        using var tracker = Tracker((Dialog, 42, true), (ExplorerA, 42, true));
        var provider = new ProviderAdapter();
        tracker.RememberPathProvider(provider, ExplorerA, @"D:\Old");
        tracker.ActiveHwnd = ExplorerA;
        tracker.UpdatePath(@"D:\New", false);

        Assert.AreEqual(@"D:\New", tracker.SelectQuickSwitchPath(Dialog));
    }

    // The gesture half: recognizing Ctrl+G must not wait for the lock the WinEvent thread holds while it reads
    // a file manager, or the keystroke is simply dropped.
    [TestMethod]
    public void CheckAndHandleQuickSwitch_DoesNotWaitForTheTrackerLock()
    {
        using var tracker = DialogTracker(browsedFolder: @"D:\Work");
        IntPtr requested = IntPtr.Zero;
        var detector = new GlobalHotkeyDetector(new UserSettings(), tracker, hwnd => requested = hwnd);

        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            lock (tracker.StateLock)
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        });

        try
        {
            Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(5)));
            detector.OnKeyDown(0xA2);
            var triggered = detector.CheckAndHandleQuickSwitch(0x47, 0, out var consumeKey);

            Assert.IsTrue(triggered);
            Assert.IsTrue(consumeKey);
            Assert.AreEqual(Dialog, requested);
        }
        finally
        {
            release.Set();
            holder.Wait(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public void CheckAndHandleQuickSwitch_NoDialog_LeavesTheKeystrokeToTheHostWindow()
    {
        using var tracker = new ExplorerTracker();
        var requested = 0;
        var detector = new GlobalHotkeyDetector(new UserSettings(), tracker, _ => requested++);

        detector.OnKeyDown(0xA2);
        var triggered = detector.CheckAndHandleQuickSwitch(0x47, 0, out var consumeKey);

        Assert.IsFalse(triggered);
        Assert.IsFalse(consumeKey);
        Assert.AreEqual(0, requested);
    }

    [TestMethod]
    public void CheckAndHandleQuickSwitch_NothingBrowsedYet_LeavesTheKeystrokeToTheHostWindow()
    {
        using var tracker = DialogTracker();
        var requested = 0;
        var detector = new GlobalHotkeyDetector(new UserSettings(), tracker, _ => requested++);

        detector.OnKeyDown(0xA2);
        var triggered = detector.CheckAndHandleQuickSwitch(0x47, 0, out var consumeKey);

        Assert.IsFalse(triggered);
        Assert.IsFalse(consumeKey);
        Assert.AreEqual(0, requested);
    }

    [TestMethod]
    public void CheckAndHandleQuickSwitch_AnotherKeyWithControlHeld_IsNotTheHotkey()
    {
        using var tracker = DialogTracker(browsedFolder: @"D:\Work");
        var requested = 0;
        var detector = new GlobalHotkeyDetector(new UserSettings(), tracker, _ => requested++);

        detector.OnKeyDown(0xA2);
        Assert.IsFalse(detector.CheckAndHandleQuickSwitch(0x48, 0, out _));
        Assert.AreEqual(0, requested);
    }

    // A freshly connected App mirror gets the Quick Switch value too, not only the dialog's own path: the hook
    // is started for the App each time, so the mirror would otherwise stay empty until some later event.
    [TestMethod]
    public void PublishCurrentState_RepeatsTheBrowsedFolderForTheAppMirror()
    {
        using var tracker = Tracker((ExplorerA, 7, true));
        tracker.ActiveHwnd = ExplorerA;
        tracker.UpdatePath(@"D:\Browsed", false);
        var published = new List<(string Path, bool IsDialog)>();
        tracker.OnPathCaptured += (path, _, isDialog) => published.Add((path, isDialog));

        tracker.PublishCurrentState();

        Assert.Contains(item => item.Path == @"D:\Browsed" && !item.IsDialog, published);
    }
}
