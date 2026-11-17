using System.Text.Json.Serialization;

namespace OverTranslate.Models;

/// <summary>
/// The keys a 鼠標增強 gesture needs held. Matched exactly: Alt does not also fire with Ctrl+Alt.
/// </summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MouseModifierKeys { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Win = 8 }

/// <summary>Which mouse button drags a window's edge while the resize keys are held.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MouseResizeButton { Right, Middle, Left }

/// <summary>
/// 鼠標增強: three small window tools after AltSnap — fade a window with the wheel, scroll
/// sideways with the wheel, and drag a window's nearest edge or corner to resize it. Each has its
/// own switch and its own keys; where two wheel gestures share keys, transparency wins.
/// </summary>
public class MouseEnhanceSettings
{
    /// <summary>鼠標增強's own switch, over the three tools' switches.</summary>
    public bool Enabled { get; set; } = true;

    public bool TransparencyEnabled { get; set; } = true;
    public MouseModifierKeys TransparencyModifier { get; set; } = MouseModifierKeys.Ctrl | MouseModifierKeys.Alt;

    /// <summary>How much one wheel notch changes the opacity, in percent.</summary>
    public int TransparencyStep { get; set; } = 10;

    /// <summary>The faintest a window can be made, in percent, so it can always be found again.</summary>
    public int MinimumOpacity { get; set; } = 20;

    public bool HorizontalScrollEnabled { get; set; } = true;
    public MouseModifierKeys HorizontalScrollModifier { get; set; } = MouseModifierKeys.Alt;

    /// <summary>Off: wheel down scrolls right. On: wheel down scrolls left.</summary>
    public bool HorizontalScrollReversed { get; set; }

    public bool ResizeEnabled { get; set; } = true;
    public MouseModifierKeys ResizeModifier { get; set; } = MouseModifierKeys.Alt;
    public MouseResizeButton ResizeButton { get; set; } = MouseResizeButton.Right;
}

/// <summary>Pure decisions for 鼠標增強, kept apart from the hook so they can be tested.</summary>
public static class MouseEnhanceRules
{
    /// <summary>The key combinations offered in settings, in menu order.</summary>
    public static readonly MouseModifierKeys[] Choices =
    [
        MouseModifierKeys.Alt,
        MouseModifierKeys.Ctrl,
        MouseModifierKeys.Shift,
        MouseModifierKeys.Win,
        MouseModifierKeys.Ctrl | MouseModifierKeys.Alt,
        MouseModifierKeys.Ctrl | MouseModifierKeys.Shift,
        MouseModifierKeys.Shift | MouseModifierKeys.Alt,
        MouseModifierKeys.Ctrl | MouseModifierKeys.Win,
    ];

