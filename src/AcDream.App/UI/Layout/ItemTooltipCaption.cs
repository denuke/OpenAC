using System;
using AcDream.Core.Items;

namespace AcDream.App.UI.Layout;

/// <summary>
/// One owner for the caption an item element shows while the cursor rests on
/// it. Every item surface reads the same name flavour the selection caption
/// uses - the composed name including the material prefix - with the stack
/// count in front once the stack holds more than one. The resolver is
/// consulted per hover, never captured into a name at mount time, so a panel
/// built before the name tables loaded still shows the composed name. Every
/// surface must supply one: a cell allowed to fall back to the plain name
/// would silently disagree with the selection caption again.
/// </summary>
internal static class ItemTooltipCaption
{
    /// <summary>The caption for one object, or null when it is not tracked.</summary>
    public static string? Resolve(
        ClientObjectTable objects,
        uint guid,
        Func<ClientObject, string> resolveAppropriateName)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(resolveAppropriateName);
        return objects.Get(guid) is { } obj
            ? obj.GetTooltipDisplayName(resolveAppropriateName(obj))
            : null;
    }
}
