namespace AcDream.App.UI;

/// <summary>
/// Flashes parts of the rendered figure in the paperdoll. The panel decides
/// WHICH parts (it knows what is worn where); the renderer decides how a flash
/// looks. Bit N of the mask is part N of the figure; a mask of 0 flashes nothing.
/// </summary>
public interface IPaperdollFigureLighting
{
    void FlashParts(uint partMask);

    /// <summary>Flash the whole figure, whatever it is made of. Selecting
    /// yourself lights everything, and a body can have more parts than any
    /// mask of named parts covers.</summary>
    void FlashWholeFigure();
}
