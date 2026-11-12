using UsageAI.Models;

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
/// table. A provider's accounts stay together: over an account of the same provider the card goes
/// beside it; over another provider the whole provider moves, so every card of the dragged
/// provider dims and the line sits at the edge of the provider under the pointer, never between two
/// of its accounts.
/// </summary>
internal static class CardReorder
{
    /// <param name="MarkIndex">The card that carries the drop line.</param>
    /// <param name="After">Whether the line, and the drop, is after that card.</param>
    /// <param name="Dimmed">The cards that would move.</param>
    internal sealed record Drop(int MarkIndex, bool After, IReadOnlyList<int> Dimmed);

    /// <summary>
    /// The feedback for the card at <paramref name="dragged"/> held over the one at
    /// <paramref name="target"/>, in display order. <paramref name="pastMiddle"/> says the pointer
    /// is in the later half of the target card. Null when there is nothing to drop on.
    /// </summary>
    public static Drop? Resolve(IReadOnlyList<string> keys, int dragged, int target, bool pastMiddle)
    {
        if (dragged < 0 || target < 0 || dragged >= keys.Count || target >= keys.Count || dragged == target)
        {
            return null;
        }

        var indexes = Enumerable.Range(0, keys.Count);
        if (SameProvider(keys[dragged], keys[target]))
        {
            return new Drop(target, pastMiddle, new[] { dragged });
        }

        var group = indexes.Where(index => SameProvider(keys[index], keys[target])).ToArray();
        // Halves of cards: the pointer is past the group's middle once it has that many behind it.
        var after = Array.IndexOf(group, target) * 2 + (pastMiddle ? 1 : 0) >= group.Length;
        return new Drop(
            after ? group[^1] : group[0],
            after,
            indexes.Where(index => SameProvider(keys[index], keys[dragged])).ToArray());
    }

    /// <summary>The neighbour a card moves past with the keyboard, or -1 at either end.</summary>
    public static int Neighbour(IReadOnlyList<string> keys, int index, bool forward)
    {
        var next = index + (forward ? 1 : -1);
        return index >= 0 && index < keys.Count && next >= 0 && next < keys.Count ? next : -1;
    }

    private static bool SameProvider(string left, string right) =>
        ProviderKey.ProviderOf(left).Equals(ProviderKey.ProviderOf(right), StringComparison.OrdinalIgnoreCase);
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
