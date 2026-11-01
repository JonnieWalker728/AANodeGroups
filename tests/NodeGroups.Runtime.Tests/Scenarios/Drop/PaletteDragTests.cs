using AzureArchive.Automation;
using HarmonyLib;
using NodeGroups.Core;
using System.Text.Json;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>
/// Explicit, isolated acceptance fixture for the menu's complete native parcel
/// gesture. Unlike the older synchronous fixture, this never calls Unpack itself.
/// Native Start, drag and release decide when creation and registration happen.
/// </summary>
internal static class PaletteDragTests
{
    private enum GestureCase
    {
        ExpandedInside, UnselectedExpanded, Outside, OverlapTop, CollapsedInside,
        UnselectedCollapsed, CrossGroups, ReturnToMenu, EscapeCancel, InactivePreview, DestroyedPreview
    }
    private sealed class State
    {
        internal EditorSession Owner = null!;
        internal int Phase, Frames, History, NodeCount, GestureHistory, CaseHistory, CompileDelta, GestureCompileDelta;
        internal string GroupId = "", CreatedId = "", Story = "", SecondaryGroup = "";
        internal string TracePath = "";
        internal string GestureStory = "";
        internal string? DestinationId, GestureSelection;
        internal byte[] Compiled = Array.Empty<byte>();
        internal WorkspaceSnapshot OriginalSnapshot = null!, GestureSnapshot = null!;
        internal Dictionary<string, (Vector3 Root, Vector3 Component)> NativePositions = new(StringComparer.Ordinal);
        internal bool GestureDirty, PressEscape;
        internal Node[] Selection = Array.Empty<Node>();
        internal string? SelectedGroup;
        internal GameObject? SelectedObject;
        internal NodeParcel? Parcel;
        internal Node? Preview;
        internal Point2 Target;
        internal Vector3 MenuWorld;
        internal Vector3 TreePosition;
        internal GestureCase Case;
        internal PointerContext? Pointer;
        internal Harmony Probe = null!;
    }
    private static State? state;

