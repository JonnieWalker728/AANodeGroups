using System.Collections.Concurrent;
using Il2CppInterop.Runtime;
using NodeGroups.Core;
using NodeGroups.Storage;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>Native creation helper compiled only into the isolated runtime test assembly.</summary>
internal sealed partial class EditorSession
{
    /// <summary>Integration-fixture helper for native creation/undo with a stable GUID. No production UI exposes group-local creation.</summary>
    internal Node CreateMember(string groupId, int kind)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")))
            throw new InvalidOperationException("Group-local creation is available only in the isolated integration fixture.");
        CancelDrag();
        Sync(); var before = Workspace.Snapshot; var group = before.GetGroup(groupId);
        var existing = group.MemberNodeIds.Select(Find).Where(n => n != null).Select(n => LayoutGeometry(n!)).ToArray();
        var pos = new Point2(group.Anchor.X + 120, group.Anchor.Y - 100);
        var type = kind switch { 0 => Il2CppType.Of<ScriptNode>(), 1 => Il2CppType.Of<SelectionNode>(), 2 => Il2CppType.Of<ExitNode>(), _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
        var id = Guid.NewGuid().ToString("D");
        var creation = new NodeCreateOperation(type, NativeGuid.Parse(id), new Vector2((float)pos.X, (float)pos.Y));
        creation.Redo();
        var node = Find(id) ?? throw new InvalidOperationException("Native node creation did not register its GUID.");
        var geometry = LayoutGeometry(node);
        var placement = NodePlacement.FindTranslationToFreeSpace(existing, new[] { geometry }, pos, id);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")))
            Plugin.Logger.LogInfo($"CreateMember placement: desired={pos}, initialRoot={Position(node)}, geometry={geometry}, delta={placement}, nativeTransform={node.transform.localPosition}, nativeRoot={node.root.localPosition}");
        MoveByCanvas(node, placement);
        // Native Redo must reconstruct the final position, not the provisional card position.
        creation.pos = new Vector2((float)Position(node).X, (float)Position(node).Y);
        Workspace.RegisterNode(new NodeState(id, Position(node), new Point2(280, 110)), groupId);
        EnsureMemberBounds();
        NativeHistory.Push(this, "组内新建节点", before, Workspace.Snapshot, true, creation);
        ObserveCommittedGroups(); ui.Refresh(); return node;
    }

}
