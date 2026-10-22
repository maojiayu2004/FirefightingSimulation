# FirefightingSimulation：项目结构、烟雾比较与融合说明

更新日期：2026-10-22。本文根据本地文件、Git 提交和已执行的离线测试整理。

本文分为两份项目说明：A 是 GitHub 仓库及本次使用的远端烟雾分支；B 是本地 Unity 项目及新增粒子烟雾。后面给出比较、融合实现、菜单、终端启动步骤和后续路线。

## 1. 先看结论与当前验证状态

两套系统可以在同一场景中组合使用。推荐职责是：旧区域烟雾负责房间总体烟量和训练节奏；新粒子烟雾负责火源附近的上升、沿顶棚扩散和几何绕流；窗口烟柱负责外部观察效果。

这次已完成的文件工作：

- 将 GitHub 对应烟雾分支的 3 个新提交快进到本地，HEAD 为 `e352212b`。
- 创建独立融合场景 `Assets/SCENARIOS/SmokeFusion/SmokeFusion.unity`，以远端新版测试场景为基础，添加本地两个粒子烟雾发射器。
- 针对融合场景烘焙独立碰撞场 `SmokeFusionField.asset`。
- 保留区域烟雾；两个粒子发射器的 `replaceLegacySmoke` 均为 `false`。
- 新增粒子组件的 `lifecycleFireSource`，将烟雾发射几何与火灾生命周期分开，修复厨房整体火源与炉灶子物体不是同一对象的问题。
- 添加 Unity 菜单和 `Tools/SmokeFusion.ps1`，由使用者自行打开、预览或录制。

验证边界：

- 旧版粒子原型已有历史运行截图、录像和 GPU 接触探针记录。
- 本次融合场景在正常编辑器里确实渲染了新粒子烟雾，也运行到了双火源阶段。
- 第一轮后台 batchmode 截图没有正常呈现 VFX，不用于判断最终视觉效果。
- 后续诊断发现旧区域烟雾未触发：20 秒时区域透明度为 0、无存活粒子，且 `sameStove=False`。因此此前截图不能证明两套烟雾已同时叠加成功。
- 已从文件层面修复这个绑定，但依照使用者要求，没有继续操控界面或自动重跑最终效果。修复后的视觉、多人和头显表现待使用者按下述步骤验证。
- `particle-smoke-results/fusion` 中当前留存的是修复前诊断资料，不应作为最终验收结果。重新执行比较会更新其中同名输出。

## 2. A：GitHub 项目说明

### 2.1 仓库与分支

仓库：<https://github.com/virtual-embodiment-lab/FirefightingSimulation>

它是基于 `jcollins-01/HazardSimulation` 演进的消防模拟项目。本次重点读取和同步的是 `demo-merge2-enhanced_smoke_0`，并没有逐一审计所有远端分支。

本次同步后：

- `firefighting/demo-merge2-enhanced_smoke_0`：`e352212b`。
- 对应的三个新增提交依次包含：区域烟雾启动与 VR 可见性修复、初始烟雾逐步增长、主摄像机后处理与抗锯齿调整。
- `main` 的头提交是 `44dd0ccd`，本次 Git 检查中它是当前烟雾分支的祖先，落后 217 个提交。

因此不能把仓库默认 `main` 与“当前最新烟雾实验”当作同一个版本。

### 2.2 远端运行环境

依据远端该分支的配置：

- Unity：`6000.0.66f1`。
- URP / Visual Effect Graph：`17.0.4`。
- Normcore：`3.5.1`。
- XR Interaction Toolkit：`3.0.10`。
- OpenXR：`1.16.1`。

这是 Git 中记录的配置，不等同于本机当前安装环境。

### 2.3 项目主要逻辑

项目包含消防场景、火灾行为、XR 交互、网络状态、热成像和训练数据记录。下面按代码职责说明，实际运行哪些功能还取决于具体场景挂载了什么组件。

