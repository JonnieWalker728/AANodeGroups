using System.Text;
using System.Text.Json;
using NodeGroups.Core;
using NodeGroups.Storage;
using Xunit;

namespace NodeGroups.Storage.Tests;

/// <summary>正式版的实际资源上限、格式边界及提交失败保护回归，全部使用独立侧车文件。</summary>
public sealed class ReleaseBoundaryStressTests : IDisposable
{
    private const string ProjectId = "release-boundary-project";
    private static readonly string Hash = new('c', 64);
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "TestResults", "release-storage", Guid.NewGuid().ToString("N"));
    private readonly GroupStore store = new();
    private string Project => Path.Combine(root, "边界工程.aap2");
    private string Sidecar => GroupStore.GetSidecarPath(Project);

    public ReleaseBoundaryStressTests() => Directory.CreateDirectory(root);

    [Fact]
    public void DefaultLimits_RoundTrip1024GroupsAnd50000MembersIncludingOne10000MemberGroup()
    {
        var members = Enumerable.Range(0, 50000).Select(NodeId).ToArray();
        var groups = new List<GroupState>(1024);
        var offset = 0;
        for (var index = 0; index < 1024; index++)
        {
            var count = index == 0 ? 10000 : 39 + (index <= 103 ? 1 : 0);
            groups.Add(new GroupState(GroupId(index), new string('界', 256), members.Skip(offset).Take(count),
                index % 2 == 0, new Point2(index, -index), zIndex: index,
                expandedBounds: new Rect2(index, -index - 500, 1000, 500)));
            offset += count;
        }
        Assert.Equal(50000, offset);
        Assert.True(store.Save(Project, new GroupFileSnapshot(ProjectId, Hash, groups)).Success);
        var bytes = File.ReadAllBytes(Sidecar);
        Assert.InRange(bytes.Length, 1, 8 * 1024 * 1024);

        var result = store.Load(Project, ProjectId, Hash, members);

        Assert.Equal(GroupLoadStatus.Loaded, result.Status);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1024, result.Snapshot!.Groups.Count);
        Assert.Equal(50000, result.Snapshot.Groups.Sum(group => group.MemberNodeIds.Count));
        Assert.Equal(10000, result.Snapshot.Groups[0].MemberNodeIds.Count);
        Assert.Equal(members, result.Snapshot.Groups.SelectMany(group => group.MemberNodeIds));
        for (var index = 0; index < groups.Count; index++)
        {
            var actual = result.Snapshot.Groups[index];
            var expected = groups[index];
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Collapsed, actual.Collapsed);
            Assert.Equal(expected.ZIndex, actual.ZIndex);
            Assert.Equal(expected.Anchor, actual.Anchor);
            Assert.Equal(expected.ExpandedBounds, actual.ExpandedBounds);
        }
        // 加载不得以“恢复”为名重写正式文件或产生临时文件。
        Assert.Equal(bytes, File.ReadAllBytes(Sidecar));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Theory]
    [InlineData("groups")]
    [InlineData("group-members")]
    [InlineData("total-members")]
    [InlineData("name")]
    public void DefaultLimitPlusOne_IsRejectedOnSaveAndLoadWithoutChangingExistingFiles(string limit)
    {
        Assert.True(store.Save(Project, Snapshot(Group(0, new[] { NodeId(0) }))).Success);
        var saved = File.ReadAllBytes(Sidecar);
        var groups = limit switch
        {
            "groups" => Enumerable.Range(0, 1025).Select(index => Group(index, Array.Empty<string>())).ToArray(),
            "group-members" => new[] { Group(0, Enumerable.Range(0, 10001).Select(NodeId)) },
            "total-members" => Enumerable.Range(0, 6).Select(index => Group(index,
                Enumerable.Range(index * 10000, index == 5 ? 1 : 10000).Select(NodeId))).ToArray(),
            _ => new[] { new GroupState(GroupId(0), new string('界', 257), Array.Empty<string>(), false, default) }
        };
        var code = limit == "groups" ? "TooManyGroups" : limit == "name" ? "InvalidName" : "TooManyMembers";
        var rejectedSave = store.Save(Project, Snapshot(groups));
        Assert.False(rejectedSave.Success);
        Assert.Contains(rejectedSave.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.Equal(saved, File.ReadAllBytes(Sidecar));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));

        var external = Encoding.UTF8.GetBytes(ExternalDocument(groups));
        File.WriteAllBytes(Sidecar, external);
        var rejectedLoad = store.Load(Project, ProjectId, Hash, Array.Empty<string>());
        Assert.Equal(GroupLoadStatus.Invalid, rejectedLoad.Status);
        Assert.Null(rejectedLoad.Snapshot);
        Assert.Contains(rejectedLoad.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.Equal(external, File.ReadAllBytes(Sidecar));
    }

    [Fact]
    public void DefaultEightMiBFileBoundary_IsInclusiveAndOverflowLeavesInputUntouched()
    {
        var json = ExternalDocument(new[] { Group(0, Array.Empty<string>()) });
        var prefix = Encoding.UTF8.GetBytes(json);
        var exact = new byte[8 * 1024 * 1024];
        Array.Fill(exact, (byte)' ');
        prefix.CopyTo(exact, 0);
        File.WriteAllBytes(Sidecar, exact);
        Assert.Equal(GroupLoadStatus.Loaded, store.Load(Project, ProjectId, Hash, Array.Empty<string>()).Status);
        Assert.Equal(exact, File.ReadAllBytes(Sidecar));

        using (var stream = new FileStream(Sidecar, FileMode.Append, FileAccess.Write)) stream.WriteByte((byte)' ');
        var over = File.ReadAllBytes(Sidecar);
        var rejected = store.Load(Project, ProjectId, Hash, Array.Empty<string>());
        Assert.Equal(GroupLoadStatus.Invalid, rejected.Status);
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == "FileTooLarge");
        Assert.Equal(over, File.ReadAllBytes(Sidecar));
    }

    [Theory]
    [InlineData("D")]
    [InlineData("B")]
    [InlineData("P")]
    [InlineData("N")]
    [InlineData("X")]
    public void GuidFormsAndCase_NormalizeForStorageAndMapBackToNativeSpelling(string format)
    {
        var identity = Guid.Parse(NodeId(123));
        var member = "  " + identity.ToString(format).ToUpperInvariant() + "  ";
        var group = new GroupState(Guid.Parse(GroupId(0)).ToString(format).ToUpperInvariant(), "😀中文", new[] { member }, false, default);
        Assert.True(store.Save(Project, Snapshot(group)).Success);
        using (var document = JsonDocument.Parse(File.ReadAllBytes(Sidecar)))
        {
            var saved = document.RootElement.GetProperty("groups")[0];
            Assert.Equal(GroupId(0), saved.GetProperty("groupId").GetString());
            Assert.Equal(NodeId(123), saved.GetProperty("memberNodeIds")[0].GetString());
        }
        var nativeSpelling = identity.ToString("B").ToUpperInvariant();
        var loaded = store.Load(Project, ProjectId, Hash, new[] { nativeSpelling });
        Assert.Equal(GroupLoadStatus.Loaded, loaded.Status);
        Assert.Equal(new[] { nativeSpelling }, loaded.Snapshot!.Groups[0].MemberNodeIds);
        Assert.Equal("😀中文", loaded.Snapshot.Groups[0].Name);
    }

    [Fact]
    public void FullCoordinateRangeAndInt32LayerExtremes_RoundTripButOneUlpOutsideIsRejected()
    {
        const double limit = 10000000;
        var full = new GroupState(GroupId(0), "画布边界", Array.Empty<string>(), false,
            new Point2(-limit, limit), zIndex: int.MinValue, expandedBounds: new Rect2(-limit, -limit, limit * 2, limit * 2));
        var other = new GroupState(GroupId(1), "顶层", Array.Empty<string>(), true, default, zIndex: int.MaxValue);
        Assert.True(store.Save(Project, Snapshot(full, other)).Success);
        var loaded = store.Load(Project, ProjectId, Hash, Array.Empty<string>());
        Assert.Equal(GroupLoadStatus.Loaded, loaded.Status);
        Assert.Equal(full.ExpandedBounds, loaded.Snapshot!.Groups[0].ExpandedBounds);
        Assert.Equal(int.MinValue, loaded.Snapshot.Groups[0].ZIndex);
        Assert.Equal(int.MaxValue, loaded.Snapshot.Groups[1].ZIndex);
        var previous = File.ReadAllBytes(Sidecar);
        var outside = new GroupState(GroupId(0), "超出边界", Array.Empty<string>(), false,
            new Point2(Math.BitIncrement(limit), 0));
        var result = store.Save(Project, Snapshot(outside));
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "InvalidAnchor");
        Assert.Equal(previous, File.ReadAllBytes(Sidecar));
    }

    [Fact]
    public void UnknownNestedDuplicatePropertiesAndDeepJson_AreRejectedWithoutRewritingInput()
    {
        var json = ExternalDocument(new[] { Group(0, Array.Empty<string>()) });
        foreach (var extra in new[] { "{\"nested\":[{\"x\":1,\"x\":2}]}", new string('[', 17) + "0" + new string(']', 17) })
        {
            var document = json[..^1] + ",\"unknown\":" + extra + "}";
            var bytes = Encoding.UTF8.GetBytes(document);
            File.WriteAllBytes(Sidecar, bytes);
            var result = store.Load(Project, ProjectId, Hash, Array.Empty<string>());
            Assert.Equal(GroupLoadStatus.Invalid, result.Status);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code is "DuplicateProperty" or "InvalidJson");
            Assert.Equal(bytes, File.ReadAllBytes(Sidecar));
        }
    }

    [Theory]
    [InlineData("name", "\\uD800")]
    [InlineData("name", "\\uDC00")]
    [InlineData("projectId", "\\uD800")]
    [InlineData("memberNodeIds", "\\uD800")]
    [InlineData("property-name", "\\uD800")]
    public void MalformedEscapedUtf16_ReturnsInvalidInsteadOfThrowingAndPreservesFile(string field, string escaped)
    {
        var group = Group(0, new[] { NodeId(0) });
        var json = ExternalDocument(new[] { group });
        var invalidValue = "\"" + escaped + "\"";
        json = field switch
        {
            "name" => json.Replace("\"name\":" + JsonSerializer.Serialize(group.Name), "\"name\":" + invalidValue),
            "projectId" => json.Replace("\"projectId\":\"" + ProjectId + "\"", "\"projectId\":" + invalidValue),
            "memberNodeIds" => json.Replace("\"memberNodeIds\":[\"" + NodeId(0) + "\"]", "\"memberNodeIds\":[" + invalidValue + "]"),
            _ => json[..^1] + "," + invalidValue + ":0}"
        };
        File.WriteAllText(Sidecar, json, new UTF8Encoding(false, throwOnInvalidBytes: true));
        var original = File.ReadAllBytes(Sidecar);

        var result = store.Load(Project, ProjectId, Hash, new[] { NodeId(0) });

        Assert.Equal(GroupLoadStatus.Invalid, result.Status);
        Assert.Null(result.Snapshot);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "InvalidJson");
        Assert.Equal(original, File.ReadAllBytes(Sidecar));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnpairedUtf16OnSave_IsRejectedRatherThanSilentlyReplaced(bool invalidProject, bool lowSurrogate)
    {
        Assert.True(store.Save(Project, Snapshot(Group(0, Array.Empty<string>()))).Success);
        var original = File.ReadAllBytes(Sidecar);
        // 在运行期构造非法 UTF-16，避免测试特性参数的 UTF-8 元数据预先替换代理项。
        var invalid = "非法" + new string((char)(lowSurrogate ? 0xDC00 : 0xD800), 1);
        var group = new GroupState(GroupId(0), invalidProject ? "合法😀" : invalid, Array.Empty<string>(), false, default);
        var snapshot = new GroupFileSnapshot(invalidProject ? invalid : ProjectId, Hash, new[] { group });

        var result = store.Save(Project, snapshot);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == (invalidProject ? "InvalidProjectId" : "InvalidName"));
        Assert.Equal(original, File.ReadAllBytes(Sidecar));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public void AvailableNodeEnumerationFailure_IsNotMisreportedAsMalformedJson()
    {
        Assert.True(store.Save(Project, Snapshot(Group(0, new[] { NodeId(0) }))).Success);
        var original = File.ReadAllBytes(Sidecar);
        var expected = new InvalidOperationException("Native node enumeration failed.");

        // 解码错误只在 JSON 解析器内部转换；调用方业务异常仍应交还调用方处理。
        var actual = Assert.Throws<InvalidOperationException>(() => store.Load(Project, ProjectId, Hash, FailedNodes()));

        Assert.Same(expected, actual);
        Assert.Equal(original, File.ReadAllBytes(Sidecar));
        IEnumerable<string> FailedNodes()
        {
            yield return NodeId(0);
            throw expected;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupFailure_ReportsWarningAfterCommitAndFailureBeforeCommit(bool writeFails)
    {
        Assert.True(store.Save(Project, Snapshot(Group(0, new[] { NodeId(0) }))).Success);
        var previous = File.ReadAllBytes(Sidecar);
        var fault = new CleanupFaultFileSystem(writeFails);
        var result = new GroupStore(fault).Save(Project, Snapshot(Group(0, new[] { NodeId(1) })));
        Assert.Equal(!writeFails, result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TemporaryCleanupError");
        if (writeFails)
        {
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SaveIoError");
            Assert.Equal(previous, File.ReadAllBytes(Sidecar));
        }
        else
        {
            Assert.Single(result.Diagnostics);
            var loaded = store.Load(Project, ProjectId, Hash, new[] { NodeId(1) });
            Assert.Equal(new[] { NodeId(1) }, loaded.Snapshot!.Groups[0].MemberNodeIds);
        }
    }

    private static GroupState Group(int index, IEnumerable<string> members) => new(GroupId(index), "正式版边界", members, false, default);
    private static GroupFileSnapshot Snapshot(params GroupState[] groups) => new(ProjectId, Hash, groups);
    private static string NodeId(int index) => new Guid(index, 0, 0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }).ToString("D");
    private static string GroupId(int index) => new Guid(index + 1, 1, 0, new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }).ToString("D");

    private static string ExternalDocument(IEnumerable<GroupState> groups) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, projectId = ProjectId, coreSnapshotHash = Hash,
        groups = groups.Select(group => new
        {
            groupId = group.Id, name = group.Name, memberNodeIds = group.MemberNodeIds,
            collapsed = group.Collapsed, anchor = new { x = group.Anchor.X, y = group.Anchor.Y },
            color = group.Color, zIndex = group.ZIndex
        })
    });

    public void Dispose()
    {
        var safePrefix = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TestResults", "release-storage")) + Path.DirectorySeparatorChar;
        var absolute = Path.GetFullPath(root);
        if (!absolute.StartsWith(safePrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }

    /// <summary>在写入前故障或提交成功后故障，区分失败保护与成功后的清理告警。</summary>
    private sealed class CleanupFaultFileSystem : IGroupFileSystem
    {
        private readonly bool writeFails;
        internal CleanupFaultFileSystem(bool writeFails) => this.writeFails = writeFails;
        public bool FileExists(string path) => File.Exists(path);
        public string ReadText(string path, int maxBytes) => File.ReadAllText(path);
        public void WriteNewText(string path, string text)
        {
            File.WriteAllText(path, text);
            if (writeFails) throw new IOException("Injected failure after temporary file write.");
        }
        public void Replace(string temporaryPath, string destinationPath) => File.Replace(temporaryPath, destinationPath, null);
        public void DeleteIfExists(string path) => throw new IOException("Injected temporary cleanup failure.");
    }
}
