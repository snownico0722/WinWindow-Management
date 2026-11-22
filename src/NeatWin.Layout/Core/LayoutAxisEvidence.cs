namespace NeatWin.Core;

// WindowIndex indexes the enclosing group's ordered members, including during replay with new session handles.
public sealed record LayoutAxisCoverage(int WindowIndex, string Axis, int[] Generated, int[] Feasible);
public sealed record HeldLayoutAxes(int WindowIndex, string Axes, double OriginalCost, double? GainFromMoving);

internal sealed class LayoutAxisEvidence
{
    private readonly Dictionary<(int, PlacementAxis), (HashSet<int> Generated, HashSet<int> Feasible)> _values = [];

    internal void Observe(RectI[] frames, bool feasible)
    {
        for (int i = 0; i < frames.Length; i++)
        for (var axis = PlacementAxis.X; axis <= PlacementAxis.Height; axis++)
        {
            if (!_values.TryGetValue((i, axis), out var values))
                _values[(i, axis)] = values = ([], []);
            int v = ShadowAxis.Get(frames[i], axis);
            values.Generated.Add(v);
            if (feasible) values.Feasible.Add(v);
        }
    }

    internal LayoutAxisCoverage[] Export() => _values.Select(p => new LayoutAxisCoverage(p.Key.Item1,
        p.Key.Item2.ToString(), p.Value.Generated.Order().ToArray(), p.Value.Feasible.Order().ToArray())).ToArray();
}
