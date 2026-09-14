using AcDream.App.Tests.Rendering;
using AcDream.Content;
using AcDream.Core.Spells;
using DatReaderWriter;
using DatReaderWriter.Options;
using Xunit;

namespace AcDream.App.Tests.Plugins;

[Trait("Lane", "InstalledDat")]
public sealed class SpellProjectileInstalledDatTests
{
    [Fact]
    public void InstalledBoltsStreaksArcsAndLifeProjectilesAreProjectilesAndDebuffsAndDrainsAreNot()
    {
        string? datDir = InstalledDatTestPath.Resolve();
        if (datDir is null)
        {
            Assert.Fail(
                "Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
            return;
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var adapter = new DatCollectionAdapter(dats);
        MagicCatalog catalog = MagicCatalog.Load(adapter);

        // Force Bolt I, Flame Streak I, Flame Arc I and Martyr's Hecatomb I fly to their target.
        foreach (uint spellId in new uint[] { 86u, 1796u, 2739u, 2760u })
        {
            Assert.True(catalog.SpellTable.TryGet(spellId, out SpellMetadata spell), $"spell {spellId} is missing");
            Assert.True(spell.IsProjectile, $"{spell.Name} ({spellId}) should be a projectile");
        }

        // Imperil Other I, Drain Health Other I and Harm Other I do not.
        foreach (uint spellId in new uint[] { 25u, 1237u, 7u })
        {
            Assert.True(catalog.SpellTable.TryGet(spellId, out SpellMetadata spell), $"spell {spellId} is missing");
            Assert.False(spell.IsProjectile, $"{spell.Name} ({spellId}) should not be a projectile");
        }
    }
}
