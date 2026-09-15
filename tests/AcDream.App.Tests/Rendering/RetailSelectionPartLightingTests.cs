using AcDream.App.Rendering.Selection;

namespace AcDream.App.Tests.Rendering;

public class RetailSelectionPartLightingTests
{
    private const uint Guid = 0xDA11D011u;
    private const uint Entity = 0xDA11D012u;
    private const double Step = RetailSelectionLightingPulse.FlipIntervalSeconds;

    private double _now;

    private RetailSelectionLightingPulse New() => new(() => _now);

    private static uint Mask(params int[] parts)
    {
        uint mask = 0u;
        foreach (int part in parts) mask |= 1u << part;
        return mask;
    }

    [Fact]
    public void APartFlash_lightsOnlyItsOwnParts()
    {
        var pulse = New();
        pulse.StartParts(Guid, Entity, Mask(0x00, 0x09, 0x0A));

        Assert.True(pulse.HasPartLighting(Guid, Entity));
        foreach (int lit in new[] { 0x00, 0x09, 0x0A })
            Assert.True(pulse.TryGetPartLighting(Guid, Entity, lit, out _), $"part 0x{lit:X} should be lit");
        foreach (int dark in new[] { 0x01, 0x08, 0x0B, 0x10, 31 })
            Assert.False(pulse.TryGetPartLighting(Guid, Entity, dark, out _), $"part 0x{dark:X} should be dark");
    }

    [Fact]
    public void APartFlash_neverAnswersTheWholeEntityQuestion()
    {
        // Otherwise the ordinary per-entity resolve would light every part of the
        // figure, which is the bug this whole path exists to avoid.
        var pulse = New();
        pulse.StartParts(Guid, Entity, Mask(0x10));

        Assert.False(pulse.TryGet(Guid, Entity, out _));
    }

    [Fact]
    public void APartFlash_isBrightThenDarkThenOver()
    {
        var pulse = New();
        pulse.StartParts(Guid, Entity, Mask(0x10));

        Assert.True(pulse.TryGetPartLighting(Guid, Entity, 0x10, out var bright));
        Assert.Equal(RetailSelectionLighting.High, bright);

        _now += Step;
        pulse.Tick();
        Assert.True(pulse.TryGetPartLighting(Guid, Entity, 0x10, out var dark));
        Assert.Equal(RetailSelectionLighting.Low, dark);

        _now += Step;
        pulse.Tick();
        Assert.False(pulse.HasPartLighting(Guid, Entity));
        Assert.False(pulse.TryGetPartLighting(Guid, Entity, 0x10, out _));
    }

    [Fact]
    public void APartFlash_doesNotReachAnotherEntity_orTheSameIdUnderAnotherGuid()
    {
        var pulse = New();
        pulse.StartParts(Guid, Entity, Mask(0x10));

        Assert.False(pulse.HasPartLighting(Guid, Entity + 1u));
        Assert.False(pulse.TryGetPartLighting(Guid, Entity + 1u, 0x10, out _));
        Assert.False(pulse.HasPartLighting(Guid + 1u, Entity));
        Assert.False(pulse.TryGetPartLighting(Guid + 1u, Entity, 0x10, out _));
    }

    [Fact]
    public void AWholeFigureFlash_lightsTheEntity_onTheDollsTwoFlipSchedule()
    {
        var pulse = New();
        pulse.StartWholeFigure(Guid, Entity);

        Assert.True(pulse.TryGet(Guid, Entity, out var bright));
        Assert.Equal(RetailSelectionLighting.High, bright);
        Assert.False(pulse.HasPartLighting(Guid, Entity));   // the whole thing, not named parts

        _now += Step;
        pulse.Tick();
        Assert.True(pulse.TryGet(Guid, Entity, out var dark));
        Assert.Equal(RetailSelectionLighting.Low, dark);

        _now += Step;
        pulse.Tick();
        Assert.False(pulse.TryGet(Guid, Entity, out _));     // over, unlike a world pick
    }

    [Fact]
    public void AnEmptyMask_flashesNothing_andDisturbsNothing()
    {
        var pulse = New();
        pulse.StartParts(Guid, Entity, 0u);

        Assert.False(pulse.HasPartLighting(Guid, Entity));
        Assert.False(pulse.TryGet(Guid, Entity, out _));   // never the whole figure

        pulse.Start(Guid, Entity);
        pulse.StartParts(Guid, Entity, 0u);
        Assert.True(pulse.TryGet(Guid, Entity, out _));    // the running pick is left alone
    }

    [Fact]
    public void AWorldPick_isUnchanged_wholeEntityAndFourFlips()
    {
        var pulse = New();
        pulse.Start(Guid, Entity);

        Assert.False(pulse.HasPartLighting(Guid, Entity));    // never per part
        for (int flip = 1; flip < RetailSelectionLightingPulse.WorldFlipBudget; flip++)
        {
            Assert.True(pulse.TryGet(Guid, Entity, out _), $"flip {flip} should still be lit");
            _now += Step;
            pulse.Tick();
        }
        Assert.True(pulse.TryGet(Guid, Entity, out _));

        _now += Step;
        pulse.Tick();
        Assert.False(pulse.TryGet(Guid, Entity, out _));
    }

    [Fact]
    public void APartFlashReplacesAWorldPick_andBackAgain()
    {
        var pulse = New();
        pulse.Start(Guid, Entity);
        pulse.StartParts(Guid, Entity, Mask(0x09));
        Assert.False(pulse.TryGet(Guid, Entity, out _));
        Assert.True(pulse.TryGetPartLighting(Guid, Entity, 0x09, out _));

        pulse.Start(Guid, Entity);
        Assert.True(pulse.TryGet(Guid, Entity, out _));
        Assert.False(pulse.HasPartLighting(Guid, Entity));
    }
}
