using System.Text;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using AzureArchive.Automation;
using Il2CppInterop.Runtime;
using Studio.Scripts;
using Studio.Scripts.Nodes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AzureArchive.NodeGroups.Runtime.Tests;

/// <summary>
/// Read-only visual resource inventory for the explicitly opted-in native fixture.
/// It never instantiates a prefab, changes a widget, or touches native graph registration.
/// </summary>
internal static class NativeStyleInspector
{
    /// <summary>Writes native resource names, widget geometry, and selection animation settings beside the test report.</summary>
    internal static void Inspect(EditorSession session, Action<bool, string> check)
    {
        var report = Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT");
        if (string.IsNullOrWhiteSpace(report)) return;
        var before = ProjectFileStore.Serialize(session.Editor.CaptureProject());
        var text = new StringBuilder();
        var script = session.Board.nodes.ToArray().Select(node => node.TryCast<ScriptNode>()).FirstOrDefault(node => node != null)
            ?? throw new InvalidOperationException("The isolated fixture has no ScriptNode.");
        var nodeSprite = script.sprite ?? throw new InvalidOperationException("The fixture ScriptNode has no visual sprite.");
        check(nodeSprite.isValid, "native ScriptNode sprite is available for visual resource reuse");
        text.AppendLine("=== ScriptNode visual hierarchy ===");
        Hierarchy(script.gameObject.transform, text, "");
        text.AppendLine($"Script sprite={nodeSprite.spriteName}; fromSlot={TransformInfo(script.fromSlot)}; toSlot={TransformInfo(script.toSlot)}");
        text.AppendLine($"native colors: normal={session.Editor.normalColor}, highlighted={session.Editor.highlightedColor}, hovered={session.Editor.hoveredColor}");
        text.AppendLine("=== Selection effect ===");
        if (script.selectedEffect != null) Hierarchy(script.selectedEffect.transform, text, "");
        foreach (var tween in script.selectedEffectAnims.ToArray())
        {
            text.AppendLine($"tween {tween.GetIl2CppType().FullName}: {TransformInfo(tween.transform)}; enabled={tween.enabled}; duration={tween.duration}; delay={tween.delay}; method={tween.method}; style={tween.style}; ignoreTimeScale={tween.ignoreTimeScale}");
            if (tween.animationCurve != null)
                foreach (var key in tween.animationCurve.keys) text.AppendLine($"  curve key t={key.time}; value={key.value}; in={key.inTangent}; out={key.outTangent}");
            var scale = tween.TryCast<TweenScale>();
            if (scale != null) text.AppendLine($"  scale from={scale.from}; to={scale.to}");
            var alpha = tween.TryCast<TweenAlpha>();
            if (alpha != null) text.AppendLine($"  alpha from={alpha.from}; to={alpha.to}");
            var color = tween.TryCast<TweenColor>();
            if (color != null) text.AppendLine($"  color from={color.from}; to={color.to}");
        }
        text.AppendLine("=== Line prefab ===");
        NativeLineAddresses(text);
        Hierarchy(session.Editor.linePrefab.transform, text, "");
        var line = session.Editor.linePrefab.GetComponent<Line>();
        check(line != null && line.lineBody != null && line.topEndL != null && line.bottomEndL != null, "native line prefab exposes original body and curved end sprites");
        LineInfo(line!, text);
        text.AppendLine("=== Live original line ===");
        var liveLine = session.Board.treeRoot.GetComponentsInChildren<Line>(true).FirstOrDefault(item => item.from != null && item.to != null);
        if (liveLine != null) { LineInfo(liveLine, text); Hierarchy(liveLine.transform, text, ""); }
        text.AppendLine("=== Native save/compile button panel resources ===");
        if (session.Editor.saveBtn != null) Hierarchy(session.Editor.saveBtn.transform.parent, text, "");
        if (session.Editor.compileBtn != null) Hierarchy(session.Editor.compileBtn.transform, text, "");
        text.AppendLine("=== Scene panel background / header resource inventory ===");
        foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            foreach (var sprite in root.GetComponentsInChildren<UISprite>(true))
            {
                if (sprite.GetComponentInParent<Node>() != null || sprite.GetComponentInParent<Line>() != null) continue;
                var name = sprite.name;
                if (!new[] { "Panel", "Background", "Header", "Title", "Bg", "Button" }.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))) continue;
                text.AppendLine($"{PathOf(sprite.transform)} active={sprite.gameObject.activeInHierarchy}; " + SpriteInfo(sprite));
            }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "native-style-probe.txt"), text.ToString());
        check(before == ProjectFileStore.Serialize(session.Editor.CaptureProject()), "native style inspection preserves the exact original project snapshot");
        Plugin.Logger.LogInfo("Native visual resource inventory written beside integration report.");
    }

    private static void LineInfo(Line line, StringBuilder text)
    {
        text.AppendLine($"Line enabled={line.enabled}; active={line.active}; mode={line.mode}; straight={line.straight}; endDiameter={line.endDiameter}; color={line.color}");
        text.AppendLine($"from={(line.from == null ? "null" : EditorSession.Id(line.from))}; to={(line.to == null ? "null" : EditorSession.Id(line.to))}; upper={TransformInfo(line.upperHandle)}; lower={TransformInfo(line.lowerHandle)}");
        foreach (var sprite in new[] { line.topEndL, line.topEndR, line.lineBody, line.bottomEndL, line.bottomEndR })
            if (sprite != null) text.AppendLine("  Line part: " + SpriteInfo(sprite));
    }

    /// <summary>Records read-only method addresses for offline analysis of the installed native binary.</summary>
    private static void NativeLineAddresses(StringBuilder text)
    {
        var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .FirstOrDefault(item => string.Equals(item.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
        if (module == null) return;
        foreach (var field in typeof(Line).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (field.Name.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal) &&
                new[] { "Awake_", "Start_", "Update_", "UpdateWidget_", "UpdateLine_", "GetPositions_", "OnDestroy_" }.Any(name => field.Name.Contains(name, StringComparison.Ordinal)))
            {
                var info = (IntPtr)field.GetValue(null)!;
                var pointer = Marshal.ReadIntPtr(info);
                text.AppendLine($"native {field.Name} RVA=0x{pointer.ToInt64() - module.BaseAddress.ToInt64():X}");
            }
            else if (field.Name.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal))
                text.AppendLine($"native field {field.Name} offset=0x{IL2CPP.il2cpp_field_get_offset((IntPtr)field.GetValue(null)!):X}");
        }
    }

    private static void Hierarchy(Transform root, StringBuilder text, string indent)
    {
        if (root == null) return;
        text.AppendLine(indent + TransformInfo(root) + $"; activeSelf={root.gameObject.activeSelf}; layer={root.gameObject.layer}");
        foreach (var component in root.GetComponents<Component>())
        {
            if (component == null) continue;
            text.AppendLine(indent + "  component=" + component.GetIl2CppType().FullName);
            var sprite = component.TryCast<UISprite>();
            if (sprite != null) text.AppendLine(indent + "  " + SpriteInfo(sprite));
            var label = component.TryCast<UILabel>();
            if (label != null) text.AppendLine(indent + $"  label text={label.text}; ttf={label.trueTypeFont?.name}; bitmap={label.bitmapFont}; fontSize={label.fontSize}; fontStyle={label.fontStyle}; color={label.color}; dimensions={label.width}x{label.height}; pivot={label.pivot}; align={label.alignment}; effect={label.effectStyle}/{label.effectColor}/{label.effectDistance}; depth={label.depth}");
            if (label != null)
            {
                text.AppendLine(indent + $"  label rendering: gradient={label.applyGradient}; gradientTop={label.gradientTop}; gradientBottom={label.gradientBottom}; mTrueColor={label.mTrueColor}; supportEncoding={label.supportEncoding}; symbolStyle={label.symbolStyle}; keepCrispWhenShrunk={label.keepCrispWhenShrunk}");
                text.AppendLine(indent + "  label material: " + MaterialInfo(label.material));
                if (label.trueTypeFont != null) text.AppendLine(indent + "  trueTypeFont material: " + MaterialInfo(label.trueTypeFont.material));
                var adapter = label.GetComponent<FontAdapter>();
                if (adapter != null) text.AppendLine(indent + $"  FontAdapter: fontType={adapter.fontType}; enabled={adapter.enabled}");
            }
            var texture = component.TryCast<UITexture>();
            if (texture != null) text.AppendLine(indent + $"  texture={texture.mainTexture?.name}; material={texture.material?.name}; dimensions={texture.width}x{texture.height}; color={texture.color}; depth={texture.depth}");
        }
        for (var i = 0; i < root.childCount; i++) Hierarchy(root.GetChild(i), text, indent + "  ");
    }

    private static string TransformInfo(Transform? value) => value == null ? "null" : $"{value.name} pos={value.localPosition} scale={value.localScale} rotation={value.localEulerAngles}";
    private static string MaterialInfo(Material? material) => material == null ? "null" : $"name={material.name}; id={material.GetInstanceID()}; shader={material.shader?.name}; mainTexture={material.mainTexture?.name}; _Color={(material.HasProperty("_Color") ? material.GetColor("_Color").ToString() : "absent")}";
    private static string PathOf(Transform value) => value.parent == null ? value.name : PathOf(value.parent) + "/" + value.name;
    private static string SpriteInfo(UISprite sprite) => $"sprite={sprite.spriteName}; atlas={sprite.atlas}; material={sprite.material?.name}; texture={sprite.mainTexture?.name}; type={sprite.type}; pivot={sprite.pivot}; dimensions={sprite.width}x{sprite.height}; border={sprite.border}; color={sprite.color}; depth={sprite.depth}; enabled={sprite.enabled}";
}
