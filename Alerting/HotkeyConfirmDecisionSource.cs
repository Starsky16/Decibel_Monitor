using System;

namespace Decibel_Monitor.Alerting;

/// <summary>
/// 判定源②：到阈值开筛选窗口，窗口内命中指定热键才提醒（无输入不提醒）。
/// </summary>
/// <remarks>
/// 判定流程：
/// <list type="number">
/// <item>窗口未开启时：平均分贝超过阈值且自身冷却已过 → <strong>打开筛选窗口</strong>（窗口期内暂不提醒）。</item>
/// <item>窗口开启期间：外部注入按键事件（<see cref="HandleKeyPress"/>）；命中配置热键 → 置为"已确认"。</item>
/// <item>后续采样判定：窗口开启期间已确认 → 触发提醒并关闭窗口、进入冷却（不要求消费该确认的那一拍仍在窗口内，
/// 否则落在窗口最后一个采样拍里的按键会被当成超时丢掉）。</item>
/// <item>窗口开启但超时未确认 → 不提醒，关闭窗口<strong>并同样进入冷却</strong>（视为"已经给过一次机会"）；
/// 冷却结束后若仍超阈值才会再次开窗。</item>
/// <item>时段闸门抑制期间（<see cref="CancelWindow"/>）→ 窗口连同其中已命中的确认一起作废，
/// <strong>不写冷却</strong>；闸门解除后若仍超阈值，重新开一个完整时长的窗口。</item>
/// <item>应急强制通道（<see cref="ForceAlertEnabled"/>）→ 不看阈值、不看窗口、不管是否在冷却，
/// 1 秒内连按 <see cref="ForceAlertPressCount"/> 次确认热键即挂起强制提醒请求，
/// 由运行时服务在 <see cref="TryConsumeForceRequest"/> 中兑现。</item>
/// </list>
/// 热键输入由外部注入（便于单元测试），本类只做纯逻辑判定，不接触键盘钩子。
/// </remarks>
public sealed class HotkeyConfirmDecisionSource : IAlertDecisionSource
{
    /// <summary>判定源标识（用于仲裁模块的优先级排序与配置映射）。</summary>
    public const string SourceId = "hotkey-confirm";

    /// <summary>应急强制通道中相邻两次按键的最大间隔，超过即重新计数。</summary>
    private static readonly TimeSpan ForcePressIntervalLimit = TimeSpan.FromSeconds(1);

    private DateTime _windowOpenUntilUtc = DateTime.MinValue;
    private DateTime _nextAlertTimeUtc = DateTime.MinValue;
    private bool _confirmed;
    private int _forcePressCount;
    private DateTime _lastForcePressUtc = DateTime.MinValue;
    private bool _forceRequested;
    private readonly SustainedDurationTracker _sustainTracker = new();

    /// <param name="threshold">开启筛选窗口的分贝阈值（显示刻度 0..150）。</param>
    /// <param name="hotkey">筛选窗口内要求命中的热键。</param>
    /// <param name="windowTimeout">筛选窗口的开启时长；为 null 时使用默认 10 秒。</param>
    /// <param name="cooldown">提醒冷却时间；为 null 时无冷却。</param>
    public HotkeyConfirmDecisionSource(
        double threshold = 120.0,
        HotkeyDefinition hotkey = default,
        TimeSpan? windowTimeout = null,
        TimeSpan? cooldown = null)
    {
        Threshold = threshold;
        Hotkey = hotkey;
        WindowTimeout = windowTimeout ?? TimeSpan.FromSeconds(10);
        Cooldown = cooldown ?? TimeSpan.Zero;
    }

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public string DisplayName => "热键确认";

    /// <inheritdoc />
    public string Description => "超过阈值时开启筛选窗口，窗口内按下热键才提醒。";

    /// <inheritdoc />
    public bool IsEnabled { get; set; }

    /// <summary>开启筛选窗口的分贝阈值（显示刻度 0..150）。</summary>
    public double Threshold { get; set; }

    /// <summary>筛选窗口内要求命中的热键。未配置时为无效热键，窗口内永远无法命中。</summary>
    public HotkeyDefinition Hotkey { get; set; }

    /// <summary>筛选窗口的开启时长。</summary>
    public TimeSpan WindowTimeout { get; set; }