    /// <summary>Begins only under NODEGROUPS_TEST_REPORT in the independent test process.</summary>
    internal static void Begin(EditorSession session, Action<bool, string> check)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")))
            throw new InvalidOperationException("Palette checks require the isolated runtime fixture.");
        if (state != null) throw new InvalidOperationException("A palette fixture is already active.");
        var current = new State
        {
            Owner = session, History = OperationManager.history.Count, NodeCount = session.Board.nodes.Count,
            Story = ProjectFileStore.Serialize(session.Editor.CaptureProject()), Selection = Blackboard.selected.ToArray(),
            Compiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray(),
            CompileDelta = OperationManager.compileDelta, OriginalSnapshot = session.Workspace.Snapshot,
            TreePosition = session.Board.treeRoot.position,
            TracePath = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT"))!,
                "palette-hover-trace.txt"),
            SelectedGroup = session.SelectedGroup, SelectedObject = UICamera.selectedObject,
            Probe = new Harmony(Plugin.Id + ".palette-test-probe")
        };
        state = current;
        current.NativePositions = session.Board.nodes.ToArray().ToDictionary(EditorSession.Id,
            node => (node.root.localPosition, node.transform.localPosition), StringComparer.Ordinal);
        File.WriteAllText(current.TracePath, "NodeGroups isolated native palette feedback trace" + Environment.NewLine);
        LogNativeAddresses();
        foreach (var method in typeof(NodeParcel).GetMethods().Where(method => method.Name == nameof(NodeParcel.Unpack)))
            current.Probe.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(PaletteDragTests), nameof(UnpackBefore))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(PaletteDragTests), nameof(UnpackAfter))));
        current.Probe.Patch(AccessTools.Method(typeof(Blackboard), nameof(Blackboard.RegisterNode)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(PaletteDragTests), nameof(Registered))));
        current.Probe.Patch(AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) }),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(PaletteDragTests), nameof(TestKeyDown))));
        // Existing acceptance groups may fit their native world-corner bounds
        // exactly. Panning/rebuilding a native sliced widget can move that tight
        // extent by one float ULP, so unrelated Sync would enlarge it. Give only
        // the borrowed fixture regions a small containment margin, through the
        // original metadata history. Preview assertions stay exact, and final
        // Undo plus OriginalSnapshot restore all original region values.
        foreach (var original in current.OriginalSnapshot.Groups)
        {
            var region = original.ExpandedBounds ?? session.Bounds(original);
            session.Commit(session.Workspace.SetExpandedBounds(original.Id,
                new Rect2(region.X - 32, region.Y - 32, region.Width + 64, region.Height + 64)));
        }
        Blackboard.selected.Clear();
        session.CreateGroup(true); current.GroupId = session.SelectedGroup!;
        var group = session.Workspace.Snapshot.GetGroup(current.GroupId);
        var bounds = session.Bounds(group);
        session.Commit(session.Workspace.SetExpandedBounds(current.GroupId,
            new Rect2(bounds.X - 200, bounds.Y - 500, 1200, 1100)));
        bounds = session.DropBounds(session.Workspace.Snapshot.GetGroup(current.GroupId));
        current.Target = new Point2(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        check(session.Workspace.Snapshot.GetGroup(current.GroupId).MemberNodeIds.Count == 0,
            "palette full-gesture fixture starts with an empty expanded target");
    }

    /// <summary>Advances native frames; returns true only after creation, Undo/Redo and cleanup pass.</summary>
    internal static bool Tick(EditorSession session, Action<bool, string> check, Action<string>? capturePreview = null)
    {
        var current = state ?? throw new InvalidOperationException("Palette fixture was not started.");
        if (!ReferenceEquals(current.Owner, session) || session.IsDisposed)
            throw new InvalidOperationException("Palette fixture lost its original editor owner.");
        if (++current.Frames < 3) return false;
        current.Frames = 0;
        if (current.Phase == 0)
        {
            current.CaseHistory = OperationManager.history.Count;
            PrepareCase(session, current, check);
            var factory = ParcelFactory.instance ?? throw new InvalidOperationException("Native palette factory missing.");
            var scriptIndex = Enumerable.Range(0, factory.nodePrefabs.Count).First(index =>
                factory.nodePrefabs[index].GetComponentInChildren<ScriptNode>(true) != null);
            var before = Object.FindObjectsOfType<NodeParcel>(true).Select(parcel => parcel.Pointer).ToHashSet();
            factory.CreateParcel(scriptIndex);
            current.Parcel = Object.FindObjectsOfType<NodeParcel>(true).First(parcel => !before.Contains(parcel.Pointer));
            current.Preview = current.Parcel.content.GetComponentInChildren<Node>(true);
            current.MenuWorld = current.Parcel.content.position;
            check(current.Preview != null && !session.Board.nodes.ToArray().Any(node => node.Pointer == current.Preview.Pointer),
                "menu preview remains unregistered before its native drag gesture");
            current.GestureHistory = OperationManager.history.Count;
            current.GestureSnapshot = session.Workspace.Snapshot;
            current.GestureDirty = session.Dirty; current.GestureSelection = session.SelectedGroup;
            Log("created menu parcel", current.Parcel);
            current.Phase = 1;
        }
        else if (current.Phase == 1)
        {
            var parcel = current.Parcel!;
            // Camera/canvas fixture setup and native prefab Start have settled
            // during the preceding natural frames. Borrow the exact state at
            // the actual gesture boundary; final cleanup still compares to the
            // original Begin story, compilation and complete group metadata.
            current.GestureStory = ProjectFileStore.Serialize(session.Editor.CaptureProject());
            current.GestureCompileDelta = OperationManager.compileDelta;
            current.GestureSnapshot = session.Workspace.Snapshot;
            current.GestureDirty = session.Dirty;
            check(NativePositionsUnchanged(session, current),
                "iteration 5 fixture canvas panning and native prefab Start retain every original node's exact local coordinates");
            current.Pointer = new PointerContext(session, parcel);
            Log("before drag start", parcel);
            parcel.OnDragStart();
            WriteTrace($"Palette selection after native Start: case={current.Case}; " +
                $"selected={session.SelectedGroup ?? "<none>"}; expected={current.GestureSelection ?? "<none>"}");
            check(session.SelectedGroup == current.GestureSelection,
                "iteration 5 native menu drag start preserves the user's current group selection");
            current.Pointer.MoveTo(current.Target);
            parcel.OnDrag();
            Log("after drag", parcel);
            check(!session.Board.nodes.ToArray().Any(node => node.Pointer == current.Preview!.Pointer) &&
                session.Workspace.Snapshot.GetGroup(current.GroupId).MemberNodeIds.Count == 0,
                "native menu drag preview does not register or add a group member");
            session.Tick();
            if (current.Case == GestureCase.ExpandedInside)
            {
                check(session.DropTarget == current.GroupId,
                    "iteration 5 the real native menu drag previews its expanded destination before node registration");
                check(DropTargetFeedbackTests.IsGreen(DropTargetFeedbackTests.Frame(session, current.GroupId).color) &&
                    session.SelectedGroup == current.GroupId,
                    "iteration 5 hovering the native menu preview turns the selected yellow destination green");
            }
            AssertPreview(session, current, current.Case == GestureCase.Outside ? null : current.GroupId, check);
            current.Phase = 2;
        }
        else if (current.Phase == 2)
        {
            // Hold natural frames before observing submitted frame geometry and
            // capturing the actual native contour, rather than only its tint.
            AssertPreview(session, current, current.Case == GestureCase.Outside ? null : current.GroupId, check);
            if (current.Case == GestureCase.ExpandedInside) capturePreview?.Invoke("menu-hover");
            if (current.Case == GestureCase.UnselectedCollapsed)
            {
                var frame = DropTargetFeedbackTests.Frame(session, current.GroupId);
                var header = frame.transform.parent.Find("Header")!.GetComponent<UISprite>();
                check(frame.geometry.hasVertices && frame.depth > header.depth,
                    "iteration 5 held unselected collapsed target submits a green contour above its native header body");
                capturePreview?.Invoke("menu-collapsed-hover");
            }
            if (current.Case == GestureCase.CrossGroups)
            {
                var bounds = session.DropBounds(session.Workspace.Snapshot.GetGroup(current.GroupId));
                current.Pointer!.MoveTo(new Point2(bounds.X - 1000, bounds.Y - 1000));
                current.Parcel!.OnDrag(); session.Tick(); AssertPreview(session, current, null, check);
                current.Target = Center(session.DropBounds(session.Workspace.Snapshot.GetGroup(current.SecondaryGroup)));
                current.Pointer.MoveTo(current.Target); current.Parcel.OnDrag(); session.Tick();
                AssertPreview(session, current, current.SecondaryGroup, check);
                current.Phase = 20; return false;
            }
            if (current.Case == GestureCase.EscapeCancel)
            {
                // The patch is scoped to this opted-in fixture and one HandleKeys
                // call. The real product Escape entry point owns cancellation.
                current.PressEscape = true;
                try { session.HandleKeys(); } finally { current.PressEscape = false; }
                session.Tick(); AssertPreview(session, current, null, check);
                current.Pointer!.MoveTo(current.Target); current.Parcel!.OnDrag(); session.Tick();
                AssertPreview(session, current, null, check);
                check(session.DropTarget == null,
                    "iteration 5 another native OnDrag after Escape cannot revive the cancelled menu target");
            }
            if (current.Case is GestureCase.InactivePreview or GestureCase.DestroyedPreview)
            {
                current.CreatedId = EditorSession.Id(current.Preview!);
                if (current.Case == GestureCase.InactivePreview) current.Preview!.gameObject.SetActive(false);
                else Object.Destroy(current.Preview!.root.gameObject);
                current.Phase = 21; return false;
            }
            Release(session, current, check);
        }
        else if (current.Phase == 20)
        {
            AssertPreview(session, current, current.SecondaryGroup, check);
            capturePreview?.Invoke("menu-cross-hover");
            Release(session, current, check);
        }
        else if (current.Phase == 21)
        {
            session.Tick(); AssertPreview(session, current, null, check);
            check(session.DropTarget == null,
                "iteration 5 native frames clear a menu target when its preview is inactive or destroyed");
            current.Pointer?.Dispose(); current.Pointer = null;
            CleanUnregisteredPreview(current);
            return FinishCancelled(session, current, check);
        }
        else if (current.Phase == 3)
        {
            session.Tick();
            check(session.DropTarget == null && session.SelectedGroup == current.GestureSelection,
                "iteration 5 native menu release clears temporary feedback and preserves the user's group selection");
            AssertFrameRestored(session, current.GroupId, check);
            if (current.Case == GestureCase.ExpandedInside) capturePreview?.Invoke("menu-restored");
            if (current.Case is GestureCase.ReturnToMenu or GestureCase.EscapeCancel)
            {
                check(session.Find(current.CreatedId) == null && session.Board.nodes.Count == current.NodeCount &&
                    session.Workspace.Snapshot.FindGroupForNode(current.CreatedId) == null &&
                    OperationManager.history.Count == current.GestureHistory,
                    "returning a dragged menu preview to the menu cancels native creation without membership or history");
                check(current.Parcel != null && !current.Parcel.outside &&
                    current.Parcel.content.parent?.Pointer != session.Board.treeRoot.Pointer,
                    "cancelled menu preview stays in its native menu parcel rather than the canvas registry");
                CleanUnregisteredPreview(current);
                return FinishCancelled(session, current, check);
            }
            check(session.Find(current.CreatedId) != null && session.Board.nodes.Count == current.NodeCount + 1,
                "full native menu drag and release register exactly one new node");
            var assigned = current.DestinationId != null;
            check(session.Workspace.Snapshot.FindGroupForNode(current.CreatedId)?.Id == current.DestinationId,
                current.Case switch
                {
                    GestureCase.Outside => "first native menu release outside every group keeps its new node ungrouped",
                    GestureCase.OverlapTop => "first native menu release in overlapping groups chooses the group promoted by selection",
                    GestureCase.CollapsedInside or GestureCase.UnselectedCollapsed => "first native menu release on a collapsed card adds and reveals its new member",
                    GestureCase.CrossGroups => "first native menu release joins the last green destination after crossing groups",
                    _ => "first native menu release inside a group adds its newly registered node without a second drag"
                });
            if (current.Case is GestureCase.CollapsedInside or GestureCase.UnselectedCollapsed)
                check(!session.Workspace.Snapshot.GetGroup(current.GroupId).Collapsed,
                    "successful first menu release expands a collapsed destination through its existing placement rule");
            check(OperationManager.history.Count == current.GestureHistory + (assigned ? 2 : 1) &&
                (!assigned || OperationManager.history.Peek().linkedPrev != null),
                assigned ? "first menu release links membership to its own native creation operation" :
                    "outside menu release retains exactly its original native creation history entry");
            OperationManager.Undo(); session.Tick();
            check(session.Find(current.CreatedId) == null &&
                session.Workspace.Snapshot.FindGroupForNode(current.CreatedId) == null &&
                OperationManager.history.Count == current.GestureHistory,
                "one Undo removes first-release menu creation and membership together");
            OperationManager.Redo(); session.Tick();
            check(session.Find(current.CreatedId) != null &&
                session.Workspace.Snapshot.FindGroupForNode(current.CreatedId)?.Id == current.DestinationId,
                "one Redo restores first-release menu node GUID and membership together");
            OperationManager.Undo(); session.Tick();
            return NextCase(session, current, check);
        }
        return false;
    }

    /// <summary>Case setup changes only temporary group metadata before a native creation gesture.</summary>
    private static void PrepareCase(EditorSession session, State current, Action<bool, string> check)
    {
        current.SecondaryGroup = "";
        var group = session.Workspace.Snapshot.GetGroup(current.GroupId);
        if (current.Case == GestureCase.OverlapTop)
        {
            Blackboard.selected.Clear(); session.CreateGroup(true);
            var upper = session.SelectedGroup!;
            session.Commit(session.Workspace.SetExpandedBounds(upper, session.Bounds(group)));
            session.SelectGroup(current.GroupId);
            check(session.Workspace.Snapshot.GetGroup(current.GroupId).ZIndex > session.Workspace.Snapshot.GetGroup(upper).ZIndex,
                "overlap fixture promotes only the selected original group before menu release");
        }
        else if (current.Case is GestureCase.CollapsedInside or GestureCase.UnselectedCollapsed) session.Toggle(current.GroupId);
        else if (current.Case == GestureCase.CrossGroups)
        {
            Blackboard.selected.Clear(); session.CreateGroup(true); current.SecondaryGroup = session.SelectedGroup!;
            var first = session.Bounds(group);
            session.Commit(session.Workspace.SetExpandedBounds(current.SecondaryGroup,
                new Rect2(first.X + first.Width + 200, first.Y, first.Width, first.Height)));
            session.SelectGroup(current.GroupId);
        }
        if (current.Case is GestureCase.UnselectedExpanded or GestureCase.UnselectedCollapsed) session.DeselectGroup();
        else if (current.Case != GestureCase.OverlapTop) session.SelectGroup(current.GroupId);
        var bounds = session.DropBounds(session.Workspace.Snapshot.GetGroup(current.GroupId));
        current.Target = current.Case == GestureCase.Outside
            ? new Point2(bounds.X + bounds.Width + 1000, bounds.Y + bounds.Height / 2)
            : Center(bounds);
        current.DestinationId = current.Case switch
        {
            GestureCase.Outside or GestureCase.ReturnToMenu or GestureCase.EscapeCancel or
                GestureCase.InactivePreview or GestureCase.DestroyedPreview => null,
            GestureCase.CrossGroups => current.SecondaryGroup,
            _ => current.GroupId
        };
        session.Tick(); PanTo(session, Center(bounds));
        WriteTrace($"Palette case starting: {current.Case}; target={current.Target}; group={current.GroupId}; " +
            $"selected={session.SelectedGroup ?? "<none>"}; destination={current.DestinationId ?? "<none>"}");
    }

    private static Point2 Center(Rect2 bounds) => new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    /// <summary>Centers only the borrowed canvas transform for observable native screenshots.</summary>
    private static void PanTo(EditorSession session, Point2 point)
    {
        var camera = UICamera.FindCameraForLayer(session.Board.treeRoot.gameObject.layer)?.GetComponent<Camera>();
        if (camera == null) return;
        var ray = camera.ViewportPointToRay(new Vector3(.5f, .55f, 0));
        var plane = new Plane(session.Board.treeRoot.forward, session.Board.treeRoot.position);
        if (plane.Raycast(ray, out var distance))
            session.Board.treeRoot.position += ray.GetPoint(distance) -
                session.Board.treeRoot.TransformPoint(new Vector3((float)point.X, (float)point.Y, 0));
        Physics.SyncTransforms();
    }

    /// <summary>Asserts feedback against the actual native parcel, never supplies a product target.</summary>
    private static void AssertPreview(EditorSession session, State current, string? expected, Action<bool, string> check)
    {
        WriteTrace($"Palette hover: case={current.Case}; phase={current.Phase}; selected={session.SelectedGroup ?? "<none>"}; " +
            $"drop={session.DropTarget ?? "<none>"}; expected={expected ?? "<none>"}; " +
            $"color={DropTargetFeedbackTests.Frame(session, current.GroupId).color}; " +
            $"history={OperationManager.history.Count}/{current.GestureHistory}; dirty={session.Dirty}/{current.GestureDirty}");
        check(session.DropTarget == expected && session.SelectedGroup == current.GestureSelection,
            "iteration 5 real native menu hover chooses only its current destination without changing group selection");
        foreach (var group in session.Workspace.Snapshot.Groups)
        {
            var frame = DropTargetFeedbackTests.Frame(session, group.Id);
            if (group.Id == expected)
                check(frame.gameObject.activeInHierarchy && DropTargetFeedbackTests.IsGreen(frame.color),
                    "iteration 5 exactly the real menu destination exposes a native green contour: " + current.Case);
            else
                check(!DropTargetFeedbackTests.IsGreen(frame.color) &&
                    (group.Id != session.SelectedGroup || DropTargetFeedbackTests.IsYellow(frame.color)),
                    "iteration 5 every other group retains its normal or selected contour during native menu hover: " + current.Case);
        }
        var actualStory = ProjectFileStore.Serialize(session.Editor.CaptureProject());
        var sameGroups = DropTargetFeedbackTests.SameGroups(current.GestureSnapshot.Groups, session.Workspace.Snapshot.Groups);
        var sameHistory = OperationManager.history.Count == current.GestureHistory;
        var sameDirty = session.Dirty == current.GestureDirty;
        var sameCount = session.Board.nodes.Count == current.NodeCount;
        var sameCompile = OperationManager.compileDelta == current.GestureCompileDelta;
        var sameStory = actualStory == current.GestureStory;
        var samePositions = NativePositionsUnchanged(session, current);
        WriteTrace($"Palette invariant details: groups={sameGroups}; history={sameHistory}; dirty={sameDirty}; " +
            $"count={sameCount}({session.Board.nodes.Count}/{current.NodeCount}); " +
            $"compile={sameCompile}({OperationManager.compileDelta}/{current.GestureCompileDelta}; begin={current.CompileDelta}); " +
            $"storyGesture={sameStory}; storyBegin={actualStory == current.Story}; nativeLocalPositions={samePositions}");
        if (!sameGroups || !sameHistory || !sameDirty || !sameCount || !sameCompile || !sameStory || !samePositions || actualStory != current.Story)
        {
            var prefix = Path.Combine(Path.GetDirectoryName(current.TracePath)!, $"palette-{current.Case}-{current.Phase}");
            File.WriteAllText(prefix + "-begin-story.json", current.Story);
            File.WriteAllText(prefix + "-gesture-story.json", current.GestureStory);
            File.WriteAllText(prefix + "-actual-story.json", actualStory);
            File.WriteAllText(prefix + "-gesture-groups.json", JsonSerializer.Serialize(current.GestureSnapshot.Groups));
            File.WriteAllText(prefix + "-actual-groups.json", JsonSerializer.Serialize(session.Workspace.Snapshot.Groups));
        }
        check(sameGroups && sameHistory && sameDirty && sameCount && sameCompile && sameStory && samePositions,
            "iteration 5 native menu hover leaves membership, counts, bounds, layers, dirty state, native graph, compile delta and history unchanged");
    }

    /// <summary>Panning the borrowed view must never move registered native roots or their story components.</summary>
    private static bool NativePositionsUnchanged(EditorSession session, State current) => current.NativePositions.All(pair =>
    {
        var node = session.Find(pair.Key);
        return node != null && node.root.localPosition.Equals(pair.Value.Root) && node.transform.localPosition.Equals(pair.Value.Component);
    });

    private static void AssertFrameRestored(EditorSession session, string id, Action<bool, string> check)
    {
        var group = session.Workspace.Snapshot.GetGroup(id);
        var frame = DropTargetFeedbackTests.Frame(session, id);
        var selected = session.SelectedGroup == id;
        check(!DropTargetFeedbackTests.IsGreen(frame.color) &&
            (!selected || DropTargetFeedbackTests.IsYellow(frame.color)) &&
            frame.gameObject.activeInHierarchy == (!group.Collapsed || selected),
            "iteration 5 ending native menu hover restores the destination's original selected or normal contour");
    }

    /// <summary>Releases through the native callback after any final menu-return movement.</summary>
    private static void Release(EditorSession session, State current, Action<bool, string> check)
    {
        if (current.Case is GestureCase.ReturnToMenu or GestureCase.EscapeCancel)
        {
            current.Pointer!.MoveToWorld(current.MenuWorld); current.Parcel!.OnDrag(); session.Tick();
            AssertPreview(session, current, null, check);
            AssertFrameRestored(session, current.GroupId, check);
        }
        else
        {
            current.Pointer!.MoveTo(current.Target); current.Parcel!.OnDrag();
        }
        Log("before release", current.Parcel!); current.Parcel!.OnDragEnd();
        current.CreatedId = EditorSession.Id(current.Preview!);
        WriteTrace($"Palette full gesture after release: previewGuid={current.CreatedId}; " +
            $"registered={session.Find(current.CreatedId) != null}; history={OperationManager.history.Count}; " +
            $"group={session.Workspace.Snapshot.FindGroupForNode(current.CreatedId)?.Id}; " +
            $"position={session.Position(current.Preview!)}; pointer={current.Target}; " +
            $"selected={session.SelectedGroup ?? "<none>"}; drop={session.DropTarget ?? "<none>"}");
        current.Pointer.Dispose(); current.Pointer = null; current.Phase = 3;
    }

    private static bool FinishCancelled(EditorSession session, State current, Action<bool, string> check)
    {
        check(session.Board.nodes.Count == current.NodeCount && OperationManager.history.Count == current.GestureHistory &&
            session.DropTarget == null && session.SelectedGroup == current.GestureSelection &&
            DropTargetFeedbackTests.SameGroups(current.GestureSnapshot.Groups, session.Workspace.Snapshot.Groups),
            "iteration 5 cancelled or lost native menu previews leave no node, member, history or temporary feedback");
        return NextCase(session, current, check);
    }

    /// <summary>Destroys only unregistered fixture previews; native created nodes remain owned by Undo.</summary>
    private static void CleanUnregisteredPreview(State current)
    {
        if (current.Preview != null && !current.Owner.Board.nodes.ToArray().Any(node => node.Pointer == current.Preview.Pointer))
        {
            var root = current.Preview.root;
            if (root != null) { root.gameObject.SetActive(false); Object.Destroy(root.gameObject); }
        }
        if (current.Parcel != null)
        { current.Parcel.gameObject.SetActive(false); Object.Destroy(current.Parcel.gameObject); }
        current.Parcel = null; current.Preview = null;
    }

    private static bool NextCase(EditorSession session, State current, Action<bool, string> check)
    {
        var guard = OperationManager.history.Count - current.CaseHistory + 8;
        while (OperationManager.history.Count > current.CaseHistory && guard-- > 0) OperationManager.Undo();
        session.Tick();
        check(OperationManager.history.Count == current.CaseHistory && session.Board.nodes.Count == current.NodeCount &&
            session.Workspace.Snapshot.GetGroup(current.GroupId).MemberNodeIds.Count == 0,
            "completed menu case leaves no new native nodes, members or history commands");
        if (current.Case < GestureCase.DestroyedPreview)
        {
            current.Case++; current.Phase = 0; current.Parcel = null; current.Preview = null; current.CreatedId = "";
            return false;
        }
        Restore(current);
        check(OperationManager.history.Count == current.History && session.Board.nodes.Count == current.NodeCount &&
            ProjectFileStore.Serialize(session.Editor.CaptureProject()) == current.Story &&
            AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(current.Compiled) &&
            OperationManager.compileDelta == current.CompileDelta && session.SelectedGroup == current.SelectedGroup &&
            DropTargetFeedbackTests.SameGroups(current.OriginalSnapshot.Groups, session.Workspace.Snapshot.Groups) &&
            session.Board.treeRoot.position.Equals(current.TreePosition),
            "full menu gesture fixture restores exact original native graph, compile bytes, history, selection, layers and canvas");
        state = null; return true;
    }

    /// <summary>Aborts only this fixture's live owner; a replacement scene's history is never touched.</summary>
    internal static void Abort(EditorSession? session)
    {
        var current = state; state = null;
        if (current == null) return;
        try
        {
            current.Pointer?.Dispose(); current.Pointer = null;
            current.Probe.UnpatchSelf();
            if (session != null && !session.IsDisposed && ReferenceEquals(session, current.Owner))
            { CleanUnregisteredPreview(current); Restore(current); }
        }
        catch (Exception error) { Plugin.Logger.LogError("Palette fixture cleanup failed: " + error); }
    }

    private static void Restore(State current)
    {
        current.Pointer?.Dispose(); current.Pointer = null;
        current.Probe.UnpatchSelf();
        var session = current.Owner;
        session.CancelDrag();
        var remaining = OperationManager.history.Count - current.History + 8;
        while (OperationManager.history.Count > current.History && remaining-- > 0) OperationManager.Undo();
        session.Workspace.Restore(current.OriginalSnapshot); session.Board.treeRoot.position = current.TreePosition;
        Physics.SyncTransforms();
        session.Tick();
        Blackboard.selected.Clear();
        foreach (var selected in current.Selection)
            if (selected != null && session.Find(EditorSession.Id(selected)) != null) Blackboard.selected.Add(selected);
        UICamera.selectedObject = current.SelectedObject; session.SelectedGroup = current.SelectedGroup; session.Tick();
    }

    private static bool TestKeyDown(KeyCode __0, ref bool __result)
    {
        if (state?.PressEscape != true || __0 != KeyCode.Escape) return true;
        __result = true; return false;
    }

    /// <summary>Copies only this process's opted-in fixture events into its report directory.</summary>
    private static void WriteTrace(string message)
    {
        Plugin.Logger.LogInfo(message);
        if (state != null) File.AppendAllText(state.TracePath, DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine);
    }

    private static void UnpackBefore(NodeParcel __instance, System.Reflection.MethodBase __originalMethod)
    { WriteTrace("Palette native Unpack ENTER: " + __originalMethod); Log("unpack before", __instance); }
    private static void UnpackAfter(NodeParcel __instance, System.Reflection.MethodBase __originalMethod)
    { WriteTrace("Palette native Unpack EXIT: " + __originalMethod); Log("unpack after", __instance); }
    private static void Registered(Node __0) => WriteTrace(
        $"Palette native RegisterNode: type={__0.GetType().Name}; guid={EditorSession.Id(__0)}; history={OperationManager.history.Count}");

    private static void Log(string phase, NodeParcel parcel)
    {
        var preview = parcel.content?.GetComponentInChildren<Node>(true);
        WriteTrace($"Palette {phase}: outside={parcel.outside}; content={parcel.content?.position}; " +
            $"parcelWorld={parcel.transform.position}; expectedDrag={parcel.expectedDragPos}; panel={parcel.panelTrans?.name}; " +
            $"rootBounds={parcel.rootBounds}; guid={(preview == null ? "<none>" : EditorSession.Id(preview))}; " +
            $"registered={(preview != null && state?.Owner.Board.nodes.ToArray().Any(node => node.Pointer == preview.Pointer) == true)}; " +
            $"history={OperationManager.history.Count}; top={(OperationManager.history.Count > 0 ? OperationManager.history.Peek().Name : "<empty>")}");
    }

    private static void LogNativeAddresses()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var module = process.Modules.Cast<System.Diagnostics.ProcessModule>().First(candidate =>
            string.Equals(candidate.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
        foreach (var type in new[] { typeof(NodeParcel), typeof(ParcelFactory) })
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .Where(field => field.Name.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal)))
            {
                if (field.GetValue(null) is not IntPtr info || info == IntPtr.Zero) continue;
                var pointer = System.Runtime.InteropServices.Marshal.ReadIntPtr(info);
                Plugin.Logger.LogInfo($"Palette native address: {type.Name}.{field.Name}; rva=0x{pointer.ToInt64() - module.BaseAddress.ToInt64():X}");
            }
    }

    /// <summary>Supplies the same NGUI pointer/canvas tracker state used by native parcel drag callbacks.</summary>
    // 回收站的 NodeParcel 与菜单共享原生拖动回调，测试也复用同一触点适配。
    internal sealed class PointerContext : IDisposable
    {
        private readonly EditorSession session;
        private readonly UICamera.MouseOrTouch? previousTouch = UICamera.currentTouch;
        private readonly UICamera? previousCamera = UICamera.current;
        private readonly Camera? previousCurrentCamera = UICamera.currentCamera;
        private readonly int previousId = UICamera.currentTouchID;
        private readonly KeyCode previousKey = UICamera.currentKey;
        private readonly Vector3 previousWorld = UICamera.lastWorldPosition;
        private readonly Vector3 previousTracker;
        private readonly Camera camera;
        private readonly UICamera.MouseOrTouch touch;
        internal PointerContext(EditorSession session, NodeParcel parcel)
        {
            this.session = session; previousTracker = session.Editor.mouseTracker.position;
            camera = UICamera.FindCameraForLayer(parcel.gameObject.layer).GetComponent<Camera>();
            var screen = camera.WorldToScreenPoint(parcel.content.position);
            touch = new UICamera.MouseOrTouch
            {
                key = KeyCode.Mouse0, pos = new Vector2(screen.x, screen.y), lastPos = new Vector2(screen.x, screen.y),
                delta = Vector2.zero, totalDelta = Vector2.zero, pressedCam = camera,
                current = parcel.gameObject, pressed = parcel.gameObject, dragged = parcel.gameObject,
                pressStarted = true, dragStarted = true
            };
            UICamera.currentTouch = touch; UICamera.currentTouchID = -1; UICamera.currentKey = KeyCode.Mouse0;
            UICamera.current = UICamera.FindCameraForLayer(parcel.gameObject.layer); UICamera.currentCamera = camera;
            UICamera.lastWorldPosition = parcel.content.position; session.Editor.mouseTracker.position = parcel.content.position;
        }
        internal void MoveTo(Point2 point)
        {
            var world = session.Board.treeRoot.TransformPoint(new Vector3((float)point.X, (float)point.Y, 0));
            MoveToWorld(world);
        }
        internal void MoveToWorld(Vector3 world)
        {
            var screen = camera.WorldToScreenPoint(world); var position = new Vector2(screen.x, screen.y);
            touch.lastPos = touch.pos; touch.delta = position - touch.pos; touch.totalDelta += touch.delta; touch.pos = position;
            // Native UICamera.Update may clear its dispatch globals between
            // natural frames. Re-publish the event context before each callback.
            UICamera.currentTouch = touch; UICamera.currentTouchID = -1; UICamera.currentKey = KeyCode.Mouse0;
            UICamera.current = UICamera.FindCameraForLayer(session.Board.gameObject.layer); UICamera.currentCamera = camera;
            UICamera.lastWorldPosition = world; session.Editor.mouseTracker.position = world;
        }
        public void Dispose()
        {
            UICamera.currentTouch = previousTouch; UICamera.currentTouchID = previousId; UICamera.currentKey = previousKey;
            UICamera.current = previousCamera; UICamera.currentCamera = previousCurrentCamera;
            UICamera.lastWorldPosition = previousWorld;
            if (session.Editor.mouseTracker != null) session.Editor.mouseTracker.position = previousTracker;
        }
    }
}