1. **空间与场景生成**：`RoomGeneration.cs` 管理房屋尺寸、房间、楼层、走廊、门窗及布局参数；`RoomDecoration.cs` 配置室内布置；`HouseLayoutManager.cs` 文件包含布局数据与生成设置的保存逻辑。`Assets/SCENARIOS` 内有受控、非受控、昼夜和旧版场景。
2. **火源与燃烧**：`Assets/OAVA-Flame` 内的 Ignis / OAVA Flame 系统负责可燃物、火焰、烟雾、火星等 VFX；`FlammableObject.cs` 管理具体物体燃烧和视觉实例。
3. **火灾训练参数**：`FireProfileController.cs` 提供 Class A/B/C/D/K 等预设，管理火源温度、喷水降温、恢复、阴燃与复燃相关参数。
4. **危险行为**：`UniversalHazardController.cs`、`HazardBehaviorModel.cs` 等包含接近、扩散及行为配置。不能仅因为脚本存在，就认定每个烟雾测试场景都启用了这些行为。
5. **多人同步**：`Assets/Scripts/Networking` 包含 Normcore 模型、火灾状态与喷水同步。`NetworkedFireState.cs` 提供一致的生命周期快照，并处理点火轮次、时间、权威状态和视觉状态；离线时可退回本地 Ignis 状态。
6. **XR 与交互**：`Assets/Scripts/Player`、`Assets/XR`、`Assets/XRI` 配合 XR Interaction Toolkit / OpenXR 管理相机、玩家、移动和交互；喷水、握持及灭火器相关逻辑分布在 `Spray.cs`、`LeverSqueeze.cs`、`SafetyPin.cs` 等脚本中。
7. **热成像与提示**：`HazardTemperature.cs`、`ThermalFireRegistry.cs`、`ThermalHeatEffects.cs` 等提供温度与 TIC 显示相关数据；`PlayerHeatCue.cs` 根据本地玩家位置、头部高度和附近火烟情况提供红色边缘提示。
8. **记录**：`Assets/Scripts/DataLogging` 包含玩家移动、音频转录与会话记录模块。是否记录以及记录内容受场景配置和组件状态影响。

整体链路可以理解为：场景提供几何与可燃物 → 火源组件决定燃烧状态 → 网络层提供一致生命周期 → 火焰、烟雾、热提示和交互读取这些状态 → 记录模块保存配置启用的数据。

### 2.4 远端烟雾的三层结构

**火源自身烟雾**：由可燃物使用 Ignis VFX 表现，随具体火源燃烧出现。

**室内区域烟雾**：`RoomSmokeController.cs` 管理手工放置的 `smokeZones`。场景中的这些对象是视觉发射区域；控制器根据火源状态调整透明度和颜色强度，使它们按距离先后出现与淡出。

**窗口烟柱**：`WindowSmokePlumeController.cs` 配合 `Assets/VFX/WindowSmokePlume.vfx` 与 `Assets/Prefabs/WindowSmokePlume.prefab`。从薄的窗口发射区域向外给初速度，再逐渐向上抬升；它不是室内烟量自然穿过窗洞后的连续流体输出。

### 2.5 远端烟雾测试场景

路径：`Assets/SCENARIOS/Smoke-VFX-Test/Scenario1-ImprovedSmoke_Test0.unity`。

当前区域控制器登记了 10 个烟雾区域，序列化参数为：

- `initialGrowthDuration = 12`：每个烟区首次增长到设定强度需要 12 秒。
- `initialMaximumDistanceDelay = 5`：最远区域相对最近区域最多晚 5 秒开始。
- `fadeDuration = 60`：灭火后每个烟区用 60 秒淡出。
- `maximumDistanceDelay = 15`：最远区域最多额外延迟 15 秒淡出。
- `reignitionRestoreDuration = 3`：复燃恢复约 3 秒。

这些时间是控制器的过渡参数；实际可见效果还取决于图中的粒子寿命、生成速度、相机和渲染设置。

“由近到远变浓”是按预设区域与距离编排的表现。它没有在相邻房间之间求解烟量、压力和通风输运，也不能自动保证烟会识别门洞绕墙。

## 3. B：本地项目说明

### 3.1 本地目录与 Git 关系

工作目录：`C:\Users\maoma\Desktop\FireFighter`。

真正的 Git / Unity 项目根目录：`C:\Users\maoma\Desktop\FireFighter\HazardSimulation`。