    /// <inheritdoc />
    public TimeSpan Cooldown { get; set; }

    /// <summary>
    /// 是否启用"应急强制提醒"：短时间内连按 <see cref="ForceAlertPressCount"/> 次确认热键即立即提醒，
    /// <strong>无视阈值、冷却期与时段闸门</strong>。默认关闭。
    /// </summary>
    public bool ForceAlertEnabled { get; set; }

    /// <summary>应急强制提醒所需的连按次数（相邻两按间隔不超过 1 秒），默认 2。</summary>
    public int ForceAlertPressCount { get; set; } = 2;

    /// <summary>
    /// 连续超阈时长要求：平均分贝需连续超过阈值至少该时长才开启筛选窗口。
    /// <see cref="TimeSpan.Zero"/>（默认）表示不做持续性要求，只看平均值。
    /// </summary>
    public TimeSpan SustainThreshold { get; set; }

    /// <summary>当前筛选窗口是否开启（供组件显示提醒状态）。</summary>
    public bool IsWindowOpen { get; private set; }

    /// <summary>
    /// 筛选窗口的截止时间（UTC）。窗口未开启时为 <see cref="DateTime.MinValue"/>
    /// （供运行时服务计算剩余时间与呼吸时机）。
    /// </summary>
    public DateTime WindowOpenUntilUtc => IsWindowOpen ? _windowOpenUntilUtc : DateTime.MinValue;

    /// <summary>当前是否处于"超过阈值"状态（供组件显示提醒状态）。</summary>
    public bool IsTriggerActive { get; private set; }

    /// <summary>下一次允许提醒的时间（UTC）。</summary>
    public DateTime NextAlertTimeUtc => _nextAlertTimeUtc;

    /// <inheritdoc />
    public bool IsCoolingDown(DateTime nowUtc) =>
        IsEnabled && _nextAlertTimeUtc != DateTime.MinValue && nowUtc < _nextAlertTimeUtc;

    /// <summary>
    /// 外部注入一次按键事件（后台钩子或测试直接调用）。
    /// 命中配置热键时：筛选窗口开启期间置为"已确认"；窗口未开启（或已超时）且启用了应急强制通道时计入连按。
    /// </summary>
    /// <returns>本次按键是否命中并确认了当前窗口。</returns>
    public bool HandleKeyPress(string? keyName, HotkeyModifiers modifiers, DateTime nowUtc)
    {
        if (!IsEnabled) return false;

        // 键名或修饰键与配置不一致的按键一律不计——应急通道要求连按的正是"确认热键"本身
        if (!Hotkey.Matches(keyName, modifiers)) return false;

        if (IsWindowOpen && nowUtc <= _windowOpenUntilUtc)
        {
            _confirmed = true;
            // 已走正常确认通道，清掉应急连按的残留计数，避免之后单独一次按键就凑满次数
            ResetForcePresses();
            return true;
        }

        // 窗口未开或已超时：计入应急连按（未启用强制通道时不计）
        if (ForceAlertEnabled) RecordForcePress(nowUtc);
        return false;
    }

    /// <summary>
    /// 读取并清除"应急强制提醒"请求。
    /// </summary>
    /// <remarks>
    /// 返回 true 时已一并写入冷却并关闭筛选窗口：前者对应"无视冷却只指触发不被冷却拦住，触发后仍重置冷却
    /// 计时"的约定，后者避免紧接着的判定再经正常确认分支发出重复提醒。
    /// </remarks>
    /// <param name="nowUtc">当前时间（UTC）。</param>
    /// <returns>是否存在待兑现的强制提醒请求。</returns>
    public bool TryConsumeForceRequest(DateTime nowUtc)
    {
        if (!_forceRequested) return false;

        _forceRequested = false;
        ResetForcePresses();
        CancelWindow();
        _nextAlertTimeUtc = nowUtc + EffectiveCooldown();
        return true;
    }

