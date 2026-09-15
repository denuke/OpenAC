namespace AcDream.App.UI;

/// <summary>
/// Stands in for the figure lighting until the renderer that provides it exists.
/// The panel is bound long before the viewport is, so it holds this and the
/// target is read at the moment of the flash, never captured at bind time.
/// </summary>
public sealed class PaperdollFigureLightingRelay : IPaperdollFigureLighting
{
    public IPaperdollFigureLighting? Target { get; set; }

    public void FlashParts(uint partMask) => Target?.FlashParts(partMask);

    public void FlashWholeFigure() => Target?.FlashWholeFigure();
}
