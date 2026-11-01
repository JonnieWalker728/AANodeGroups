# AANodeGroups 开发与测试指南

本文面向维护产品源码、编写测试和构建发行包的开发人员。功能和使用方法见 [README](README.md)。项目发布的 Mod 名称仍为 NodeGroups。

## 环境准备

| 项目 | 要求 |
| --- | --- |
| 平台 | Windows 版 AzureArchive 1.0.1，BepInEx 6 IL2CPP 环境。 |
| 构建工具 | Windows PowerShell 5.1 或 PowerShell 7；推荐使用 .NET SDK 10，`dotnet` 可在 PATH 中调用。 |
| 测试脚本宿主 | Unity 集成、PE 构建边界及部署保护检查使用 PowerShell 7；构建脚本兼容性检查支持 5.1 和 7。 |
| 目标框架 | `net6.0`，C# 10。 |
| 托管测试 | 需要可运行 `net6.0` 的 .NET 6 运行时；SDK 10 本身不能替代该运行时。 |
| 原生引用 | 本地应用的 `BepInEx/core` 与 `BepInEx/interop`。 |
| 包还原 | [NuGet.Config](NuGet.Config) 配置的公开包源，或已有本地缓存。 |

项目未通过 global.json 固定 SDK 版本，本文命令按 .NET SDK 10 说明。原应用、Unity/BepInEx 二进制和互操作程序集由开发人员自行准备，不作为项目文件分发。纯 Core/Storage 单元测试不启动应用，也不依赖 Unity 对象。

构建应使用当前应用生成的互操作程序集；升级应用后，不要用旧版本的 `BepInEx/interop` 覆盖新目录。当前 1.0.1 环境的目标框架仍为 `net6.0`，无需因应用版本变化而升级 Mod 的目标框架。

当前脚本固定使用项目根目录的上两级目录定位应用，目录应为：

```text
AzureArchive/
├─ AzureArchive.exe
├─ ActiveProfile.txt
├─ BepInEx/
│  ├─ core/
│  └─ interop/
├─ profiles/
├─ mods/
└─ ModDev/
   └─ AANodeGroups/
```

[Directory.Build.props](Directory.Build.props) 的 `GameDir` 用于 MSBuild 引用路径；构建安装和运行时测试脚本仍按上述位置定位应用。仅覆盖 MSBuild 的 `GameDir` 不会改变脚本的安装或进程启动目录。

## 项目结构与职责

打开 [NodeGroups.slnx](NodeGroups.slnx) 查看产品和测试工程。

| 位置 | 职责与边界 |
| --- | --- |
| [src/NodeGroups.Core](src/NodeGroups.Core) | 纯托管模型、组成员、几何、层序、拖放规则及历史快照；不访问 Unity 或文件系统。 |
| [src/NodeGroups.Storage](src/NodeGroups.Storage) | 侧车格式、加载保存、工程身份校验、损坏文件保护及恢复存储；不改写原剧情文件。 |
| [src/NodeGroups.Runtime](src/NodeGroups.Runtime) | Unity/NGUI 界面、原生节点适配、事件补丁、保存回执及 Undo/Redo。 |
| [tests/NodeGroups.Core.Tests](tests/NodeGroups.Core.Tests) | 分组、几何、投影、拖放和历史规则的单元测试。 |
| [tests/NodeGroups.Storage.Tests](tests/NodeGroups.Storage.Tests) | 序列化、输入校验、文件读写及保护行为的单元测试。 |
| [tests/NodeGroups.Runtime.Tests](tests/NodeGroups.Runtime.Tests/README.md) | 真实 Unity/IL2CPP 场景、原生事件、界面和生命周期测试。 |
| [tests/build](tests/build/README.md) | 编译 DLL 的隔离检查及部署失败恢复检查。 |
| [build](build/README.md) | 共享产品源码和原生引用配置、部署独占锁。 |

运行时入口的分工：