同级 `particle-smoke-results` 保存实验输出，不是 Unity 的 Assets 目录。

远端配置：

- 原 `origin` 仍指向 `https://github.com/jcollins-01/HazardSimulation.git`，未覆盖。
- 新增 `firefighting` 指向使用者提供的 GitHub 仓库。
- 当前本地分支跟踪 `firefighting/demo-merge2-enhanced_smoke_0`。

同步指 Git 提交已经对齐。工作目录仍有本地未提交改动与未跟踪文件，并不与 GitHub 的每个文件完全一致。本次没有提交或推送这些本地修改。

### 3.2 本地环境差异

本地配置目前是：

- Unity：`6000.6.5f1`。
- URP / VFX Graph：`17.6.0`。
- Normcore：`3.6.1`。
- XR Interaction Toolkit：`3.6.1`。
- OpenXR：`1.18.0`。

本地还存在材质、贴图 `.meta`、项目设置、包锁文件与部分脚本的修改。它们有不少在本次操作前就存在，不能全部归因于本次融合。回传 GitHub 时应把环境升级和烟雾逻辑分别审查，尤其不要把整个工作目录一次性视为单一烟雾补丁。

### 3.3 本地粒子烟雾的组成

- `Assets/Scripts/ParticleRoomSmoke.cs`：读取火源生命周期，计算发射率、发射表面与视觉参数，并传入 VFX Graph。
- `Assets/VFX/ParticleRoomSmokeGeometry.vfx`：GPU 粒子图。
- `ParticleRoomSmokeGeometryInitialize.hlsl`：粒子初始化。
- `ParticleRoomSmokeGeometryUpdate.hlsl`：粒子上升、横向输运、扰动、顶棚烟层、碰撞及透明度变化。
- `Assets/Scripts/SmokeCollisionField.cs`：向图绑定三维场纹理和采样范围。
- `Assets/VFX/SmokeColliderBake.compute` 与 `Assets/Editor/SmokeCollisionBaker.cs`：将场景碰撞几何烘焙成表面距离与法线场。
- `Assets/Editor/SmokeGeometryProbe.cs`、`SmokeGeometryVerification.cs`：接触探针与离线记录工具。

原型入口：`Assets/SCENARIOS/ParticleSmokePrototype/ParticleSmokeGeometry.unity`。

### 3.4 粒子如何扩散

1. 从真实火源的发射位置或测量过的顶面生成粒子。
2. 初期向上升，同时保留一定侧向展开。
3. 采样头顶实际几何，接近顶棚后增强水平流动，形成上层烟雾。
4. 在更新中查询三维碰撞纹理；接近表面时将粒子中心投回安全距离，并去掉朝向表面内部的速度分量。
5. 保留切向运动，让粒子沿表面移动，而不是简单反弹。
6. 开放门洞没有对应实心碰撞表面，粒子有机会通过。是否大量通过仍取决于输运方向和局部几何，不保证出现真实通风流。
7. 灭火后发射率逐渐降到 0；已有粒子继续运动，按年龄淡出。

这套实现没有把纹理采样范围当作封闭房间边界；超出场纹理范围被当作自由空间。

### 3.5 当前原型参数

现有几何原型中，每个发射器约为：

- 192 粒子/秒；容量 8192；标称寿命 30 秒。
- 上升速度参数 1.1；顶棚横向速度参数 0.7；扰动 0.24。
- 视觉尺寸从 0.45 增长到 1.05；透明度 0.13。
- 最大视觉烟层深度参数 1.65；烟层增长时间参数 24 秒。
- 碰撞半径 0.08；烘焙体素间距 0.12 Unity 世界单位。

这些是视觉调节参数，不是经过验证的质量浓度、温度场或消防工程测量值。粒子容量与寿命还会限制有效发射率。

### 3.6 本地已知限制

