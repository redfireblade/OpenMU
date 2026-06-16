// <copyright file="EventWatcherService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;

/// <summary>
/// 事件活动广播器 — 群体级服务，监听 MiniGameDefinition 的开放/关闭状态，定期广播提醒。
///
/// 不再依赖单个 AI 角色的生命周期，由 AiPlayerManager 创建并启动独立 Timer。
///
/// 工作原理：
///   1. 每隔 ~10 秒扫描一次所有 MiniGameDefinition
///   2. 通过 IPeriodicMiniGameStartPlugIn 推断当前 PeriodicTaskState
///   3. 状态迁移时通过 IEventBroadcaster 广播到所有活跃 AI
///
/// 状态迁移: NotStarted → Prepared → Started → NotStarted
///   EventOpenEvent:    Prepared 时发布（活动入场窗口打开）
///   EventReminderEvent: Started 时首次发布（MinutesLeft=0），之后每 60 秒一次 (-1)
///   EventClosedEvent:  回到 NotStarted 时发布
///
/// Prepared 阶段过短（PreStartMessageDelay=0）时自动合并为 NotStarted→Started 双事件。
/// </summary>
public sealed class EventWatcherService : IDisposable
{
    private readonly IGameContext _gameContext;
    private readonly ILogger _logger;
    private readonly IEventBroadcaster _broadcaster;
    private readonly Timer _timer;

    /// <summary>上一次看到的各事件状态。</summary>
    private readonly Dictionary<(MiniGameType, int), PeriodicTaskState> _knownStates = new();

    /// <summary>各事件上次提醒时间。</summary>
    private readonly Dictionary<(MiniGameType, int), DateTime> _lastReminderTime = new();

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Prepared 阶段推断阈值：GetDurationUntilNextStartAsync 返回正值且 ≤ 5 分钟 → Prepared。
    /// 覆盖所有已知事件的 PreStartMessageDelay（0s ~ 3min）。
    /// </summary>
    private static readonly TimeSpan PreparedThreshold = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Initializes a new instance of the <see cref="EventWatcherService"/> class.
    /// </summary>
    /// <param name="gameContext">The game context for configuration access.</param>
    /// <param name="broadcaster">The event broadcaster to notify AI players.</param>
    /// <param name="logger">The logger.</param>
    public EventWatcherService(IGameContext gameContext, IEventBroadcaster broadcaster, ILogger logger)
    {
        this._gameContext = gameContext;
        this._broadcaster = broadcaster;
        this._logger = logger;

        // 启动独立 Timer，不再依赖 Heartbeat 驱动
        this._timer = new Timer(
            async _ => await this.ScanAsync().ConfigureAwait(false),
            null,
            TimeSpan.Zero,
            ScanInterval);
    }

    /// <summary>
    /// 每 Tick 调用，检查事件状态变化并广播。
    /// 受 ScanInterval 节流限制（~10 秒一次）。
    /// </summary>
    public async ValueTask CheckEventsAsync()
    {
        await this.ScanAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this._timer.Dispose();
    }

