namespace NodeGroups.Core;

/// <summary>纯托管的参考历史实现；运行时可将同一 WorkspaceChange 挂接到原生操作上。</summary>
public sealed class UndoRedoHistory
{
    private readonly Stack<WorkspaceChange> history = new();
    private readonly Stack<WorkspaceChange> future = new();
    /// <summary>记录已经执行的原子命令并清空重做分支；忽略空操作。</summary>
    /// <param name="change">已在工作区执行的命令及其前后快照。</param>
    public void Push(WorkspaceChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!change.HasChanges) return;
        history.Push(change);
        future.Clear();
    }
    /// <summary>恢复一个执行前快照；无可撤销命令时返回 false。</summary>
    /// <param name="workspace">最初生成这些命令的同一个编辑器会话。</param>
    public bool Undo(GroupWorkspace workspace)
    {
        if (!history.TryPeek(out var change)) return false;
        workspace.Restore(change.Before);
        history.Pop();
        future.Push(change);
        return true;
    }
    /// <summary>恢复一个执行后快照；无可重做命令时返回 false。</summary>
    /// <param name="workspace">最初生成这些命令的同一个编辑器会话。</param>
    public bool Redo(GroupWorkspace workspace)
    {
        if (!future.TryPeek(out var change)) return false;
        workspace.Restore(change.After);
        future.Pop();
        history.Push(change);
        return true;
    }
    /// <summary>编辑器会话结束时释放撤销和重做两个分支。</summary>
    public void Clear()
    {
        history.Clear();
        future.Clear();
    }
}