- 碰撞场为静态、双面、无符号表面距离与法线场，不是带内部体积判定的完整流体求解器。
- 门窗开合、家具移动和墙体修改后需要退出 Play Mode 并重新烘焙。
- 小开口和细小几何受体素精度限制。
- 碰撞约束粒子中心；烟雾 billboard 有尺寸，仍可能在边缘出现视觉穿插。
- 新粒子的出生数量与轨迹没有逐粒子网络同步，不能仅凭火源状态一致就宣称两名玩家画面完全一致。
- 没有把视觉粒子浓度自动接入旧区域的玩家热提示接口。`PlayerHeatCue` 读取旧区域曝光度的语义，需要后续统一。
- 历史录制带编辑器、截图和读回开销，不能作为 Quest、Vision Pro 或其他头显性能数据。

## 4. 两份项目的核心差异

### 4.1 扩散的来源

远端区域烟雾由手动布局和时间曲线决定覆盖范围；本地粒子由发射位置、速度、扰动和几何接触决定可见轨迹。前者容易控制训练节奏，后者更适合观察方向与障碍物关系。

### 4.2 灭火后的行为

区域烟雾有专门的 60 秒淡出加距离延迟；粒子依赖停止新增与已有粒子寿命。叠加后可能出现运动烟雾先消失、房间区域烟雾仍持续存在的情况，需要作为设计选择检查，而不是直接认定为 bug。

### 4.3 地图适配

区域方案需要重新布置烟区；粒子方案需要正确的碰撞几何、发射位置和重新烘焙。两者都不是换一张地图就自动达到理想效果。

### 4.4 视觉与性能

叠加会增加透明层数量和 GPU overdraw，浓度也不会按 Inspector 数值简单相加。多层半透明混合会使背景透过率降低。远端区域 `smokeAlpha` 与新图的 `opacity` 处于不同着色与乘法链，数值不能直接比较。

### 4.5 版本兼容

本地 Unity 与包版本高于远端分支。融合源码、VFX 图和场景回传时，需要独立验证目标 Unity 版本能否导入；不建议直接把本地自动升级后的全部 ProjectSettings / Packages 一并覆盖团队版本。

## 5. 本次融合方式与火源绑定修复

### 5.1 独立场景

`SmokeFusion.unity` 复制自新版仓库烟雾测试场景，并保留该场景原有区域烟雾；添加的粒子图来自本地原型。补充碰撞体和启用顶棚仅发生在融合副本中，烘焙纹理单独保存。

原型用 `replaceLegacySmoke = true` 替代旧烟雾；融合副本设为 `false`，允许旧系统继续显示。

### 5.2 发现的绑定问题

旧区域控制器读取：

`RoomGenerator/House/Floor_0/Room_1 [Kitchen]/kitchen set (to scale)` 上的火源。

本地粒子原型的 Source 则是炉灶子物体。点燃子物体并不会自动等价于点燃父物体，因此旧区域烟雾始终保持 Dormant。

### 5.3 修复后的职责

`ParticleRoomSmoke.source` 继续定义发射位置、测量表面与几何引用。

新增可选 `ParticleRoomSmoke.lifecycleFireSource` 指向旧区域控制器使用的 `FireProfileController`。配置后，粒子读取它所在对象的 `FlammableObject / NetworkedFireState`；留空则保持原型原来的行为。

融合场景中的炉灶粒子已序列化这个共享生命周期引用。构建工具也会设置它。离线预览与比较工具按这个共享来源点火和灭火，避免只点燃炉灶子物体。

这次没有把独立窗口烟柱强行改成室内粒子的延续，也没有加入真实通风计算。

## 6. 自己在 Unity 中打开效果

### 6.1 仅打开场景，不自动运行

1. 打开本地 `HazardSimulation`，使用 Unity `6000.6.5f1`。
2. 等待脚本和 VFX 导入结束；Console 有红色编译错误时先处理错误，菜单才会正常出现。
3. 保存当前场景并退出 Play Mode。
4. 点击 `Tools > Smoke Fusion > Open Fusion Scene`。
5. 这里只打开融合场景，不会自动点火。

另外两个对照入口：

- `Tools > Smoke Fusion > Open Repository Smoke Scene`：远端新版区域烟雾场景。
- `Tools > Smoke Fusion > Open Particle Prototype Scene`：本地几何粒子原型。

### 6.2 预览融合，不录制

