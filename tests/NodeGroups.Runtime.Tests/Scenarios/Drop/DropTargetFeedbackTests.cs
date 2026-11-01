using AzureArchive.Automation;
using NodeGroups.Core;
using Studio.Scripts;
using Studio.Scripts.OperationManagement;
using UnityEngine;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>
/// Isolated rendering contracts for temporary drop feedback. These checks may
/// set the existing view-only DropTarget field to separate color precedence
/// from native parcel input. The palette fixture exercises the real gesture.
/// </summary>
internal static class DropTargetFeedbackTests
{
    internal static void Run(EditorSession session, Action<bool, string> check)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")))
            throw new InvalidOperationException("Iteration 5 checks require the isolated runtime fixture.");
        var boundary = OperationManager.history.Count;
        var compileDelta = OperationManager.compileDelta;
        var previousNodes = Blackboard.selected.ToArray();
        var previousGroup = session.SelectedGroup;
        var previousObject = UICamera.selectedObject;
        var previousSnapshot = session.Workspace.Snapshot;
        var previousDropTarget = session.DropTarget;
        var story = ProjectFileStore.Serialize(session.Editor.CaptureProject());
        var compiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray();
        try
        {
            Blackboard.selected.Clear(); session.CreateGroup(true); session.Tick();
            var id = session.SelectedGroup ?? throw new InvalidOperationException("Feedback fixture group was not selected.");
            var frame = Frame(session, id);
            check(IsYellow(frame.color) && frame.gameObject.activeInHierarchy,
                "iteration 5 selected expanded group starts with its native yellow contour");
            var selectedSnapshot = session.Workspace.Snapshot;
            session.DropTarget = id; session.Tick();
            check(IsGreen(frame.color) && frame.gameObject.activeInHierarchy && session.SelectedGroup == id,
                "iteration 5 temporary drop feedback is green even when its destination is selected");
            check(SameGroups(session.Workspace.Snapshot.Groups, selectedSnapshot.Groups),
                "iteration 5 rendering a drop target changes neither membership nor group layers");
            session.DropTarget = null; session.Tick();
            check(IsYellow(frame.color) && session.SelectedGroup == id,
                "iteration 5 clearing temporary drop feedback restores the original selected yellow contour");
            CheckRegisteredNodePreview(session, id, frame, check);
            session.Toggle(id); session.DeselectGroup(); session.Tick();
            check(!frame.gameObject.activeInHierarchy,
                "iteration 5 an unselected collapsed group has no persistent region contour");
            session.DropTarget = id; session.Tick();
            check(frame.gameObject.activeInHierarchy && IsGreen(frame.color) && session.SelectedGroup == null,
                "iteration 5 an unselected collapsed destination temporarily exposes its native green contour");
            session.DropTarget = null; session.Tick();
            check(!frame.gameObject.activeInHierarchy,
                "iteration 5 leaving an unselected collapsed destination removes its temporary contour");
        }
        finally
        {
            session.CancelDrag(); session.Cursor.Reset();
            var guard = Math.Max(0, OperationManager.history.Count - boundary) + 8;
            while (OperationManager.history.Count > boundary && guard-- > 0) OperationManager.Undo();
            // Selection promotes layers outside history. Restore the exact
            // borrowed metadata rather than relying only on native Undo.
            session.Workspace.Restore(previousSnapshot);
            Blackboard.selected.Clear();
            foreach (var node in previousNodes)
                if (node != null && session.Find(EditorSession.Id(node)) != null) Blackboard.selected.Add(node);
            UICamera.selectedObject = previousObject;
            session.SelectedGroup = previousGroup; session.DropTarget = previousDropTarget; session.Tick();
        }
        check(OperationManager.history.Count == boundary && OperationManager.compileDelta == compileDelta &&
            ProjectFileStore.Serialize(session.Editor.CaptureProject()) == story &&
            AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(compiled) &&
            session.SelectedGroup == previousGroup && SameGroups(previousSnapshot.Groups, session.Workspace.Snapshot.Groups),
            "iteration 5 feedback rendering restores exact original native story, compile bytes, history, selection and layers");
    }

    /// <summary>
    /// Uses an existing registered node and the normal drag adapter. The empty
    /// fixture region is placed around its original root so a zero-distance
    /// preview can test feedback without perturbing fractional native positions.
    /// </summary>
    private static void CheckRegisteredNodePreview(EditorSession session, string id, UISprite frame, Action<bool, string> check)
    {
        var source = session.Board.nodes.ToArray().First(node => node != null && node.root != null);
        var position = session.Position(source);
        session.Commit(session.Workspace.SetExpandedBounds(id, new Rect2(position.X - 100, position.Y - 100, 1200, 900)));
        session.Tick();
        var groups = session.Workspace.Snapshot.Groups;
        var history = OperationManager.history.Count;
        Blackboard.selected.Clear(); Blackboard.selected.Add(source);
        session.BeginNodeDrag(source); session.PreviewNodeDrag();
        check(session.DropTarget == id && IsGreen(frame.color) && session.SelectedGroup == id,
            "iteration 5 registered node drag feedback also takes green precedence over selected yellow");
        check(SameGroups(groups, session.Workspace.Snapshot.Groups) && OperationManager.history.Count == history,
            "iteration 5 registered node hover changes neither membership, bounds, layers nor native history");
        session.CancelDrag(); session.Tick();
        check(session.DropTarget == null && IsYellow(frame.color) && session.Position(source) == position,
            "iteration 5 cancelling registered node hover restores yellow and the exact original native position");
        // Native registered gestures normally deliver their End callback even
        // after Escape. Complete that latch so the fixture borrows no suppression.
        session.EndNodeDrag(); session.Tick();
    }

    /// <summary>Compares presentation values, independent of immutable snapshot allocation.</summary>
    internal static bool SameGroups(IEnumerable<GroupState> expected, IEnumerable<GroupState> actual)
    {
        var left = expected.ToArray(); var right = actual.ToArray();
        return left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name &&
            pair.First.Collapsed == pair.Second.Collapsed && pair.First.Anchor == pair.Second.Anchor &&
            pair.First.Color == pair.Second.Color && pair.First.ZIndex == pair.Second.ZIndex &&
            pair.First.ExpandedBounds == pair.Second.ExpandedBounds &&
            pair.First.MemberNodeIds.SequenceEqual(pair.Second.MemberNodeIds, StringComparer.Ordinal));
    }

    internal static UISprite Frame(EditorSession session, string id) =>
        session.Board.treeRoot.Find("NodeGroup " + id)?.Find("Region frame")?.GetComponent<UISprite>()
        ?? throw new InvalidOperationException("Feedback fixture group has no native region frame.");
    internal static bool IsGreen(Color color) => color.r < .65f && color.g > .95f && color.b > .45f && color.b < .65f && color.a > .95f;
    internal static bool IsYellow(Color color) => color.r > .9f && color.g > .8f && color.b < .2f && color.a > .95f;
}