- [Plugin.cs](src/NodeGroups.Runtime/Plugin.cs)：插件加载、原生操作注册和 Harmony 补丁安装。
- [GroupHost.cs](src/NodeGroups.Runtime/GroupHost.cs)：Unity 主线程轮询及编辑器会话生命周期。
- [EditorSession.cs](src/NodeGroups.Runtime/EditorSession.cs)：组操作、节点适配、显示、保存和历史协调。
- [GroupUi.cs](src/NodeGroups.Runtime/GroupUi.cs)：可拖动创建入口、组卡片、边框和编辑控件。
- [RecoveryPrompt.cs](src/NodeGroups.Runtime/RecoveryPrompt.cs)：会话拥有的原生恢复确认窗口及迟到回调隔离。
- [EditorPatches.cs](src/NodeGroups.Runtime/EditorPatches.cs)：观察原生事件并交给对应会话。
- [TrashPatches.cs](src/NodeGroups.Runtime/TrashPatches.cs)：观察原生回收完成与具体回收操作的 Undo/Redo，更新有效计数及分组快照。
- [NativeHistory.cs](src/NodeGroups.Runtime/NativeHistory.cs)：原生 Undo/Redo 桥接，将分组快照附着到原生回收与取回操作。

代码注释使用中文，保留原生 API、类型和环境变量的原名以便检索。公开接口使用 XML 注释说明参数、返回值和重要约束；实现注释解释规则或原因，避免逐句复述代码。修改时尤其需要维护以下边界：

- 拖拽起始快照和命中区域在手势内保持不变，重叠区域按最高层优先、同层后项优先判断。
- 只缓存由当前会话创建和持有的界面组件；原生节点和窗口可能随编辑操作销毁，不应跨生命周期缓存。
- 保存回执的会话实例、顺序号、剧情哈希及原生文件版本共同决定是否有效，不能仅用路径或工程 ID 判断。
- 纯性能重构先用回归测试固定现有语义，再运行同一测试确认结果；无需为重构制造功能性失败。

## 发行构建与测试构建

在项目根目录使用 Windows PowerShell 5.1：

```powershell
# 默认发行构建，不安装
powershell.exe -NoProfile -File .\build.ps1

# 独立运行时测试构建，不安装
powershell.exe -NoProfile -File .\build.ps1 -TestBuild
```

或使用 PowerShell 7 ，使用同一入口：

```powershell
# 默认发行构建，不安装
pwsh -NoProfile -File ./build.ps1

# 独立运行时测试构建，不安装
pwsh -NoProfile -File ./build.ps1 -TestBuild
```

| 构建 | 工程 | 输出 |
| --- | --- | --- |
| 发行版 | [NodeGroups.Runtime.csproj](src/NodeGroups.Runtime/NodeGroups.Runtime.csproj) | `src/NodeGroups.Runtime/bin/Release/net6.0/NodeGroups.dll` |
| 测试版 | [NodeGroups.Runtime.Tests.csproj](tests/NodeGroups.Runtime.Tests/NodeGroups.Runtime.Tests.csproj) | `tests/NodeGroups.Runtime.Tests/bin/Release/net6.0/NodeGroups.dll` |

发行项目编入 Core/Storage 产品源码，部署只需一个 NodeGroups.dll。测试项目链接同一产品源码，编入自己的运行器、场景和夹具；两者输出目录独立，程序集名称相同。

测试项目独有 `NODEGROUPS_RUNTIME_TESTS` 编译符号及测试入口。测试专用 `EditorSession.CreateMember` 位于 [EditorSession.TestSupport.cs](tests/NodeGroups.Runtime.Tests/Support/EditorSession.TestSupport.cs)，通过 partial 类访问内部接口，发行版不包含该方法。图像编码引用也只属于测试项目。

程序集元数据 `NodeGroups.BuildFlavor` 分别为 `Release` 与 `RuntimeTests`。测试 DLL 缺少显式报告环境变量时不会启用 Mod，且不能作为发行包使用。

关闭应用后，可安装并启用发行版：

```powershell
powershell.exe -NoProfile -File .\build.ps1 -Install -Enable
```

根目录的 [icon.png](icon.png) 会自动复制到发行构建输出与 publish 输出，测试工程不复制该资产。`-Install` 将发行 DLL、manifest 和图标放入对应版本的 `mods/NodeGroups/` 目录。`-Enable` 更新当前 Profile 的 NodeGroups 条目，保留其他 Mod。`-Enable` 必须同时使用 `-Install`；`-TestBuild` 不能与这两个参数组合。

上述构建与安装参数在 5.1 和 7 中保持一致，使用 `pwsh` 也可安装。供 5.1 执行的脚本保存为带 BOM 的 UTF-8，避免中文注释和帮助文本被按 ANSI 误读；清单、Profile 名称和 JSON 配置显式按 UTF-8 读取，Profile 仍写为无 BOM 的 UTF-8。构建目标继续为 `net6.0`，PowerShell 的版本不改变 Mod 的目标框架。

## 测试入口

