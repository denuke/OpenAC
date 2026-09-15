using System;

namespace AcDream.App.UI;

/// <summary>
/// The pointer surface of an element that is an interactive mask over an image
/// map: the region under the pointer, not the element itself, decides what a
/// click, a right click, a lift or a drop means. Every coordinate handed to a
/// hook is element-local, so a hook can look the point straight up in its map.
/// <para>
/// This lives on <see cref="UiElement"/> rather than on one widget class because
/// which class a mask imports as is an authoring detail: the same mask can be
/// authored as a button on one panel and as a plain element on another, and a
/// panel that binds behaviour to it should not have to care which.
/// </para>
/// </summary>
public sealed class UiPointerRegion
{
    /// <summary>Left click at element-local (x, y).</summary>
    public Action<int, int>? Clicked { get; set; }

    /// <summary>Right click at element-local (x, y).</summary>
    public Action<int, int>? RightClicked { get; set; }

    /// <summary>Drag payload for a lift from the press point. A non-null hook
    /// makes the element a drag source; returning null refuses the lift.</summary>
    public Func<int, int, object?>? DragPayloadAt { get; set; }

    /// <summary>Ghost art for a lift from the press point.</summary>
    public Func<int, int, (uint tex, int w, int h)?>? DragGhostAt { get; set; }

    /// <summary>A drag carrying the payload is over element-local (x, y).</summary>
    public Action<object?, int, int>? DragOverAt { get; set; }

    /// <summary>The drag left this element.</summary>
    public Action? DragLeft { get; set; }

    /// <summary>A drag carrying the payload was released at element-local (x, y).</summary>
    public Action<object?, int, int>? DropReleasedAt { get; set; }

    /// <summary>Element-local point of the most recent press. A lift carries no
    /// coordinates of its own, so the press point is what it started from.</summary>
    internal int PressX { get; set; }

    /// <inheritdoc cref="PressX"/>
    internal int PressY { get; set; }
}