点击 `Tools > Smoke Fusion > Preview Fusion (No Recording)`。

该菜单由你主动启动 Play Mode，使用固定厨房观察相机，并临时关闭测试运行中的 Normcore 自动入房，不把这个改动保存回场景。

- 约第 2 秒：点燃共享厨房火源，供区域烟雾和炉灶粒子共同读取。
- 区域烟雾按 12 秒增长、最多 5 秒距离延迟逐渐出现。
- 约第 45 秒：点燃第二个粒子火源。
- 预览不会自动灭火，也不会导出录制帧。
- 点击 Unity 顶部停止按钮，或 `Tools > Smoke Fusion > Stop Preview Or Comparison`，结束预览。

只按普通 Play 并不保证自动执行这套点火时间线；自动点火属于上述显式预览/比较入口，未将定时点火组件保存进正式场景。

### 6.3 重新构建融合副本

点击 `Tools > Smoke Fusion > Build Latest Repository Scene With Particle Smoke`。

它会从仓库烟雾场景重新创建融合场景，复制两个粒子发射器，设置共享生命周期，并针对融合几何重新烘焙碰撞纹理。它会覆盖融合副本已有的已保存调参结果，执行前请另存需要保留的融合版本。原始仓库场景与原型场景不被保存修改。

如果只想查看现有融合内容，使用 Open 或 Preview，不必每次 Build。

### 6.4 录制对照

点击 `Tools > Smoke Fusion > Run Offline Comparison`。

测试时间线：第 2 秒点燃厨房共享火源；第 20、45 秒采样单火源；第 45 秒后点燃第二火源；第 75 秒采样双火源并熄灭厨房火源；第 110、155 秒采样残烟。结束后退出 Play Mode，保留编辑器打开。

输出目录：`C:\Users\maoma\Desktop\FireFighter\particle-smoke-results\fusion`。

- `*-view0-legacy.png` / `particles.png` / `overlay.png`：厨房机位的旧烟、新烟和叠加诊断截图。
- `*-view1-*.png`：门口机位。
- `samples.txt`：发射率、存活粒子数、共享来源与旧区域状态。
- `frames.csv`、`overlay-*.png`：连续叠加画面及实际时间戳。
- `status.txt`：运行状态。

诊断截图通过临时设置旧烟透明度与新烟 renderer 可见性切换贡献；GPU 属性更新、透明排序和图内 alpha 计算可能影响瞬时结果。应结合连续画面和区域 alive 数判断，不能仅看文件名就认定隔离完全正确。窗口独立烟柱没有作为第三套独立对照轨道拆出。

## 7. 从 PowerShell 终端打开

以下命令供你自行执行；文档生成过程不会自动执行这些打开和运行命令。

先切换到项目目录：

```powershell
Set-Location 'C:\Users\maoma\Desktop\FireFighter\HazardSimulation'
```

仅打开融合场景：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\SmokeFusion.ps1 -Mode Open
```

预览融合效果：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\SmokeFusion.ps1 -Mode Preview
```

重建副本及碰撞场：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\SmokeFusion.ps1 -Mode Build
```

执行离线对照记录：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\SmokeFusion.ps1 -Mode Compare
```

