using NodeGroups.Core;
using Studio.Scripts;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>
/// 用原生 Line 几何算法绘制的只读连接投影，独占端点 Transform，
/// 不绑定原生图中的节点，也不运行 Line.Update。
/// 交叉检测/删除补丁会将此投影排除在原生编辑操作之外。
/// </summary>
internal sealed class NativeProjectedLine : IDisposable
{
    internal readonly GameObject Root;
    internal readonly Line Line;

    /// <summary>在画布坐标中创建一条投影线；调用方负责在投影失效或会话结束时释放。</summary>
    /// <param name="owner">提供原生预制体、画布父级和颜色的当前会话。</param>
    /// <param name="identity">调试用连线身份；不会注册进原生连接表。</param>
    /// <param name="start">画布局部坐标中的输出端点。</param>
    /// <param name="end">画布局部坐标中的输入端点。</param>
    /// <param name="depth">NGUI 绘制深度，应低于节点卡片。</param>
    internal NativeProjectedLine(EditorSession owner, string identity, Point2 start, Point2 end, int depth)
    {
        Root = Object.Instantiate(owner.Editor.linePrefab, owner.Board.treeRoot);
        Root.name = "Read-only projected connection " + identity;
        Root.transform.localPosition = Vector3.zero;
        Root.transform.localRotation = Quaternion.identity;
        Root.transform.localScale = Vector3.one;
        Line = Root.GetComponent<Line>();
        // Awake 仅初始化颜色；在 Start/Update 访问原生节点图前禁用行为组件。
        Line.enabled = false; Line.from = null; Line.to = null;
        Line._active = false; // 仅禁用逻辑；设置 active 属性会连同可视组件一起隐藏。
        Line.upperHandle = Endpoint("Projected output", start);
        Line.lowerHandle = Endpoint("Projected input", end);
        if (Line.text != null) Line.text.gameObject.SetActive(false); // 原生预制体自带需要隐藏的“A”占位文字。
        foreach (var collider in Root.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        foreach (var collider in Root.GetComponentsInChildren<Collider2D>(true)) collider.enabled = false;
        foreach (var sprite in new[] { Line.topEndL, Line.topEndR, Line.lineBody, Line.bottomEndL, Line.bottomEndR })
        {
            sprite.depth = depth; sprite.color = owner.Editor.normalColor;
        }
        // 这两个原生方法仅使用端点与可视组件，不写入节点连接或撤销历史。
        Line.UpdateWidget(); Line.UpdateLine();
    }

    private Transform Endpoint(string name, Point2 position)
    {
        var endpoint = new GameObject(name) { layer = Root.layer };
        endpoint.transform.SetParent(Root.transform, false);
        endpoint.transform.localPosition = new Vector3((float)position.X, (float)position.Y, 0);
        return endpoint.transform;
    }

    public void Dispose() { Root.SetActive(false); Object.Destroy(Root); }
}
