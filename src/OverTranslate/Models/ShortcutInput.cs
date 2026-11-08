using System.Text.Json.Serialization;

namespace OverTranslate.Models;

/// <summary>
/// The physical input used to trigger a shortcut. Keyboard keeps the existing Windows RegisterHotKey
/// path; the mouse buttons are observed without swallowing the original input, so the foreground
/// application still receives them.
/// </summary>
/// <remarks>
/// The side buttons are named after Windows' own XBUTTON1/XBUTTON2 rather than after what a mouse
/// prints on them, because that is what the hook reports and mice disagree about the rest: the pair
/// is back/forward on one and thumb-up/thumb-down on the next. The interface calls them 側鍵 1 and
/// 側鍵 2, in the order Windows numbers them.
///
/// The two key sequences are keyboard input, but not of the kind RegisterHotKey can take: Windows
/// only knows single combinations. They are observed like the mouse buttons, so the keys still reach
/// the foreground application — which for Ctrl+C+C is the point, the first Ctrl+C being the copy.
///
/// These are persisted by name, so a value may be renamed only alongside a settings migration.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShortcutInputKind
{
    Keyboard,
    MouseMiddle,
    MouseX1,
    MouseX2,

    /// <summary>Ctrl pressed and released twice in quick succession, nothing else in between.</summary>
    CtrlDoubleTap,

    /// <summary>C pressed twice while Ctrl is held, the first press being an ordinary copy.</summary>
    CtrlCC,
}
