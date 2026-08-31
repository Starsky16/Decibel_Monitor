using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;

namespace Decibel_Monitor.Services;

/// <summary>
/// 分贝提醒通知提供方。
/// 当分贝值超出设定阈值时，通过 ClassIsland 通知系统弹出提醒。
/// </summary>
[NotificationProviderInfo(
    "54f1b836-efa4-4755-bbac-7460f697cbb0",
    "分贝提醒",
    "\uE02A",
    "当分贝值超出设定阈值时发出提醒。"
)]
public sealed class DecibelNotificationProvider : NotificationProviderBase, IDisposable
{
    private static DecibelNotificationProvider? _instance;

    /// <summary>
    /// 当前通知提供方实例（宿主启动后由 DI 以单例方式创建）。
    /// </summary>
    public static DecibelNotificationProvider? Instance => _instance;

    public DecibelNotificationProvider()
    {
        _instance = this;
    }

    /// <summary>
    /// 发送"超过分贝阈值"提醒。
    /// </summary>
    /// <param name="text">提醒正文。</param>
    public void NotifyOverThreshold(string text)
    {
        try
        {
            var request = new NotificationRequest
            {
                MaskContent = NotificationContent.CreateSimpleTextContent(text),
            };
            ShowNotification(request);
        }
        catch
        {
            // 通知失败不应影响组件的主功能
        }
    }

    /// <summary>
    /// 释放时清理静态实例引用，避免插件重载或宿主重启后残留已失效的引用。
    /// </summary>
    public void Dispose()
    {
        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }
    }
}
