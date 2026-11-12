using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The one property the escape hatch rests on: the hooks do not run on the caller's thread.
/// </summary>
/// <remarks>
/// A low-level hook is delivered to the thread that installed it, so a hook installed from the
/// dispatcher stops being serviced the moment the dispatcher stops pumping — which is exactly when
/// Esc has to still work. These tests pin the thread being a separate, pumping, STA one; the hook
/// itself is Windows' side of the arrangement and cannot be asserted on without real key input.
/// </remarks>
public class HookThreadTests
{
    [Fact]
    public void WorkRunsOnItsOwnStaThread_NotTheCallersThread()
    {
        using var hookThread = new HookThread("test hook thread");
        hookThread.Start();

        int threadId = 0;
        ApartmentState apartment = ApartmentState.Unknown;
        hookThread.Invoke(() =>
        {
            threadId = Environment.CurrentManagedThreadId;
            apartment = Thread.CurrentThread.GetApartmentState();
        });

        Assert.NotEqual(Environment.CurrentManagedThreadId, threadId);

        // STA because the thread pumps messages and hosts window-adjacent OS callbacks.
        Assert.Equal(ApartmentState.STA, apartment);
    }

    [Fact]
    public void StartIsIdempotent_SoRepeatedInstallsDoNotLeakThreads()
    {
        using var hookThread = new HookThread("test hook thread");
        hookThread.Start();

        int first = 0, second = 0;
        hookThread.Invoke(() => first = Environment.CurrentManagedThreadId);
        hookThread.Start();
        hookThread.Invoke(() => second = Environment.CurrentManagedThreadId);

        Assert.Equal(first, second);
    }

    [Fact]
    public void StopRunsTeardownOnTheHookThread_BecauseAHookOnlyComesOffWhereItWentOn()
    {
        var hookThread = new HookThread("test hook thread");
        hookThread.Start();

        int installedOn = 0;
        hookThread.Invoke(() => installedOn = Environment.CurrentManagedThreadId);

        int tornDownOn = 0;
        hookThread.Stop(() => tornDownOn = Environment.CurrentManagedThreadId);

        Assert.Equal(installedOn, tornDownOn);

        // Stopped means stopped: nothing is left pumping, so Invoke has nowhere to run.
        bool ran = false;
        hookThread.Invoke(() => ran = true);
        Assert.False(ran);
    }

    [Fact]
    public void StartAfterStopRunsAgain_OnANewThreadOnceTheOldOneHasEnded()
    {
        using var hookThread = new HookThread("test hook thread");
        hookThread.Start();
        int first = 0;
        hookThread.Invoke(() => first = Environment.CurrentManagedThreadId);

        // Stop no longer waits for the thread to end; a restart straight after it must still work.
        hookThread.Stop();
        hookThread.Start();

        int second = 0;
        hookThread.Invoke(() => second = Environment.CurrentManagedThreadId);
        Assert.NotEqual(0, second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void StopDoesNotWaitForTheThreadToEnd()
    {
        using var hookThread = new HookThread("test hook thread", TimeSpan.FromSeconds(10));
        // Not disposed: the hook thread may only get to waiting on it after this method has returned.
        var release = new ManualResetEventSlim();
        hookThread.Start();

        // Work the teardown leaves in front of the shutdown keeps the thread from ending — the
        // capture's hook threads were seen to take seconds over it. Stop returns all the same.
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            hookThread.Stop(() =>
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => release.Wait()));
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Null(hookThread.Dispatcher);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void StopWithoutStartDoesNothing()
    {
        var hookThread = new HookThread("test hook thread");

        bool ran = false;
        hookThread.Stop(() => ran = true);

        Assert.False(ran);
    }
}
