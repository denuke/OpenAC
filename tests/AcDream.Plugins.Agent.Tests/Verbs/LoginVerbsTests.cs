using System.Text.Json;
using AcDream.Plugin.Abstractions;
using AcDream.Plugins.Agent.Contract;
using AcDream.Plugins.Agent.Egress;
using AcDream.Plugins.Agent.Intake;
using AcDream.Plugins.Agent.Tests.Fakes;
using AcDream.Plugins.Agent.Verbs;

namespace AcDream.Plugins.Agent.Tests.Verbs;

public sealed class LoginVerbsTests
{
    private const uint Tester = 0x50000001u;
    private const uint Deleted = 0x50000002u;
    private const uint Mule = 0x50000003u;
    private const uint Muriel = 0x50000004u;

    [Fact]
    public void CharactersListsTheAccountsCharactersAndWhereTheClientStands()
    {
        var (_, verbs, _, ring) = AtTheCharacterList();

        Assert.Equal("handled", verbs.Handle(Line("characters")).Outcome);

        JsonElement listed = Kinds(ring, RecordKinds.Characters).Single();
        Assert.Equal("choosing-character", listed.GetProperty("stage").GetString());
        Assert.Equal("Testworld", listed.GetProperty("world").GetString());
        JsonElement[] characters = [.. listed.GetProperty("characters").EnumerateArray()];
        Assert.Equal(
            ["Tester", "Dan the Deleted", "Mule", "Muriel"],
            characters.Select(character => character.GetProperty("name").GetString()));
        Assert.Equal("0x50000001", characters[0].GetProperty("guid").GetString());
        Assert.True(characters[0].GetProperty("canEnter").GetBoolean());
        Assert.False(characters[1].GetProperty("canEnter").GetBoolean());
        Assert.True(characters[1].GetProperty("pendingDelete").GetBoolean());
    }

    [Fact]
    public void LoginEntersTheWorldAsTheNamedCharacterAndCompletesOnceItIsThere()
    {
        var (host, verbs, correlator, ring) = AtTheCharacterList();

        Assert.Equal("handled", verbs.Handle(Line("login tester")).Outcome);

        Assert.Equal([Tester], host.FakeAutomation.FakeLogin.Entered);
        Assert.Equal("Tester", Kinds(ring, RecordKinds.LoginSent).Single().GetProperty("name").GetString());
        correlator.Tick(inWorld: false);
        Assert.Empty(Kinds(ring, RecordKinds.LoginOutcome));

        host.FakeAutomation.IsAvailable = true;
        host.FakeAutomation.FakeCharacter.IsInWorld = true;
        correlator.Tick(inWorld: true);

        JsonElement outcome = Kinds(ring, RecordKinds.LoginOutcome).Single();
        Assert.Equal("completed", outcome.GetProperty("outcome").GetString());
        Assert.Equal("confirmed", outcome.GetProperty("class").GetString());
    }

    [Theory]
    [InlineData("login 0x50000003", Mule)]
    [InlineData("login mul", Mule)]
    [InlineData("login TESTER", Tester)]
    public void ACharacterIsNamedByIdByNameOrByTheStartOfItsName(string text, uint expected)
    {
        var (host, verbs, _, _) = AtTheCharacterList();

        Assert.Equal("handled", verbs.Handle(Line(text)).Outcome);

        Assert.Equal([expected], host.FakeAutomation.FakeLogin.Entered);
    }

    [Theory]
    [InlineData("login", "usage: login")]
    [InlineData("login Nobody", "no character on this account is named 'Nobody'")]
    [InlineData("login 0x50000009", "no character on this account has the id 0x50000009")]
    [InlineData("login mu", "more than one character's name starts with 'mu': Mule, Muriel")]
    [InlineData("login Dan the Deleted", "waiting to be deleted")]
    public void ALoginWithoutOneCharacterThatCanEnterIsRefused(string text, string reason)
    {
        var (host, verbs, _, ring) = AtTheCharacterList();

        Assert.Equal("refused", verbs.Handle(Line(text)).Outcome);

        Assert.Contains(reason, Kinds(ring, RecordKinds.LoginRefused).Single().GetProperty("reason").GetString());
        Assert.Empty(host.FakeAutomation.FakeLogin.Entered);
    }

