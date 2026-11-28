#if LOCAL_DIAGNOSTICS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using NLog;

namespace OverTranslate.Services.LocalDiagnostics;

/// <summary>
/// 本機檢測: every foreground change between 替換翻譯 copying a selection and pasting the
/// translation, so a refused paste ("the foreground moved") says which window took it, and when.
/// </summary>
/// <remarks>
/// Local builds only. The whole file, and every call into it, sits behind LOCAL_DIAGNOSTICS, which
/// the project defines unless built with <c>-p:LocalDiagnostics=false</c> — as
/// tools/release/build-release.ps1 does, and then refuses to package a TextTack.dll that still
/// carries <see cref="Marker"/>.
///
/// Window titles are left out on purpose: in a chat program they are the names of the people the
/// user is talking to. Process, class and handles are enough to tell the windows apart.
/// </remarks>
internal sealed class ForegroundTrail : IDisposable
{
    /// <summary>Starts every line this writes; the release script looks for it.</summary>
    public const string Marker = "[本機檢測]";

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint GA_ROOTOWNER = 3;

    private delegate void WinEventProc(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    // Kept in a field: Windows holds a raw pointer to this delegate.
    private readonly WinEventProc _proc;
    private readonly string _what;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _changes = [];
    private IntPtr _expected;
    private IntPtr _hookId;

    private ForegroundTrail(string what, IntPtr expected)
    {
        _what = what;
        _expected = expected;
        _proc = OnForegroundChanged;
    }

    /// <summary>The trail being kept, for <see cref="Note"/>. Dispatcher thread only.</summary>
    private static ForegroundTrail? _current;

    /// <summary>
    /// Adds a line to the trail being kept, if there is one: what the hint label did about the
    /// foreground, between the switches the hook records.
    /// </summary>
    public static void Note(string what)
    {
        var trail = _current;
        if (trail is null || trail._hookId == IntPtr.Zero) return;
        trail._changes.Add($"+{trail._clock.ElapsedMilliseconds}ms {what}");
    }

    /// <summary>Starts watching from the calling (dispatcher) thread.</summary>
    public static ForegroundTrail Start(string what)
    {
        var trail = new ForegroundTrail(what, GetForegroundWindow());
        _current = trail;
        trail._hookId = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, trail._proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        return trail;
    }

    /// <summary>The window the paste will be held to, read after the copy.</summary>
    public void SetExpected(IntPtr expected)
    {
        if (expected != _expected)
            _changes.Add($"+{_clock.ElapsedMilliseconds}ms 複製後記下的前台與按鍵時不同：{Describe(expected)}");
        _expected = expected;
    }

    /// <summary>Writes the whole trail: the paste was refused because the foreground moved.</summary>
    public void ReportFocusMoved()
    {
        var current = GetForegroundWindow();
        GetWindowThreadProcessId(_expected, out var expectedPid);
        GetWindowThreadProcessId(current, out var currentPid);

        var sb = new StringBuilder();
        sb.Append($"{Marker} {_what} 前台被換走（+{_clock.ElapsedMilliseconds}ms）");
        sb.Append($"\n  原本：{Describe(_expected)}");
        sb.Append($"\n  現在：{Describe(current)}");
        sb.Append($"\n  同一程序：{(expectedPid != 0 && expectedPid == currentPid ? "是" : "否")}");
        sb.Append($"，同一根擁有者：{(GetAncestor(_expected, GA_ROOTOWNER) == GetAncestor(current, GA_ROOTOWNER) ? "是" : "否")}");
        sb.Append($"，原視窗還在：{(IsWindow(_expected) ? (IsWindowVisible(_expected) ? "是，可見" : "是，不可見") : "否")}");
        if (_changes.Count == 0) sb.Append("\n  期間沒有收到前台切換事件");
        foreach (var change in _changes) sb.Append("\n  ").Append(change);

        Log.Info(sb.ToString());
    }

    /// <summary>
    /// Writes the whole trail although the paste went through: the foreground had to be taken back
    /// from the hint label first.
    /// </summary>
    public void ReportReclaimed(bool reclaimed)
    {
        var sb = new StringBuilder();
        sb.Append($"{Marker} {_what} 貼上前前台在提示標籤上，{(reclaimed ? "已拿回" : "拿不回")}（+{_clock.ElapsedMilliseconds}ms）");
        sb.Append($"\n  原本：{Describe(_expected)}");
        foreach (var change in _changes) sb.Append("\n  ").Append(change);
        Log.Info(sb.ToString());
    }

    public void Dispose()
    {
        if (ReferenceEquals(_current, this)) _current = null;
        if (_hookId == IntPtr.Zero) return;
        UnhookWinEvent(_hookId);
        _hookId = IntPtr.Zero;
    }

    private void OnForegroundChanged(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_hookId == IntPtr.Zero) return;
        // Described now: the window may be gone by the time the trail is written.
        _changes.Add($"+{_clock.ElapsedMilliseconds}ms 前台 → {Describe(hwnd)}");
    }

    private static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "（無）";

        GetWindowThreadProcessId(hwnd, out var pid);
        string process;
        try { using var p = Process.GetProcessById((int)pid); process = p.ProcessName; }
        catch { process = $"pid {pid}"; }

        var cls = new StringBuilder(128);
        GetClassName(hwnd, cls, cls.Capacity);

        string? own = null;
        if (pid == (uint)Environment.ProcessId && System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            foreach (Window w in System.Windows.Application.Current.Windows)
                if (new WindowInteropHelper(w).Handle == hwnd) { own = w.GetType().Name; break; }

        var root = GetAncestor(hwnd, GA_ROOTOWNER);
        var rootText = root != IntPtr.Zero && root != hwnd ? $"，根擁有者 0x{root:X}" : "";
        return $"\"{process}/{own ?? cls.ToString()}\" (0x{hwnd:X}{rootText})";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
#endif
