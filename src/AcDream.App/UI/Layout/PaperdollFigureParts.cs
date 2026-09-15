using AcDream.Core.Items;

namespace AcDream.App.UI.Layout;

/// <summary>
/// Which of the figure's parts belong to the item the player just selected.
/// <para>
/// Selecting an item flashes the parts of the rendered figure that item covers -
/// a helm lights the head alone, a hauberk lights chest, abdomen and both arms.
/// Two steps decide that: first the selected item is matched against every body
/// location it is the outermost worn item at, which yields a set of body-location
/// bits; then each of those bits names the parts of the figure that location is
/// drawn from. Selecting the player themselves lights the whole figure.
/// </para>
/// </summary>
internal static class PaperdollFigureParts
{
    /// <summary>A body location, and the bit that stands for it. The location
    /// masks pair each wear slot with its armor slot, because one item can be
    /// the outermost thing at either.</summary>
    private static readonly EquipMask[] BodyLocations =
    {
        EquipMask.HeadWear,                                          // 0x0001
        EquipMask.ChestWear    | EquipMask.ChestArmor,               // 0x0202
        EquipMask.AbdomenWear  | EquipMask.AbdomenArmor,             // 0x0404
        EquipMask.UpperArmWear | EquipMask.UpperArmArmor,            // 0x0808
        EquipMask.LowerArmWear | EquipMask.LowerArmArmor,            // 0x1010
        EquipMask.HandWear,                                          // 0x0020
        EquipMask.UpperLegWear | EquipMask.UpperLegArmor,            // 0x2040
        EquipMask.LowerLegWear | EquipMask.LowerLegArmor,            // 0x4080
        EquipMask.FootWear,                                          // 0x0100
    };

    /// <summary>The parts each body location is drawn from, by index into the
    /// figure's part list. Limbs come in pairs; the feet carry four.</summary>
    private static readonly int[][] PartsByBodyLocation =
    {
        new[] { 0x10 },                     // head
        new[] { 0x09 },                     // chest
        new[] { 0x00 },                     // abdomen
        new[] { 0x0A, 0x0D },               // upper arms
        new[] { 0x0B, 0x0E },               // lower arms
        new[] { 0x0C, 0x0F },               // hands
        new[] { 0x01, 0x05 },               // upper legs
        new[] { 0x02, 0x06 },               // lower legs
        new[] { 0x03, 0x07, 0x04, 0x08 },   // feet
    };

    /// <summary>Highest part index this mapping can name. A figure with more
    /// parts than this still flashes whole when the player selects themselves.</summary>
    public const int HighestPartIndex = 0x10;

    /// <summary>What to flash for a selected object: the whole figure, some of
    /// its parts, or nothing.</summary>
    public readonly record struct FigureFlash(bool WholeFigure, uint PartMask)
    {
        public bool Any => WholeFigure || PartMask != 0u;

        public static readonly FigureFlash None = new(false, 0u);
    }

    /// <summary>Resolve the flash for a selected object. Selecting yourself
    /// flashes the whole figure; an item flashes the parts it is the outermost
    /// worn thing at; anything else flashes nothing.</summary>
    public static FigureFlash Resolve(
        ClientObjectTable objects,
        uint playerId,
        uint selectedObjectId)
    {
        if (objects is null || playerId == 0u || selectedObjectId == 0u)
            return FigureFlash.None;
        if (selectedObjectId == playerId)
            return new FigureFlash(WholeFigure: true, PartMask: 0u);

        Span<uint> outermost = stackalloc uint[BodyLocations.Length];
        PaperdollSelectionPolicy.GetUpperInventoryObjects(
            objects, playerId, BodyLocations, outermost);

        uint parts = 0u;
        for (int location = 0; location < outermost.Length; location++)
        {
            if (outermost[location] != selectedObjectId)
                continue;
            foreach (int part in PartsByBodyLocation[location])
                parts |= 1u << part;
        }
        return new FigureFlash(WholeFigure: false, PartMask: parts);
    }
}
