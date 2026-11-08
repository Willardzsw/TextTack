using OverTranslate.Services.Ocr;

namespace OverTranslate.Models;

/// <summary>
/// Everything 截圖翻譯 keeps between capture sessions, under one key.
/// </summary>
public class CaptureSettings
{
    /// <summary>
    /// Whether the capture toolbar last translated text written downwards in columns. False means
    /// the ordinary horizontal layout and remains the default for existing settings files.
    /// </summary>
    public bool VerticalText { get; set; } = false;

    /// <summary>
    /// What the capture toolbar last said the framed material is. Standard is the default, which is
    /// also what every settings file written before this switch existed reads as.
    /// </summary>
    /// <remarks>
    /// <para>Stored as the enum's name rather than as a flag. 標準 and 漫畫・文章 are very unlikely
    /// to be the last two answers — realtime already has more than two — and a bool would have to
    /// change data format the day a third arrives, taking every existing settings file with it.</para>
    ///
    /// <para>A name this build does not know reads as Standard: the settings reader keeps a
    /// property's default when a value will not deserialize, and logs the field it dropped. That is
    /// the whole of the unknown-value handling, and it belongs there rather than here — every enum
    /// in the file gets it for free.</para>
    /// </remarks>
    public CaptureLayoutMode LayoutMode { get; set; } = CaptureLayoutMode.General;

    /// <summary>
    /// Whether 截圖 ends the capture once the picture is on the clipboard. Off by default; set from
    /// the toolbar's 設定 menu (#323).
    /// </summary>
    /// <remarks>
    /// <para>For someone who captures to paste the picture somewhere, for whom 截圖 then Esc is two
    /// steps every time. Off by default because the other habit is just as common: copy the picture,
    /// then go on reading the translation it came with.</para>
    ///
    /// <para>A setting rather than a key held while clicking. A modifier on the button would do the
    /// same and nobody would ever find it; a switch in the menu beside 儲存截圖 is where the other
    /// things that happen after a copy are already decided.</para>
    /// </remarks>
    public bool CloseAfterCopy { get; set; } = false;

    /// <summary>
    /// 複製文字後自動關閉: ends the capture once 複製文字 has put the recognised text on the
    /// clipboard. Its own switch, beside the picture's and the translation's, because each is a
    /// different habit. Only after a copy that worked — a failed one keeps the capture up so its
    /// notice can be read.
    /// </summary>
    public bool CloseAfterCopyText { get; set; } = false;

    /// <summary>複製譯文後自動關閉: the same, for 複製譯文. See <see cref="CloseAfterCopyText"/>.</summary>
    public bool CloseAfterCopyTranslation { get; set; } = false;

    /// <summary>
    /// Whether a double-click inside a settled selection copies the picture and ends the capture,
    /// the way Snipaste finishes one. Only while the box is still editable: once translation has
    /// started the capture is click-through by design.
    /// </summary>
    public bool DoubleClickToFinish { get; set; } = true;

    /// <summary>
    /// Whether a right-click on the capture ends it, the same as Esc. Only while the box is still
    /// editable, for the same reason as <see cref="DoubleClickToFinish"/>.
    /// </summary>
    public bool RightClickToCancel { get; set; } = true;

    /// <summary>
    /// What pins the selection while a capture is up (貼圖 for the box rather than the clipboard).
    /// The middle button by default.
    /// </summary>
    public PinSelectionTrigger PinSelectionTrigger { get; set; } = PinSelectionTrigger.MiddleClick;

    /// <summary>Whether the size bar over the box counts in dip (the monitor's scaling taken out) rather than px.</summary>
    public bool SizeInDip { get; set; } = false;

    /// <summary>
    /// Whether the box has rounded corners — and so the copied, saved and pinned pictures, whose
    /// corners are cut transparent. Recognition and translation still read the whole rectangle.
    /// </summary>
    public bool RoundedCorners { get; set; } = false;

