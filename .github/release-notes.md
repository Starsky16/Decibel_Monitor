# Decibel_Monitor 0.1.0.0

> **AI 辅助说明**：本 Release 由 AI 辅助生成，作者负责审查其行为并保证功能与测试正常。

本仓库是 [Yeson38/Decibel_Monitor](https://github.com/Yeson38/Decibel_Monitor) 的 fork，由 [Starsky16](https://github.com/Starsky16) 维护（保留原作者署名）。这是本 fork 的首次发布。

## 相对上游 master 的变化

- 麦克风采样与计算核心重构：抽取共享采样服务、常驻静默捕获流，设备枚举与一次性采样分离
- 组件与设置重构：设置收敛为「测量全局 / 显示随组件 / 提醒在通知」三处，新增插件设置页，校准上收为全局设置
- 阈值提醒：新增「分贝提醒」通知提供方与提醒渠道
- 判定源框架：①超过阈值自动提醒；②超过阈值开筛选窗口，窗口内按下确认热键才提醒（需 KeyboardCapture 插件）；各判定源带独立冷却
- 提醒状态点：组件上以形状与呼吸表达当前状态，数字颜色随判据变化并带滞回
- 时段闸门：课间休息与每节课开头若干分钟内抑制提醒，且抑制期间不占用冷却
- 提醒正文时长可在通知提供方处配置，冷却时长下限提升至 60 秒

## 安装

1. 下载下方的 `Decibel_Monitor.cipx`
2. 在 ClassIsland 的插件管理中导入该文件
3. 重启 ClassIsland

## 可选依赖

[KeyboardCapture](https://github.com/Starsky16/KeyboardCapture)：仅「热键确认」判定源需要，未安装不影响其余功能。

## 校验

下载后请核对下方 MD5 与文件是否一致。