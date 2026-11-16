namespace DeadWare.Core;

/// <summary>A rectangle of screen pixels. Right and Bottom are exclusive.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public bool IsEmpty => Right <= Left || Bottom <= Top;
    public ScreenRect Intersect(ScreenRect other) =>
        new(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
}

/// <summary>Whether the windows above an area hide every pixel of it, worked out from their rectangles.</summary>
public static class ScreenCoverage
{
    /// <summary>The most pieces the uncovered part may break into before the answer is "not covered".</summary>
    public const int MaximumPieces = 64;

    /// <summary>
    /// True when <paramref name="covers"/> together cover all of <paramref name="area"/>, or the area is empty. The uncovered part is
    /// kept as pieces and each cover is cut out of them; when the pieces grow past <see cref="MaximumPieces"/> the answer is false, so
    /// a crowded screen costs little and is never wrongly called covered.
    /// </summary>
    public static bool Covered(ScreenRect area, IEnumerable<ScreenRect> covers)
    {
        if (area.IsEmpty) return true;
        var open = new List<ScreenRect> { area };
        var next = new List<ScreenRect>();
        foreach (var cover in covers)
        {
            if (cover.IsEmpty) continue;
            next.Clear();
            foreach (var piece in open) Subtract(piece, cover, next);
            (open, next) = (next, open);
            if (open.Count == 0) return true;
            if (open.Count > MaximumPieces) return false;
        }
        return false;
    }

    // Adds what is left of the piece outside the cover: the bands above and below it, then the parts to its left and right.
    private static void Subtract(ScreenRect piece, ScreenRect cover, List<ScreenRect> into)
    {
        var overlap = piece.Intersect(cover);
        if (overlap.IsEmpty) { into.Add(piece); return; }
        if (piece.Top < overlap.Top) into.Add(piece with { Bottom = overlap.Top });
        if (overlap.Bottom < piece.Bottom) into.Add(piece with { Top = overlap.Bottom });
        if (piece.Left < overlap.Left) into.Add(new(piece.Left, overlap.Top, overlap.Left, overlap.Bottom));
        if (overlap.Right < piece.Right) into.Add(new(overlap.Right, overlap.Top, piece.Right, overlap.Bottom));
    }
}
