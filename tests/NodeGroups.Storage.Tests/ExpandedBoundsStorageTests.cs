using System.Text.Json.Nodes;
using NodeGroups.Core;
using NodeGroups.Storage;
using Xunit;

namespace NodeGroups.Storage.Tests;

/// <summary>验证手动调整组区域的 schema 1 兼容性与输入边界。</summary>
public sealed class ExpandedBoundsStorageTests : IDisposable
{
    private const string ProjectId = "bounds-project";
    private static readonly string Hash = new('a', 64);
    private static readonly string GroupId = Guid.NewGuid().ToString("D");
    private static readonly string NodeId = Guid.NewGuid().ToString("D");
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "TestResults", "bounds", Guid.NewGuid().ToString("N"));
    private readonly GroupStore store = new();
    private string ProjectPath => Path.Combine(root, "resized.aap2");
    private string SidecarPath => GroupStore.GetSidecarPath(ProjectPath);

    public ExpandedBoundsStorageTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacySchemaOne_WithoutRegionOrWithNullRegion_PreservesAutomaticLayoutAndStacking(bool explicitNull)
    {
        var document = LegacyDocument();
        if (explicitNull) document["groups"]![0]!["expandedBounds"] = null;
        File.WriteAllText(SidecarPath, document.ToJsonString());

        var loaded = store.Load(ProjectPath, ProjectId, Hash, new[] { NodeId });

        Assert.Equal(GroupLoadStatus.Loaded, loaded.Status);
        Assert.Equal(1, GroupStore.SchemaVersion);
        Assert.Null(loaded.Snapshot!.Groups[0].ExpandedBounds);
        Assert.Equal(7, loaded.Snapshot.Groups[0].ZIndex);
        Assert.Equal(new[] { NodeId }, loaded.Snapshot.Groups[0].MemberNodeIds);
    }

    [Fact]
    public void SaveLoad_RoundTripsUserRegionWhileCollapsedAndKeepsItWhenExpanded()
    {
        var bounds = new Rect2(-600, -200, 880, 460);
        var group = new GroupState(GroupId, "自定义区域", new[] { NodeId }, true, new Point2(80, 90),
            "#4C88B8", 15, bounds);
        var saved = store.Save(ProjectPath, new GroupFileSnapshot(ProjectId, Hash, new[] { group }));

        Assert.True(saved.Success);
        var loaded = store.Load(ProjectPath, ProjectId, Hash, new[] { NodeId });
        Assert.Equal(GroupLoadStatus.Loaded, loaded.Status);
        var actual = loaded.Snapshot!.Groups[0];
        Assert.Equal(bounds, actual.ExpandedBounds);
        Assert.True(actual.Collapsed);
        Assert.Equal(15, actual.ZIndex);
        var region = JsonNode.Parse(File.ReadAllText(SidecarPath))!["groups"]![0]!["expandedBounds"]!;
        Assert.Equal(new[] { "x", "y", "width", "height" }, region.AsObject().Select(p => p.Key));
        Assert.Equal(880, region["width"]!.GetValue<double>());

        var expanded = new GroupState(actual.Id, actual.Name, actual.MemberNodeIds, false, actual.Anchor,
            actual.Color, actual.ZIndex, actual.ExpandedBounds);
        Assert.True(store.Save(ProjectPath, new GroupFileSnapshot(ProjectId, Hash, new[] { expanded })).Success);
        Assert.Equal(bounds, store.Load(ProjectPath, ProjectId, Hash, new[] { NodeId }).Snapshot!.Groups[0].ExpandedBounds);
    }

    [Fact]
    public void HashMismatchAndMissingMembers_PreserveUserRegionAndLayerWithoutRewritingFile()
    {
        var bounds = new Rect2(-600, -200, 880, 460);
        var group = new GroupState(GroupId, "自定义区域", new[] { NodeId }, false, new Point2(80, 90),
            "#4C88B8", -4, bounds);
        Assert.True(store.Save(ProjectPath, new GroupFileSnapshot(ProjectId, Hash, new[] { group })).Success);
        var original = File.ReadAllText(SidecarPath);

        var loaded = store.Load(ProjectPath, ProjectId, new string('b', 64), Array.Empty<string>());

        Assert.Equal(GroupLoadStatus.Recovered, loaded.Status);
        Assert.True(loaded.RequiresLayoutRecalculation);
        Assert.Equal(bounds, loaded.Snapshot!.Groups[0].ExpandedBounds);
        Assert.Equal(-4, loaded.Snapshot.Groups[0].ZIndex);
        Assert.Empty(loaded.Snapshot.Groups[0].MemberNodeIds);
        Assert.Equal(original, File.ReadAllText(SidecarPath));
    }

    [Theory]
    [InlineData(0, 0, 0, 10)]
    [InlineData(0, 0, 10, -1)]
    [InlineData(9999999, 0, 2, 10)]
    [InlineData(0, 9999999, 10, 2)]
    [InlineData(double.NaN, 0, 10, 10)]
    [InlineData(0, 0, double.PositiveInfinity, 10)]
    public void InvalidManagedRegion_CannotReplacePreviouslySavedMetadata(double x, double y, double width, double height)
    {
        var initial = new GroupState(GroupId, "旧数据", new[] { NodeId }, false, new Point2(80, 90));
        Assert.True(store.Save(ProjectPath, new GroupFileSnapshot(ProjectId, Hash, new[] { initial })).Success);
        var original = File.ReadAllText(SidecarPath);
        var invalid = new GroupState(GroupId, "新数据", new[] { NodeId }, false, new Point2(80, 90),
            expandedBounds: new Rect2(x, y, width, height));

        var saved = store.Save(ProjectPath, new GroupFileSnapshot(ProjectId, Hash, new[] { invalid }));

        Assert.False(saved.Success);
        Assert.Contains(saved.Diagnostics, d => d.Code == "InvalidExpandedBounds");
        Assert.Equal(original, File.ReadAllText(SidecarPath));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public void RegionThatSpansEntireAllowedCanvas_IsAcceptedByItsEdges()
    {
        var bounds = new Rect2(-10000000, -10000000, 20000000, 20000000);
        var group = new GroupState(GroupId, "全范围区域", Array.Empty<string>(), false, new Point2(0, 0),
            expandedBounds: bounds);

        Assert.True(store.Save(ProjectPath, new GroupFileSnapshot(ProjectId, Hash, new[] { group })).Success);
        Assert.Equal(bounds, store.Load(ProjectPath, ProjectId, Hash, Array.Empty<string>()).Snapshot!.Groups[0].ExpandedBounds);
    }

    [Theory]
    [InlineData("\"rectangle\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"x\":0,\"y\":0,\"width\":10}")]
    [InlineData("{\"x\":0,\"y\":0,\"width\":\"10\",\"height\":10}")]
    [InlineData("{\"x\":0,\"y\":0,\"width\":0,\"height\":10}")]
    [InlineData("{\"x\":0,\"y\":0,\"width\":10,\"height\":-1}")]
    [InlineData("{\"x\":10000001,\"y\":0,\"width\":10,\"height\":10}")]
    [InlineData("{\"x\":0,\"y\":-10000001,\"width\":10,\"height\":10}")]
    [InlineData("{\"x\":9999999,\"y\":0,\"width\":2,\"height\":10}")]
    [InlineData("{\"x\":0,\"y\":9999999,\"width\":10,\"height\":2}")]
    [InlineData("{\"x\":0,\"y\":0,\"width\":1e309,\"height\":10}")]
    public void MalformedExpandedBounds_IsInvalidAndLeavesOriginalFileUntouched(string rectangle)
    {
        var document = LegacyDocument();
        document["groups"]![0]!["expandedBounds"] = JsonNode.Parse(rectangle);
        var original = document.ToJsonString();
        File.WriteAllText(SidecarPath, original);

        var loaded = store.Load(ProjectPath, ProjectId, Hash, new[] { NodeId });

        Assert.Equal(GroupLoadStatus.Invalid, loaded.Status);
        Assert.Null(loaded.Snapshot);
        Assert.Contains(loaded.Diagnostics, d => d.Code == "InvalidExpandedBounds");
        Assert.Equal(original, File.ReadAllText(SidecarPath));
    }

    [Fact]
    public void DuplicateExpandedBoundsCoordinates_AreRejectedBeforeImport()
    {
        var document = LegacyDocument().ToJsonString();
        document = document.Replace("\"zIndex\":7", "\"expandedBounds\":{\"x\":0,\"x\":1,\"y\":0,\"width\":10,\"height\":10},\"zIndex\":7");
        File.WriteAllText(SidecarPath, document);

        var loaded = store.Load(ProjectPath, ProjectId, Hash, new[] { NodeId });

        Assert.Equal(GroupLoadStatus.Invalid, loaded.Status);
        Assert.Contains(loaded.Diagnostics, d => d.Code == "DuplicateProperty");
        Assert.Equal(document, File.ReadAllText(SidecarPath));
    }

    private static JsonObject LegacyDocument() => new()
    {
        ["schemaVersion"] = 1,
        ["projectId"] = ProjectId,
        ["coreSnapshotHash"] = Hash,
        ["groups"] = new JsonArray(new JsonObject
        {
            ["groupId"] = GroupId, ["name"] = "旧版组", ["memberNodeIds"] = new JsonArray(NodeId),
            ["collapsed"] = false, ["anchor"] = new JsonObject { ["x"] = 20, ["y"] = -30 },
            ["color"] = "#4C88B8", ["zIndex"] = 7
        })
    };

    public void Dispose()
    {
        var safeParent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TestResults", "bounds")) + Path.DirectorySeparatorChar;
        var absolute = Path.GetFullPath(root);
        if (absolute.StartsWith(safeParent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(absolute))
            Directory.Delete(absolute, recursive: true);
    }
}
