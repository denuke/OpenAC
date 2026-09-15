using AcDream.App.UI;

namespace AcDream.App.Rendering.Selection;

/// <summary>Flashes the paperdoll figure's parts through the shared selection
/// pulse, against the doll's own entity rather than anything in the world.</summary>
internal sealed class DollFigureLighting : IPaperdollFigureLighting
{
    private readonly RetailSelectionScene _scene;

    public DollFigureLighting(RetailSelectionScene scene)
        => _scene = scene ?? throw new System.ArgumentNullException(nameof(scene));

    public void FlashParts(uint partMask)
        => _scene.BeginPartLightingPulse(
            DollEntityBuilder.DollServerGuid,
            DollEntityBuilder.DollRenderId,
            partMask);

    public void FlashWholeFigure()
        => _scene.BeginWholeFigureLightingPulse(
            DollEntityBuilder.DollServerGuid,
            DollEntityBuilder.DollRenderId);
}
