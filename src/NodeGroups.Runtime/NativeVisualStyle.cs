using Studio.Scripts;
using Studio.Scripts.Nodes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>
/// 借用已加载的 NGUI 资源，避免复制原生 Node 或其节点图回调。
/// 共享图集、字体和材质归 AzureArchive 所有；本对象仅拥有冻结的视觉模板，
/// 并将原生动画配置复制到独立的选中效果中。
/// </summary>
internal sealed class NativeVisualStyle : IDisposable
{
    internal readonly UISprite Body, Selection, Incoming, Outgoing, Button;
    internal readonly UILabel NodeFont, UiFont;
    private readonly GameObject templates;
    private readonly GameObject selectionTemplate;

    /// <summary>在原生调色板就绪后捕获样式；只能在 Unity 主线程创建和释放。</summary>
    internal NativeVisualStyle(EditorSession session)
    {
        var prototype = ParcelFactory.instance?.nodePrefabs.ToArray()
            .Select(prefab => prefab.GetComponentInChildren<ScriptNode>(true)).FirstOrDefault(node => node != null)
            ?? throw new InvalidOperationException("The native palette has no ScriptNode visual prototype.");
        // 一次性冻结纯视觉组件，避免后续创建组时引用已删除的剧情节点或临时窗口标签。
        // 此未激活的模板父级不持有 Node、Line、碰撞体或节点图回调。
        templates = new GameObject("NodeGroups visual templates") { layer = session.Board.treeRoot.gameObject.layer };
        templates.SetActive(false); templates.transform.SetParent(session.Board.treeRoot.parent, false);
        Body = FreezeSprite("Body", prototype.sprite);
        Incoming = FreezeSprite("Incoming", prototype.fromSlot.GetComponentInChildren<UISprite>(true));
        Outgoing = FreezeSprite("Outgoing", prototype.toSlot.GetComponentInChildren<UISprite>(true));
        var live = session.Board.nodes.ToArray().Select(node => node.TryCast<ScriptNode>()).FirstOrDefault(node => node != null);
        // 已激活节点的字体已适配当前语言；仅保留共享字体/材质和格式数值。
        NodeFont = FreezeLabel("Node font", live?.nameLabel ?? prototype.nameLabel ?? prototype.placeholderLabel);
        // 仅含 Entry/Exit 的新剧情还没有初始化后的 Script 标签，调色板预制体也尚未
        // 执行 FontAdapter。通过原生本地化管理器解析字体类别，只调整模组拥有的副本。
        if (live == null)
        {
            var adapter = (prototype.nameLabel ?? prototype.placeholderLabel).GetComponent<global::FontAdapter>();
            var manager = global::LocalizationManager.Instance;
            var font = adapter != null && manager != null ? manager.GetFont(manager.CurrentLanguage, adapter.fontType) : null;
            if (font != null)
            {
                NodeFont.trueTypeFont = font;
                // UILabel 换字体后可能保留旧材质覆盖，须同步字体及其配套材质。
                NodeFont.material = font.material;
            }
        }
        var sprites = Object.FindObjectsOfType<UISprite>(true).ToArray();
        Button = FreezeSprite("Button", sprites.FirstOrDefault(sprite => sprite.spriteName == "Common_Bg_Raius10px") ?? Body);
        UiFont = FreezeLabel("UI font", Object.FindObjectsOfType<UILabel>(true).ToArray().FirstOrDefault(label =>
            label.trueTypeFont != null && label.fontStyle == FontStyle.Normal && label.fontSize is >= 22 and <= 40) ?? NodeFont);
        selectionTemplate = Object.Instantiate(prototype.selectedEffect, templates.transform);
        selectionTemplate.name = "Selection template";
        Selection = selectionTemplate.GetComponent<UISprite>();
        PrepareSelection(selectionTemplate);
    }

