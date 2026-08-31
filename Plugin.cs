using System;
using System.IO;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Decibel_Monitor
{
    [PluginEntrance]
    public class Plugin : PluginBase
    {
        public override void Initialize(HostBuilderContext context, IServiceCollection services)
        {
            // 共享的麦克风峰值采样服务（单例，供组件与设置控件复用）
            services.AddSingleton<Services.AudioPeakMeter>();

            // 一次性注册组件与其设置控件（不要重复注册）
            services.AddComponent<Controls.Components.DecibelComponent, Controls.ComponentSettings.DecibelComponentSettingsControl>();

            // 注册分贝提醒通知提供方（超过阈值时通过 ClassIsland 通知系统提醒）
            services.AddNotificationProvider<Services.DecibelNotificationProvider>();

            // CI 集成测试标记：仅当显式开启环境变量时写入，
            // 供 GitHub Actions 在启动 ClassIsland 后确认插件已成功加载（Initialize 完整执行、注册无异常）。
            if (Environment.GetEnvironmentVariable("DECIBEL_MONITOR_CI_MARKER") == "1")
            {
                try
                {
                    Directory.CreateDirectory(PluginConfigFolder);
                    File.WriteAllText(
                        Path.Combine(PluginConfigFolder, "loaded.marker"),
                        $"loaded at {DateTime.UtcNow:O}");
                }
                catch
                {
                    // 标记写入失败不应影响插件正常初始化
                }
            }
        }
    }
}