using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using Microsoft.Extensions.Hosting;

namespace Decibel_Monitor.Alarm;

/// <summary>
/// 托盘「更多选项…」下的「分贝监测」子菜单：静默本节课 / 一个午 / 今日结束、临时开启、恢复常态，
/// 并显示当前覆盖状态与剩余时间。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><strong>不能在 <c>Plugin.Initialize</c> 里加</strong>：那时 DI 尚未构建、宿主托盘菜单还不存在。
/// 就绪判据用 <c>AppBase.Current.MainWindow is not null</c>，由 1 秒节拍的计时器轮询，
/// 就绪后安装一次并判重。</description></item>
/// <item><description>宿主<strong>没有插件卸载/重载回调</strong>（启用/停用/删除插件都要重启宿主），
/// 同进程不会重复安装；仍在 <see cref="Dispose"/> 中把菜单项移除，避免万一的残留。</description></item>
/// <item><description>图标只占位在标题里：状态用「当前：…」一行文字表达（含剩余时间），
/// 不依赖原生菜单的勾选控件；文字不变时不写回，避免 Win32 菜单无谓重绘。</description></item>
/// </list>
/// </remarks>
public sealed class TrayMenuOverrideController : IHostedService, IDisposable
{
    /// <summary>就绪轮询与状态刷新的节拍（1 秒，兼顾"剩余时间"的秒级精度与菜单重绘开销）。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>托盘上「临时开启」的固定时长（分钟）；需要别的时长用自动化动作。</summary>
    private const int ForceEnableMinutes = 60;

    private readonly AlertOverrideService _overrideService;
    private readonly DispatcherTimer _timer;
    private NativeMenuItem? _rootItem;
    private NativeMenuItem? _statusItem;
    private bool _disposed;

    /// <param name="overrideService">临时静默 / 临时开启服务。</param>
    public TrayMenuOverrideController(AlertOverrideService overrideService)
    {
        _overrideService = overrideService ?? throw new ArgumentNullException(nameof(overrideService));
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += Timer_Tick;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer.Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer.Stop();
        return Task.CompletedTask;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_disposed) return;

        try
        {
            if (_rootItem is null && !TryInstallMenu()) return;
            UpdateStatusText();
        }
        catch
        {
            // 托盘菜单的任何异常都不应影响宿主与其它插件；下一拍会重试
        }
    }

    /// <summary>宿主就绪时安装托盘菜单项；未就绪返回 false（下一拍重试）。</summary>
    private bool TryInstallMenu()
    {
        if (AppBase.Current?.MainWindow is null) return false;

        var taskBarIconService = IAppHost.TryGetService<ITaskBarIconService>();
        if (taskBarIconService is null) return false;

        var subMenu = new NativeMenu();
        subMenu.Items.Add(CreateItem("静默本节课", () => _overrideService.SilenceForThisClass()));
        subMenu.Items.Add(CreateItem("静默一个午", () => _overrideService.SilenceForNoon()));
        subMenu.Items.Add(CreateItem("静默到今日结束", () => _overrideService.SilenceForToday()));
        subMenu.Items.Add(CreateItem($"临时开启 {ForceEnableMinutes} 分钟",
            () => _overrideService.ForceEnableForMinutes(ForceEnableMinutes)));
        subMenu.Items.Add(CreateItem("恢复常态", () => _overrideService.Clear()));

        subMenu.Items.Add(new NativeMenuItemSeparator());
        _statusItem = new NativeMenuItem("当前：常态") { IsEnabled = false };
        subMenu.Items.Add(_statusItem);

        _rootItem = new NativeMenuItem("分贝监测") { Menu = subMenu };
        taskBarIconService.MoreOptionsMenuItems.Add(_rootItem);
        return true;
    }

    /// <summary>把当前覆盖状态与剩余时间写进状态行（文字未变化时不写回）。</summary>
    private void UpdateStatusText()
    {
        if (_statusItem is null) return;

        var nowUtc = DateTime.UtcNow;
        var remaining = _overrideService.SilenceRemaining(nowUtc);
        string text;
        if (remaining > TimeSpan.Zero)
        {
            text = $"当前：静默中（剩余 {FormatRemaining(remaining)}）";
        }
        else if (_overrideService.IsForceEnabled(nowUtc))
        {
            text = $"当前：临时开启（剩余 {FormatRemaining(_overrideService.ForceEnableUntilUtc - nowUtc)}）";
        }
        else
        {
            text = "当前：常态";
        }

        if (!string.Equals(_statusItem.Header as string, text, StringComparison.Ordinal))
        {
            _statusItem.Header = text;
        }
    }

    private static NativeMenuItem CreateItem(string header, Action onClick)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>剩余时间文本：不足 1 小时用 mm:ss，否则用 h:mm:ss。</summary>
    private static string FormatRemaining(TimeSpan remaining) =>
        remaining.TotalHours >= 1 ? remaining.ToString(@"h\:mm\:ss") : remaining.ToString(@"m\:ss");

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        _timer.Tick -= Timer_Tick;

        if (_rootItem is not null)
        {
            try
            {
                IAppHost.TryGetService<ITaskBarIconService>()?.MoreOptionsMenuItems.Remove(_rootItem);
            }
            catch
            {
                // 宿主已在退出流程中：移除失败可忽略
            }
        }

        _rootItem = null;
        _statusItem = null;
    }
}