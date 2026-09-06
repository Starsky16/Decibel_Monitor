using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using Decibel_Monitor.Models;

namespace Decibel_Monitor.Services;

/// <summary>
/// 插件全局设置服务：在插件配置目录中以 JSON 保存
/// <see cref="DecibelMonitorGlobalSettings"/>，并在修改后自动保存。
/// </summary>
public sealed class DecibelMonitorSettingsService : IDisposable
{
    private readonly object _saveLock = new();
    private readonly string? _filePath;
    private Timer? _saveTimer;
    private bool _disposed;

    /// <summary>
    /// 当前全局设置（属性变化自动触发延迟保存）。
    /// </summary>
    public DecibelMonitorGlobalSettings Settings { get; } = new();

    /// <param name="configFolder">插件配置目录（<c>PluginBase.PluginConfigFolder</c>）；为空时仅内存保存。</param>
    public DecibelMonitorSettingsService(string? configFolder)
    {
        if (!string.IsNullOrWhiteSpace(configFolder))
        {
            _filePath = Path.Combine(configFolder, "plugin-settings.json");
            Load();
        }

        Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;

        // 去抖后写盘，避免滑块拖动等高频变更频繁落盘
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => Save(), null, 400, Timeout.Infinite);
    }

    private void Load()
    {
        if (_filePath is null || !File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<DecibelMonitorGlobalSettings>(json);
            if (loaded is not null)
            {
                Settings.FallbackCaptureMs = loaded.FallbackCaptureMs;
                Settings.SignalThreshold = loaded.SignalThreshold;
            }
        }
        catch
        {
            // 读取失败按默认值继续
        }
    }

    /// <summary>
    /// 立即将全局设置写入磁盘。
    /// </summary>
    public void Save()
    {
        if (_filePath is null || _disposed) return;
        try
        {
            lock (_saveLock)
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(Settings));
            }
        }
        catch
        {
            // 写盘失败不应影响运行
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Settings.PropertyChanged -= OnSettingsPropertyChanged;
        _saveTimer?.Dispose();
        Save();
    }
}
