using AcDream.Core.Player;

namespace AcDream.Core.Tests.Player;

// OpenAC #88: the client's built-in skill-name list, which an appraisal's
// weapon line and the salvage message both read. It is not the authored skill
// data a skill window reads, and the two disagree on purpose.
public sealed class RetailSkillNamesTests
{
    [Fact]
    public void ItNamesEverySkillItEverKnew_AndNotShield()
    {
        for (int skill = 1; skill <= 54; skill++)
        {
            if (skill == 48)
                continue;
            Assert.True(
                RetailSkillNames.TryGetName(skill, out string? name),
                $"skill {skill} should have a built-in name");
            Assert.False(string.IsNullOrWhiteSpace(name));
        }

        // Shield came after the list was written; only the authored data names it.
        Assert.False(RetailSkillNames.TryGetName(48, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(55)]
    [InlineData(200)]
    public void ItNamesNothingOutsideItsRange(int skillId)
        => Assert.False(RetailSkillNames.TryGetName(skillId, out _));

    // The three the authored data renamed or dropped. Do not "correct" these
    // to the authored spellings: the lines that read this list show these.
    [Theory]
    [InlineData(19, "Person Appraisal")]
    [InlineData(26, "Armor Repair")]
    [InlineData(27, "Creature Appraisal")]
    public void ItKeepsTheOriginalNames(int skillId, string expected)
    {
        Assert.True(RetailSkillNames.TryGetName(skillId, out string? name));
        Assert.Equal(expected, name);
    }

    [Fact]
    public void DescribeNumbersASkillNothingNames()
    {
        Assert.Equal("Missile Weapons", RetailSkillNames.Describe(47));
        Assert.Equal("Skill 48", RetailSkillNames.Describe(48));
        Assert.Equal("Skill 200", RetailSkillNames.Describe(200));
    }
}
