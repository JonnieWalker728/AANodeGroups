using AzureArchive.Automation;
using Il2CppInterop.Runtime;
using NodeGroups.Core;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;
using System.Text.Json;
using UnityEngine;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>
/// Checks selected contours, resize surfaces, editing-caret color and direct dissolve controls.
/// Each synchronous scenario restores the generated fixture before subsequent regressions.
/// </summary>
internal static partial class GroupControlsTests
{
    /// <summary>Runs synchronous visual, editing and dissolution acceptance checks.</summary>
    /// <param name="session">The current isolated test fixture's editor owner.</param>
    /// <param name="check">Assertion sink which throws immediately on failure.</param>
    internal static void Run(EditorSession session, Action<bool, string> check)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")))
            throw new InvalidOperationException("Iteration 3 checks require the explicitly isolated runtime fixture.");
        var boundary = OperationManager.history.Count;
        var previousNodes = Blackboard.selected.ToArray();
        var previousGroup = session.SelectedGroup;
        var previousObject = UICamera.selectedObject;
        var nodes = session.ReadNodes();
        var story = ProjectFileStore.Serialize(session.Editor.CaptureProject());
        var compiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray();
        var compileDelta = OperationManager.compileDelta;
        try
        {
            var source = session.Board.nodes.ToArray().First(node => node.TryCast<ScriptNode>() != null);
            Blackboard.selected.Clear(); Blackboard.selected.Add(source);
            session.CreateGroup(false);
            var id = session.SelectedGroup ?? throw new InvalidOperationException("Created group has no selection.");
            session.Rename(id, "缩放验收组");
            session.Tick();
            CheckSelectedContours(session, id, check);
            CheckResizeSurfaces(session, id, check);
            CheckEditingCaret(session, id, check);
            CheckDissolveButton(session, id, nodes, story, compiled, check);
        }
        finally
        {
            session.CancelDrag();
            var guard = Math.Max(0, OperationManager.history.Count - boundary) + 8;
            while (OperationManager.history.Count > boundary && guard-- > 0) OperationManager.Undo();
            Blackboard.selected.Clear();
            foreach (var node in previousNodes)
                if (node != null && session.Find(EditorSession.Id(node)) != null) Blackboard.selected.Add(node);
            session.SelectedGroup = previousGroup;
            UICamera.selectedObject = previousObject;
            session.Tick();
        }
        check(OperationManager.history.Count == boundary && NodesUnchanged(session, nodes) &&
            ProjectFileStore.Serialize(session.Editor.CaptureProject()) == story &&
            AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(compiled) &&
            OperationManager.compileDelta == compileDelta,
            "iteration 3 appearance, editing and dissolve checks restore the original graph, compile bytes and native-history boundary");
    }

    private static void CheckSelectedContours(EditorSession session, string id, Action<bool, string> check)
    {
        var root = GroupRoot(session, id);
        var frame = root.Find("Region frame")!.GetComponent<UISprite>();
        check(frame.gameObject.activeInHierarchy && frame.enabled && IsYellow(frame.color),
            "iteration 3 selected expanded region has a persistent opaque yellow border");
        for (var index = 0; index < 5; index++) session.Tick();
        check(IsYellow(frame.color), "iteration 3 repeated refresh does not fade the selected yellow region border");
        session.Toggle(id); session.Tick();
        var header = GroupRoot(session, id).Find("Header")!.GetComponent<UISprite>();
        check(GroupRoot(session, id).GetComponentsInChildren<UISprite>(true).Any(sprite =>
            sprite.gameObject.activeInHierarchy && sprite.enabled && !sprite.fillCenter && IsYellow(sprite.color) &&
            sprite.width >= header.width && sprite.height >= header.height && sprite.height <= header.height + 24),
            "iteration 3 selected collapsed card has a persistent yellow outline surrounding its whole header");
        session.DeselectGroup(); session.Tick();
        check(!GroupRoot(session, id).GetComponentsInChildren<UISprite>(true).Any(sprite =>
            sprite.gameObject.activeInHierarchy && sprite.enabled && !sprite.fillCenter && IsYellow(sprite.color)),
            "iteration 3 cancelling group selection removes the persistent yellow contour");
        session.SelectGroup(id); session.Toggle(id); session.Tick();
    }

    private static void CheckResizeSurfaces(EditorSession session, string id, Action<bool, string> check)
    {
        var root = GroupRoot(session, id);
        var handles = root.GetComponentsInChildren<Transform>(true)
            .Where(transform => transform.name.StartsWith("Resize ", StringComparison.Ordinal) && transform.GetComponent<Collider>() != null)
            .ToArray();
        check(handles.Select(transform => transform.name).OrderBy(name => name, StringComparer.Ordinal)
            .SequenceEqual(new[] { "Resize 1", "Resize 2", "Resize 4", "Resize 5", "Resize 6" }),
            "iteration 3 resize input exposes only left, right, bottom and the two bottom corners");
        var width = root.Find("Region frame")!.GetComponent<UISprite>().width;
        var height = root.Find("Region frame")!.GetComponent<UISprite>().height;
        Physics.SyncTransforms();
        foreach (var handle in handles)
        {
            var box = handle.GetComponent<Collider>();
            var localBounds = box.bounds;
            var minimum = root.InverseTransformPoint(localBounds.min);
            var maximum = root.InverseTransformPoint(localBounds.max);
            // The retained native sprite has a shadow outside its opaque line.
            // The iteration-4 GPU probe checks the actual visible-line hits;
            // this older contract checks that input remains inside the frame.
            var insideFrame = minimum.x >= -.1f && maximum.x <= width + .1f &&
                minimum.y >= -height - .1f && maximum.y <= .1f;
            var reachesInside = maximum.x > 0 && minimum.x < width && maximum.y > -height && minimum.y < 0;
            check(insideFrame && reachesInside,
                "retained " + handle.name + " input stays inside the native frame rectangle");
        }
        foreach (var mask in new[] { 5, 6 })
        {
            var handle = root.Find("Resize " + mask)!;
            check(handle.GetComponentsInChildren<UIWidget>(true).Any(widget =>
                widget.gameObject.activeInHierarchy && widget.enabled && widget.color.a >= .5f && widget.width >= 12 && widget.height >= 12),
                "retained " + handle.name + " supplies an alpha-visible NGUI input widget for diagonal resizing");
        }
        check(root.Find("Resize 5")!.GetComponent<UITexture>() == null && root.Find("Resize 6")!.GetComponent<UITexture>() == null,
            "retained corner input does not restore the removed triangle textures");
    }

    private static void CheckEditingCaret(EditorSession session, string id, Action<bool, string> check)
    {
        var title = GroupRoot(session, id).Find("Header/Name (double click)")!;
        UIEventListener.Get(title.gameObject).OnDoubleClick();
        var input = GroupRoot(session, id).GetComponentsInChildren<UIInput>(true).Single();
        check(input.enabled && input.isSelected && input.caretColor.r < .05f &&
            input.caretColor.g < .05f && input.caretColor.b < .05f && input.caretColor.a > .95f,
            "iteration 3 native name editor uses an opaque black insertion caret over its white header");
        input.isSelected = false; session.HandleKeys(); session.Tick();
        check(title.gameObject.activeInHierarchy && !input.gameObject.activeInHierarchy && !input.enabled &&
            title.GetComponent<UILabel>().text == "缩放验收组 (1)",
            "iteration 3 leaving an unchanged Chinese name edit restores the membership count display");
        check(title.GetComponent<UIInput>() == null,
            "iteration 3 the non-editing membership-count label has no native input callbacks which can overwrite its display");
    }

    private static void CheckDissolveButton(EditorSession session, string id, NodeState[] nodes, string story,
        byte[] compiled, Action<bool, string> check)
    {
        var root = GroupRoot(session, id);
        var header = root.Find("Header")!;
        var button = header.GetComponentsInChildren<UILabel>(true)
            .FirstOrDefault(label => label.text == "解散")?.transform.parent;
        check(button != null && button.gameObject.activeInHierarchy && button.GetComponent<Collider>() != null,
            "iteration 3 group header exposes a visible directly clickable dissolve button");
        var previousId = UICamera.currentTouchID;
        try
        {
            UICamera.currentTouchID = -2;
            UIEventListener.Get(header.gameObject).OnClick(); session.Tick();
        }
        finally { UICamera.currentTouchID = previousId; }
        check(!session.Board.treeRoot.GetComponentsInChildren<Transform>(true).Any(transform =>
            transform.name == "NodeGroup context menu" && transform.gameObject.activeInHierarchy),
            "iteration 3 right clicking a group no longer creates the old dissolve context menu");
        AssertDissolve(session, id, button!, nodes, story, compiled, check, "expanded");
        session.Toggle(id); session.Tick();
        button = GroupRoot(session, id).Find("Header")!.GetComponentsInChildren<UILabel>(true)
            .First(label => label.text == "解散").transform.parent;
        AssertDissolve(session, id, button, nodes, story, compiled, check, "collapsed");
    }

    private static void AssertDissolve(EditorSession session, string id, Transform button, NodeState[] nodes,
        string story, byte[] compiled, Action<bool, string> check, string appearance)
    {
        var before = session.Workspace.Snapshot.GetGroup(id);
        var history = OperationManager.history.Count;
        UIEventListener.Get(button.gameObject).OnClick(); session.Tick();
        check(session.Workspace.Snapshot.Groups.All(group => group.Id != id) &&
            OperationManager.history.Count == history + 1 && NodesUnchanged(session, nodes) &&
            ProjectFileStore.Serialize(session.Editor.CaptureProject()) == story &&
            AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(compiled),
            "iteration 3 direct " + appearance + " dissolve preserves every native graph value in one Undo command");
        check(before.MemberNodeIds.All(nodeId => session.Find(nodeId) is { } node && node.gameObject.activeInHierarchy &&
            node.GetComponentsInChildren<UIWidget>(true).Any(widget => widget.enabled)),
            "iteration 3 dissolving the " + appearance + " group reveals its original member widgets");
        OperationManager.Undo(); session.Tick();
        var restored = session.Workspace.Snapshot.GetGroup(id);
        check(restored.Name == before.Name && restored.Collapsed == before.Collapsed && restored.ExpandedBounds == before.ExpandedBounds &&
            restored.MemberNodeIds.SequenceEqual(before.MemberNodeIds),
            "iteration 3 native Undo restores the " + appearance + " group's name, folded state, region and exact members");
        OperationManager.Redo(); session.Tick();
        check(session.Workspace.Snapshot.Groups.All(group => group.Id != id) && NodesUnchanged(session, nodes) &&
            ProjectFileStore.Serialize(session.Editor.CaptureProject()) == story,
            "iteration 3 native Redo reapplies direct " + appearance + " dissolution without altering the original graph");
        OperationManager.Undo(); session.Tick();
    }

    private static Transform GroupRoot(EditorSession session, string id) => session.Board.treeRoot.Find("NodeGroup " + id)
        ?? throw new InvalidOperationException("Group view is missing: " + id);
    private static bool IsYellow(Color color) => color.r > .9f && color.g > .8f && color.b < .2f && color.a > .95f;
    private static bool NodesUnchanged(EditorSession session, NodeState[] baseline) =>
        session.Board.nodes.Count == baseline.Length && baseline.All(node => session.Find(node.Id) is { } current &&
            Math.Abs(session.Position(current).X - node.Position.X) < .01 && Math.Abs(session.Position(current).Y - node.Position.Y) < .01);
}
