using Studio.Scripts;
using UnityEngine;
#if NODEGROUPS_RUNTIME_TESTS
using AzureArchive.NodeGroups.Runtime.Tests;
#endif

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>
/// 管理一个编辑器会话。场景切换时先恢复被隐藏的组件，再移除模组界面；
/// 始终保留原生节点 GameObject 的激活状态，让原生逻辑继续运行。
/// </summary>
public sealed class GroupHost : MonoBehaviour
{
    internal static EditorSession? Session;
    private float nextPoll;
#if NODEGROUPS_RUNTIME_TESTS
    private RuntimeTestRunner? tests;
#endif
    private bool faulted;
    private bool started;

    /// <summary>供 IL2CPP 创建 MonoBehaviour 时包装现有原生实例。</summary>
    public GroupHost(IntPtr pointer) : base(pointer) { }

    /// <summary>逐帧处理输入与光标；较重的场景发现、模型同步和保存回执每 0.2 秒执行一次。</summary>
    public void Update()
    {
        if (faulted) return;
        try
        {
            if (!started) { started = true; Plugin.Logger.LogInfo("NodeGroups host Update entered."); }
#if NODEGROUPS_RUNTIME_TESTS
            tests ??= RuntimeTestRunner.FromEnvironment();
            tests?.BeforeEditor();
#endif
            if (Time.unscaledTime >= nextPoll)
            {
                nextPoll = Time.unscaledTime + .2f;
                var editor = StudioCommon.instance;
                var board = Blackboard.Instance;
                if (editor != null && board != null && board.treeRoot != null)
                {
                    if (Session == null || Session.Editor.Pointer != editor.Pointer)
                    {
                        // 原生 Load 跨多个初始化回调注册节点。等待入口和唯一 GUID 就绪，
                        // 避免从短暂为空或含重复注册的列表构造分组模型。
                        var nodes = board.nodes.ToArray().Where(node => node != null && node.root != null).ToArray();
                        if (editor.entryNode == null || nodes.Length == 0 ||
                            nodes.Select(EditorSession.Id).Distinct(StringComparer.Ordinal).Count() != nodes.Length) return;
                        Session?.Dispose();
                        Session = new EditorSession(editor, board);
                    }
                    Session.Tick();
                }
                else if (Session != null) { Session.Dispose(); Session = null; }
            }
            Session?.Cursor.Poll();
            Session?.HandleKeys();
#if NODEGROUPS_RUNTIME_TESTS
            tests?.Tick(Session);
#endif
        }
        catch (Exception error)
        {
            // 故障隔离：清理模组状态并停止宿主更新，让原生编辑器继续工作。
            Plugin.Logger.LogError(error);
#if NODEGROUPS_RUNTIME_TESTS
            tests?.Fail(error);
#endif
            Session?.Dispose(); Session = null; faulted = true;
        }
    }

    /// <summary>在原生更新之后再次隐藏折叠内容，避免原生逻辑重新启用组件导致一帧闪烁。</summary>
    public void LateUpdate()
    {
        try { Session?.EnforceVisibility(); }
        catch (Exception error)
        {
            Plugin.Logger.LogError(error); Session?.Dispose(); Session = null; faulted = true;
#if NODEGROUPS_RUNTIME_TESTS
            tests?.Fail(error);
#endif
        }
    }
}
