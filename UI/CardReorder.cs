namespace UsageAI.UI;

/// <summary>A card was dropped before or after another; both are shown provider keys.</summary>
internal sealed class CardReorderEventArgs(string moved, string target, bool after) : EventArgs
{
    public string Moved { get; } = moved;

    public string Target { get; } = target;

    public bool After { get; } = after;
}

/// <summary>Where the drop line is drawn on a card.</summary>
internal enum DropMark
{
    None,
    Before,
    After,
}

/// <summary>
/// What a card being dragged over another shows, the same in the popup's tiles and the dashboard's
/// table. Each card moves on its own: the dragged card fades, and the line sits on the side of
/// the card under the pointer where it would land.
/// </summary>
internal static class CardReorder
{
    /// <param name="MarkIndex">The card that carries the drop line.</param>
    /// <param name="After">Whether the line, and the drop, is after that card.</param>
    internal sealed record Drop(int MarkIndex, bool After);

    /// <summary>
    /// The feedback for the card at <paramref name="dragged"/> held over the one at
    /// <paramref name="target"/>, in display order. <paramref name="pastMiddle"/> says the pointer
    /// is in the later half of the target card. Null when there is nothing to drop on.
    /// </summary>
    public static Drop? Resolve(int count, int dragged, int target, bool pastMiddle) =>
        dragged < 0 || target < 0 || dragged >= count || target >= count || dragged == target
            ? null
            : new Drop(target, pastMiddle);

    /// <summary>The neighbour a card moves past with the keyboard, or -1 at either end.</summary>
    public static int Neighbour(int count, int index, bool forward)
    {
        var next = index + (forward ? 1 : -1);
        return index >= 0 && index < count && next >= 0 && next < count ? next : -1;
    }
}

internal enum TileDragPhase
{
    Move,
    Drop,
    Cancel,
}

/// <summary>A popup tile being dragged: where the pointer is on screen, and whether the drag ended.</summary>
internal sealed class TileDragEventArgs(Point screenPoint, TileDragPhase phase) : EventArgs
{
    public Point ScreenPoint { get; } = screenPoint;

    public TileDragPhase Phase { get; } = phase;
}