以下单元与 Unity 测试示例使用 PowerShell 7。仅构建发行 DLL 或测试 DLL 可以使用上述 Windows PowerShell 5.1 命令。

### 托管单元测试

```powershell
pwsh -NoProfile -File ./test.ps1
```

脚本先还原两个测试工程，再串行执行 Core/Storage 的 Release 测试。没有额外参数；不启动应用或安装 Mod，临时 TRX 位于 `TestResults/core` 和 `TestResults/storage`。

### Unity 集成测试

```powershell
# 默认重新构建两版 DLL 并运行全部场景
pwsh -NoProfile -File ./test-runtime.ps1 -TimeoutSeconds 180

# 选择已有专项
pwsh -NoProfile -File ./test-runtime.ps1 -Focus Palette

# 在独立工程中验证压力、持久化容量和资源清理
pwsh -NoProfile -File ./test-runtime.ps1 -Focus Stress -TimeoutSeconds 360

# 分组保存点、原生保存按钮、保存失败和离开提醒
pwsh -NoProfile -File ./test-runtime.ps1 -Focus SaveState -TimeoutSeconds 300

# 原生回收站暂存、有效成员计数、取回归属与 Undo/Redo
pwsh -NoProfile -File ./test-runtime.ps1 -Focus Trash -TimeoutSeconds 360

# 独立进程真实验证“仍然退出”；不属于 Full，以免提前终止其他测试
pwsh -NoProfile -File ./test-runtime.ps1 -Focus QuitConfirm -TimeoutSeconds 180

# 复用已有两版输出
pwsh -NoProfile -File ./test-runtime.ps1 -SkipBuild -Focus Full
```

| 参数 | 说明 |
| --- | --- |
| `-TimeoutSeconds` | 1–3600 秒，默认 180；超时进入进程关闭与部署恢复。 |
| `-Focus` | 默认 `Full`（包括压力、保存状态和回收站场景）；可选 `Hover`、`Visual`、`Palette`、`Gestures`、`Stress`、`SaveState`、`Trash`、`QuitConfirm`。 |
| `-SkipBuild` | 跳过构建，仍检查 DLL 隔离、版本和实际加载身份；源码变更后应先重建两版。 |

脚本使用独占部署锁，备份原 DLL、manifest 和当前 Profile 的原始字节及存在状态，临时安装测试 DLL，再启动自己的隐藏进程。测试环境变量只传给该进程，父进程环境会还原。

运行成功、断言失败或超时，均关闭本次进程并恢复原安装。恢复失败会明确报错并保留恢复副本。脚本不关闭其他应用实例。不要在测试期间手动替换安装文件或编辑当前 Profile。

每次运行使用独立的 `TestResults/runtime-<时间>-<随机标识>/` 目录，包含报告、临时工程、Player 日志、截图、安装备份和 `deployment-restore.json`。检查 `report.json` 的 `success`、`error`、`testBuild` 及实际版本，并核对恢复报告；公共 BepInEx 日志可能混合多个实例，不能代替本次报告。

运行时测试由 Unity 主线程运行器执行，不通过 `dotnet test` 执行。同步套件使用 `Run(session, check)`；跨帧场景通过 `Begin*`、`Tick*`、`Abort*` 管理状态和清理。完整功能目录与接口约定见 [运行时测试说明](tests/NodeGroups.Runtime.Tests/README.md)。

Core 的 `ReleaseBoundaryStressTests` 使用固定种子验证 3000 次操作及分批 Undo/Redo，并覆盖 50000 节点、1000 组和独立选项连线；`GroupNameBoundaryTests` 验证名称长度、控制字符和完整 Unicode。Storage 的同名压力套件覆盖 1024 组、50000 成员、每组 10000 成员、8 MiB 文件及各上限加一的拒绝、原子保存故障和非法 UTF-16。超限或损坏输入必须保留已有文件。

运行时压力套件使用 104 个真实节点，分别在 64/260 组下预热 10 次、测量 80 次，记录主线程耗时与托管分配 p50/p95，并验证 120 次命令循环和 1024 空组实际界面。结果在 `stress/runtime-stress.json`，不代表完整渲染帧率或原生/GPU 内存。新增性能优化应在同一宿主、相同输入和配置下配对测量，排除构造、预热和报告写入；正确性不依赖机器相关的毫秒阈值。

### 构建边界与部署保护

验证构建脚本在当前 PowerShell 宿主中的参数、编码、安装与失败清理契约：

