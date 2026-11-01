using System.Collections.ObjectModel;

namespace NodeGroups.Core;

/// <summary>仅在运行期使用的原生节点标识和几何，不包含剧情文本或连接。</summary>
/// <param name="Id">以稳定字符串表示的原生节点 GUID。</param>
/// <param name="Position">treeRoot 局部坐标中的节点标题命中点；适配层须一致地转换原生节点根坐标。</param>
/// <param name="Size">节点显示宽高；Bounds 以 Position 为中心。</param>
public sealed record NodeState(string Id, Point2 Position, Point2 Size)
{
    /// <summary>布局用几何边界；拖放目标判断使用 Position。</summary>
    public Rect2 Bounds => Rect2.FromCenter(Position, Size);
}

/// <summary>不可变的组元数据。成员保留原生 GUID，包括暂时不存在但可通过原生撤销恢复的节点。</summary>
public sealed record GroupState
{
    /// <summary>创建元数据并复制成员标识，防止外部集合修改影响快照。</summary>
    /// <param name="id">独立组标识，与原生节点标识及编译用 groupId 无关。</param>
    /// <param name="name">显示名称；工作区命令去除首尾空白并限制长度为 1 到 256 个字符。</param>
    /// <param name="memberNodeIds">原生节点 GUID；原生撤销历史失效前允许保留暂时缺失的节点标识。</param>
    /// <param name="collapsed">初始折叠卡片状态。</param>
    /// <param name="anchor">折叠卡片或空展开组的画布位置。</param>
    /// <param name="color">显示颜色；持久化输入校验由存储适配层负责。</param>
    /// <param name="zIndex">值越大，显示层级越靠上。</param>
    /// <param name="expandedBounds">用户设置的展开区域，使用画布坐标；null 表示由旧版适配层自动计算，直到首次明确调整尺寸。</param>
    /// <remarks>此值对象仅复制成员；数据校验交由 WorkspaceSnapshot 与持久化边界完成。</remarks>
    public GroupState(string id, string name, IEnumerable<string> memberNodeIds, bool collapsed, Point2 anchor,
        string color = "#4C88B8", int zIndex = 0, Rect2? expandedBounds = null)
    {
        Id = id;
        Name = name;
        MemberNodeIds = Array.AsReadOnly(memberNodeIds.ToArray());
        Collapsed = collapsed;
        Anchor = anchor;
        Color = color;
        ZIndex = zIndex;
        ExpandedBounds = expandedBounds;
    }

    /// <summary>独立组标识，不是应用编译时使用的 groupId。</summary>
    public string Id { get; }
    /// <summary>用户可编辑的显示名称。</summary>
    public string Name { get; }
    /// <summary>稳定的原生节点标识，只读集合不可修改。</summary>
    public IReadOnlyList<string> MemberNodeIds { get; }
    /// <summary>是否以紧凑卡片代替成员显示。</summary>
    public bool Collapsed { get; }
    /// <summary>画布卡片位置及布局回退锚点。</summary>
    public Point2 Anchor { get; }
    /// <summary>组显示颜色，约定使用 HTML 十六进制颜色字符串。</summary>
    public string Color { get; }
    /// <summary>显示层序，值越大越靠上。</summary>
    public int ZIndex { get; }

    /// <summary>独立的展开边框几何；修改区域不会根据覆盖的节点推断归属。</summary>
    /// <remarks>null 表示旧元数据使用自动边界；显式区域须有限且宽高严格为正。</remarks>
    public Rect2? ExpandedBounds { get; }

    /// <summary>复制元数据并替换指定字段；未显式传入时保留固定区域和层号。</summary>
    /// <remarks>替换值为 null 时保留原值；此辅助方法不能将显式区域重置为自动边界。</remarks>
    internal GroupState Replace(string? name = null, IEnumerable<string>? members = null, bool? collapsed = null,
        Point2? anchor = null, int? zIndex = null, Rect2? expandedBounds = null) =>
        new(Id, name ?? Name, members ?? MemberNodeIds, collapsed ?? Collapsed, anchor ?? Anchor, Color,
            zIndex ?? ZIndex, expandedBounds ?? ExpandedBounds);
}

/// <summary>不可变的完整编辑器视图状态，用于单个命令执行前后的快照。</summary>
public sealed class WorkspaceSnapshot
{
    private readonly ReadOnlyDictionary<string, NodeState> nodeIndex;
    private readonly ReadOnlyDictionary<string, GroupState> groupIndex;
    private readonly ReadOnlyDictionary<string, GroupState> membershipIndex;

