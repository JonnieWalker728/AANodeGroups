using System.Security.Cryptography;
using System.Text.Json;

namespace NodeGroups.Core;

/// <summary>在 Unity 主线程捕获的原生连线只读显示输入。</summary>
/// <param name="Id">原生连线标识；一条连线代表多个选项时，还需包含选项标识。</param>
/// <param name="FromNodeId">原生起点 GUID。</param>
/// <param name="ToNodeId">原生终点 GUID。</param>
/// <param name="Start">实际显示的画布端点，包含当前原生接口几何。</param>
/// <param name="End">实际显示的画布端点。</param>
/// <param name="Text">当前原生分支标签。</param>
/// <param name="OptionId">原生选项标识；普通连线为 null。</param>
/// <param name="IsDefault">原生选项是否为起点节点的默认分支。</param>
/// <param name="ConnectionCount">原生连接数量，用于检测原生显示变化。</param>
/// <param name="NativeLineText">原生连线汇总标签；即使各选项有自己的标签也仍需保留。</param>
public sealed record ProjectionEdgePresentation(string Id, string FromNodeId, string ToNodeId, Point2 Start, Point2 End,
    string? Text, string? OptionId, bool IsDefault, int ConnectionCount, string? NativeLineText = null);

/// <summary>生成无歧义的投影缓存键，不访问原生对象或修改图数据。</summary>
public static class ProjectionCacheKey
{
    /// <summary>根据组归属、折叠状态、边界，以及每条独立原生边或选项边的显示几何生成指纹。</summary>
    /// <param name="snapshot">当前组元数据；成员顺序不影响投影连接，因此计算前会规范化顺序。</param>
    /// <param name="groupBounds">与连线在同一帧捕获的画布边界。</param>
    /// <param name="edges">全部原生连接或选项记录；不得按端点或 OptionId 去重。</param>
    public static string Create(WorkspaceSnapshot snapshot, IReadOnlyDictionary<string, Rect2> groupBounds,
        IEnumerable<ProjectionEdgePresentation> edges)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(groupBounds);
        ArgumentNullException.ThrowIfNull(edges);
        // 结构化 JSON 会转义分隔符并使用与区域无关的数值编码，避免简单拼接时不同标识或标签产生相同键。
        // 归属和实时分支变化也必须进入缓存键，否则会复用过期的投影。
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Groups = snapshot.Groups.OrderBy(group => group.Id, StringComparer.Ordinal).Select(group => new
            {
                group.Id,
                group.Collapsed,
                Members = group.MemberNodeIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                Bounds = groupBounds[group.Id]
            }).ToArray(),
            // 保留全部输入边和选项；不能按相同端点分组或去重。
            Edges = edges.ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
