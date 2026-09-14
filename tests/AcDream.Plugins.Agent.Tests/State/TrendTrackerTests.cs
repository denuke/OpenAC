using System.Text.Json.Nodes;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.State;
using AcDream.Plugins.Agent.Tests.Fakes;

namespace AcDream.Plugins.Agent.Tests.State;

public sealed class TrendTrackerTests
{
    [Fact]
    public void NothingIsCountedUntilTheCharacterHasSettledInTheWorld()
    {
        var host = new FakePluginHost();
        var trends = new TrendTracker(host);

        trends.Sample(0d);
        Assert.False(trends.TryCapture(0d, new JsonObject()));

        trends.Sample(TrendTracker.SettleSeconds);
        Assert.True(trends.TryCapture(TrendTracker.SettleSeconds, new JsonObject()));
    }

    [Fact]
    public void ARateIsNullUntilAMinuteOfItsWindowHasBeenCounted()
    {
        var host = new FakePluginHost();
        var trends = new TrendTracker(host);
        trends.Sample(0d);
        trends.Sample(10d);
        trends.Sample(20d);

        var fields = new JsonObject();
        Assert.True(trends.TryCapture(20d, fields));

        Assert.Null(fields["xpPerHour"]!["5m"]);
        Assert.Null(fields["spentPerHour"]!["60m"]);
    }

    [Fact]
    public void ExperienceKillsAndComponentsAreRatesPerHourOverEachWindow()
    {
        var host = new FakePluginHost();
        FakeAutomation automation = host.FakeAutomation;
        automation.FakeCharacter.TotalExperience = 1_000_000L;
        automation.FakeItems.Owned.Add(InventoryChangeEventsTests.Item(1u, "Prismatic Taper", 100));
        var trends = new TrendTracker(host);
        trends.Sample(0d);
        double now = TrendTracker.SettleSeconds;
        trends.Sample(now);

        for (int sample = 1; sample <= 300; sample++)
        {
            now += TrendTracker.SampleSeconds;
            automation.FakeCharacter.TotalExperience += 100;
            trends.Sample(now);
        }
        for (int sample = 1; sample <= 30; sample++)
        {
            now += TrendTracker.SampleSeconds;
            automation.FakeCharacter.TotalExperience += 1_000;
            automation.FakeItems.Owned[0] = InventoryChangeEventsTests.Item(1u, "Prismatic Taper", 100 - sample);
            if (sample % 6 == 0)
                automation.FakeCombat.Kills.Add(new PluginKill(sample, 0x80000001u, "Drudge Robber"));
            trends.Sample(now);
        }

        var fields = new JsonObject();
        Assert.True(trends.TryCapture(now, fields));
        Assert.Equal(300d, fields["countedSeconds"]!["5m"]!.GetValue<double>());
        Assert.Equal(3300d, fields["countedSeconds"]!["60m"]!.GetValue<double>());
        Assert.Equal(360_000d, fields["xpPerHour"]!["5m"]!.GetValue<double>());
        Assert.Equal(65_455d, fields["xpPerHour"]!["60m"]!.GetValue<double>());
        Assert.Equal(60d, fields["killsPerHour"]!["5m"]!.GetValue<double>());
        Assert.Equal(360d, fields["spentPerHour"]!["5m"]!["Prismatic Taper"]!.GetValue<double>());
        Assert.Equal(33d, fields["spentPerHour"]!["60m"]!["Prismatic Taper"]!.GetValue<double>());
        Assert.Empty(fields["gainedPerHour"]!["5m"]!.AsObject());
        Assert.Equal(0d, fields["secondsSinceXp"]!.GetValue<double>());
        Assert.Equal(0d, fields["secondsSinceKill"]!.GetValue<double>());
    }

    [Fact]
    public void ExperienceTheClientHearsOfLateIsNotCountedAsGained()
    {
        var host = new FakePluginHost();
        var trends = new TrendTracker(host);
        trends.Sample(0d);
        trends.Sample(10d);
        host.FakeAutomation.FakeCharacter.TotalExperience = 9_000_000_000L;
        for (double now = 20d; now <= 90d; now += 10d)
            trends.Sample(now);

        var fields = new JsonObject();
        Assert.True(trends.TryCapture(90d, fields));

        Assert.Equal(0d, fields["xpPerHour"]!["5m"]!.GetValue<double>());
    }

    [Fact]
    public void SecondsSinceTheCharacterMovedCountFromItsLastStepOfAMeterOrMore()
    {
        var host = new FakePluginHost();
        host.FakeAutomation.FakeNavigation.Snapshot = At(0d);
        var trends = new TrendTracker(host);
        trends.Sample(0d);
        trends.Sample(10d);
        host.FakeAutomation.FakeNavigation.Snapshot = At(2d / 240d);
        trends.Sample(20d);
        host.FakeAutomation.FakeNavigation.Snapshot = At(2.5d / 240d);
        trends.Sample(30d);
        trends.Sample(40d);

        var fields = new JsonObject();
        Assert.True(trends.TryCapture(40d, fields));

        Assert.Equal(20d, fields["secondsSinceMoved"]!.GetValue<double>());
    }

    private static PluginNavigationSnapshot At(double eastWest) =>
        new(true, false, 0x50000001u, new PluginNavigationPosition(0u, eastWest, 0d, 0d, 0f, true), false, false);
}
