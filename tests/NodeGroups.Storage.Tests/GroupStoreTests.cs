using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using NodeGroups.Core;
using NodeGroups.Storage;
using Xunit;

namespace NodeGroups.Storage.Tests;

/// <summary>通过真实侧车文件和故障注入验证保存、恢复与原文件保护规则。</summary>
public sealed class GroupStoreTests : IDisposable
{
    private const string ProjectId = "azurearchive-project-id";
    private static readonly string Hash = new('a', 64);
    private static readonly string OtherHash = new('b', 64);
    private static readonly string GroupA = Guid.NewGuid().ToString("D");
    private static readonly string GroupB = Guid.NewGuid().ToString("D");
    private static readonly string NodeA = Guid.NewGuid().ToString("D");
    private static readonly string NodeB = Guid.NewGuid().ToString("D");
    private static readonly string NewNode = Guid.NewGuid().ToString("D");
    private static readonly string NativeEntry = Guid.Empty.ToString("D");
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "TestResults", "storage", Guid.NewGuid().ToString("N"));
    private readonly GroupStore _store = new();

    private string ProjectPath => Path.Combine(_root, "sample.azurearchive");
    private string SidecarPath => GroupStore.GetSidecarPath(ProjectPath);

    public GroupStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SidecarPath_PreservesCompleteNativeFileName() =>
        Assert.Equal(ProjectPath + ".nodegroups.json", SidecarPath);

    [Fact]
    public void SaveLoad_RoundTripsCollapsedEmptyAndNewlyCreatedMembers_WithoutNodePositions()
    {
        var populated = Group(GroupA, new[] { NodeA, NodeB, NewNode }, true);
        var empty = Group(GroupB, Array.Empty<string>(), false);
        Assert.True(_store.Save(ProjectPath, Snapshot(populated, empty)).Success);

        var result = _store.Load(ProjectPath, ProjectId, Hash, new[] { NodeA, NodeB, NewNode });

        Assert.Equal(GroupLoadStatus.Loaded, result.Status);
        Assert.False(result.RequiresLayoutRecalculation);
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(2, result.Snapshot.Groups.Count);
        var first = result.Snapshot.Groups[0];
        Assert.Equal(populated.Id, first.Id);
        Assert.Equal("序章组", first.Name);
        Assert.True(first.Collapsed);
        Assert.Equal(populated.Anchor, first.Anchor);
        Assert.Equal("#4C88B8", first.Color);
        Assert.Equal(3, first.ZIndex);
        Assert.Equal(populated.MemberNodeIds, first.MemberNodeIds);
        Assert.Empty(result.Snapshot.Groups[1].MemberNodeIds);
        using var json = JsonDocument.Parse(File.ReadAllText(SidecarPath));
        Assert.False(json.RootElement.TryGetProperty("nodes", out _));
        Assert.Equal(new[] { "groupId", "name", "memberNodeIds", "collapsed", "anchor", "color", "zIndex" },
            json.RootElement.GetProperty("groups")[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void HashMismatch_PreservesMatchingMembersAndEmptyGroup_ButRequestsViewRecalculation()
    {
        Assert.True(_store.Save(ProjectPath, Snapshot(Group(GroupA, new[] { NodeA, NodeB }, true),
            Group(GroupB, Array.Empty<string>(), false))).Success);

        var before = File.ReadAllText(SidecarPath);
        var result = _store.Load(ProjectPath, ProjectId, OtherHash, new[] { NodeA });

        Assert.Equal(GroupLoadStatus.Recovered, result.Status);
        Assert.True(result.RequiresLayoutRecalculation);
        Assert.Equal(new[] { NodeA }, result.Snapshot!.Groups[0].MemberNodeIds);
        Assert.Empty(result.Snapshot.Groups[1].MemberNodeIds);
        Assert.Contains(result.Diagnostics, d => d.Code == "CoreSnapshotMismatch");
        Assert.Contains(result.Diagnostics, d => d.Code == "MissingMembers");
        Assert.Equal(before, File.ReadAllText(SidecarPath));
    }

    [Fact]
    public void MatchingHashWithMissingNode_PrunesMembershipWithoutDiscardingEmptyGroup()
    {
        Assert.True(_store.Save(ProjectPath, Snapshot(Group(GroupA, new[] { NodeA }, false))).Success);
        var result = _store.Load(ProjectPath, ProjectId, Hash, Array.Empty<string>());
        Assert.Equal(GroupLoadStatus.Recovered, result.Status);
        Assert.Single(result.Snapshot!.Groups);
        Assert.Empty(result.Snapshot.Groups[0].MemberNodeIds);
        Assert.True(result.RequiresLayoutRecalculation);
    }

    [Fact]
    public void MissingFile_IsAnEmptySessionWithoutErrors()
    {
        var result = _store.Load(ProjectPath, ProjectId, Hash, Array.Empty<string>());
        Assert.Equal(GroupLoadStatus.Missing, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void NativeEntryWithZeroGuid_DoesNotInvalidateOrdinaryGroupLoad()
    {
        Assert.True(_store.Save(ProjectPath, Snapshot(Group(GroupA, new[] { NodeA }, false))).Success);

        var result = _store.Load(ProjectPath, ProjectId, Hash, new[] { NativeEntry, NodeA });

        Assert.Equal(GroupLoadStatus.Loaded, result.Status);
        Assert.Equal(new[] { NodeA }, result.Snapshot!.Groups[0].MemberNodeIds);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void NativeEntryWithZeroGuid_CanBeAGroupMemberAndRoundTripWithoutIdentityReplacement()
    {
        var saved = _store.Save(ProjectPath, Snapshot(Group(GroupA, new[] { NativeEntry, NodeA }, true)));

        Assert.True(saved.Success);
        var loaded = _store.Load(ProjectPath, ProjectId, Hash, new[] { NativeEntry, NodeA });
        Assert.Equal(GroupLoadStatus.Loaded, loaded.Status);
        Assert.Equal(new[] { NativeEntry, NodeA }, loaded.Snapshot!.Groups[0].MemberNodeIds);
        Assert.Empty(loaded.Diagnostics);
    }

    [Fact]
    public void EmptyGuid_RemainsInvalidAsIndependentGroupIdentity()
    {
        var result = _store.Save(ProjectPath, Snapshot(Group(NativeEntry, new[] { NodeA }, false)));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "InvalidGroupId");
        Assert.False(File.Exists(SidecarPath));
    }

    [Theory]
    [InlineData("schemaVersion", "2", "UnsupportedSchema")]
    [InlineData("schemaVersion", "\"1\"", "UnsupportedSchema")]
    [InlineData("projectId", "\"other-project\"", "ProjectMismatch")]
    [InlineData("coreSnapshotHash", "\"not-a-hash\"", "InvalidHash")]
    public void InvalidDocument_IsRecoverableAndNeverRewritten(string property, string value, string code)
    {
        var document = ValidJson();
        document[property] = JsonNode.Parse(value);
        AssertInvalidUnchanged(document.ToJsonString(), code);
    }

    [Theory]
    [InlineData("groupId", "\"invalid-guid\"", "InvalidGroupId")]
    [InlineData("name", "\"\"", "InvalidName")]
    [InlineData("name", "null", "InvalidName")]
    [InlineData("color", "\"red\"", "InvalidColor")]
    [InlineData("collapsed", "null", "InvalidCollapsedState")]
    [InlineData("memberNodeIds", "[\"invalid-guid\"]", "InvalidMemberId")]
    [InlineData("anchor", "{\"x\":10000001,\"y\":0}", "InvalidAnchor")]
    [InlineData("anchor", "{\"x\":0}", "InvalidAnchor")]
    [InlineData("zIndex", "\"top\"", "InvalidZIndex")]
    public void InvalidGroup_IsRejectedRatherThanPartiallyImported(string property, string value, string code)
    {
        var document = ValidJson();
        document["groups"]![0]![property] = JsonNode.Parse(value);
        AssertInvalidUnchanged(document.ToJsonString(), code);
    }

    [Fact]
    public void DuplicateMemberAcrossGroups_IsRejectedEvenWhenGuidFormattingDiffers()
    {
        var document = ValidJson();
        var second = JsonNode.Parse(document["groups"]![0]!.ToJsonString())!;
        second["groupId"] = GroupB;
        second["memberNodeIds"]![0] = Guid.Parse(NodeA).ToString("B").ToUpperInvariant();
        document["groups"]!.AsArray().Add(second);
        AssertInvalidUnchanged(document.ToJsonString(), "DuplicateMember");
    }

    [Fact]
    public void DuplicateGroupId_IsRejected()
    {
        var document = ValidJson();
        var second = JsonNode.Parse(document["groups"]![0]!.ToJsonString())!;
        second["memberNodeIds"] = new JsonArray();
        document["groups"]!.AsArray().Add(second);
        AssertInvalidUnchanged(document.ToJsonString(), "DuplicateGroup");
    }

    [Theory]
    [InlineData("{broken json")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":1}")]
    public void CorruptFile_ReturnsActionableErrorAndPreservesOriginal(string contents)
    {
        File.WriteAllText(SidecarPath, contents);
        var result = _store.Load(ProjectPath, ProjectId, Hash, new[] { NodeA });
        Assert.Equal(GroupLoadStatus.Invalid, result.Status);
        Assert.Null(result.Snapshot);
        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, d => Assert.False(string.IsNullOrWhiteSpace(d.Message)));
        Assert.Equal(contents, File.ReadAllText(SidecarPath));
    }

    [Fact]
    public void DuplicateJsonProperties_AreRejectedToAvoidAmbiguousIdentity()
    {
        var json = ValidJson().ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1");
        AssertInvalidUnchanged(json, "DuplicateProperty");
    }

    [Fact]
    public void InvalidUtf8_ReturnsMalformedFileInsteadOfThrowingOrTreatingAsMissing()
    {
        File.WriteAllBytes(SidecarPath, new byte[] { 0xFF, 0xFE, 0xFF });
        var result = _store.Load(ProjectPath, ProjectId, Hash, new[] { NodeA });
        Assert.Equal(GroupLoadStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == "InvalidJson");
        Assert.Equal(new byte[] { 0xFF, 0xFE, 0xFF }, File.ReadAllBytes(SidecarPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiBlockUtf8File_RespectsExactByteLimitAndOptionalBom(bool emitBom)
    {
        var document = ValidJson();
        var groups = document["groups"]!.AsArray();
        groups.Clear();
        // 使用未转义的中文名称，使文件同时覆盖多块读取和多字节 UTF-8 解码。
        var name = new string('组', 200);
        for (var index = 0; index < 12; index++)
        {
            var group = JsonNode.Parse(ValidJson()["groups"]![0]!.ToJsonString())!;
            group["groupId"] = Guid.NewGuid().ToString("D");
            group["name"] = name;
            group["memberNodeIds"] = new JsonArray();
            groups.Add(group);
        }
        var json = document.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(SidecarPath, json, new UTF8Encoding(emitBom, throwOnInvalidBytes: true));
        var original = File.ReadAllBytes(SidecarPath);
        Assert.True(original.Length > 4096);

        var exactLimit = new GroupStore(limits: new GroupStoreLimits(MaxFileBytes: original.Length));
        var loaded = exactLimit.Load(ProjectPath, ProjectId, Hash, Array.Empty<string>());

        Assert.Equal(GroupLoadStatus.Loaded, loaded.Status);
        Assert.Equal(12, loaded.Snapshot!.Groups.Count);
        Assert.All(loaded.Snapshot.Groups, group => Assert.Equal(name, group.Name));
        // 文件上限按实际字节（含 BOM）判断，不能用解码后的字符数量代替。
        var belowLimit = new GroupStore(limits: new GroupStoreLimits(MaxFileBytes: original.Length - 1));
        var rejected = belowLimit.Load(ProjectPath, ProjectId, Hash, Array.Empty<string>());
        Assert.Equal(GroupLoadStatus.Invalid, rejected.Status);
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == "FileTooLarge");
        Assert.Equal(original, File.ReadAllBytes(SidecarPath));
    }

    [Fact]
    public void Snapshot_DefensivelyCopiesGroupsAndMembersDuringAsyncSave()
    {
        var members = new List<string> { NodeA };
        var group = Group(GroupA, members, false);
        var groups = new List<GroupState> { group };
        var snapshot = new GroupFileSnapshot(ProjectId, Hash, groups);
        members.Add(NodeB);
        groups.Clear();
        Assert.Single(snapshot.Groups);
        Assert.Equal(new[] { NodeA }, snapshot.Groups[0].MemberNodeIds);
        Assert.True(((IList<GroupState>)snapshot.Groups).IsReadOnly);
    }

    [Fact]
    public void OverLimitFile_IsRejectedBeforeImport()
    {
        File.WriteAllText(SidecarPath, ValidJson().ToJsonString());
        var store = new GroupStore(limits: new GroupStoreLimits(MaxFileBytes: 64));
        var result = store.Load(ProjectPath, ProjectId, Hash, new[] { NodeA });
        Assert.Equal(GroupLoadStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == "FileTooLarge");
    }

    [Fact]
    public void GroupAndMemberLimits_AreEnforcedBeforeWriting()
    {
        var store = new GroupStore(limits: new GroupStoreLimits(MaxGroups: 1, MaxMembersPerGroup: 1));
        Assert.False(store.Save(ProjectPath, Snapshot(Group(GroupA, Array.Empty<string>(), false),
            Group(GroupB, Array.Empty<string>(), false))).Success);
        Assert.False(store.Save(ProjectPath, Snapshot(Group(GroupA, new[] { NodeA, NodeB }, false))).Success);
        Assert.False(File.Exists(SidecarPath));
    }

    [Fact]
    public void TotalMemberLimit_IsEnforcedAcrossDifferentGroupsDuringLoadAndSave()
    {
        var snapshot = Snapshot(Group(GroupA, new[] { NodeA }, false), Group(GroupB, new[] { NodeB }, false));
        Assert.True(_store.Save(ProjectPath, snapshot).Success);
        var store = new GroupStore(limits: new GroupStoreLimits(MaxTotalMembers: 1));
        var result = store.Load(ProjectPath, ProjectId, Hash, new[] { NodeA, NodeB });
        Assert.Equal(GroupLoadStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == "TooManyMembers");
        Assert.False(store.Save(ProjectPath, snapshot).Success);
    }

    [Fact]
    public void SaveValidationFailure_PreservesPreviouslySavedFile()
    {
        Assert.True(_store.Save(ProjectPath, Snapshot(Group(GroupA, new[] { NodeA }, false))).Success);
        var before = File.ReadAllText(SidecarPath);
        var invalid = Group(GroupA, new[] { NodeA, NodeA }, false);
        var result = _store.Save(ProjectPath, Snapshot(invalid));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "DuplicateMember");
        Assert.Equal(before, File.ReadAllText(SidecarPath));
    }

    [Fact]
    public void AtomicReplacementFailure_PreservesLastGoodFileAndRemovesTemporaryFile()
    {
        Assert.True(_store.Save(ProjectPath, Snapshot(Group(GroupA, new[] { NodeA }, false))).Success);
        var before = File.ReadAllText(SidecarPath);
        var failing = new GroupStore(new ReplacementFailureFileSystem());

        var result = failing.Save(ProjectPath, Snapshot(Group(GroupA, new[] { NodeA, NewNode }, true)));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "SaveIoError");
        Assert.Equal(before, File.ReadAllText(SidecarPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        var previous = _store.Load(ProjectPath, ProjectId, Hash, new[] { NodeA, NewNode });
        Assert.Equal(new[] { NodeA }, previous.Snapshot!.Groups[0].MemberNodeIds);
    }

    [Fact]
    public void SuccessfulReplacement_ContainsOnlyNewCompleteSnapshot()
    {
        Assert.True(_store.Save(ProjectPath, Snapshot(Group(GroupA, new[] { NodeA }, false))).Success);
        Assert.True(_store.Save(ProjectPath, Snapshot(Group(GroupA, new[] { NodeA, NewNode }, true))).Success);
        var loaded = _store.Load(ProjectPath, ProjectId, Hash, new[] { NodeA, NewNode });
        Assert.True(loaded.Snapshot!.Groups[0].Collapsed);
        Assert.Equal(new[] { NodeA, NewNode }, loaded.Snapshot.Groups[0].MemberNodeIds);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Hash_IsDeterministicSha256OfActualProjectBytes() =>
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            GroupStore.ComputeCoreSnapshotHash(Array.Empty<byte>()));

    private void AssertInvalidUnchanged(string contents, string expectedCode)
    {
        File.WriteAllText(SidecarPath, contents);
        var result = _store.Load(ProjectPath, ProjectId, Hash, new[] { NodeA, NodeB });
        Assert.Equal(GroupLoadStatus.Invalid, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Contains(result.Diagnostics, d => d.Code == expectedCode);
        Assert.Equal(contents, File.ReadAllText(SidecarPath));
    }

    private static GroupState Group(string id, IEnumerable<string> members, bool collapsed) =>
        new(id, "序章组", members, collapsed, new Point2(120, -80), "#4C88B8", 3);

    private static GroupFileSnapshot Snapshot(params GroupState[] groups) => new(ProjectId, Hash, groups);

    private static JsonObject ValidJson() => new()
    {
        ["schemaVersion"] = 1,
        ["projectId"] = ProjectId,
        ["coreSnapshotHash"] = Hash,
        ["groups"] = new JsonArray(new JsonObject
        {
            ["groupId"] = GroupA,
            ["name"] = "序章组",
            ["memberNodeIds"] = new JsonArray(NodeA),
            ["collapsed"] = false,
            ["anchor"] = new JsonObject { ["x"] = 120, ["y"] = -80 },
            ["color"] = "#4C88B8",
            ["zIndex"] = 3
        })
    };

    public void Dispose()
    {
        // 仅清理当前测试夹具创建的 bin/TestResults/storage 子目录，避免误删工程文件。
        var safeParent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TestResults", "storage"))
            + Path.DirectorySeparatorChar;
        var absolute = Path.GetFullPath(_root);
        if (absolute.StartsWith(safeParent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(absolute))
            Directory.Delete(absolute, recursive: true);
    }

    private sealed class ReplacementFailureFileSystem : IGroupFileSystem
    {
        public bool FileExists(string path) => File.Exists(path);
        public string ReadText(string path, int maxBytes) => File.ReadAllText(path);
        public void WriteNewText(string path, string text) => File.WriteAllText(path, text);
        public void Replace(string temporaryPath, string destinationPath) =>
            throw new IOException("Injected atomic-replace failure before destination modification.");
        public void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }
    }
}
