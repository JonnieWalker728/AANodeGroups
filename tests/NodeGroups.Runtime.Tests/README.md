# Unity 运行时集成测试

本目录测试需要真实 AzureArchive、Unity、NGUI 与 IL2CPP 对象的行为。产品源码位于 [src/NodeGroups.Runtime](../../src/NodeGroups.Runtime)，Core 和 Storage 单元测试分别位于相邻工程。

## 构建与运行

从仓库根目录执行：

```powershell
pwsh -NoProfile -File ./build.ps1 -TestBuild
pwsh -NoProfile -File ./test-runtime.ps1 -TimeoutSeconds 180
pwsh -NoProfile -File ./test-runtime.ps1 -SkipBuild -Focus Palette
```

[NodeGroups.Runtime.Tests.csproj](NodeGroups.Runtime.Tests.csproj) 链接产品源码并定义 `NODEGROUPS_RUNTIME_TESTS`，生成独立的 `bin/Release/net6.0/NodeGroups.dll`。该项目由 Unity 测试运行器执行，不通过 `dotnet test` 启动。测试和产品源码在同一测试程序集编译，测试可访问 `internal` 成员，无需向发行程序集公开测试接口。

发行工程不包含本目录。测试 DLL 不用于正常安装，也不包含在发行包中；测试入口要求子进程设置 `NODEGROUPS_TEST_REPORT`，未设置时不会启用 Mod。报告记录实际加载程序集的包版本（`pluginVersion`）、信息版本（`buildVersion`）和 `testBuild: true` 标记，供脚本核实加载身份。可取得源码标识时，它包含在信息版本中；没有 Git 元数据时，信息版本可能只有包版本。

[test-runtime.ps1](../../test-runtime.ps1) 负责构建边界检查、临时部署、子进程和精确还原。它支持 `-Focus Full|Hover|Visual|Palette|Gestures|Stress|SaveState|QuitConfirm|Trash`、`-TimeoutSeconds` 和 `-SkipBuild`，完整参数与环境要求见 [开发与测试指南](../../DEVELOPMENT.md)。本地报告、日志、截图和备份位于被 Git 忽略的 `TestResults/runtime-<时间>-<随机标识>/`。

## 功能目录与职责

| 位置 | 职责 |
| --- | --- |
| [Runner/RuntimeTestRunner.cs](Runner/RuntimeTestRunner.cs) | 在 Unity 主线程编排流程，创建临时工程，记录断言、报告和截图，失败时清理并退出测试进程。 |
| [Fixtures/NativeProjectFixture.cs](Fixtures/NativeProjectFixture.cs) | 生成包含真实节点类型、分支身份和连接的原生剧情数据。 |
| [Support/NativeStyleInspector.cs](Support/NativeStyleInspector.cs) | 只读检查字体、图集、几何与选中动画，将诊断写入本次报告目录。 |
| [Support/EditorSession.TestSupport.cs](Support/EditorSession.TestSupport.cs) | 通过 `EditorSession` 的 partial 类提供测试专用创建辅助方法。 |
| [Scenarios/NativeCommandIntegrationTests.cs](Scenarios/NativeCommandIntegrationTests.cs) | 原生事件回调、命名、折叠、移动、取消、多选入组和历史联动。 |
| [Scenarios/Canvas/GroupCanvasTests.cs](Scenarios/Canvas/GroupCanvasTests.cs) | 画布交互、层序、重叠拖放、区域调整及层数压力。 |
| [Scenarios/Canvas/GroupCanvasTests.CreateEntry.cs](Scenarios/Canvas/GroupCanvasTests.CreateEntry.cs) | 单创建入口的原生外观、轻微抖动、拖动后点击抑制、视口定位及历史保持。 |
| [Scenarios/Canvas/GroupCanvasTests.NativeStyle.cs](Scenarios/Canvas/GroupCanvasTests.NativeStyle.cs) | 原生资源复用、语言字体和冻结模板。 |
| [Scenarios/Canvas/GroupCanvasTests.SceneLifecycle.cs](Scenarios/Canvas/GroupCanvasTests.SceneLifecycle.cs) | 保存后的场景切换、字体回退、样式源销毁和会话替换。 |
| [Scenarios/Canvas/GroupCanvasTests.Raycast.cs](Scenarios/Canvas/GroupCanvasTests.Raycast.cs) | 自然 Unity 帧后的真实 NGUI 命中，以及借用输入和相机状态的恢复。 |
| [Scenarios/Controls/GroupControlsTests.cs](Scenarios/Controls/GroupControlsTests.cs) | 黄色选中边框、五处缩放、黑色文字光标和直接解散按钮。 |
| [Scenarios/Controls/GroupControlsTests.PointerAndEditing.cs](Scenarios/Controls/GroupControlsTests.PointerAndEditing.cs) | 跨帧文字编辑、稳定标题及移动和缩放光标生命周期。 |
| [Scenarios/Borders/BorderHitTargetTests.cs](Scenarios/Borders/BorderHitTargetTests.cs) | 单层边框、可见边线缩放命中及普通移动指针。 |
| [Scenarios/Drop/DropTargetFeedbackTests.cs](Scenarios/Drop/DropTargetFeedbackTests.cs) | 绿色入组反馈优先级、折叠目标边框及原色恢复。 |
| [Scenarios/Drop/PaletteDragTests.cs](Scenarios/Drop/PaletteDragTests.cs) | 菜单节点注册前预览、原生释放和创建、跨组、取消、销毁及 Undo/Redo。 |
| [Scenarios/Drop/TrashMembershipTests.cs](Scenarios/Drop/TrashMembershipTests.cs) | 独立原生工程中的回收站计数、真实节点入站/Parcel 取回、原组/其他组/组外/折叠/重叠/取消与 Undo/Redo；记录原生身份和历史，不伪造注册结果。 |
| [Scenarios/Persistence/PersistenceLifecycleTests.cs](Scenarios/Persistence/PersistenceLifecycleTests.cs) | 保存、重新打开、损坏文件保护、工程身份、原生恢复确认/取消/关闭、迟到回调与会话隔离。 |
| [Scenarios/Persistence/SaveStateIntegrationTests.cs](Scenarios/Persistence/SaveStateIntegrationTests.cs) | 独立工程中的空组保存按钮、保存点 Undo/Redo、预览保存隔离、跨路径回执、Ctrl+S 分组兜底、保存失败与退出取消/迟到回调。 |
| [Scenarios/Persistence/QuitConfirmationTests.cs](Scenarios/Persistence/QuitConfirmationTests.cs) | 独立专项真实请求关闭、首次阻止、原生确认点击、关闭前批准及父脚本核对实际进程退出；不在 Full 中提前退出。 |
| [Scenarios/Stress/RuntimeStressTests.cs](Scenarios/Stress/RuntimeStressTests.cs) | 独立场景内 100 个真实临时节点、64/260 组的 Tick 与可见性基准、120 次原生命令循环、1024 空组实际界面边界与清理。 |

