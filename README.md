# Decibel_Monitor

一个用于 ClassIsland 的分贝监测插件，实时读取系统麦克风峰值并在主界面显示分贝映射值。支持校准（将数字音频幅度映射到目标 dBFS），并提供多重回退的采样方式以提高兼容性（AudioMeterInformation、WasapiCapture、WaveInEvent）。

## 主要功能

- 实时显示麦克风分贝值（映射到 0–150 范围以便在 UI 中直观显示）。默认通过**一条常驻的低开销捕获流**持续计算峰值——数值稳定、无周期性开/关录音造成的闪烁（Windows 麦克风占用图标会常亮而非闪烁）。
- 校准功能：根据目标 dBFS 自动计算并保存放大倍数（组件设置 → `Magnification`）。校准时才会进行一次显式采样。
- 阈值提醒：超过指定分贝值时，在组件上显示提示文字，并通过 ClassIsland 通知系统弹出醒目提醒（带冷却时间防刷屏）。

## 设置位置一览

本插件的设置分布在 ClassIsland 的三个位置：

| 位置 | 内容 |
| --- | --- |
| ① 组件设置（右键组件 → 设置） | 校准参考 dB（`ReferenceDecibel`）、放大倍数（`Magnification`）、校准按钮 |
| ② 通知设置（设置 → 通知 → 分贝提醒） | 启用提醒、阈值（dB）、提醒文字、冷却时间（分钟） |
| ③ 插件设置页（设置 → Decibel_Monitor） | 采样偏好（高级，含回退录音采样开关）、默认捕获设备诊断、捕获设备列表 |

## 依赖

- .NET 8
- ClassIsland

确保项目正确加载插件。

## 使用说明

1. 在宿主中将本组件加入布局（或通过插件管理启用）。
2. 主界面会显示当前分贝映射值（0–150）。
3. 打开组件的设置面板进行校准（**组件设置**）：
   - 将麦克风对准参考声源或播放测试音。
   - 将参考滑块设置为目标显示 dB（默认 70，范围 0..150，对应 dBFS -80）。
   - 点击“校准”按钮。插件采样后计算放大倍数并保存到该组件的 `Magnification`。
   - 采样或静音表现异常时，可在 **设置 → Decibel_Monitor 页** 使用“捕获设备列表”诊断，或调整采样偏好。

### 阈值提醒（通知设置）

1. 打开 **设置 → 通知 → 分贝提醒**。
2. 开启“启用提醒”，设置阈值（dB）、提醒文字与冷却时间（分钟）。
3. 当分贝值超过阈值时：
   - 组件上显示红色提示文字；
   - 通过 ClassIsland 通知系统弹出醒目提醒；
   - 冷却时间内不会重复提醒（默认 10 分钟）。

### 校准公式

- 测量得到的线性峰值：`measuredLinear`（0..1）
- 目标线性值：`targetLinear = 10^(targetDbFS / 20)`
- 放大倍数：`magnification = targetLinear / measuredLinear`

插件会对计算结果做上限保护（默认上限 1024；下限可为 0），以避免极端错误值。

## 设置项

### ① 组件设置（`DecibelComponentSettings`，每组件独立）

- `ReferenceDecibel` (double)：校准参考（显示刻度 dB），默认 70（对应 -80 dBFS）。
- `Magnification` (double)：放大倍数，由校准或手动设置，默认 1.0。
- `ShowAlertTextOnComponent` (bool)：是否在组件上显示“超过阈值”的提示文字，默认 true；关闭后仍会发送系统通知，但不在组件上显示提示文字。

### ② 通知提供方设置（`DecibelNotificationProviderSettings`，全局）

- `IsAlertEnabled` (bool)：是否启用阈值提醒，默认 false。
- `AlertThreshold` (double)：提醒阈值（显示刻度 0..150），默认 120。
- `AlertText` (string)：提醒文字（通知横幅与组件提示），默认“请保持安静”。
- `AlertCooldownMinutes` (int)：触发提醒后的冷却时间（分钟），默认 10。

### ③ 插件全局设置（`DecibelMonitorGlobalSettings`）

- `EnableContinuousMonitoring` (bool)：是否启用“持续监听麦克风”（常驻静默捕获流），默认 true。关闭后仅使用系统实时计量，多数设备会一直显示 0。
- `FallbackCaptureMs` (int)：显式采样（如校准）的单次录音时长（毫秒），默认 400。
- `SignalThreshold` (double)：信号检测阈值（存在配置文件中，可在采样异常时调低）。

## 故障排查

- 提醒一直没有触发：
  - 确认 ClassIsland 全局通知开关与 **设置 → 通知 → 分贝提醒** 的开关均已开启。
  - 确认分贝值确实超过了设定阈值（长期显示 0 请参考下一条）。
  - 若宿主日志中无“注册提醒渠道：…（超过分贝阈值）”，说明加载的是旧版本插件，请更新插件并重启。
- 分贝值长期显示 `0.0`：
  - 若关闭了“持续监听麦克风”，则只使用系统实时计量，多数设备会一直为 0；请到 **设置 → Decibel_Monitor 页** 确认该开关已开启（开启后麦克风占用图标会常亮）。
  - 确认宿主应用有麦克风权限（Windows 隐私设置）。
  - 确认默认捕获设备已启用并未被独占。
  - 在 **设置 → Decibel_Monitor 页** 使用“捕获设备列表”检查系统设备；增加音量或靠近声源再测。
- 出现“麦克风正在使用”提示且图标**闪烁**：说明加载的是旧版本插件（旧版周期性开/关录音）。请更新插件；当前版本为常驻监听，图标只会常亮不会闪烁。如不希望常亮，可关闭“持续监听麦克风”。
- 校准结果异常：
  - 确认目标单位为 dBFS（数字音频相对量），而非声压级 dB SPL。
  - 若测量值接近 0，会导致放大倍数非常大，请提高测试音量或选择更高的目标 dBFS。

## 隐私与权限

- 插件需要访问麦克风进行本地采样，宿主必须授予麦克风权限。
- 插件不会上传或外传音频数据，所有采样仅在本地处理。

## 开发与贡献

- 欢迎提交 issue 或 PR。请遵循仓库中的贡献指南（若存在 CONTRIBUTING.md）提交风格一致的修改。
- 单元测试：`Tests/Decibel_Monitor.Tests` 覆盖 `DecibelCalculator` 与 `PeakSample` 纯函数（Windows 环境运行 `dotnet test`）。
- Idea来自：[HAHAHAHAHAYINING](https://github.com/HAHAHAHAHAYINING),[讨论#561](https://github.com/ClassIsland/ClassIsland/discussions/561)
- 主要开发者：[Yeson38](https://github.com/Yeson38)
- 参考代码：[CIImage](https://github.com/lrsgzs/CIImage)

