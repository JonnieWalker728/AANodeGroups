using AzureArchive.Automation;
using Il2CppInterop.Runtime;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;
using UnityEngine;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>单创建入口的原生外观、点击、拖动和视口定位回归。</summary>
internal static partial class GroupCanvasTests
{
    /// <summary>在原生场景初始化后检查单按钮入口，避免旧面板仅被隐藏却仍拦截画布输入。</summary>
    internal static void CheckInitialUi(EditorSession session, Action<bool, string> check)
    {
        var toolbar = session.Board.treeRoot.parent.Find("NodeGroups toolbar");
        check(toolbar != null, "single-create entry is attached outside the zooming canvas");
        var names = toolbar!.GetComponentsInChildren<UIEventListener>(true)
            .Where(listener => listener.onClick != null).Select(listener => listener.gameObject.name).ToArray();
        check(names.Length == 1 && names[0] == "创建组",
            "single-create entry has exactly one create action and no save action");
        check(toolbar.Find("Panel") == null && toolbar.Find("Title") == null && toolbar.Find("Status") == null &&
            toolbar.Find("保存组") == null,
            "single-create entry removes the old panel, heading, save button and status objects");
        var button = toolbar.Find("创建组");
        var body = button?.GetComponent<UISprite>();
        var title = button?.Find("Label")?.GetComponent<UILabel>();
        var script = session.Board.nodes.ToArray().Select(node => node.TryCast<ScriptNode>()).First(node => node != null)!;
        check(body != null && SameSprite(body, script.sprite) && body.color == script.sprite.color,
            "single-create button reuses the native ScriptNode body atlas, material, sprite and color");
        var nativeTitle = script.nameLabel;
        check(title != null && title.text == "创建组" && title.alignment == NGUIText.Alignment.Center &&
            SameObject(title.trueTypeFont, nativeTitle.trueTypeFont) && SameObject(title.bitmapFont, nativeTitle.bitmapFont) &&
            SameObject(title.material, nativeTitle.material) && title.fontStyle == nativeTitle.fontStyle &&
            title.applyGradient == nativeTitle.applyGradient && title.gradientTop == nativeTitle.gradientTop &&
            title.gradientBottom == nativeTitle.gradientBottom,
            "single-create caption reuses centered native blue typography, font resources and material");
        var widgets = toolbar.GetComponentsInChildren<UIWidget>(true);
        var colliders = toolbar.GetComponentsInChildren<Collider>(true);
        check(widgets.Length == 2 && colliders.Length == 1 && button != null &&
            colliders[0].gameObject.Pointer == button.gameObject.Pointer && toolbar.GetComponent<Collider>() == null &&
            toolbar.GetComponentsInChildren<Node>(true).Length == 0 && toolbar.GetComponentsInChildren<Line>(true).Length == 0,
            "single-create entry owns only a button and caption with no leftover panel collider or native graph objects");
    }