    public static string Display(MouseModifierKeys keys)
    {
        var parts = new List<string>(4);
        if ((keys & MouseModifierKeys.Ctrl) != 0) parts.Add("Ctrl");
        if ((keys & MouseModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((keys & MouseModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((keys & MouseModifierKeys.Win) != 0) parts.Add("Win");
        return string.Join("+", parts);
    }

    /// <summary>Whether anything is on at all: the master switch and at least one tool.</summary>
    public static bool IsActive(MouseEnhanceSettings s) =>
        s.Enabled && (s.TransparencyEnabled || s.HorizontalScrollEnabled || s.ResizeEnabled);

    /// <summary>Whether <paramref name="held"/> is the key combination of a tool that is on.</summary>
    public static bool IsConfigured(MouseEnhanceSettings s, MouseModifierKeys held) =>
        s.Enabled && held != MouseModifierKeys.None
        && ((s.TransparencyEnabled && s.TransparencyModifier == held)
            || (s.HorizontalScrollEnabled && s.HorizontalScrollModifier == held)
            || (s.ResizeEnabled && s.ResizeModifier == held));

    /// <summary>
    /// Adds <paramref name="delta"/> to what is pending and takes out the whole notches it makes,
    /// so a touchpad's small wheel pieces add up instead of each counting as a notch.
    /// </summary>
    public static int Accumulate(ref int remainder, int delta)
    {
        const int Notch = 120;
        remainder += delta;
        int notches = remainder / Notch;
        remainder -= notches * Notch;
        return notches;
    }

    /// <summary>
    /// How much of each wheel turn 橫向捲動 passes on: half. A full turn sideways moved most pages
    /// further than the same turn moves them down, since a page is far less wide than it is long.
    /// </summary>
    public const int HorizontalScrollPercent = 50;

    /// <summary>
    /// The share of <paramref name="delta"/> sent sideways, what is left over kept in
    /// <paramref name="remainder"/> so a touchpad's small pieces are not rounded away.
    /// </summary>
    public static int ScaleHorizontal(ref int remainder, int delta)
    {
        remainder += delta * HorizontalScrollPercent;
        int scaled = remainder / 100;
        remainder -= scaled * 100;
        return scaled;
    }

    /// <summary>What a wheel notch with <paramref name="held"/> does. Transparency first.</summary>
    public static MouseWheelAction ForWheel(MouseEnhanceSettings s, MouseModifierKeys held)
    {
        if (!s.Enabled || held == MouseModifierKeys.None) return MouseWheelAction.None;
        if (s.TransparencyEnabled && s.TransparencyModifier == held) return MouseWheelAction.Transparency;
        if (s.HorizontalScrollEnabled && s.HorizontalScrollModifier == held) return MouseWheelAction.HorizontalScroll;
        return MouseWheelAction.None;
    }

    /// <summary>Whether pressing <paramref name="button"/> with <paramref name="held"/> starts a resize.</summary>
    public static bool StartsResize(MouseEnhanceSettings s, MouseModifierKeys held, MouseResizeButton button) =>
        s.Enabled && s.ResizeEnabled && held != MouseModifierKeys.None && s.ResizeModifier == held && s.ResizeButton == button;

    /// <summary>
    /// The new alpha (0-255) after <paramref name="notches"/> wheel notches, up making the window
    /// more solid.
    /// </summary>
    public static byte NextAlpha(byte current, int notches, int stepPercent, int minimumPercent)
    {
        stepPercent = Math.Clamp(stepPercent, 1, 50);
        minimumPercent = Math.Clamp(minimumPercent, 5, 100);
        double percent = current * 100.0 / 255 + notches * stepPercent;
        percent = Math.Clamp(percent, minimumPercent, 100);
        return (byte)Math.Round(percent * 255 / 100);
    }

    /// <summary>
    /// Which edges move, from where in the window the drag started: the outer thirds take that
    /// side, the middle third leaves it alone, and the very middle takes the bottom-right corner.
    /// </summary>
    public static ResizeEdges EdgesFor(int left, int top, int right, int bottom, int x, int y)
    {
        var edges = ResizeEdges.None;
        int w = Math.Max(1, right - left), h = Math.Max(1, bottom - top);
        int fx = (x - left) * 3 / w, fy = (y - top) * 3 / h;
        if (fx <= 0) edges |= ResizeEdges.Left;
        else if (fx >= 2) edges |= ResizeEdges.Right;
        if (fy <= 0) edges |= ResizeEdges.Top;
        else if (fy >= 2) edges |= ResizeEdges.Bottom;
        return edges == ResizeEdges.None ? ResizeEdges.Right | ResizeEdges.Bottom : edges;
    }

    /// <summary>The window's rectangle after the pointer has moved by (dx, dy) during a resize.</summary>
    public static (int Left, int Top, int Right, int Bottom) Resize(
        (int Left, int Top, int Right, int Bottom) start, ResizeEdges edges, int dx, int dy, int minWidth, int minHeight)
    {
        var (l, t, r, b) = start;
        if ((edges & ResizeEdges.Left) != 0) l = Math.Min(l + dx, r - minWidth);
        if ((edges & ResizeEdges.Right) != 0) r = Math.Max(r + dx, l + minWidth);
        if ((edges & ResizeEdges.Top) != 0) t = Math.Min(t + dy, b - minHeight);
        if ((edges & ResizeEdges.Bottom) != 0) b = Math.Max(b + dy, t + minHeight);
        return (l, t, r, b);
    }
}

public enum MouseWheelAction { None, Transparency, HorizontalScroll }

[Flags]
public enum ResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }
