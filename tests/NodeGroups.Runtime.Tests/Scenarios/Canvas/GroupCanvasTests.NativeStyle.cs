using AzureArchive.Automation;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using NodeGroups.Core;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using Studio.Scripts.OperationManagement;
using System.Text;
using System.Text.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>
/// Verifies reuse of native fonts, materials, sprites, animation and inactive visual templates.
/// Shares fixture helpers with the other canvas scenarios without modifying resource lifetimes.
/// </summary>
internal static partial class GroupCanvasTests
{
    private static void CheckLocaleGroupTitle(EditorSession session, string id, Action<bool, string> check, string phase)
    {
        var prototype = ParcelFactory.instance.nodePrefabs.ToArray().Select(prefab => prefab.GetComponentInChildren<ScriptNode>(true)).First(node => node != null)!;
        var fontType = (prototype.nameLabel ?? prototype.placeholderLabel).GetComponent<FontAdapter>().fontType;
        var manager = LocalizationManager.Instance; var expected = manager.GetFont(manager.CurrentLanguage, fontType);
        var title = GroupRoot(session, id).Find("Header/Name (double click)")!.GetComponent<UILabel>();
        check(SameObject(title.trueTypeFont, expected) && SameObject(title.material, expected.material) &&
            title.trueTypeFont.HasCharacter('组') && Math.Abs(title.color.a - 1) < .001,
            "iteration 2 actual Chinese group title uses native locale font and material in the " + phase);
    }

    /// <summary>Constructs a fresh style with no live ScriptNode, so only the real palette/locale fallback can supply its owned label font.</summary>
    private static void CheckNoScriptFontFallback(EditorSession session, Action<bool, string> check)
    {
        var prototype = ParcelFactory.instance.nodePrefabs.ToArray()
            .Select(prefab => prefab.GetComponentInChildren<ScriptNode>(true)).FirstOrDefault(node => node != null)
            ?? throw new InvalidOperationException("Native palette-font fallback requires its original ScriptNode prototype.");
        var source = prototype.nameLabel ?? prototype.placeholderLabel;
        var adapter = source.GetComponent<FontAdapter>()
            ?? throw new InvalidOperationException("Native palette-font fallback requires the prototype's FontAdapter.");
        var manager = LocalizationManager.Instance
            ?? throw new InvalidOperationException("Native palette-font fallback requires the current localization manager.");
        var expected = manager.GetFont(manager.CurrentLanguage, adapter.fontType)
            ?? throw new InvalidOperationException("The native localization manager did not resolve its palette font.");
        var sourceFont = source.trueTypeFont; var sourceMaterial = source.material;
        var history = OperationManager.history.Count; var story = CanonicalStory(session);
        NativeVisualStyle? style = null;
        var ownRoot = new GameObject("Iteration 2 no-Script locale-font fixture"); ownRoot.SetActive(false);
        try
        {
            style = new NativeVisualStyle(session);
            var label = NativeVisualStyle.Label(ownRoot, style.NodeFont, 500, 120, 0, "节点组");
            Plugin.Logger.LogInfo($"Iteration 2 palette-font fallback: language={manager.CurrentLanguage}; fontType={adapter.fontType}; " +
                $"expectedFont={expected.name}; ownedFont={style.NodeFont.trueTypeFont?.name}; expectedMaterial={expected.material?.name}; " +
                $"ownedMaterial={style.NodeFont.material?.name}; liveScriptCount={session.Board.nodes.ToArray().Count(node => node.TryCast<ScriptNode>() != null)}");
            check(SameObject(style.NodeFont.trueTypeFont, expected) && SameObject(label.trueTypeFont, expected),
                "iteration 2 a fresh style with no live ScriptNode uses the native current-language palette font");
            check(SameObject(style.NodeFont.material, expected.material) && SameObject(label.material, expected.material),
                "iteration 2 no-Script owned labels use the resolved native locale-font material");
            check(label.trueTypeFont.HasCharacter('节') && label.trueTypeFont.HasCharacter('点') && label.trueTypeFont.HasCharacter('组') &&
                Math.Abs(label.color.a - 1) < .001,
                "iteration 2 no-Script group typography supports its Chinese title characters at visible alpha");
            check(SameObject(source.trueTypeFont, sourceFont) && SameObject(source.material, sourceMaterial) &&
                OperationManager.history.Count == history && CanonicalStory(session) == story,
                "iteration 2 palette-font resolution changes neither the original prototype nor native story or history");
        }
        finally { style?.Dispose(); UnityEngine.Object.Destroy(ownRoot); }
    }

    private static void CheckFrozenTemplates(EditorSession session, Action<bool, string> check)
    {
        var root = session.Board.treeRoot.parent.Find("NodeGroups visual templates");
        check(root != null && !root.gameObject.activeInHierarchy && root.GetComponentsInChildren<Node>(true).Length == 0 &&
            root.GetComponentsInChildren<Line>(true).Length == 0 && root.GetComponentsInChildren<Collider>(true).Length == 0 &&
            root.GetComponentsInChildren<Collider2D>(true).Length == 0,
            "iteration 2 frozen visual templates remain inactive and contain no story nodes, graph lines or colliders");
    }

    private static void CheckVisualResources(EditorSession session, string id, ScriptNode script, Action<bool, string> check)
    {
        var root = GroupRoot(session, id);
        var body = root.Find("Header")?.GetComponent<UISprite>();
        check(body != null && SameSprite(body, script.sprite),
            "iteration 2 group body reuses the native ScriptNode atlas, sprite, material and sliced type");
        var title = root.Find("Header/Name (double click)")?.GetComponent<UILabel>();
        var nativeTitle = script.nameLabel;
        check(title != null && nativeTitle != null && SameObject(title.trueTypeFont, nativeTitle.trueTypeFont) &&
            SameObject(title.bitmapFont, nativeTitle.bitmapFont) && title.fontStyle == nativeTitle.fontStyle,
            "iteration 2 group name reuses the native ScriptNode font resources and italic font style");
        check(title != null && nativeTitle != null && title.applyGradient == nativeTitle.applyGradient &&
            title.gradientTop == nativeTitle.gradientTop && title.gradientBottom == nativeTitle.gradientBottom &&
            SameObject(title.material, nativeTitle.material),
            "iteration 2 group title retains the native blue gradient colors and font material");
        check(root.Find("Native selected effect") == null,
            "retained native style regression keeps the removed outer group selection effect absent");
        check(root.Find("Region frame")!.GetComponent<UISprite>().spriteName == script.sprite.spriteName,
            "retained group contour uses the original native body sprite as its only region outline");
        var incoming = root.Find("Incoming port"); var outgoing = root.Find("Outgoing port");
        check(incoming != null && outgoing != null &&
            SameSprite(incoming.GetComponentInChildren<UISprite>(true), script.fromSlot.GetComponentInChildren<UISprite>(true)) &&
            SameSprite(outgoing.GetComponentInChildren<UISprite>(true), script.toSlot.GetComponentInChildren<UISprite>(true)),
            "iteration 2 folded group ports reuse the native green input and pink output sprite resources");
        check(root.GetComponentsInChildren<UIEventListener>(true).All(listener => listener.gameObject.name != "+") &&
            session.Board.treeRoot.Find("Create in group") == null,
            "iteration 2 group has no plus action or in-group creation menu");
        var handles = root.GetComponentsInChildren<UIEventListener>(true)
            .Where(listener => listener.gameObject.name.StartsWith("Resize ", StringComparison.Ordinal)).ToArray();
        check(handles.Length == 5 && handles.All(listener => listener.GetComponent<Collider>() != null &&
            listener.onDragStart != null && listener.onDrag != null && listener.onDragEnd != null),
            "retained group regression has the five current resize handles with native drag callbacks");

        var before = ProjectFileStore.Serialize(session.Editor.CaptureProject());
        session.Toggle(id); session.Tick();
        Blackboard.selected.Clear(); session.CreateGroup(false);
        var foldedSourceCreation = session.SelectedGroup!;
        var foldedSourceFrame = GroupRoot(session, foldedSourceCreation).Find("Region frame")!.GetComponent<UISprite>();
        check(foldedSourceFrame.enabled && foldedSourceFrame.gameObject.activeInHierarchy && foldedSourceFrame.isValid &&
            SameSprite(foldedSourceFrame, script.sprite),
            "retained group creation while a native source is folded keeps its sole native frame enabled and valid");
        OperationManager.Undo(); session.Tick();
        var projectedLines = session.Board.treeRoot.GetComponentsInChildren<Line>(true)
            .Where(line => !line.enabled && line.from == null && line.to == null && line.gameObject.activeInHierarchy).ToArray();
        var linePrototype = session.Editor.linePrefab.GetComponent<Line>();
        check(projectedLines.Length > 0 && projectedLines.All(line =>
            SameSprite(line.lineBody, linePrototype.lineBody) && SameSprite(line.topEndL, linePrototype.topEndL) &&
            SameSprite(line.bottomEndL, linePrototype.bottomEndL) && line.upperHandle != null && line.lowerHandle != null),
            "iteration 2 collapsed projections reuse the native line body, curved ends and endpoint handles");
        check(projectedLines.All(line => line.GetComponentsInChildren<UILabel>(true)
                .All(label => !label.enabled || !label.gameObject.activeInHierarchy) &&
            Math.Abs(line.lineBody.color.r - 1) < .01 && Math.Abs(line.lineBody.color.g - 1) < .01 &&
            Math.Abs(line.lineBody.color.b - 1) < .01),
            "iteration 2 projected native curves remain white and show no native placeholder label");
        check(session.Board.treeRoot.GetComponentsInChildren<UILabel>(true)
                .All(label => !label.gameObject.name.StartsWith("Original branch ", StringComparison.Ordinal)) &&
            session.Board.treeRoot.GetComponentsInChildren<UIWidget>(true)
                .All(widget => widget.gameObject.name != "Branch label background"),
            "iteration 2 collapsed connections contain no extra branch letters or black label backgrounds");
        check(ProjectFileStore.Serialize(session.Editor.CaptureProject()) == before,
            "iteration 2 visual resource reuse and collapse preserve the exact native story snapshot");
        session.Toggle(id); session.Tick();
    }

    private static bool SameObject(Il2CppObjectBase? first, Il2CppObjectBase? second) =>
        first == null ? second == null : second != null && first.Pointer == second.Pointer;

    /// <summary>Hidden native placeholder/name labels supply typography, never visibility, to an owned group label.</summary>
    private static void CheckDormantFontSource(EditorSession session, ScriptNode script, Action<bool, string> check)
    {
        var source = script.nameLabel;
        var originalColor = source.color;
        NativeVisualStyle? style = null;
        var ownRoot = new GameObject("Iteration 2 dormant-font fixture"); ownRoot.SetActive(false);
        var story = ProjectFileStore.Serialize(session.Editor.CaptureProject());
        try
        {
            var transparent = originalColor; transparent.a = 0; source.color = transparent;
            style = new NativeVisualStyle(session);
            var label = NativeVisualStyle.Label(ownRoot, style.NodeFont, 400, 100, 0, "可见组名");
            check(Math.Abs(label.color.a - 1) < .001 && SameObject(label.trueTypeFont, source.trueTypeFont) &&
                label.fontStyle == source.fontStyle && label.applyGradient == source.applyGradient &&
                label.gradientTop == source.gradientTop && label.gradientBottom == source.gradientBottom,
                "iteration 2 a transparent native name-label source supplies font and gradient without hiding an owned group title");
            check(ProjectFileStore.Serialize(session.Editor.CaptureProject()) == story,
                "iteration 2 dormant-font visibility regression preserves the original native story snapshot");
        }
        finally
        {
            source.color = originalColor; style?.Dispose(); UnityEngine.Object.Destroy(ownRoot);
        }
    }

    private static bool SameSprite(UISprite? first, UISprite? second) => first != null && second != null &&
        SameObject(first.atlas, second.atlas) && SameObject(first.material, second.material) &&
        first.spriteName == second.spriteName && first.type == second.type;

    private static bool SameAnimation(UITweener first, UITweener second)
    {
        if (first.GetIl2CppType().FullName != second.GetIl2CppType().FullName ||
            Math.Abs(first.duration - second.duration) > .0001f || Math.Abs(first.delay - second.delay) > .0001f ||
            first.method != second.method || first.style != second.style || first.ignoreTimeScale != second.ignoreTimeScale) return false;
        var firstKeys = first.animationCurve?.keys.ToArray(); var secondKeys = second.animationCurve?.keys.ToArray();
        if (firstKeys == null || secondKeys == null) return firstKeys == null && secondKeys == null;
        if (firstKeys.Length != secondKeys.Length || firstKeys.Zip(secondKeys).Any(pair =>
            pair.First.time != pair.Second.time || pair.First.value != pair.Second.value ||
            pair.First.inTangent != pair.Second.inTangent || pair.First.outTangent != pair.Second.outTangent)) return false;
        var firstScale = first.TryCast<TweenScale>(); var secondScale = second.TryCast<TweenScale>();
        if (firstScale != null && (secondScale == null || firstScale.from != secondScale.from || firstScale.to != secondScale.to)) return false;
        var firstAlpha = first.TryCast<TweenAlpha>(); var secondAlpha = second.TryCast<TweenAlpha>();
        return firstAlpha == null || secondAlpha != null && firstAlpha.from == secondAlpha.from && firstAlpha.to == secondAlpha.to;
    }

}