    [Theory]
    [InlineData(PluginLoginStage.InWorld, "already in the world")]
    [InlineData(PluginLoginStage.EnteringWorld, "already entering the world")]
    [InlineData(PluginLoginStage.Connecting, "still connecting")]
    [InlineData(PluginLoginStage.None, "not connected")]
    public void ALoginAwayFromTheCharacterListIsRefused(PluginLoginStage stage, string reason)
    {
        var (host, verbs, _, _) = AtTheCharacterList();
        host.FakeAutomation.FakeLogin.State = host.FakeAutomation.FakeLogin.State with { Stage = stage };

        VerbResult result = verbs.Handle(Line("login Tester"));

        Assert.Equal("refused", result.Outcome);
        Assert.Contains(reason, result.Reason);
        Assert.Empty(host.FakeAutomation.FakeLogin.Entered);
    }

    [Fact]
    public void AnEnterTheClientRejectsIsRefusedWithItsMessage()
    {
        var (host, verbs, _, ring) = AtTheCharacterList();
        FakeLogin login = host.FakeAutomation.FakeLogin;
        login.EnterStatus = PluginLoginCommandStatus.Rejected;
        login.State = login.State with { Error = "The selected character is temporarily unavailable." };

        VerbResult result = verbs.Handle(Line("login Tester"));

        Assert.Equal("refused", result.Outcome);
        Assert.Equal("The selected character is temporarily unavailable.", result.Reason);
        Assert.Empty(Kinds(ring, RecordKinds.LoginSent));
    }

    [Fact]
    public void ALoginTheServerTurnsBackEndsRefusedWithTheClientsMessage()
    {
        var (host, verbs, correlator, ring) = AtTheCharacterList();
        verbs.Handle(Line("login Tester"));
        FakeLogin login = host.FakeAutomation.FakeLogin;

        login.State = login.State with
        {
            Stage = PluginLoginStage.ChoosingCharacter,
            Error = "One of this account's characters is still in the world. Please try again shortly.",
        };
        correlator.Tick(inWorld: false);

        JsonElement outcome = Kinds(ring, RecordKinds.LoginOutcome).Single();
        Assert.Equal("refused", outcome.GetProperty("outcome").GetString());
        Assert.Contains("still in the world", outcome.GetProperty("reason").GetString());
    }

