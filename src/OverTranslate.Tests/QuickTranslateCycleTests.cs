using OverTranslate.Models;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>替換翻譯's double-press language switch.</summary>
public class QuickTranslateCycleTests
{
    [Fact]
    public void NextGoesRoundInOrderAndWraps()
    {
        var languages = new[] { "EN-US", "TH", "ZH-HANS" };

        Assert.Equal("TH", QuickTranslateCycle.Next(languages, "EN-US"));
        Assert.Equal("ZH-HANS", QuickTranslateCycle.Next(languages, "th"));
        Assert.Equal("EN-US", QuickTranslateCycle.Next(languages, "ZH-HANS"));
    }

    [Fact]
    public void NextStartsAtTheFirstWhenTheCurrentTargetIsNotPicked()
    {
        Assert.Equal("EN-US", QuickTranslateCycle.Next(["EN-US", "TH"], "JA"));
        Assert.Equal("EN-US", QuickTranslateCycle.Next(["EN-US", "TH"], null));
    }

    [Fact]
    public void LanguagesDropsUnknownAndRepeatedCodesAndStopsAtFive()
    {
        var known = LanguageData.TargetLanguages.Select(l => l.Code).Take(7).ToList();
        var settings = new QuickTranslateSettings
        {
            CycleLanguages = ["NOT-A-LANGUAGE", known[0], known[0].ToLowerInvariant(), .. known.Skip(1)],
        };

        Assert.Equal(known.Take(QuickTranslateCycle.MaxLanguages), QuickTranslateCycle.Languages(settings));
    }

    [Fact]
    public void ActiveOnlyWithTwoLanguagesAndBilingualOff()
    {
        Assert.False(QuickTranslateCycle.IsActive(new QuickTranslateSettings()));
        Assert.False(QuickTranslateCycle.IsActive(new QuickTranslateSettings { CycleLanguages = ["EN-US"] }));
        Assert.True(QuickTranslateCycle.IsActive(new QuickTranslateSettings { CycleLanguages = ["EN-US", "TH"] }));
        Assert.False(QuickTranslateCycle.IsActive(
            new QuickTranslateSettings { CycleLanguages = ["EN-US", "TH"], BilingualEnabled = true }));
    }

    [Fact]
    public void TwoPressesInsideTheWindowAreADoublePress()
    {
        var presses = new DoublePressDetector();

        Assert.False(presses.Register(1000, append: false, windowMs: 400));
        Assert.True(presses.Register(1350, append: false, windowMs: 400));
    }

    [Fact]
    public void ASlowSecondPressStartsOverAsAFirstPress()
    {
        var presses = new DoublePressDetector();

        Assert.False(presses.Register(1000, append: false, windowMs: 400));
        Assert.False(presses.Register(1500, append: false, windowMs: 400));
        Assert.True(presses.Register(1700, append: false, windowMs: 400));
    }

    [Fact]
    public void ADoublePressIsUsedUpSoAThirdPressDoesNotPairWithIt()
    {
        var presses = new DoublePressDetector();

        presses.Register(1000, append: false, windowMs: 400);
        Assert.True(presses.Register(1200, append: false, windowMs: 400));
        Assert.False(presses.Register(1300, append: false, windowMs: 400));
    }

    [Fact]
    public void ReplaceAndAppendPressesDoNotPair()
    {
        var presses = new DoublePressDetector();

        presses.Register(1000, append: false, windowMs: 400);
        Assert.False(presses.Register(1100, append: true, windowMs: 400));
        Assert.True(presses.Register(1200, append: true, windowMs: 400));
    }
}
