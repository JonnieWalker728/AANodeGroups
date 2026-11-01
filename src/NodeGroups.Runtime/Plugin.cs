using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Studio.Scripts;
using UnityEngine;

namespace AzureArchive.NodeGroups.Runtime;

/// <summary>插件入口；Unity/NGUI 操作统一通过 GroupHost 在主线程执行。</summary>
[BepInPlugin(Id, "NodeGroups", Version)]
public sealed class Plugin : BasePlugin
{
    public const string Id = "azurearchive.nodegroups";
    public const string Version = "1.0.2";
    internal static ManualLogSource Logger = null!;

    /// <summary>注册原生历史派生类、安装补丁并创建会话宿主；测试构建须由测试脚本启动。</summary>
    public override void Load()
    {
        Logger = Log;
#if NODEGROUPS_RUNTIME_TESTS
        // 临时测试 DLL 不允许在普通启动中生效，避免误安装后执行集成测试。
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODEGROUPS_TEST_REPORT")))
        {
            Log.LogError("This NodeGroups DLL is a runtime-test build. Use test-runtime.ps1 or install the production release.");
            return;
        }
#endif
        // 提供原生 Operation 的抽象槽位，避免补丁化可能被 IL2CPP 合并的基类方法。
        // 注册必须先于任何分组命令的构造。
        Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<InjectedOperation>();
        new Harmony(Id).PatchAll(typeof(Plugin).Assembly);
        AddComponent<GroupHost>();
        Log.LogInfo($"NodeGroups {Version} loaded; editor graph remains native.");
    }
}
