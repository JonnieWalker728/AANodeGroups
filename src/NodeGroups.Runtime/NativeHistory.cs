using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using NodeGroups.Core;
using Studio.Scripts.OperationManagement;

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>
/// 将注入的原生操作身份关联到托管分组快照，复用 AzureArchive 的撤销栈。
/// 原生操作保留自己的虚函数表和执行实现；不可补丁化 Operation 的基类方法，
/// 因为 IL2CPP 可能将不同签名的空虚方法折叠到同一地址，导致补丁相互冲突。
/// </summary>
internal static class NativeHistory
{
    private sealed record Command(EditorSession Owner, string Name, WorkspaceSnapshot Before,
        WorkspaceSnapshot After, bool Major, NodeCreateOperation? Creation, bool Native = false);
    private static readonly Dictionary<IntPtr, Command> Commands = new();
    private static readonly HashSet<IntPtr> acceptedGestureOperations = new();
    // 仅在节点组拖动接管的原生移动回调中抑制重复历史，不影响其他原生命令。
    internal static bool SuppressNativeMovement;
    // 撤销/重做重放期间，事件补丁须跳过成员归属与历史写入，避免生成递归命令。
    internal static bool Applying;
    internal static int NativeMovementCallbackDepth;
    private static bool writing;

    /// <summary>在 Unity 主线程写入一次完整手势；可附带原生节点创建以保证一次撤销完成回退。</summary>
    /// <param name="owner">拥有快照的具体编辑器会话；切换场景后旧命令不得应用到新会话。</param>
    /// <param name="name">原生撤销界面显示的操作名称。</param>
    /// <param name="before">手势开始前的分组与节点快照。</param>
    /// <param name="after">手势完成后的分组与节点快照。</param>
    /// <param name="major">是否按原生编辑器的主要变更处理。</param>
    /// <param name="creation">需要一起撤销/重做的原生创建操作；普通分组手势传空。</param>
    internal static void Push(EditorSession owner, string name, WorkspaceSnapshot before,
        WorkspaceSnapshot after, bool major = false, NodeCreateOperation? creation = null)
    {
        var operation = new InjectedOperation();
        Commands.Add(operation.Pointer, new Command(owner, name, before, after, major, creation));
        // WriteIsolated 用于插入历史。部分原生版本可能同时调用 Redo，
        // 此时禁止重放已完成的手势，避免重复创建节点。
        writing = true;
        try { OperationManager.WriteIsolated(operation); }
        finally { writing = false; }
        Prune();
    }

    internal static void Clear() { Commands.Clear(); acceptedGestureOperations.Clear(); }

    /// <summary>标记一次由 Mod 接管移动的节点手势，避免回收操作误链接到手势之前的历史。</summary>
    internal static void BeginNodeGesture() => acceptedGestureOperations.Clear();
    internal static void EndNodeGesture() => acceptedGestureOperations.Clear();

    /// <summary>将分组快照附着到已完成的原生回收历史，不另写历史、不重放原生操作。</summary>
    internal static void AttachNative(EditorSession owner, Operation operation, WorkspaceSnapshot before, WorkspaceSnapshot after)
    {
        Commands[operation.Pointer] = new Command(owner, operation.Name, before, after, false, null, Native: true);
        Prune();
    }

    /// <summary>原生具体回收操作已成功重放后，仅恢复其分组和几何快照。</summary>
    internal static void RestoreNative(Operation operation, bool redo)
    {
        if (Commands.TryGetValue(operation.Pointer, out var command) && command.Native) Execute(operation, redo);
    }

    /// <summary>仅释放已不被原生历史/重做栈及其链表引用的托管命令快照。</summary>
    internal static void Prune()
    {
        // 尚无托管命令时无需遍历整个原生历史；也没有需要释放的快照。
        if (Commands.Count == 0) return;
        var manager = OperationManager.Instance;
        if (manager == null) return;
        var live = new HashSet<IntPtr>();
        void Walk(Operation? operation)
        {
            if (operation == null || !live.Add(operation.Pointer)) return;
            Walk(operation.linkedPrev); Walk(operation.linkedNext);
        }
        for (var i = 0; i < OperationManager.history.Count; i++) Walk(OperationManager.history._array[i]);
        for (var i = 0; i < OperationManager.future.Count; i++) Walk(OperationManager.future._array[i]);
        foreach (var key in Commands.Keys.Where(key => !live.Contains(key)).ToArray()) Commands.Remove(key);
    }

