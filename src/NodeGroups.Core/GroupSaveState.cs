namespace NodeGroups.Core;

/// <summary>以最后成功持久化的组内容为保存点，追踪已提交分组是否仍有未保存修改。</summary>
/// <remarks>
/// 调用方应在编辑器主线程中按提交/保存确认顺序调用，并提供已经过工作区或存储验证的元数据。
/// 此类型冻结组序列；不可变 GroupState 已独立持有成员序列。节点几何、版本号与临时拖拽预览不参与比较。
/// </remarks>
public sealed class GroupSaveState
{
    private GroupState[] savedGroups;
    private GroupState[] committedGroups;
    private bool requiresSave;

    /// <summary>以读取成功的组元数据建立初始保存点和初始已提交状态。</summary>
    /// <param name="savedGroups">已经持久化的组内容；尚无侧车文件时传入空序列。</param>
    /// <param name="requiresSave">恢复或先前保存失败等情况，是否必须再确认一次成功保存。</param>
    /// <exception cref="ArgumentNullException">组序列为 null。</exception>
    /// <exception cref="ArgumentException">组序列含 null 项。</exception>
    public GroupSaveState(IEnumerable<GroupState> savedGroups, bool requiresSave = false)
    {
        this.savedGroups = Freeze(savedGroups, nameof(savedGroups));
        committedGroups = this.savedGroups;
        this.requiresSave = requiresSave;
        Dirty = requiresSave;
    }

    /// <summary>已提交内容与保存点不同，或尚需确认成功保存时为 true；读取此属性为常数时间。</summary>
    public bool Dirty { get; private set; }

    /// <summary>发布一次完成或撤销/重做后的组内容，并重新比较保存点。</summary>
    /// <param name="groups">当前已提交的组元数据；此方法不能用于尚可取消的拖拽或缩放预览。</param>
    /// <remarks>重新回到保存点可清除内容差异，但不能清除 RequireSave 所要求的保存确认。</remarks>
    /// <exception cref="ArgumentNullException">组序列为 null。</exception>
    /// <exception cref="ArgumentException">组序列含 null 项。</exception>
    public void ObserveCommitted(IEnumerable<GroupState> groups)
    {
        // 先完整冻结；调用方的延迟枚举失败不能部分替换当前状态或保存基准。
        var frozen = Freeze(groups, nameof(groups));
        committedGroups = frozen;
        Refresh();
    }

    /// <summary>确认某次实际成功写入的组快照，更新保存点并保留较新的已提交修改。</summary>
    /// <param name="groups">成功保存回执所冻结的组内容，不能用保存完成时的实时组内容代替。</param>
    /// <remarks>
    /// 适配层须先验证文件写入成功、会话归属和保存顺序；此类不判断路径或拒绝过期回执。
    /// 确认旧快照时仍以当前已提交内容比较，因此不会把保存期间的后续修改标为干净。
    /// </remarks>
    /// <exception cref="ArgumentNullException">组序列为 null。</exception>
    /// <exception cref="ArgumentException">组序列含 null 项。</exception>
    public void AcceptSaved(IEnumerable<GroupState> groups)
    {
        var frozen = Freeze(groups, nameof(groups));
        savedGroups = frozen;
        requiresSave = false;
        Refresh();
    }

    /// <summary>保留恢复或保存失败产生的待保存状态，直到 AcceptSaved 确认成功。</summary>
    /// <remarks>重复调用不改变保存点，也不会覆盖当前已提交内容。</remarks>
    public void RequireSave()
    {
        requiresSave = true;
        Dirty = true;
    }

    private void Refresh() => Dirty = requiresSave || !EqualGroups(savedGroups, committedGroups);

    private static GroupState[] Freeze(IEnumerable<GroupState> groups, string parameter)
    {
        ArgumentNullException.ThrowIfNull(groups, parameter);
        var frozen = groups.ToArray();
        foreach (var group in frozen)
            if (group is null) throw new ArgumentException("Group metadata cannot contain null entries.", parameter);
        return frozen;
    }

    private static bool EqualGroups(GroupState[] first, GroupState[] second)
    {
        if (first.Length != second.Length) return false;
        for (var index = 0; index < first.Length; index++)
        {
            var left = first[index]; var right = second[index];
            if (ReferenceEquals(left, right)) continue;
            // 同层组的序列顺序决定命中优先级；成员顺序也属于实际写入的元数据，不能排序后比较。
            if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal) ||
                !string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
                !string.Equals(left.Color, right.Color, StringComparison.Ordinal) ||
                left.Collapsed != right.Collapsed || left.Anchor != right.Anchor || left.ZIndex != right.ZIndex ||
                left.ExpandedBounds != right.ExpandedBounds ||
                !left.MemberNodeIds.SequenceEqual(right.MemberNodeIds, StringComparer.Ordinal)) return false;
        }
        return true;
    }
}
