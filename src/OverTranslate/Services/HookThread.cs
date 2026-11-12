using System.Diagnostics;
using System.Windows.Threading;
using NLog;

namespace OverTranslate.Services;

/// <summary>
/// A private message-pumping thread for the low-level hooks to live on.
/// </summary>
/// <remarks>
/// <para>A low-level hook is delivered to the thread that installed it, and only while that thread
/// is pumping messages. Installing one from the dispatcher therefore ties every callback to the
/// interface being idle — and a callback that outstays <c>LowLevelHooksTimeout</c> (5s by default)
/// is silently dropped from the hook chain by Windows, after which that hook is dead for the rest
/// of the session and nothing says why.</para>
///
/// <para>Worse than losing the hook: Windows holds every keyboard and mouse event in the system
/// until the callback returns, so a busy UI thread delays the whole desktop's input for as long as
/// it is busy. That is exactly the moment Esc exists for, and exactly the moment a UI-thread hook
/// stops answering it.</para>
///
/// <para>Its own thread decouples the two. Nothing here runs the hook's <em>action</em> — the
/// callback should still hand that to the dispatcher and return at once.</para>
/// </remarks>
internal sealed class HookThread : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // How long each step of Stop waits. Bounded rather than infinite: a hook listener is not worth
    // hanging the application's close on.
    private readonly TimeSpan _shutdownTimeout;

    private readonly string _name;
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private bool _stopping;

    public HookThread(string name) : this(name, TimeSpan.FromSeconds(2)) { }

    internal HookThread(string name, TimeSpan shutdownTimeout)
    {
        _name = name;
        _shutdownTimeout = shutdownTimeout;
    }

    /// <summary>The thread's dispatcher, or null while it is not running.</summary>
    public Dispatcher? Dispatcher => !_stopping && _dispatcher is { HasShutdownStarted: false } d ? d : null;

    /// <summary>Starts the thread if it is not already pumping. Safe to call repeatedly.</summary>
    public void Start()
    {
        if (_thread is { IsAlive: true } running)
        {
            if (!_stopping) return;

            // Stop does not wait for the thread to end, so a restart has to: normally a matter of
            // milliseconds. One that outlasts the wait still owns its thread — do not start a second
            // set of hooks beside it.
            if (!running.Join(_shutdownTimeout))
            {
                Log.Warn("Hook thread '{Name}' is still shutting down after {Timeout}; not restarted", _name, _shutdownTimeout);
                return;
            }
        }
        _stopping = false;

        // Waited on rather than assumed: the dispatcher belongs to the new thread and does not
        // exist until that thread reaches for it, and callers post work to it immediately.
        using var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = _name,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        _thread = thread;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the hook thread and waits for it, or does nothing when the
    /// thread is not running.
    /// </summary>
    public void Invoke(Action work) => Dispatcher?.Invoke(work);

    /// <summary>
    /// Ends the thread, running <paramref name="teardown"/> on it first — hooks have to be removed
    /// from the thread that installed them.
    /// </summary>
    /// <remarks>
    /// Waits (bounded) for the teardown, since the caller counts on the hooks being off, but not for
    /// the thread to end. Joining it here held the interface thread — the usual caller — for up to
    /// the whole timeout per hook whenever the thread was slow to exit, with the capture's windows
    /// still on screen: seen as two and four seconds while another program was slow to answer an
    /// element query. Nothing needs the thread gone; <see cref="Start"/> waits where it matters.
    /// </remarks>
    public void Stop(Action? teardown = null)
    {
        if (_stopping || _dispatcher is not { } dispatcher) return;
        _stopping = true;

        if (!dispatcher.HasShutdownStarted)
        {
            // Bounded, not a plain Invoke. This thread exists precisely because it can be sitting
            // inside an OS callback that nobody controls the length of, and waiting on it without a
            // limit would hang the application's close on exactly that — the hang this class is
            // here to keep off the interface thread in the first place.
            try
            {
                if (teardown is not null)
                {
                    if (dispatcher.CheckAccess()) teardown();
                    else if (dispatcher.InvokeAsync(teardown).Wait(_shutdownTimeout) != DispatcherOperationStatus.Completed)
                        Log.Warn("Hook thread '{Name}' did not run its teardown within {Timeout}", _name, _shutdownTimeout);
                }
            }
            finally
            {
                // InvokeShutdown marshals synchronously and can hang forever on the very callback
                // that just timed out. Queue shutdown behind teardown — the same priority, so it
                // keeps its place in line; a background one also waits for the thread's input
                // queue to be empty, which is one more thing that can hold it up.
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            }
        }

        // The fields stay, so Start cannot create a second thread beside a live one. The exit is
        // only watched, off this thread, so a slow one is in the log.
        if (_thread is not { } thread || thread == Thread.CurrentThread) return;
        long started = Stopwatch.GetTimestamp();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (!thread.Join(_shutdownTimeout))
            {
                Log.Warn("Hook thread '{Name}' still running after {Timeout}", _name, _shutdownTimeout);
                if (!thread.Join(TimeSpan.FromSeconds(30))) return;
            }

            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (ms >= SlowExitMs) Log.Info("Hook thread '{Name}' took {Ms:F0} ms to end", _name, ms);
        });
    }

    /// <summary>An exit this long or longer is logged.</summary>
    private const double SlowExitMs = 100;

    public void Dispose() => Stop();
}