```powershell
powershell.exe -NoProfile -File .\tests\build\Verify-BuildCompatibility.ps1 -ReportPath .\TestResults\build-compatibility-51.json
pwsh -NoProfile -File ./tests/build/Verify-BuildCompatibility.ps1 -ReportPath ./TestResults/build-compatibility-7.json
```

该检查只在 `TestResults` 的独立中文路径夹具中运行，模拟 SDK 命令，不安装到真实应用或启动游戏。真实编译仍使用 `build.ps1`。其余 PE 元数据和 Unity 部署检查继续使用 PowerShell 7。

先构建两版 DLL，再运行：

```powershell
pwsh -NoProfile -File ./tests/build/Verify-BuildSeparation.ps1 -ReleaseDll ./src/NodeGroups.Runtime/bin/Release/net6.0/NodeGroups.dll -TestDll ./tests/NodeGroups.Runtime.Tests/bin/Release/net6.0/NodeGroups.dll -ReportPath ./TestResults/build-separation/report.json
```

该脚本读取实际 PE 元数据与方法 IL，不加载 Unity/BepInEx、不执行插件。它检查发行版排除测试类型、环境入口、辅助方法和截图依赖，测试版具有完整测试套件，以及两版身份、版本和信息版本一致。

验证实际超时与恢复：

```powershell
pwsh -NoProfile -File ./tests/build/Verify-DeploymentGuards.ps1 -ReportPath ./TestResults/deployment-guards.json
```

该脚本要求已有两版构建和本地应用环境。它验证测试版安装拒绝、部署锁冲突、真实一秒超时后的环境与文件恢复。预期超时才算该项通过，其他启动错误不算通过。参数和失败传播详见 [构建与部署检查](tests/build/README.md)。

## 修改时必须保持的行为

- 原生节点 GUID、坐标、连接和剧情数据由 AzureArchive 维护；组成员用 GUID 关联，组状态不能成为另一套权威剧情或节点坐标源。运行期几何快照用于布局和撤销，不持久化为侧车中的真实节点位置。
- 每个节点只属于一个组。边框调整只改变区域，不自动增减成员；成员移出通过节点拖放完成。
- 运行时数量准入与 `GroupStoreLimits` 保持一致；缺失但可撤销恢复的成员也计入容量，组间转移不重复计数。拒绝普通拖放须回滚预览位置，拒绝菜单节点入组须保留原生创建和撤销。
- 组显示数量通过 `WorkspaceSnapshot.CountPresentMembers` 计算保留归属与当前画布节点注册表的交集；回收或缺失成员保留 GUID 归属以支持撤销，但不计入显示。折叠隐藏只影响可见性，仍计入显示数量。标题及界面缓存须使用同一有效计数。
- 回收站观察以原生成功注销或注册为准，不根据旧坐标猜测状态，不改写 GUID 或代替原生暂存。`TrashPatches` 只补丁具有独立原生实现的 `NodeMoveToTrashOperation`、`NodeMoveOutTrashOperation` 的具体 Undo/Redo；避免补丁化可能被 IL2CPP 合并的构造函数或基类虚方法。
- 原生回收与取回历史通过 `NativeHistory.AttachNative` 附着分组快照，不新增一条分组历史。被 Mod 接管的移动历史已受抑制，须隔离本手势中第一条被原生历史实际接受的操作；原生回收前可能先写断连操作，因此不能仅隔离第一条 `NodeMoveToTrashOperation`。同一手势内被接受的断连与回收操作保持原生链接，确保一次 Undo 恢复节点、连接和分组，又不会撤销此前的无关命令。
- 回收手势在全部结束后冻结统一的 After 快照，Undo 使用手势开始前的位置，以避免多次原生回调记录中间状态。此约束不代表支持批量回收：AzureArchive 1.0.1 在多选回收时提示“不支持批量删除”，Mod 沿用该限制；测试须确认节点仍在画布，并按完成的普通拖动处理位置与归属。
- 原生链接 Undo/Redo 执行中不得按每次 Register/Unregister 的中间位置扩大边框；等待完整操作结束再同步。取回前冻结目标区域，注册完成后按最终落点显式转移或移出原组，防止旧归属把原组边框扩大到组外落点。预览和取消不提交归属或历史。
- 组元数据几何须可持久化；名称不得含控制字符或孤立 UTF-16 代理项。空闲快照、可见性和界面状态缓存依赖不可变集合或完整值比较，不能缓存可变的原生节点/连接注册表。
- `GroupSaveState` 只接收已提交的组内容，以最后成功持久化快照比较；`Dirty` 是常数时间缓存。提交、撤销/重做、层序变动和空闲区域校正后发布内容；失败或待重新保存的恢复状态通过 `RequireSave` 保留，只有有效成功回执能推进保存点。
- `SnapshotForSave` 冻结最后已提交的组集合，不能把缩放或移动预览写入侧车。原生节点坐标和剧情仍以原生保存快照为准。
- `NativeSaveState` 仅将组脏状态补充到原生未保存判断，并在原生按钮刷新后补充保存按钮颜色。原生按钮函数存在内联判断，因此不能只补丁 getter；不能修改 `saveDelta`、`compileDelta` 或借主要剧情变更触发提示。Ctrl+S 兜底使用当前线程旁路读取原生原始判断。
- `UnsavedQuitPrompt` 只订阅本会话的 Unity 关闭前事件。确认后延迟至下一帧请求正常关闭，批准限一次且绑定会话；取消、外部关窗和旧回调都不能关闭另一工程。释放和构造失败时精确移除自身订阅，不能清空其他订阅者。
- 拖放目标按主节点位置和最高层命中判断；只有选中组才改变层序，悬浮不增加历史步骤或成员数量。
- 目标绿色优先于选中黄色，结束或取消后恢复原状态。
- 一个完成的用户手势应对应一个历史操作；菜单创建与入组的 Undo/Redo 需保持联动。
- Unity/NGUI 和原生互操作对象在主线程访问；场景更换、对象销毁和会话退出时释放本会话的引用与临时状态。
- 创建入口只包含原生节点样式的按钮和文字。拖动阈值使用屏幕像素；发生拖动后同时抑制 NGUI 点击通知和本次手势的迟到点击，直到下一次按下再重置。入口不持有原生 Node 或 Line，不产生剧情注册。
- 恢复确认框每次会话至多展示一次；取消或关闭不导入、不删除恢复文件。窗口及回调绑定具体会话实例，释放旧会话时仅关闭其拥有的窗口，不能把迟到确认应用到新会话。原生弹窗打开时阻止 Mod 快捷键与创建按钮输入穿透。
- 原剧情通过原生保存路径持久化；组元数据写入同名侧车文件。身份不匹配、损坏或读取失败时保护原文件。
- 未保存组信息的恢复目录仍为应用目录下的 `ModDev/NodeGroups/artifacts/recovery/`，它是 Mod 的运行时数据目录，与源码项目目录名不同。

