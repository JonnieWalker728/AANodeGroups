using NodeGroups.Core;
using Xunit;

namespace NodeGroups.Core.Tests;

public sealed class NodePlacementTests
{
    [Fact]
    public void EntireMultiNodeFootprintMovesBelowExistingMembersWithOneCommonDelta()
    {
        var existing = new[]
        {
            new NodeState("existing-1", new(100, 200), new(200, 120)),
            new NodeState("existing-2", new(300, 80), new(300, 90))
        };
        var incoming = new[]
        {
            new NodeState("a", new(10, 20), new(80, 40)),
            new NodeState("b", new(190, 90), new(140, 60)),
            new NodeState("c", new(40, -50), new(100, 70))
        };
        var delta = NodePlacement.FindTranslationToFreeSpace(existing, incoming, default, "a");
        var placed = incoming.Select(node => node with { Position = node.Position + delta }).ToArray();
        Assert.Equal(incoming[1].Position - incoming[0].Position, placed[1].Position - placed[0].Position);
        Assert.Equal(incoming[2].Position - incoming[0].Position, placed[2].Position - placed[0].Position);
        foreach (var added in placed)
        foreach (var member in existing) Assert.False(Intersects(added.Bounds, member.Bounds));
        Assert.Equal(existing.Min(node => node.Bounds.Y) - 24,
            placed.Max(node => node.Bounds.Y + node.Bounds.Height));
        Assert.Equal(existing.Min(node => node.Bounds.X), placed.Min(node => node.Bounds.X));
    }

    [Fact]
    public void EmptyGroupPlacesPrimaryAtFallbackAnchorAndPreservesOtherPositions()
    {
        var incoming = new[] { WorkspaceTests.Node("a", 10, 20), WorkspaceTests.Node("b", 100, 80) };
        var anchor = new Point2(320, -140);
        var delta = NodePlacement.FindTranslationToFreeSpace(Array.Empty<NodeState>(), incoming, anchor, "b");
        Assert.Equal(anchor, incoming[1].Position + delta);
        Assert.Equal(new Point2(-90, -60), (incoming[0].Position + delta) - (incoming[1].Position + delta));
    }

    [Fact]
    public void MovingMembersAlreadyInTargetDoesNotCollideWithTheirOwnPreviousBounds()
    {
        var incoming = new[] { WorkspaceTests.Node("a", 10, 20) };
        var delta = NodePlacement.FindTranslationToFreeSpace(incoming, incoming, new Point2(100, 100), "a");
        Assert.Equal(new Point2(90, 80), delta);
    }

    [Fact]
    public void InvalidCoordinatesSizesInputsAndOverflowAreRejectedWithoutMutatingCollections()
    {
        var existing = new List<NodeState> { WorkspaceTests.Node("existing", 10, 20) };
        var incoming = new List<NodeState> { WorkspaceTests.Node("a", 100, 200) };
        Assert.Throws<ArgumentException>(() => NodePlacement.FindTranslationToFreeSpace(existing, incoming, new Point2(double.NaN, 0), "a"));
        Assert.Throws<ArgumentException>(() => NodePlacement.FindTranslationToFreeSpace(existing, Array.Empty<NodeState>(), default, "a"));
        Assert.Throws<ArgumentException>(() => NodePlacement.FindTranslationToFreeSpace(existing, incoming, default, "missing"));
        Assert.Throws<ArgumentException>(() => NodePlacement.FindTranslationToFreeSpace(existing, new[] { incoming[0] with { Size = new Point2(-1, 40) } }, default, "a"));
        Assert.Throws<ArgumentException>(() => NodePlacement.FindTranslationToFreeSpace(existing, new[] { incoming[0] with { Position = new Point2(double.PositiveInfinity, 0) } }, default, "a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => NodePlacement.FindTranslationToFreeSpace(existing, incoming, default, "a", gap: 0));
        var huge = new NodeState("huge", new(-double.MaxValue, 0), default);
        Assert.Throws<ArgumentException>(() => NodePlacement.FindTranslationToFreeSpace(Array.Empty<NodeState>(), new[] { huge }, new(double.MaxValue, 0), "huge"));
        Assert.Equal(new Point2(10, 20), Assert.Single(existing).Position);
        Assert.Equal(new Point2(100, 200), Assert.Single(incoming).Position);
    }

    private static bool Intersects(Rect2 first, Rect2 second) => first.X <= second.X + second.Width &&
        first.X + first.Width >= second.X && first.Y <= second.Y + second.Height && first.Y + first.Height >= second.Y;
}