    /// <summary>通过 NGUI 按压、拖动和点击回调验证单按钮同时支持移动与创建，且不污染剧情或历史。</summary>
    private static void CheckCreateButtonGestures(EditorSession session, Action<bool, string> check)
    {
        var panel = session.Board.treeRoot.parent.Find("NodeGroups toolbar")!;
        var button = panel.Find("创建组")!;
        var listener = UIEventListener.Get(button.gameObject);
        check(listener.onPress != null && listener.onClick != null && listener.onDragStart != null &&
            listener.onDrag != null && listener.onDragEnd != null,
            "single-create button exposes native press, click and drag callbacks without a separate title");
        var original = panel.localPosition;
        var history = OperationManager.history.Count;
        var groups = session.Workspace.Snapshot.Groups.Count;
        var previousGroup = session.SelectedGroup;
        var previousNodes = Blackboard.selected.ToArray();
        var nativeBefore = ProjectFileStore.Serialize(session.Editor.CaptureProject());
        var compiled = AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray();
        var tree = session.Board.treeRoot;
        var treePosition = tree.position; var treeScale = tree.localScale;
        var snapshot = session.Workspace.Snapshot;
        var camera = UICamera.FindCameraForLayer(button.gameObject.layer)?.GetComponent<Camera>()
            ?? throw new InvalidOperationException("The floating entry requires its native UI camera.");
        var originalScreen = camera.WorldToScreenPoint(button.position);

        try
        {
            // 小于拖动阈值的手部抖动仍应是一次点击，不能把入口移走或吞掉本次创建。
            using (var touch = new PointerContext(session, button.gameObject, button.position, dragging: false))
            {
                listener.OnPress(true);
                var jitter = touch.MoveScreenBy(new Vector2(1, 1));
                listener.OnDrag(jitter);
                listener.OnPress(false); listener.OnClick();
            }
            check(session.Workspace.Snapshot.Groups.Count == groups + 1 && OperationManager.history.Count == history + 1 &&
                (panel.localPosition - original).sqrMagnitude < .01,
                "single-create click tolerates one-pixel pointer jitter and creates exactly one group without moving the entry");
            OperationManager.Undo(); session.SelectedGroup = previousGroup; session.Tick();
            check(session.Workspace.Snapshot.Groups.Count == groups && OperationManager.history.Count == history,
                "single-create click remains one native Undo operation");

            using (var touch = new PointerContext(session, button.gameObject, button.position))
            {
                listener.OnPress(true); listener.OnDragStart();
                listener.OnDrag(touch.MoveScreenBy(new Vector2(130, -75)));
                listener.OnDragEnd(); listener.OnPress(false);
                // 额外发送尾随点击，确保拖动结束时也不会误执行创建动作。
                listener.OnClick();
            }
            var moved = panel.localPosition;
            check((moved - original).sqrMagnitude > 100 && OperationManager.history.Count == history &&
                session.Workspace.Snapshot.Groups.Count == groups,
                "single-create native drag moves its entry without creating a group or writing history even with a trailing click");
            session.Tick();
            check((panel.localPosition - moved).sqrMagnitude < .01 && ProjectFileStore.Serialize(session.Editor.CaptureProject()) == nativeBefore,
                "single-create periodic refresh preserves the dragged entry position and original story snapshot");

            var screenBefore = camera.WorldToScreenPoint(button.position);
            try
            {
                tree.position += new Vector3(150, -80, 0);
                tree.localScale = treeScale * .8f;
                session.Tick();
                check((camera.WorldToScreenPoint(button.position) - screenBefore).sqrMagnitude < .01,
                    "single-create entry retains its screen position while the native canvas pans and zooms");
            }
            finally
            {
                tree.position = treePosition; tree.localScale = treeScale; session.Tick();
            }

            // 通过相同事件反向拖回，保持视图控制器中的偏移量与真实位置一致。
            using (var touch = new PointerContext(session, button.gameObject, button.position))
            {
                listener.OnPress(true); listener.OnDragStart(); listener.OnDrag(touch.MoveScreenBy(new Vector2(-130, 75)));
                listener.OnDragEnd(); listener.OnPress(false); listener.OnClick();
            }
            session.Tick();
            check((panel.localPosition - original).sqrMagnitude < .1,
                "single-create reverse drag restores the entry's original viewport position");

            // 新一轮普通点击必须清除前次拖动抑制，不能让按钮拖动后一直失去创建功能。
            using (var touch = new PointerContext(session, button.gameObject, button.position, dragging: false))
            {
                listener.OnPress(true); listener.OnPress(false); listener.OnClick();
            }
            check(session.Workspace.Snapshot.Groups.Count == groups + 1 && OperationManager.history.Count == history + 1,
                "single-create first normal click after dragging still creates exactly one group");
            OperationManager.Undo(); session.SelectedGroup = previousGroup; session.Tick();
            check(OperationManager.history.Count == history && session.Workspace.Snapshot.Groups.Count == groups &&
                Blackboard.selected.ToArray().Select(EditorSession.Id).SequenceEqual(previousNodes.Select(EditorSession.Id)) &&
                ProjectFileStore.Serialize(session.Editor.CaptureProject()) == nativeBefore &&
                AuthoringCompiler.CompileBytes(session.Editor.CaptureProject()).ToArray().SequenceEqual(compiled),
                "single-create gestures restore the native graph, compile bytes, node selection and history boundary");
        }
        finally
        {
            // 断言失败也通过公开回调还原入口偏移，不直接修改控制器的私有手势字段。
            tree.position = treePosition; tree.localScale = treeScale;
            var screen = camera.WorldToScreenPoint(button.position);
            var delta = new Vector2(originalScreen.x - screen.x, originalScreen.y - screen.y);
            if (delta.sqrMagnitude > .01)
            {
                using var touch = new PointerContext(session, button.gameObject, button.position);
                listener.OnPress(true); listener.OnDragStart();
                listener.OnDrag(touch.MoveScreenBy(delta + new Vector2(100, 0)));
                listener.OnDrag(touch.MoveScreenBy(new Vector2(-100, 0)));
                listener.OnDragEnd(); listener.OnPress(false);
            }
            var guard = Math.Max(0, OperationManager.history.Count - history) + 8;
            while (OperationManager.history.Count > history && guard-- > 0) OperationManager.Undo();
            session.Workspace.Restore(snapshot); session.SelectedGroup = previousGroup;
            Blackboard.selected.Clear();
            foreach (var node in previousNodes)
                if (node != null && session.Find(EditorSession.Id(node)) != null) Blackboard.selected.Add(node);
            session.Tick();
        }
    }

}