新增测试应借用真实原生回调和生命周期，记录开始时的剧情、编译结果、历史、节点及输入状态，并在结束或失败后恢复。测试输入补丁只能在测试作用域安装；不得通过改变产品逻辑来满足测试。

## 发布与问题定位

版本需同步 [manifest.json](manifest.json) 的 `version_number`、[Directory.Build.props](Directory.Build.props) 的 `Version` 和 [Plugin.cs](src/NodeGroups.Runtime/Plugin.cs) 的版本常量。发布采用默认发行工程输出，安装目录与清单版本一致。

发行包只放发行版 NodeGroups.dll、manifest.json、icon.png 和必要的使用说明。图标源文件位于项目根目录；生成的版本目录与 ZIP 放在 `artifacts/`，打包时显式选择这三个文件，避免复制整个 bin 目录。原应用、互操作依赖、测试 DLL、测试截图、日志、工程、Profile、恢复副本与缓存不随包分发。开发过程和验收记录不属于公开项目内容，`docs/`、`TestResults/`、`artifacts/` 和生成目录由忽略规则排除。

| 问题 | 首先检查 |
| --- | --- |
| 编译缺少 Unity/BepInEx 类型 | 项目放置位置、`BepInEx/core` 和 `BepInEx/interop` 是否与本地应用匹配。 |
| 托管测试无法启动 | `dotnet --list-runtimes` 是否包含可运行 net6.0 的运行时。 |
| 运行时没有报告 | 本次 Player 日志、实际进程退出、加载环境，以及是否误用发行 DLL。 |
| 报告版本或隔离检查失败 | 重新构建两种输出；不要将旧测试报告当作本次结果。 |
| 部署锁冲突 | 确认同一项目中另一安装或测试操作已结束，避免同时修改同一安装。 |
| 部署恢复失败 | 读取本次 `deployment-restore.json`，根据 `.before` 副本检查原文件；先保留备份再处理。 |

更新原应用、BepInEx 或互操作程序集后，应执行托管、DLL 边界、完整 Unity 与部署保护检查，并补充真实鼠标操作、不同画布尺寸及实际工程的手工验证。