    [Fact]
    public void LogoutLogsTheCharacterOutAndCompletesOnceTheCharacterListIsBack()
    {
        var (host, verbs, correlator, ring) = InTheWorld();

        Assert.Equal("handled", verbs.Handle(Line("logout")).Outcome);

        Assert.Equal(1, host.FakeAutomation.FakeLogin.LogOuts);
        Assert.Equal("Tester", Kinds(ring, RecordKinds.LoginSent).Single().GetProperty("name").GetString());
        correlator.Tick(inWorld: true);
        host.FakeAutomation.IsAvailable = false;
        host.FakeAutomation.FakeCharacter.IsInWorld = false;
        correlator.Tick(inWorld: false);
        Assert.Empty(Kinds(ring, RecordKinds.LoginOutcome));

        host.FakeAutomation.FakeLogin.State = host.FakeAutomation.FakeLogin.State with { Stage = PluginLoginStage.ChoosingCharacter };
        correlator.Tick(inWorld: false);

        Assert.Equal("completed", Kinds(ring, RecordKinds.LoginOutcome).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public void LogoutWithNoCharacterInTheWorldIsRefused()
    {
        var (host, verbs, _, _) = AtTheCharacterList();

        VerbResult result = verbs.Handle(Line("logout"));

        Assert.Equal("refused", result.Outcome);
        Assert.Equal("no character is in the world", result.Reason);
        Assert.Equal(0, host.FakeAutomation.FakeLogin.LogOuts);
    }

    [Fact]
    public void LogoutInMidAirIsRefused()
    {
        var (host, verbs, _, _) = InTheWorld();
        host.FakeAutomation.FakeNavigation.Snapshot = new PluginNavigationSnapshot(
            IsAvailable: true,
            IsPortalSpace: false,
            LocalObjectId: Tester,
            Position: default,
            IsMoving: true,
            IsAirborne: true);

        VerbResult result = verbs.Handle(Line("logout"));

        Assert.Equal("refused", result.Outcome);
        Assert.Equal("the character cannot log out in mid-air", result.Reason);
        Assert.Equal(0, host.FakeAutomation.FakeLogin.LogOuts);
    }

    [Theory]
    [InlineData(PluginLoginCommandStatus.Rejected, "did not log the character out")]
    [InlineData(PluginLoginCommandStatus.Unavailable, "cannot log a character out")]
    public void ALogoutTheClientDoesNotSendIsRefused(PluginLoginCommandStatus status, string reason)
    {
        var (host, verbs, _, ring) = InTheWorld();
        host.FakeAutomation.FakeLogin.LogOutStatus = status;

        VerbResult result = verbs.Handle(Line("logout"));

        Assert.Equal("refused", result.Outcome);
        Assert.Contains(reason, result.Reason);
        Assert.Empty(Kinds(ring, RecordKinds.LoginSent));
    }

    [Fact]
    public void AClientThatCannotLogInForAPluginRefusesEveryLine()
    {
        var (host, verbs, _, _) = AtTheCharacterList();
        host.FakeAutomation.FakeLogin.Available = false;

        Assert.All(
            new[] { "characters", "login Tester", "logout" },
            text => Assert.Equal("refused", verbs.Handle(Line(text)).Outcome));
    }

    private static (FakePluginHost Host, LoginVerbs Verbs, OutcomeCorrelator Correlator, RecordRing Ring) AtTheCharacterList()
    {
        var (host, verbs, correlator, ring) = InTheWorld();
        host.FakeAutomation.IsAvailable = false;
        host.FakeAutomation.FakeCharacter.IsInWorld = false;
        host.FakeAutomation.FakeLogin.State = new PluginLoginSnapshot(
            PluginLoginStage.ChoosingCharacter,
            "account",
            "Testworld",
            Tester,
            null);
        return (host, verbs, correlator, ring);
    }

    private static (FakePluginHost Host, LoginVerbs Verbs, OutcomeCorrelator Correlator, RecordRing Ring) InTheWorld()
    {
        var host = new FakePluginHost();
        FakeLogin login = host.FakeAutomation.FakeLogin;
        login.Roster.Add(new PluginLoginCharacter(Tester, "Tester", 0, IsPendingDelete: false));
        login.Roster.Add(new PluginLoginCharacter(Deleted, "Dan the Deleted", 1, IsPendingDelete: true));
        login.Roster.Add(new PluginLoginCharacter(Mule, "Mule", 2, IsPendingDelete: false));
        login.Roster.Add(new PluginLoginCharacter(Muriel, "Muriel", 3, IsPendingDelete: false));
        var clock = new AgentClock();
        var ring = new RecordRing(64);
        var publisher = new Publisher(clock, ring);
        var correlator = new OutcomeCorrelator(publisher, clock);
        return (host, new LoginVerbs(host, publisher, correlator), correlator, ring);
    }

    private static CommandLine Line(string text) => CommandLine.Parse(9, text, "mcp");

    private static IEnumerable<JsonElement> Kinds(RecordRing ring, string kind) =>
        ring.Read(-1, new HashSet<string> { kind }).Records
            .Select(record => JsonDocument.Parse(record.Json).RootElement);
}