    /// <summary>重放当前会话的托管快照；返回值沿用前置补丁约定，false 表示已接管操作。</summary>
    internal static bool Execute(Operation operation, bool redo)
    {
        if (!Commands.TryGetValue(operation.Pointer, out var command)) return true;
        if (writing) return false;
        if (GroupHost.Session != command.Owner) return false;
        var wasApplying = Applying;
        Applying = true;
        try
        {
            if (redo) command.Creation?.Redo(); else command.Creation?.Undo();
            command.Owner.Restore(redo ? command.After : command.Before, redo ? command.Before : command.After);
        }
        finally { Applying = wasApplying; }
        return false;
    }

    internal static string GetName(IntPtr pointer) => Commands.TryGetValue(pointer, out var command)
        ? command.Name : "节点组操作";
    internal static bool IsMajor(IntPtr pointer) => Commands.TryGetValue(pointer, out var command) && command.Major;
    [HarmonyPatch(typeof(OperationManager), nameof(OperationManager.WriteHistory))]
    private static class WritePatch
    {
        static bool Prefix(Operation __0, ref bool __1)
        {
            if (!SuppressNativeMovement || Applying) return true;
            if (NativeMovementCallbackDepth > 0 &&
                (__0.TryCast<NodeMoveOperation>() != null || __0.TryCast<NodeBatchMoveOperation>() != null)) return false;
            if (NativeMovementCallbackDepth > 0 || __0.TryCast<NodeMoveToTrashOperation>() != null)
            {
                var previous = OperationManager.history.Count == 0 ? null : OperationManager.history.Peek();
                // 被抑制的 Move 没有入栈。原生回收还会先写断连命令；
                // 首条实际命令必须隔离，后续断连和回收保留原生链接。
                if (previous == null || !acceptedGestureOperations.Contains(previous.Pointer))
                { __1 = false; OperationManager.needToLinkWithPrev = false; }
            }
            return true;
        }
        static void Postfix(Operation __0)
        {
            // 空操作或被合并的操作未必入栈，只记录真实接受的身份。
            if (SuppressNativeMovement && !Applying && OperationManager.history.Count > 0 &&
                OperationManager.history.Peek().Pointer == __0.Pointer &&
                (NativeMovementCallbackDepth > 0 || __0.TryCast<NodeMoveToTrashOperation>() != null))
                acceptedGestureOperations.Add(__0.Pointer);
            if (!writing) Prune();
        }
    }
    [HarmonyPatch(typeof(OperationManager), nameof(OperationManager.Clear))]
    private static class ClearPatch { static void Postfix() => Clear(); }
}

/// <summary>
/// 带独立 IL2CPP 虚函数分派的原生历史标记。生成的 Operation 包装器虽然是具体 C# 类，
/// 原生 Name/Undo/Redo 槽位却是抽象的；先注册派生类型，再用实际重写安全提供这些槽位。
/// </summary>
/// <remarks>
/// 构造前必须注册此类型。快照始终由 NativeHistory 托管，
/// AzureArchive 的历史栈只接收此对象身份，不持有托管快照的副本。
/// </remarks>
public sealed class InjectedOperation : Operation
{
    /// <summary>包装由 IL2CPP 域创建或封送过来的实例。</summary>
    /// <param name="pointer">现有 IL2CPP 对象指针。</param>
    public InjectedOperation(IntPtr pointer) : base(pointer) { }

    /// <summary>从托管代码分配已经注册的 IL2CPP 派生实例。</summary>
    public InjectedOperation() : base(ClassInjector.DerivedConstructorPointer<InjectedOperation>()) =>
        ClassInjector.DerivedConstructorBody(this);

    /// <summary>恢复操作前的托管快照，并撤销关联的原生创建操作。</summary>
    public override void Undo() => NativeHistory.Execute(this, false);
    /// <summary>恢复操作后的托管快照，并重做关联的原生创建操作。</summary>
    public override void Redo() => NativeHistory.Execute(this, true);
    /// <summary>显示在原生历史界面中的操作名称。</summary>
    public override string Name => NativeHistory.GetName(Pointer);
    /// <summary>原生编辑器是否应将此手势视为主要变更。</summary>
    public override bool IsMajor => NativeHistory.IsMajor(Pointer);
    /// <summary>仅插入有效操作，因此此历史标记始终不是空操作。</summary>
    public override bool isEmpty => false;
    /// <summary>分组命令在原生历史中可见，且能够独立撤销。</summary>
    public override bool isTransparent => false;
    /// <summary>每次完成的分组手势保持原子性，避免与相邻的原生编辑合并。</summary>
    /// <param name="other">候选的原生或分组命令；此方法不会修改它。</param>
    /// <returns>始终为 false，此命令不合并其他历史项。</returns>
    public override bool TryCombine(Operation other) => false;
}