Canvas 与 Controls 的 partial 文件共享类内状态与恢复工具。除 `EditorSession.TestSupport.cs` 扩展产品类外，测试类使用 `AzureArchive.NodeGroups.Runtime.Tests` 命名空间。

`Stress` 专项与 `Full` 末尾执行同一压力场景。测试先通过原生保存冻结主夹具，随后加载报告目录下的独立工程；压力场景中临时节点通过原生创建操作登记，并通过原生 Undo 清理。返回主场景时核对图、编译、坐标、组元数据及工程/侧车原始字节；场景切换按原生规则重置历史，不假称保留跨场景的 Undo/Redo 栈。

压力性能数据写入本次报告目录的 `stress/runtime-stress.json`。每项先预热 10 次、测量 80 次，记录耗时和当前托管线程分配的 p50/p95；setup、预热与报告序列化不计入样本。64/260 组分别测量全展开 Tick、全展开可见性及成员折叠后的可见性。另记录 1024 空组真实视图创建时间、进程私有内存与一次空闲 Tick，不把单次结果包装成稳定 p95。性能数据仅描述本机与该夹具，原生/GPU 分配不包含在托管线程计数中，不以机器相关毫秒门槛判定功能正确。

准入边界在性能样本落盘之后验证：利用为撤销保留的缺失 GUID 快照，直接检查单组 10000、总数 50000 的新成员拒绝、重复身份去重、组间转移及移出允许；这些中间快照不刷新、不保存，也不创建五万个 Unity 对象。几何纯计算检查覆盖正负 10000000 精确边界、相邻浮点数、NaN/Infinity、非正区域及远端相加溢出。实际 1024 组界面还验证第 1025 组创建被拒绝且不改变选中、剧情和历史。

## 测试接口与状态恢复

同步套件使用 `Run(session, check)`。需要自然 Unity 帧的场景通过 `Begin*` 建立状态、`Tick*` 推进，失败时由 `Abort*` 释放该场景拥有的资源。`check` 来自运行器，失败立即抛出异常并写入报告。

新增场景应通过原生事件和真实生命周期验证行为，记录开始时的剧情、编译结果、历史边界、节点位置、组元数据及借用的界面状态，结束后检查恢复。输入补丁只在测试作用域内安装，失败路径也必须撤销；不要修改产品帧同步、布局或创建逻辑来满足测试。

`Full` 包含保存状态与压力场景。`QuitConfirm` 单独运行：调用真实 `Application.Quit`，未保存组首次阻止关闭；点击原生确认后，由产品再次请求退出。当前 Unity 构建裁剪了 `quitting` 的订阅方法，测试通过可用的关闭前回调记录批准，父脚本还必须确认同一 PID 正常退出、退出码为 0，并匹配实际 DLL 哈希，之后才将 `quit-confirmation.json` 标为最终成功。未退出或仅提前生成报告都不能通过。生成的独立工程恢复文件经身份与路径校验后备份到报告目录并精确移除。

`Trash` 专项和 `Full` 末尾通过正常场景加载独立报告工程，不使用用户工程或回收站。它借用现有真实原生触点夹具，调用节点和 Parcel 的拖动回调；注册/注销、回收站栈和操作历史均由 AzureArchive 原实现完成。`trash-native-trace.log` 记录 GUID、对象身份、父对象、回收站栈、历史与标题，失败也保留时序证据。返回主场景后核对原生图、编译、坐标、分组和工程/侧车原始字节；原生场景切换按原规则清空历史。

`NODEGROUPS_TEST_FOCUS` 专项值为 `hover`、`visual`、`palette`、`gestures`、`stress`、`savestate`、`quitconfirm`、`trash`，由运行脚本设置；完整运行清除子进程中的专项值。Ctrl+S 专项验证 Mod 的仅分组改动兜底，输入补丁只在一次 `HandleKeys` 调用内有效，不冒充操作系统键盘或原生 Workbench 全流程验证。发布前应执行完整测试和独立 `QuitConfirm`，并另外检查实际鼠标输入和不同工程规模。
