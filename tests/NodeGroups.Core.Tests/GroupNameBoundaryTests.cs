using NodeGroups.Core;
using Xunit;

namespace NodeGroups.Core.Tests;

/// <summary>进入工作区的名称必须可被侧车无损保存，合法中文与完整 Unicode 字符保持不变。</summary>
public sealed class GroupNameBoundaryTests
{
    [Fact]
    public void EmbeddedControlCharactersCannotCreateOrRenameUnsavableGroups()
    {
        foreach (var name in new[] { "内部\n换行", "内部\t制表", "内部\0空字符", "内部\u0085换行" })
        {
            var workspace = new GroupWorkspace(Array.Empty<NodeState>());
            var before = workspace.Snapshot;
            Assert.Throws<ArgumentException>(() => workspace.CreateGroup("bad", name, Array.Empty<string>(), default));
            Assert.Same(before, workspace.Snapshot);
            workspace.CreateGroup("good", "可保存名称", Array.Empty<string>(), default);
            before = workspace.Snapshot;
            Assert.Throws<ArgumentException>(() => workspace.RenameGroup("good", name));
            Assert.Same(before, workspace.Snapshot);
        }
    }

    [Fact]
    public void UnpairedUtf16CannotCreateOrRenameGroupsOrEnterSnapshots()
    {
        // 数据放在方法体中，避免测试框架把非法 UTF-16 测试参数写入 XML/TRX 元数据。
        foreach (var name in new[] { "高代理\ud800末尾", "低代理\udfff末尾", "反序\udfff\ud800末尾" })
        {
            var workspace = new GroupWorkspace(Array.Empty<NodeState>());
            var before = workspace.Snapshot;
            Assert.Throws<ArgumentException>(() => workspace.CreateGroup("bad", name, Array.Empty<string>(), default));
            Assert.Same(before, workspace.Snapshot);
            workspace.CreateGroup("good", "可保存名称", Array.Empty<string>(), default);
            before = workspace.Snapshot;
            Assert.Throws<ArgumentException>(() => workspace.RenameGroup("good", name));
            Assert.Same(before, workspace.Snapshot);
            Assert.Throws<ArgumentException>(() => new WorkspaceSnapshot(Array.Empty<NodeState>(), new[]
            { new GroupState("raw", name, Array.Empty<string>(), false, default) }));
        }
    }

    [Fact]
    public void RawSnapshotNamesCannotBypassControlValidationThroughOuterWhitespace()
    {
        foreach (var name in new[] { "\t原始名称", "原始名称\n", "内\0部" })
            Assert.Throws<ArgumentException>(() => new WorkspaceSnapshot(Array.Empty<NodeState>(), new[]
            { new GroupState("raw", name, Array.Empty<string>(), false, default) }));
    }

    [Fact]
    public void CommandsKeepWhitespaceNormalizationAndCompleteUnicodeAtTheLengthBoundary()
    {
        var workspace = new GroupWorkspace(Array.Empty<NodeState>());
        workspace.CreateGroup("g", "\t\r\n 中文 🧩 \r\n", Array.Empty<string>(), default);
        Assert.Equal("中文 🧩", workspace.Snapshot.GetGroup("g").Name);
        var name = string.Concat(Enumerable.Repeat("🧩", 128));
        Assert.Equal(256, name.Length);
        workspace.RenameGroup("g", name);
        Assert.Equal(name, workspace.Snapshot.GetGroup("g").Name);
        workspace.RenameGroup("g", "👩‍💻 开发者");
        Assert.Equal("👩‍💻 开发者", workspace.Snapshot.GetGroup("g").Name);
        var before = workspace.Snapshot;
        Assert.Throws<ArgumentException>(() => workspace.RenameGroup("g", name + "a"));
        Assert.Same(before, workspace.Snapshot);
    }
}