    /// <summary>记一次应急连按；距上次不超过 1 秒则累加，否则从 1 重新开始；达到所需次数即挂起强制提醒请求。</summary>
    private void RecordForcePress(DateTime nowUtc)
    {
        var continued = _lastForcePressUtc != DateTime.MinValue
                        && nowUtc - _lastForcePressUtc <= ForcePressIntervalLimit;

        _forcePressCount = continued ? _forcePressCount + 1 : 1;
        _lastForcePressUtc = nowUtc;

        if (_forcePressCount >= Math.Max(1, ForceAlertPressCount))
        {
            _forceRequested = true;
            _forcePressCount = 0;
        }
    }

    private void ResetForcePresses()
    {
        _forcePressCount = 0;
        _lastForcePressUtc = DateTime.MinValue;
    }

    /// <summary>
    /// 作废当前筛选窗口（含其中已命中的确认），<strong>不写入冷却</strong>。未开窗时为空操作。
    /// </summary>
    /// <remarks>
    /// 供时段闸门抑制期间调用。闸门期间不发通知，且状态点也不显示三角（用户没有被要求按键），
    /// 因此此时开着的窗口（闸门开始前遗留的）必须作废，否则闸门解除后会用一段早已过期的确认去发提醒
    /// ——表现为"课间按的键，上课后才响"。
    /// 与"抑制期间不推进判定源"的约定一致：不写冷却，闸门结束后仍超阈值即重新开窗。
    /// </remarks>
    public void CancelWindow()
    {
        IsWindowOpen = false;
        _confirmed = false;
        _windowOpenUntilUtc = DateTime.MinValue;
    }

    /// <inheritdoc />
    public AlertDecision Decide(AlertContext context, DateTime nowUtc)
    {
        if (!IsEnabled)
        {
            // 未启用时清空内部状态
            Reset();
            return default;
        }

        var isActive = context.AverageDecibel > Threshold;
        IsTriggerActive = isActive;

        // 连续超阈要求（与平均窗口正交）只决定"是否开窗"；是否超阈值的显示仍用原始判断，
        // 否则状态点的数字与形状会自相矛盾。
        var sustained = _sustainTracker.Update(isActive, nowUtc, SustainThreshold);

        var windowOpen = IsWindowOpen;

        // 窗口未开 && 超阈值 && 冷却已过 → 开窗（不立即提醒，等待热键确认）
        if (!windowOpen)
        {
            if (sustained && nowUtc >= _nextAlertTimeUtc && Hotkey.IsValid)
            {
                IsWindowOpen = true;
                _confirmed = false;
                _windowOpenUntilUtc = nowUtc + (WindowTimeout > TimeSpan.Zero ? WindowTimeout : TimeSpan.Zero);
            }

            return new AlertDecision(false, isActive);
        }

        // 已命中热键 → 提醒。本分支必须先于超时判定：
        // HandleKeyPress 接受按键时已经校验过"未超时"，因此 _confirmed 为真即代表按键确实落在窗口内。
        // 若先判超时，落在窗口最后一个采样拍里的确认会被当成"超时未确认"清掉：
        // 用户明明按了键却既不提醒、又白进一次冷却。
        if (_confirmed)
        {
            IsWindowOpen = false;
            _confirmed = false;
            _nextAlertTimeUtc = nowUtc + EffectiveCooldown();
            return new AlertDecision(true, isActive);
        }

        // 窗口已开但超时未确认 → 关窗并进入冷却。
        // 不按键同样算一次"已经给过一次机会"：若只关窗不写冷却，下一拍就会立即重新开窗，
        // 低阈值用法下三角会长期常亮，并永远看不到冷却态与正常态。
        if (nowUtc > _windowOpenUntilUtc)
        {
            IsWindowOpen = false;
            _confirmed = false;
            _nextAlertTimeUtc = nowUtc + EffectiveCooldown();
            return new AlertDecision(false, isActive);
        }

        return new AlertDecision(false, isActive);
    }

    /// <summary>生效冷却时间：负值按无冷却处理。</summary>
    private TimeSpan EffectiveCooldown() => Cooldown > TimeSpan.Zero ? Cooldown : TimeSpan.Zero;

    private void Reset()
    {
        IsWindowOpen = false;
        IsTriggerActive = false;
        _confirmed = false;
        _windowOpenUntilUtc = DateTime.MinValue;
        _nextAlertTimeUtc = DateTime.MinValue;
        _forceRequested = false;
        ResetForcePresses();
        _sustainTracker.Reset();
    }
}