    /// <summary>Whether dragging a corner of the box keeps its width-to-height ratio.</summary>
    public bool LockAspectRatio { get; set; } = false;

    /// <summary>The sizes 調整截圖區域 offers under 預設, each as "寬x高".</summary>
    public List<string> SizePresets { get; set; } = ["100x100", "500x500", "640x480", "1024x768"];

    /// <summary>Whether 偵錯輔助 in the 設定 menu was left unfolded.</summary>
    /// <remarks>
    /// Folded by default: the boxes are for reporting a problem, and unfolded they took half the
    /// menu from everyone who never does. Remembered so that whoever is reporting one is not folding
    /// it open again on every capture.
    /// </remarks>
    public bool DebugAssistExpanded { get; set; } = false;

    /// <summary>How the 標記 tools were last set up.</summary>
    /// <remarks>
    /// The user's preferences for the tools, not the state of a drawing: the colour, each tool's
    /// width, how faint a highlight is and which shape 形狀 gives. Which tool is in hand is not kept —
    /// every capture starts on the pen, because one that started on the eraser would be a drag that
    /// draws nothing and looks broken. The marks themselves belong to the capture they were drawn on.
    /// </remarks>
    public CaptureAnnotationSettings Annotation { get; set; } = new();
}

/// <summary>The 標記 preferences kept between captures. See <see cref="CaptureSettings.Annotation"/>.</summary>
/// <remarks>
/// The widths and the opacity are slider positions from 0 to 1, not DIP: what each position means is
/// the panel's to decide, and keeping the position is what lets that change without every settings
/// file written before it coming back at a width nobody chose.
/// </remarks>
public class CaptureAnnotationSettings
{
    /// <summary>The ink colour as #RRGGBB. One the palette does not offer reads as its first colour.</summary>
    public string Color { get; set; } = "#000000";

    public double PenThickness { get; set; } = 0.5;

    public double HighlighterThickness { get; set; } = 0.5;

    /// <summary>One width for all three shapes: they are one tool behind one button.</summary>
    public double ShapeThickness { get; set; } = 0.5;

    public double EraserSize { get; set; } = 0.5;

    public double HighlighterOpacity { get; set; } = 0.5;

    /// <summary>馬賽克's block size, as a slider position.</summary>
    public double MosaicStrength { get; set; } = 0.5;

    /// <summary>模糊's radius, as a slider position.</summary>
    public double BlurStrength { get; set; } = 0.5;

    /// <summary>序號's disc size, as a slider position.</summary>
    public double NumberSize { get; set; } = 0.5;

    /// <summary>What 形狀 gives when pressed. Anything but one of the three shapes reads as Rectangle.</summary>
    public AnnotationTool Shape { get; set; } = AnnotationTool.Rectangle;

    /// <summary>The 文字 size in physical pixels of the capture, which is what the panel shows (#362).</summary>
    /// <remarks>
    /// A number of pixels rather than a slider position like the widths, because the user types it
    /// and reads it back. One outside <see cref="MinTextSize"/>..<see cref="MaxTextSize"/> is put back
    /// inside by the panel.
    /// </remarks>
    public int TextSize { get; set; } = DefaultTextSize;

    public bool TextBold { get; set; } = false;

    public bool TextItalic { get; set; } = false;

    public bool TextUnderline { get; set; } = false;

    public bool TextStrikethrough { get; set; } = false;

    public const int DefaultTextSize = 24;

    /// <summary>Below this, CJK glyphs are a smudge.</summary>
    public const int MinTextSize = 8;

    /// <summary>A quarter of a 4K screen's height; at 1080p one line is already half the screen.</summary>
    public const int MaxTextSize = 500;
}

/// <summary>What pins the selection during a capture. Stored by name.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum PinSelectionTrigger
{
    None,
    MiddleClick,
    CtrlT,
    CtrlP,
}
