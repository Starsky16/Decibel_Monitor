using System;

namespace Decibel_Monitor.Alerting;

/// <summary>
/// 一次判定的输入上下文（由运行时编排服务在每个采样周期构造）。
/// </summary>
/// <param name="CurrentDecibel">当前瞬时映射分贝值（显示刻度 0..150）。</param>
/// <param name="AverageDecibel">滑动窗口平均映射分贝值（显示刻度 0..150），窗口内无样本时等于当前值。</param>
public readonly record struct AlertContext(double CurrentDecibel, double AverageDecibel);

/// <summary>
/// 单个判定源的判定结果。
/// </summary>
/// <param name="ShouldAlert">本次判定是否需要发出提醒。</param>
/// <param name="IsTriggerActive">当前是否处于"触发条件成立"状态（供组件显示提醒状态）。</param>
public readonly record struct AlertDecision(bool ShouldAlert, bool IsTriggerActive);

/// <summary>
/// 判定源：一种"判断方式"。
/// </summary>
/// <remarks>
/// 设计约束：
/// <list type="bullet">
/// <item><description>判定源只负责"判断何时该提醒"，<strong>不接触通知</strong>——通知由
/// <see cref="AlertDecisionCoordinator"/> 统一发出。</description></item>
/// <item><description>判定源自带状态（冷却、筛选窗口等），实现需保证 <see cref="Decide"/> 可在每个采样周期被反复调用。</description></item>
/// <item><description>判定源设置项只有：启用开关、冷却时间、专属参数；<strong>不含优先级</strong>（优先级归仲裁模块）。</description></item>
/// </list>
/// </remarks>
public interface IAlertDecisionSource
{
    /// <summary>判定源标识（用于优先级排序与配置映射，须稳定且唯一）。</summary>
    string Id { get; }

    /// <summary>判定源显示名（设置页展示）。</summary>
    string DisplayName { get; }

    /// <summary>判定源简要说明（设置页优先级列表展示）。</summary>
    string Description { get; }

    /// <summary>是否启用该判定源。</summary>
    bool IsEnabled { get; }

    /// <summary>该判定源自身的提醒冷却时间（两次提醒之间的最小间隔）。</summary>
    TimeSpan Cooldown { get; }

    /// <summary>
    /// 当前是否处于冷却期（上次提醒后尚未超过 <see cref="Cooldown"/>，不具备再次提醒的资格）。
    /// 未启用的判定源恒为 false。
    /// </summary>
    /// <param name="nowUtc">当前时间（UTC）。</param>
    bool IsCoolingDown(DateTime nowUtc);

    /// <summary>
    /// 探测本拍是否满足提醒条件（供仲裁模块按优先级选出唯一目标源）。
    /// <strong>不得改变任何内部状态</strong>——仲裁要求所有判定源在同一份"谁都没推进过"的快照上被比较；
    /// 返回值须与"此刻调用 <see cref="Decide"/> 是否会返回 ShouldAlert"一致。
    /// </summary>
    /// <param name="context">当前判定上下文。</param>
    /// <param name="nowUtc">当前时间（UTC）。</param>
    bool WouldAlert(AlertContext context, DateTime nowUtc);

    /// <summary>
    /// 推进本拍状态但<strong>不发出提醒</strong>（供本拍未被选为目标源的判定源调用）。
    /// 内部状态推进与 <see cref="Decide"/> 完全一致（含条件满足时照常写入冷却、关闭筛选窗口），
    /// 只是提醒被抑制：它保证同一拍最多产生一条通知，且被抑制的判定源不会在下一拍补发第二条
    /// （其本次条件按"已给过一次机会"落地，而不是顺延兑现）。
    /// </summary>
    /// <param name="context">当前判定上下文。</param>
    /// <param name="nowUtc">当前时间（UTC）。</param>
    AlertDecision Observe(AlertContext context, DateTime nowUtc);

    /// <summary>
    /// 依据当前上下文判定是否提醒。未启用时应返回"不提醒"并清空内部状态。
    /// </summary>
    /// <param name="context">当前判定上下文。</param>
    /// <param name="nowUtc">当前时间（UTC）。</param>
    AlertDecision Decide(AlertContext context, DateTime nowUtc);
}
