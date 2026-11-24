using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The shape of the chords 取詞翻譯 and 替換翻譯 synthesise into somebody else's window.
/// </summary>
/// <remarks>
/// The keystrokes themselves are not testable here — they go to whatever has the foreground, which
/// in a test run is the runner. What is pinned is the order, because getting it wrong leaves a
/// modifier held down in another application.
/// </remarks>
public class KeyboardInputTests
{
    [Fact]
    public async Task The_chord_is_released_only_after_its_hold_interval()
    {
        var events = new List<string>();

        await KeyboardInput.HoldChordAsync(
            () => events.Add("press"),
            () => { events.Add("hold"); return Task.CompletedTask; },
            () => events.Add("release"));

        Assert.Equal(["press", "hold", "release"], events);
    }

    [Fact]
    public async Task A_chord_that_fails_while_held_is_still_released()
    {
        // Otherwise the failure costs the user their Ctrl key in whatever they were working in,
        // which is the whole keyboard broken rather than one translation missed.
        var released = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => KeyboardInput.HoldChordAsync(
            () => { },
            () => throw new InvalidOperationException("interrupted"),
            () => released = true));

        Assert.True(released);
    }

    [Theory]
    [InlineData(KeyboardInput.VK_MENU)]
    [InlineData(KeyboardInput.VK_LWIN)]
    [InlineData(KeyboardInput.VK_RWIN)]
    public void A_held_Alt_or_Win_is_masked_before_it_is_released(ushort held)
    {
        // Released bare, it is a lone tap: a menu bar (Claude's desktop app) or the Start menu
        // opens and swallows the Ctrl+C that follows.
        var events = KeyboardInput.ReleaseModifiers(vk => vk == held).ToList();

        Assert.Equal(KeyboardInput.VK_MASK, events[0].u.ki.wVk);
        Assert.Equal(0u, events[0].u.ki.dwFlags);
        Assert.Equal(KeyboardInput.VK_MASK, events[1].u.ki.wVk);
        Assert.Equal(2u, events[1].u.ki.dwFlags);
        var heldUp = events.FindIndex(e => e.u.ki.wVk == held);
        Assert.True(heldUp > 1);
    }

    [Fact]
    public void Only_Ctrl_or_Shift_held_sends_no_mask()
    {
        var events = KeyboardInput.ReleaseModifiers(
            vk => vk is KeyboardInput.VK_CONTROL or KeyboardInput.VK_SHIFT).ToList();

        Assert.DoesNotContain(events, e => e.u.ki.wVk == KeyboardInput.VK_MASK);
        Assert.Equal(5, events.Count);
    }
}