停止当前测试：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\SmokeFusion.ps1 -Mode Stop
```

脚本检测到本项目的 Unity 已打开时，通过项目现有编辑器命令文件转交操作；如果没打开，则启动安装好的 Unity 并调用相应方法。命令文件位置为上一级的 `particle-smoke-command.txt`，编辑器约每 2 秒轮询，编译期间会延后。

`-ExecutionPolicy Bypass` 只作用于这次 PowerShell 进程，没有修改机器的长期执行策略。Unity 安装路径不同时，可传 `-UnityPath '你的 Unity.exe 路径'`。

如果启动停在恢复提示、许可提示或有未保存场景，由你自行处理后再执行。场景切换工具遇到未保存编辑会拒绝继续，详情见 Unity Console。

不使用 `-batchmode -nographics` 检查烟雾视觉；本次后台渲染曾出现控制逻辑正常而 VFX 不显示的情况。终端入口使用正常图形编辑器。

需要手动编码录制时：

```powershell
python .\Tools\encode_fusion_recording.py ..\particle-smoke-results\fusion
```

编码器需要 Python 的 `imageio_ffmpeg`，生成 `smoke-fusion.mp4`。它按记录到的实际帧间隔编码，重复采样画面，不补造未捕获的运动。先确认 `status.txt` 为 COMPLETE 且 `frames.csv` 属于本次有效运行。

## 8. 你应如何验收这次修复

1. 用 Preview 观察厨房共享火源是否确实点燃，两个层次是否同时出现。
2. 用 Compare 导出诊断：在 `single-020` 或 `single-045` 段应看到 `sameStove=True`；旧烟区应有燃烧状态、非零 alpha 和存活粒子。若仍为 0，不把当前画面判定为融合成功。
3. 检查 45 秒左右是否仍能看清炉灶、柜体和门口，确认房间不会过早全白或全黑。
4. 观察顶棚流动、门口扩散和家具边缘穿插。
5. 灭火后确认厨房粒子发射率降到 0；旧区域烟雾按自己的时间线消散；第二火源可以继续发烟。
6. 在目标头显和多人模式另测帧时间、透明 overdraw 与生命周期一致性。桌面离线通过不能替代这一步。

当前本地 Build Settings 默认启用的仍是 `Scenario 1 (controlled; 1 story; daytime) 1.unity`，并非融合测试场景。直接 Build 现有项目不会自动把融合场景作为启动场景；需要正式集成时再明确调整构建配置。

## 9. 后续融合路线

### 第一阶段：确认共享生命周期

保留现有独立场景；验证点火、复燃、灭火都读取同一逻辑火源。视觉发射位置可以在炉灶子物体，状态来源则使用厨房主火源。这是本次新增字段的目的。

### 第二阶段：避免重复浓度

先分别观察旧烟与新烟，再调叠加。建议让新粒子承担近火源可见运动，降低大面积区域烟的遮挡贡献。可先尝试区域原始强度约 30%–50%、粒子保留当前强度作为调参起点；这只是实验建议，不是已验证最佳配置，也尚未应用到当前场景。

优先比较着火后 10、20、45、75 秒的可见度和顶棚厚度；不要同时改发射率、寿命、尺寸和 alpha，否则难以判断变化来源。

### 第三阶段：统一烟量代理

引入每个房间的共享烟量代理值，让区域 alpha、粒子发射/烟层厚度和玩家暴露提示读取同一层数据。它仍可先是训练用近似值，但要给参数清楚的语义。

以后才考虑把燃烧产烟率、房间容积、门窗交换与衰减写成可解释模型。没有质量守恒和通风验证前，不应把它称为真实烟气浓度计算。

### 第四阶段：门窗与几何变化

明确哪些门窗会运动。静态碰撞场先保留；动态门窗可增加独立动态碰撞近似或局部更新。窗口烟柱将来可由室内烟量代理、开口状态和风向控制，减少“窗户封闭仍持续排烟”的不一致。

### 第五阶段：多人和性能

同步火源生命周期、必要的随机种子和低频房间烟量代理，不优先逐粒子联网。分别验证两名玩家相同阶段的宏观能见度。再根据目标头显性能决定容量、分辨率、LOD、粒子尺寸和区域层数量。

### 第六阶段：回传团队仓库

建议把变更拆成可审查的部分：生命周期解耦与兼容旧行为；融合演示场景与独立烘焙资源；测试和使用文档；最后单独处理引擎/包升级。每个 `.cs`、`.vfx`、场景和资源应保留配套 `.meta`，不要把 Library、Temp、日志或录制帧当作项目源码提交。

## 10. Git 日常检查

只查看状态与远端差异：

```powershell
git status --short --branch
git remote -v
git fetch firefighting
git rev-list --left-right --count HEAD...firefighting/demo-merge2-enhanced_smoke_0
```

本次同步完成时最后一条为 `0 0`，表示提交层面对齐。后续远端变化以重新 fetch 后为准。

`origin` 仍是旧仓库，因此未来推送前应明确确认远端，不要只依赖 `git push` 的默认行为。本次没有创建提交、PR 或推送。
