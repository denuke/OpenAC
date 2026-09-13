using System.Text.Json.Nodes;
using AcDream.Plugins.Agent.Contract;

namespace AcDream.Plugins.Agent.Tests.Contract;

public sealed class FactsTests
{
    [Fact]
    public void AnObservedValueCarriesItsPresenceAndAnEmptyReason()
    {
        Assert.Equal(
            """{"presence":"observed","value":329,"because":null}""",
            Json(Facts.Observed(329)));
    }

    [Fact]
    public void AnUnknownValueIsNeverAZero()
    {
        Assert.Equal(
            """{"presence":"unknown","value":null,"because":"not received yet"}""",
            Json(Facts.Unknown("not received yet")));
    }

    [Fact]
    public void AnUnsupportedValueSaysWhy()
    {
        Assert.Equal(
            """{"presence":"unsupported","value":null,"because":"no such surface"}""",
            Json(Facts.Unsupported("no such surface")));
    }

    [Fact]
    public void ADerivedValueNamesWhatItWasComputedFrom()
    {
        Assert.Equal(
            """{"presence":"observed","value":0.5,"from":"health","because":"current over maximum"}""",
            Json(Facts.Derived(0.5, "health", "current over maximum")));
    }

    [Fact]
    public void UnknownAndUnsupportedRequireAReason()
    {
        Assert.ThrowsAny<ArgumentException>(() => Facts.Unknown(" "));
        Assert.ThrowsAny<ArgumentException>(() => Facts.Unsupported(""));
    }

    [Fact]
    public void AnIdResolvesAdditively()
    {
        Assert.Equal(
            """{"id":"0x5000000A","resolved":false}""",
            Json(Facts.Id(0x5000000Au)));
        Assert.Equal(
            """{"id":"0x5000000A","resolved":true,"name":"Drudge Skulker"}""",
            Json(Facts.Id(0x5000000Au, "Drudge Skulker")));
    }

    private static string Json(JsonObject value) =>
        value.ToJsonString(AgentJson.Options);
}
