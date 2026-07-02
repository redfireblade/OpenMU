// <copyright file="HealthCheckService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using System.Diagnostics;
using Microsoft.Extensions.Logging;

/// <summary>
/// 健康检查服务 — 每心跳检查内存/CPU/后台任务状态。
/// 确保 AI 角色运行在稳定的内存环境中，防止泄漏积累。
/// </summary>
public sealed class HealthCheckService
{
    private readonly ILogger _logger;
    private readonly Process _currentProcess;
    private DateTime _lastGcTime = DateTime.MinValue;
    private int _sampleCounter;

    /// <summary>最大内存阈值（MB），超过触发警告。</summary>
    public const long MemoryWarningMB = 500;

    /// <summary>临界内存阈值（MB），超过触发强制 GC。</summary>
    public const long MemoryCriticalMB = 800;

    /// <summary>最大允许后台任务数，超过需清理。</summary>
    public const int MaxBackgroundTasks = 20;

    /// <summary>GC 最小间隔（秒），防止频繁 GC 影响性能。</summary>
    private static readonly TimeSpan GcMinInterval = TimeSpan.FromSeconds(30);

    public HealthCheckService(ILogger logger)
    {
        this._logger = logger;
        this._currentProcess = Process.GetCurrentProcess();
    }

    /// <summary>
    /// 每心跳检查一次健康状态。
    /// </summary>
    /// <param name="taskManager">后台任务管理器（用于清理残留任务）。</param>
    /// <returns>本次检查报告。</returns>
    public HealthReport Check(AITaskManager taskManager)
    {
        this._sampleCounter++;
        var report = new HealthReport();

        // 1) 内存检查
        var memBytes = GC.GetTotalMemory(false);
        report.MemoryMB = memBytes / (1024L * 1024L);
        report.MemoryWarning = report.MemoryMB > MemoryWarningMB;
        report.MemoryCritical = report.MemoryMB > MemoryCriticalMB;

        if (report.MemoryCritical && (DateTime.UtcNow - this._lastGcTime) > GcMinInterval)
        {
            this._logger.LogWarning("[HealthCheck] ⚠️ 内存 {Mem}MB 超过临界值 {Crit}MB，触发强制 GC",
                report.MemoryMB, MemoryCriticalMB);
            GC.Collect(2, GCCollectionMode.Forced);
            GC.WaitForPendingFinalizers();
            this._lastGcTime = DateTime.UtcNow;
            report.GcTriggered = true;
        }

        // 2) 残留任务清理
        var stale = taskManager.CleanupStaleTasks();
        report.StaleTasksCleaned = stale;
        if (stale > 0)
        {
            this._logger.LogWarning("[HealthCheck] 清理 {Count} 个残留后台任务", stale);
        }

        // 3) CPU 采样（每 10 tick ≈ 4 秒）
        if (this._sampleCounter % 10 == 0)
        {
            try
            {
                this._currentProcess.Refresh();
                report.CpuPercent = Math.Round(this._currentProcess.TotalProcessorTime.TotalMilliseconds, 1);
            }
            catch
            {
                // Process 信息不可用时静默跳过
            }
        }

        // 4) 后台任务数
        report.ActiveBackgroundTasks = taskManager.ActiveTaskCount;
        report.TotalTasks = taskManager.TotalTaskCount;

        // 5) 总体健康判定
        report.IsHealthy = !report.MemoryCritical && report.ActiveBackgroundTasks < MaxBackgroundTasks;

        if (!report.IsHealthy && this._sampleCounter % 5 == 0)
        {
            this._logger.LogWarning(
                "[HealthCheck] ⚠️ 健康警告: Mem={Mem}MB Tasks={Active}/{Total} GC={Gc} Cleaned={Cleaned}",
                report.MemoryMB, report.ActiveBackgroundTasks, report.TotalTasks,
                report.GcTriggered, report.StaleTasksCleaned);
        }

        return report;
    }

    /// <summary>
    /// 仅记录健康状态到日志（debug 级），不触发任何操作。
    /// </summary>
    public void LogStatus(AITaskManager taskManager)
    {
        if (this._sampleCounter % 30 == 0)
        {
            this._logger.LogDebug(
                "[HealthCheck] Mem={Mem}MB Tasks={Active}/{Total}",
                GC.GetTotalMemory(false) / (1024L * 1024L),
                taskManager.ActiveTaskCount,
                taskManager.TotalTaskCount);
        }
    }
}

/// <summary>一次健康检查的结果报告。</summary>
public record HealthReport
{
    public long MemoryMB { get; set; }
    public bool MemoryWarning { get; set; }
    public bool MemoryCritical { get; set; }
    public bool GcTriggered { get; set; }
    public int StaleTasksCleaned { get; set; }
    public double CpuPercent { get; set; }
    public int ActiveBackgroundTasks { get; set; }
    public int TotalTasks { get; set; }
    public bool IsHealthy { get; set; }
}