    /// <summary>复制输入集合，并拒绝重复标识或交叉成员归属。</summary>
    /// <param name="nodes">当前原生节点几何，不含剧情内容或连接。</param>
    /// <param name="groups">独立组元数据；允许保留缺失的原生成员标识以支持撤销。</param>
    /// <param name="revision">在同一编辑器会话内单调递增的版本号。</param>
    /// <exception cref="ArgumentException">标识、名称、几何或成员独占归属约束无效。</exception>
    public WorkspaceSnapshot(IEnumerable<NodeState> nodes, IEnumerable<GroupState> groups, long revision = 0)
    {
        var nodeArray = nodes.ToArray();
        var groupArray = groups.ToArray();
        var nodeMap = new Dictionary<string, NodeState>(StringComparer.Ordinal);
        var groupMap = new Dictionary<string, GroupState>(StringComparer.Ordinal);
        var memberMap = new Dictionary<string, GroupState>(StringComparer.Ordinal);
        foreach (var node in nodeArray)
        {
            Guard.Id(node.Id, nameof(nodes));
            Guard.Point(node.Position, nameof(nodes));
            Guard.Size(node.Size, nameof(nodes));
            // 坐标和尺寸分别有限仍可能使右/上边界溢出；完整几何必须可安全显示。
            if (!node.Bounds.IsValid) throw new ArgumentException("Node bounds must be finite.", nameof(nodes));
            if (!nodeMap.TryAdd(node.Id, node)) throw new ArgumentException("Duplicate node identity.", nameof(nodes));
        }
        foreach (var group in groupArray)
        {
            Guard.Id(group.Id, nameof(groups));
            Guard.Name(group.Name, nameof(groups), normalize: false);
            Guard.Point(group.Anchor, nameof(groups));
            if (group.ExpandedBounds is { } bounds) Guard.ExpandedBounds(bounds, nameof(groups));
            if (!groupMap.TryAdd(group.Id, group)) throw new ArgumentException("Duplicate group identity.", nameof(groups));
            foreach (var member in group.MemberNodeIds)
            {
                Guard.Id(member, nameof(groups));
                if (!memberMap.TryAdd(member, group)) throw new ArgumentException("A node may belong to only one group.", nameof(groups));
            }
        }
        Nodes = Array.AsReadOnly(nodeArray);
        Groups = Array.AsReadOnly(groupArray);
        Revision = revision;
        nodeIndex = new ReadOnlyDictionary<string, NodeState>(nodeMap);
        groupIndex = new ReadOnlyDictionary<string, GroupState>(groupMap);
        membershipIndex = new ReadOnlyDictionary<string, GroupState>(memberMap);
    }

    /// <summary>运行期节点几何。原生坐标由原工程保存，不能写入组 JSON。</summary>
    public IReadOnlyList<NodeState> Nodes { get; }
    /// <summary>不可变的独立组元数据。</summary>
    public IReadOnlyList<GroupState> Groups { get; }
    /// <summary>单调递增的会话版本号，用于拒绝过期的拖拽提交。</summary>
    public long Revision { get; }
    /// <summary>获取已有原生节点；不存在时抛出 KeyNotFoundException。</summary>
    /// <param name="id">原生节点 GUID。</param>
    public NodeState GetNode(string id) => nodeIndex[id];
    /// <summary>获取已有组；不存在时抛出 KeyNotFoundException。</summary>
    /// <param name="id">独立组标识。</param>
    public GroupState GetGroup(string id) => groupIndex[id];
    /// <summary>统计本快照中仍存在运行期节点几何的组成员，不计入仅为撤销保留的缺失标识。</summary>
    /// <param name="groupId">已存在的独立组标识。</param>
    /// <returns>成员标识与当前 Nodes 标识的交集数量；空组或全部成员缺失时为 0。</returns>
    /// <remarks>
    /// 原生适配层应以画布注册表提供 Nodes；折叠隐藏不等于注销，节点位置也不用于推断归属。
    /// 此只读查询不删除保留的成员，不修改快照或版本号；利用节点索引，复杂度为 O(组成员数)。
    /// </remarks>
    /// <exception cref="KeyNotFoundException">groupId 不属于当前快照。</exception>
    public int CountPresentMembers(string groupId)
    {
        var group = GetGroup(groupId);
        var count = 0;
        foreach (var memberId in group.MemberNodeIds)
            if (nodeIndex.ContainsKey(memberId)) count++;
        return count;
    }
    /// <summary>返回当前归属，支持查询暂时不存在的原生节点。</summary>
    /// <param name="nodeId">原生节点 GUID。</param>
    /// <returns>唯一的所属组；节点不属于任何组时返回 null。</returns>
    public GroupState? FindGroupForNode(string nodeId) => membershipIndex.TryGetValue(nodeId, out var group) ? group : null;
}

/// <summary>一次原子视图命令；与原生操作的整合由运行时历史适配层负责。</summary>
/// <param name="Name">供用户阅读的历史记录名称。</param>
/// <param name="Before">命令执行前的状态。</param>
/// <param name="After">命令执行后的状态。</param>
public sealed record WorkspaceChange(string Name, WorkspaceSnapshot Before, WorkspaceSnapshot After)
{
    /// <summary>命令生成新快照时为 true；空操作复用 Before。</summary>
    public bool HasChanges => !ReferenceEquals(Before, After);
}

internal static class Guard
{
    internal static void Id(string id, string parameter)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Identity cannot be empty.", parameter);
    }
    internal static string Name(string name, string parameter, bool normalize = true)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name must contain 1 to 256 characters.", parameter);
        // 命令先去首尾空白；快照校验原值，不能让被忽略的规范化结果掩盖无法持久化的名称。
        var value = normalize ? name.Trim() : name;
        if (value.Length > 256) throw new ArgumentException("Name must contain 1 to 256 characters.", parameter);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character)) throw new ArgumentException("Name cannot contain control characters.", parameter);
            if (!char.IsSurrogate(character)) continue;
            // 合法补充平面字符（如 emoji）占一个高/低代理对，必须完整保留。
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            { index++; continue; }
            throw new ArgumentException("Name must contain valid UTF-16 characters.", parameter);
        }
        return value;
    }
    internal static void Point(Point2 point, string parameter)
    {
        if (!point.IsFinite) throw new ArgumentException("Canvas coordinates must be finite.", parameter);
    }
    internal static void Size(Point2 size, string parameter)
    {
        Point(size, parameter);
        if (size.X < 0 || size.Y < 0) throw new ArgumentException("Node dimensions cannot be negative.", parameter);
    }
    internal static void ExpandedBounds(Rect2 bounds, string parameter)
    {
        // 远端边界同样需要校验：各坐标分别有限，相加后仍可能溢出。
        if (!bounds.IsValid || bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentException("Expanded group regions must be finite and have positive dimensions.", parameter);
    }
}
