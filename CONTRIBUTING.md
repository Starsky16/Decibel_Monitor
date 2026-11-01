# 贡献指南（CONTRIBUTING）

感谢关注本插件。本文档收录面向开发者的内容：构建与测试方法、校准公式、设置项完整参考、以及几处有意保留的设计口径。用户向导请看 [README](README.md)。

## 构建、测试与打包

环境要求：Windows + .NET 8 SDK（`net8.0-windows` 目标，仅 Windows 可构建）。

```bash
# 构建（须在仓库根执行）
dotnet build -c Release

# 单元测试（覆盖计算器、采样、判定源、仲裁、状态解析与时段闸门等纯逻辑）
dotnet test Tests/Decibel_Monitor.Tests -c Release

# 打包 .cipx（须在仓库根执行，脚本内部使用相对路径）
pwsh -ep bypass ./tools/publish.ps1
```

注意：

- `tools/publish.ps1` 必须在仓库根目录执行（内部使用相对路径 `dotnet publish Decibel_Monitor.csproj`），传绝对路径会报 `MSB1009 项目文件不存在`。
- `cipx/` 整个目录是打包中间产物，已被 `.gitignore` 忽略，不要提交入库。
- 正式发布走仓库的 GitHub Actions（`Build.yml` 在 tag 推送时自动构建并建 Release）；另有 `Test.yml`（单测）与 `IntegrationTest.yml`（把插件加载进真实宿主的冒烟测试）。发版流程细节可参考 `.github/workflows/` 下各工作流定义。

## 校准公式

- 测量得到的线性峰值：`measuredLinear`（0..1）
- 目标线性值：`targetLinear = 10^(targetDbFS / 20)`
- 放大倍数：`magnification = targetLinear / measuredLinear`

插件会对计算结果做上限保护（默认上限 1024；下限可为 0），以避免极端错误值。

## 设置项完整参考

### ① 组件设置（`DecibelComponentSettings`，每组件独立）

- `ShowStatusIndicator` (bool)：是否在组件上显示提醒状态点，默认 true；关闭后仍会正常发出系统通知，只是不在组件上显示状态点（形状含义见 README“提醒状态点”）。
- `ShowDecibelPrefix` (bool)：数值前是否显示“分贝:”前缀（如“分贝: 70.0”），默认 true。
- `UpdateIntervalMs` (int)：数值更新频率（毫秒，运行时会夹取到 100..5000），默认 200。

### ② 通知提供方设置（`DecibelNotificationProviderSettings`，全局）

- `AlertText` (string)：提醒正文（通知横幅显示的文字），默认“请保持安静”。
- `OverlayDurationSeconds` (int)：提醒正文（第二段）的显示时长（秒，面板范围 1..600，运行时夹取 1..600），默认 30。一次提醒由遮罩（固定 5 秒，宿主 `NotificationContent` 默认值）与正文两段拼成且文案相同，故**一次提醒的总时长 = 5 秒 + 本值**；总时长比判定源的冷却时间短时，提醒结束后才能看到冷却期的状态点。启用开关、阈值与冷却**不在此处**，由插件设置页的判定源负责（避免“判定源已裁定提醒、提供方却因自身阈值不发通知”的双重门槛）。

### ③ 插件全局设置（`DecibelMonitorGlobalSettings`）

- `ReferenceDecibel` (double)：校准参考（显示刻度 dB 0..150），默认 70（对应 -80 dBFS）。
- `Magnification` (double)：全局放大倍数，校准后保存，所有组件统一使用。
- `EnableContinuousMonitoring` (bool)：是否启用“持续监听麦克风”（常驻静默捕获流），默认 true。关闭后仅使用系统实时计量，多数设备会一直显示 0。
- `FallbackCaptureMs` (int)：显式采样（如校准）的单次录音时长（毫秒），默认 400。
- `SignalThreshold` (double)：信号检测阈值（存在配置文件中，可在采样异常时调低）。
- `AverageWindowSeconds` (double)：判定源比较的平均窗口时长（秒），默认 3。
- `SourcePriorityOrder` (`List<string>`)：判定源优先级顺序，默认“自动提醒”在前。
- 时段闸门（对两个判定源生效）：`SuppressAlertDuringBreak` (bool，默认 true)、`ClassStartProtectionMinutes` (int，默认 3，运行时夹取到 0..10，0 表示关闭该保护)。
- 临时静默 / 临时开启：「静默本节课」取不到本节课剩余时间时的兜底时长 `SilenceFallbackMinutes` (int，默认 45，运行时夹取到 1..1440)、“静默一个午”的时长 `NoonSilenceMinutes` (int，默认 60，同样夹取)。覆盖状态本身不写进设置（不持久化）。
- 判定源①（自动提醒）：`AutoSourceEnabled` (bool)、`AutoSourceThreshold` (double，默认 120)、`AutoSourceCooldownSeconds` (int，默认 600)、`AutoSourceSustainSeconds` (int，默认 0，运行时夹取到 0..60，0 表示不做持续性要求)。
- 判定源②（热键确认）：`HotkeySourceEnabled` (bool)、`HotkeySourceThreshold` (double，默认 120)、`HotkeySourceCooldownSeconds` (int，默认 600)、`HotkeySourceSustainSeconds` (int，默认 0，夹取同判定源①)、`HotkeyWindowSeconds` (int，默认 10)、`HotkeyKey` (string，默认 `F5`) 与 `HotkeyCtrl` / `HotkeyAlt` / `HotkeyShift` / `HotkeyMeta` (bool)、应急强制提醒 `ForceAlertEnabled` (bool，默认 false) 与 `ForceAlertPressCount` (int，默认 2，运行时夹取到 2..10)。

## 显示值与判定值不一致（有意保留的口径差异）

组件上显示的分贝是**瞬时值**，而提醒判定（开筛选窗口、自动提醒）与数字颜色判据用的是**滑动平均值**。因此下面两种错觉都可能出现，属于既定行为、不是缺陷：

- **读数看着不高却变红 / 变三角**：窗口平均值已被之前的一小段高值拉高，瞬时值回落但平均仍超阈值。
- **读数明明很高却没有提醒**：单次尖峰被平均窗口摊薄，平均值尚未过线；若想减少这类情况，可调小平均窗口（更灵敏）或依赖判定源②的按键确认。

想让两者口径一致就得二选一：显示改成平均值（数字会变得粘滞、响应迟钝），或判定改用瞬时值（单点尖峰就会误触发），两者都被否决，故如实写明由使用者自行取舍。

## 致谢

- 原项目与主要开发者：[Yeson38](https://github.com/Yeson38)（原作者）
- Idea 来自：[HAHAHAHAHAYINING](https://github.com/HAHAHAHAHAYINING)、[ClassIsland 讨论 #561](https://github.com/ClassIsland/ClassIsland/discussions/561)
- 参考代码：[CIImage](https://github.com/lrsgzs/CIImage)