    /// <summary>
    /// 扫描并广播事件状态变化。
    /// </summary>
    private async Task ScanAsync()
    {
        try
        {
            await this.ScanCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "[EventWatcher] 扫描异常");
        }
    }

    private async Task ScanCoreAsync()
    {
        var now = DateTime.UtcNow;
        var config = this._gameContext.Configuration;
        if (config?.MiniGameDefinitions is null)
        {
            return;
        }

        foreach (var mg in config.MiniGameDefinitions)
        {
            var key = (mg.Type, (int)mg.GameLevel);
            var currentState = await this.GetCurrentMiniGameStateAsync(mg).ConfigureAwait(false);
            var nameStr = mg.Name.ToString() ?? mg.Type.ToString();

            // 首次检测到此事件
            if (!this._knownStates.TryGetValue(key, out var prevState))
            {
                this._knownStates[key] = currentState;

                if (currentState == PeriodicTaskState.Started)
                {
                    // 启动时已开放 → 发布提醒以同步
                    this._broadcaster.OnEventReminder(mg.Type, (int)mg.GameLevel, nameStr, -1);
                }
                else if (currentState == PeriodicTaskState.Prepared)
                {
                    // 启动时已在准备中 → 发布开放事件
                    this._broadcaster.OnEventOpen(mg.Type, (int)mg.GameLevel, nameStr, mg.EntranceFee);
                }

                continue;
            }

            // === 状态变化检测 ===
            if (prevState != currentState)
            {
                this._logger.LogDebug(
                    "[EventWatcher] 状态变化: {Name} {Prev} → {Curr}",
                    nameStr,
                    prevState,
                    currentState);

                if (prevState == PeriodicTaskState.NotStarted && currentState == PeriodicTaskState.Prepared)
                {
                    // 入场窗口打开
                    this._broadcaster.OnEventOpen(mg.Type, (int)mg.GameLevel, nameStr, mg.EntranceFee);
                    this._logger.LogInformation("[EventWatcher] 🔔 入场窗口打开: {Name} Lv.{Level}", nameStr, mg.GameLevel);
                }
                else if (prevState == PeriodicTaskState.Prepared && currentState == PeriodicTaskState.Started)
                {
                    // 正式开放（入场变为可进入）
                    this._broadcaster.OnEventReminder(mg.Type, (int)mg.GameLevel, nameStr, 0);
                    this._logger.LogInformation("[EventWatcher] ⏰ 活动已开放: {Name} Lv.{Level}", nameStr, mg.GameLevel);
                }
                else if (prevState == PeriodicTaskState.NotStarted && currentState == PeriodicTaskState.Started)
                {
                    // 跳过 Prepared 直接开放（PreStartMessageDelay=0 的情况）
                    this._broadcaster.OnEventOpen(mg.Type, (int)mg.GameLevel, nameStr, mg.EntranceFee);
                    this._broadcaster.OnEventReminder(mg.Type, (int)mg.GameLevel, nameStr, 0);
                    this._logger.LogInformation(
                        "[EventWatcher] 🔔⏰ 活动快速开放(跳过Prepared): {Name} Lv.{Level}",
                        nameStr,
                        mg.GameLevel);
                }
                else if (prevState == PeriodicTaskState.Started && currentState == PeriodicTaskState.NotStarted)
                {
                    // 活动结束
                    this._broadcaster.OnEventClosed(mg.Type, (int)mg.GameLevel, nameStr);
                    this._lastReminderTime.Remove(key);
                    this._logger.LogInformation("[EventWatcher] 🔴 活动已结束: {Name} Lv.{Level}", nameStr, mg.GameLevel);
                }

                this._knownStates[key] = currentState;
            }

            // 开放期间定期提醒（每 ReminderInterval 一次）
            if (currentState == PeriodicTaskState.Started)
            {
                if (!this._lastReminderTime.TryGetValue(key, out var lastReminder) ||
                    now - lastReminder >= ReminderInterval)
                {
                    this._lastReminderTime[key] = now;
                    this._broadcaster.OnEventReminder(mg.Type, (int)mg.GameLevel, nameStr, -1);
                }
            }
        }
    }

    /// <summary>
    /// 通过插件接口推断 MiniGameDefinition 的当前 <see cref="PeriodicTaskState"/>。
    /// 由于 AIPlayer 无法直接访问服务端 PeriodicTaskGameServerState，需要间接推断。
    /// </summary>
    /// <param name="mg">迷你游戏定义。</param>
    /// <returns>推断的 PeriodicTaskState。</returns>
    private async ValueTask<PeriodicTaskState> GetCurrentMiniGameStateAsync(MiniGameDefinition mg)
    {
        var plugIn = this._gameContext.PlugInManager
            .GetStrategy<MiniGameType, IPeriodicMiniGameStartPlugIn>(mg.Type);
        if (plugIn is null)
        {
            return PeriodicTaskState.NotStarted;
        }

        // 1) MiniGameContext 存在 → 事件已启动（Started）
        //    GetMiniGameContextAsync 内部只在 State == Started 时返回非 null
        var miniGame = await plugIn.GetMiniGameContextAsync(this._gameContext, mg)
            .ConfigureAwait(false);
        if (miniGame is not null)
        {
            return PeriodicTaskState.Started;
        }

        // 2) 查询距离下次开放的时间
        var duration = await plugIn.GetDurationUntilNextStartAsync(this._gameContext, mg)
            .ConfigureAwait(false);

        // TimeSpan.Zero → 当前开放
        if (duration == TimeSpan.Zero)
        {
            return PeriodicTaskState.Started;
        }

        // 3) 正值且 ≤ 5 分钟 → Prepared（PreStartMessageDelay 期间）
        //    超过 5 分钟 → 等待下次排程（NotStarted）
        if (duration.HasValue && duration.Value > TimeSpan.Zero && duration.Value <= PreparedThreshold)
        {
            return PeriodicTaskState.Prepared;
        }

        return PeriodicTaskState.NotStarted;
    }
}
