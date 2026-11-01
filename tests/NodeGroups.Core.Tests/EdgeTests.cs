using NodeGroups.Core;
using Xunit;

namespace NodeGroups.Core.Tests;

public sealed class EdgeTests
{
    [Fact]
    public void ProjectionClassifiesHiddenInternalExternalAndBetweenGroupsWithoutMutatingGraph()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("one", "一", new[] { "a", "b" }, default);
        workspace.CreateGroup("two", "二", new[] { "c" }, default);
        workspace.SetCollapsed("one", true);
        var edges = new[]
        {
            new EdgeState("internal", "a", "b"),
            new EdgeState("external", "b", "c")
        };
        var before = workspace.Snapshot;
        var result = EdgeProjector.Project(before, edges);
        Assert.Equal(EdgeVisibility.HiddenInternal, result[0].Visibility);
        Assert.Equal(EdgeVisibility.Projected, result[1].Visibility);
        Assert.Equal(new ProjectedEndpoint(EndpointKind.Group, "one"), result[1].From);
        Assert.Equal(new ProjectedEndpoint(EndpointKind.Node, "c"), result[1].To);
        workspace.SetCollapsed("two", true);
        var between = EdgeProjector.Project(workspace.Snapshot, edges)[1];
        Assert.Equal(new ProjectedEndpoint(EndpointKind.Group, "two"), between.To);
        Assert.Equal(new Point2(500, 0), workspace.Snapshot.GetNode("c").Position);
        Assert.Equal("b", edges[1].FromNodeId);
        Assert.Equal("c", edges[1].ToNodeId);
    }

    [Fact]
    public void ExpandedGroupsKeepOriginalEdgesAndOptionsWithSameTargetStayIndependent()
    {
        var workspace = WorkspaceTests.Workspace();
        workspace.CreateGroup("g", "组", new[] { "a" }, default);
        var edges = new[]
        {
            new EdgeState("choice1", "a", "c", "option-1", "左边"),
            new EdgeState("choice2", "a", "c", "option-2", "右边")
        };
        Assert.All(EdgeProjector.Project(workspace.Snapshot, edges), e => Assert.Equal(EdgeVisibility.Original, e.Visibility));
        workspace.SetCollapsed("g", true);
        var result = EdgeProjector.Project(workspace.Snapshot, edges);
        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "option-1", "option-2" }, result.Select(e => e.Source.OptionId));
        Assert.Equal(new[] { "左边", "右边" }, result.Select(e => e.Source.Text));
        Assert.All(result, e => Assert.Equal(EdgeVisibility.Projected, e.Visibility));
    }
}