    private GameObject Template(string name)
    {
        var target = new GameObject(name) { layer = templates.layer };
        target.transform.SetParent(templates.transform, false); return target;
    }
    private UISprite FreezeSprite(string name, UISprite source)
    {
        var sprite = Sprite(Template(name), source, source.width, source.height, 0);
        sprite.pivot = source.pivot; sprite.transform.localPosition = source.transform.localPosition;
        sprite.transform.localScale = source.transform.localScale; return sprite;
    }
    private UILabel FreezeLabel(string name, UILabel source) => Label(Template(name), source, source.width, source.height, 0, "");

    /// <summary>将原生精灵样式复制到目标对象；图集仍共享，尺寸和深度由调用方控制。</summary>
    internal static UISprite Sprite(GameObject target, UISprite source, int width, int height, int depth)
    {
        var sprite = target.AddComponent<UISprite>();
        sprite.atlas = source.atlas; sprite.spriteName = source.spriteName;
        sprite.type = source.type; sprite.flip = source.flip;
        sprite.fillDirection = source.fillDirection; sprite.fillAmount = source.fillAmount;
        sprite.invert = source.invert; sprite.fillCenter = source.fillCenter;
        sprite.applyGradient = source.applyGradient;
        sprite.gradientTop = source.gradientTop; sprite.gradientBottom = source.gradientBottom;
        sprite.color = source.color; sprite.pivot = UIWidget.Pivot.TopLeft;
        sprite.SetDimensions(width, height); sprite.depth = depth;
        return sprite;
    }

    /// <summary>复制原生文字样式并设为左上锚点；强制可见度，避免继承源占位标签的隐藏状态。</summary>
    internal static UILabel Label(GameObject target, UILabel source, int width, int height, int depth, string text)
    {
        var label = target.AddComponent<UILabel>();
        label.trueTypeFont = source.trueTypeFont; label.bitmapFont = source.bitmapFont;
        label.material = source.material;
        // 原生标签依赖顶点渐变绘制蓝色文字，color 本身为白色；只复制字体和材质会丢失蓝色。
        label.applyGradient = source.applyGradient;
        label.gradientTop = source.gradientTop; label.gradientBottom = source.gradientBottom;
        label.fontSize = source.fontSize; label.fontStyle = source.fontStyle;
        label.effectStyle = source.effectStyle; label.effectColor = source.effectColor;
        label.effectDistance = source.effectDistance;
        // 原生节点用 alpha 切换名称/占位文字，不能让这一状态隐藏新组标题，
        // 即使样式来源是尚未命名的节点也应正常显示。
        label.color = new Color(source.color.r, source.color.g, source.color.b, 1);
        label.pivot = UIWidget.Pivot.TopLeft; label.alignment = NGUIText.Alignment.Left;
        label.overflowMethod = UILabel.Overflow.ShrinkContent;
        label.SetDimensions(width, height); label.depth = depth; label.text = text;
        return label;
    }

    /// <summary>创建独立选中效果；禁用交互与原节点回调，由调用方控制动画和销毁时机。</summary>
    internal GameObject SelectionEffect(Transform parent)
    {
        // 只克隆视觉对象，不复制 ScriptNode 或其连接状态。
        var effect = Object.Instantiate(selectionTemplate, parent);
        effect.name = "Native selected effect"; effect.SetActive(true);
        PrepareSelection(effect);
        return effect;
    }

    private static void PrepareSelection(GameObject effect)
    {
        foreach (var widget in effect.GetComponentsInChildren<UIWidget>(true))
        { widget.enabled = true; }
        foreach (var collider in effect.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        foreach (var collider in effect.GetComponentsInChildren<Collider2D>(true)) collider.enabled = false;
        foreach (var tween in effect.GetComponentsInChildren<UITweener>(true))
        {
            tween.enabled = false;
            tween.onFinished?.Clear(); // 视觉副本不能保留源节点的完成回调。
            tween.ResetToBeginning();
        }
    }

    public void Dispose() { templates.SetActive(false); Object.Destroy(templates); }
}
