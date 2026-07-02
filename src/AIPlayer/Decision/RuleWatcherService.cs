// <copyright file="RuleWatcherService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// 规则文件热更新监控服务。基于 FileSystemWatcher 的 JSON 规则文件自动重载。
/// 监控 Decision/scripts-rules.json 的变更，自动调用 RuleEngine.LoadFromJson 热重载。
/// </summary>
public sealed class RuleWatcherService : IDisposable
{
    private readonly string _rulesFilePath;
    private readonly Func<string, int> _reloadAction;
    private readonly ILogger _logger;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounceTimer;
    private bool _pendingReload;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="RuleWatcherService"/> class.
    /// </summary>
    /// <param name="rulesFilePath">规则 JSON 文件路径。</param>
    /// <param name="reloadAction">接收 JSON 字符串，返回加载的规则数量。通常为 RuleEngine.LoadFromJson。</param>
    /// <param name="logger">日志。</param>
    public RuleWatcherService(string rulesFilePath, Func<string, int> reloadAction, ILogger logger)
    {
        this._rulesFilePath = Path.GetFullPath(rulesFilePath);
        this._reloadAction = reloadAction;
        this._logger = logger;

        var dir = Path.GetDirectoryName(this._rulesFilePath);
        var fileName = Path.GetFileName(this._rulesFilePath);

        if (Directory.Exists(dir))
        {
            this._watcher = new FileSystemWatcher(dir!, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            this._watcher.Changed += this.OnFileChanged;
        }
        else
        {
            this._logger.LogWarning("[RuleWatcher] 监控目录不存在: {Dir}", dir);
            this._watcher = null!;
        }

        this._debounceTimer = new Timer(this.DebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        this._logger.LogInformation("[RuleWatcher] 启动监控: {Path}", this._rulesFilePath);
    }

    /// <summary>加载规则文件并返回规则数（同步调用，用于启动时首次加载）。</summary>
    public int LoadFromFile()
    {
        if (!File.Exists(this._rulesFilePath))
        {
            this._logger.LogWarning("[RuleWatcher] 规则文件不存在: {Path}", this._rulesFilePath);
            return 0;
        }

        var json = File.ReadAllText(this._rulesFilePath);
        var count = this._reloadAction(json);
        this._logger.LogInformation("[RuleWatcher] 加载 {Count} 条规则", count);
        return count;
    }

    /// <summary>触发手动重载（供 API 调用）。</summary>
    public bool Reload()
    {
        this.ProcessChange();
        return true;
    }

    public void Dispose()
    {
        if (this._disposed) return;
        this._disposed = true;

        if (this._watcher is not null)
        {
            this._watcher.EnableRaisingEvents = false;
            this._watcher.Changed -= this.OnFileChanged;
            this._watcher.Dispose();
        }

        this._debounceTimer.Dispose();
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (this._lock)
        {
            this._pendingReload = true;
        }

        // 500ms 防抖
        this._debounceTimer.Change(500, Timeout.Infinite);
    }

    private void DebounceElapsed(object? state)
    {
        bool shouldReload;
        lock (this._lock)
        {
            shouldReload = this._pendingReload;
            this._pendingReload = false;
        }

        if (shouldReload)
        {
            this.ProcessChange();
        }
    }

    private void ProcessChange()
    {
        try
        {
            // 等文件写完（FileSystemWatcher 可能在写入中途触发）
            System.Threading.Thread.Sleep(200);

            if (!File.Exists(this._rulesFilePath))
            {
                return;
            }

            var json = File.ReadAllText(this._rulesFilePath);
            var count = this._reloadAction(json);
            this._logger.LogInformation("[RuleWatcher] 热重载 {Count} 条规则", count);
        }
        catch (JsonException ex)
        {
            this._logger.LogWarning(ex, "[RuleWatcher] JSON 解析失败，保留旧规则");
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "[RuleWatcher] 重载失败");
        }
    }
}
