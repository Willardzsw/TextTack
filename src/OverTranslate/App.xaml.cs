using System.Threading;
using System.Windows;
using NLog;
using OverTranslate.Services;
using OverTranslate.Models;
using System.Threading.Tasks;
using OverTranslate.Views;
using OverTranslate.Views.Shell;
using OverTranslate.Views.Translation;

namespace OverTranslate;

public partial class App
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const string ActivateEventName = "OverTranslate_Thai_Dev_Activate_9F3C7B2E";

    private EventWaitHandle? _activateEvent;

    [STAThread]
    private static void Main(string[] args)
    {
        // Before any window or tray icon, which is when the shell settles who this process is.
        NotificationIdentityService.Register();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error(ex.Exception, "Unhandled UI exception");

            // A crash during a capture session used to take the process down while its full-screen
            // dim window stayed painted over the desktop — unclosable, because the code that owned
            // it was gone. Tear the session down first; if there was one, its state is now fully
            // reset (windows closed, fields cleared, session id bumped), so keeping the app alive in
            // its tray-idle state is strictly better than dying with the overlay stuck on screen.
            // Exceptions outside a capture session keep the original fail-fast behaviour.
            if ((MainWindow as Views.MainWindow)?.ForceCloseOverlays() == true)
                ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Fatal(ex.ExceptionObject as Exception, "Unhandled domain exception");
            LogManager.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error(ex.Exception, "Unobserved Task exception");
            ex.SetObserved();
        };
        _activateEvent = new EventWaitHandle(
            false, EventResetMode.AutoReset, ActivateEventName, out bool createdNew);

        if (!createdNew)
        {
            // Another instance is already running — signal it to show the window and exit
            _activateEvent.Set();
            _activateEvent.Dispose();
            Shutdown();
            return;
        }

        // Background thread listens for activation signals from future launch attempts
        new Thread(MonitorActivationRequests) { IsBackground = true }.Start();

        // Before the first line worth keeping is written, and early enough that a user who ticked
        // 記錄詳細資訊 gets the detail from startup onwards rather than from their next toggle.
        LogLevelService.Apply(SettingsService.Instance.Current.VerboseLogging);

        // Straight after the log level and before anything else runs, so the identity is on the
        // first lines of every session's log rather than turning up partway down whichever of them
        // happened to reach it first.
        AppIdentityService.Initialize();

        ThemeService.Apply(SettingsService.Instance.Current.Theme);

        // Windows waits its mouse-hover time, 400ms, before a ToolTip comes up — long enough that
        // people decide there is nothing there and move on. Half of it is still long enough not to
        // flash one up as the pointer crosses a control on its way somewhere else.
        System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(
            typeof(DependencyObject), new FrameworkPropertyMetadata(200));

        // Before any window is built, so nothing is ever constructed against the wrong dictionary.
        LocalizationService.Apply(LocalizationService.Current);

        // Kept in the shipped log: the process's DPI awareness is fixed at launch, and a misreported
        // monitor size is invisible to every managed API, so without this a display-geometry report
        // cannot be diagnosed from a log at all.
        DisplayDiagnostics.LogSnapshot("startup", level: LogLevel.Info);

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Past the single-instance check for the reason given below, and before any window: the
        // card and the hints read the switch this puts in step.

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.InitializeApp();
    }

    private void MonitorActivationRequests()
    {
        try
        {
            while (_activateEvent!.WaitOne())
            {
                if (Dispatcher.HasShutdownStarted) break;
                Dispatcher.Invoke(ShowOrActivateShell);
            }
        }
        catch { /* event disposed on exit — normal shutdown */ }
    }

    internal static void ShowOrActivateShell() => ShellWindow.ShowOrActivate();

    protected override void OnExit(ExitEventArgs e)
    {
        _activateEvent?.Dispose();
        Services.ClipboardBackup.HandOverOnExit();
        base.OnExit(e);
    }
}
