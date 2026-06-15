// <copyright file="SystemEventScanner.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// 系统事件扫描器 — Layer 3。
/// 遍历 GameConfiguration.MiniGameDefinitions。
/// 等级过滤 -> 检查活动状态(Prepared/Started) -> 生成 event_Type_Level 任务。
/// 跳过 NotStarted 事件，只在实际开放窗口内注入事件任务。
/// 与 EventWatcherService 协作：EventWatcher 是外部状态变更发布源，
/// EventScanner 是每 tick 决策层同步注入。
/// </summary>
public sealed class SystemEventScanner
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly BoardState _boardState;
    private readonly ILogger _logger;

    // 与 EventWatcherService 保持一致的阈值
    private static readonly TimeSpan PreparedThreshold = TimeSpan.FromMinutes(5);

    public SystemEventScanner(AiPlayer player, IGameAdapter adapter, BoardState boardState, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._boardState = boardState;
        this._logger = logger;
    }

    /// <summary>
    /// 扫描可参加事件。只注入状态为 Prepared 或 Started 的事件任务。
    /// NotStarted 事件不注入（由 EventWatcherService 的 EventOpenEvent 驱动）。
    /// </summary>
    public async ValueTask<List<MissionItem>> ScanAsync()
    {
        var result = new List<MissionItem>();
        var config = this._player.GameContext?.Configuration;
        if (config?.MiniGameDefinitions is null) return result;

        var level = this._adapter.GetPlayerLevel();

        foreach (var mg in config.MiniGameDefinitions)
        {
            if (level < mg.MinimumCharacterLevel) continue;
            if (mg.MaximumCharacterLevel > 0 && level > mg.MaximumCharacterLevel) continue;
            if (mg.RequiresMasterClass && this._player.SelectedCharacter?.CharacterClass?.IsMasterClass != true) continue;

            var eventId = $"event_{mg.Type}_{mg.GameLevel}";

            // 检查看板: 是否已存在且处于有效状态
            var existing = this._boardState.Missions.FirstOrDefault(m => m.Id == eventId);
            if (existing is not null)
            {
                if (existing.Status is MissionStatus.Active or MissionStatus.Pending or MissionStatus.Blocked)
                    continue;
                if (existing.IsDeadTask)
                    continue;
                if (existing.Status == MissionStatus.Completed)
                    continue;
            }

            // 异步检查活动状态：跳过 NotStarted
            if (!await this.IsEventOpenAsync(mg).ConfigureAwait(false))
            {
                continue;
            }

            result.Add(new MissionItem
            {
                Id = eventId,
                Title = mg.Name.ToString() ?? $"{mg.Type} Lv.{mg.GameLevel}",
                Priority = 15,
                Type = MissionType.Quest,
                Category = QuestCategory.InstanceEvent,
                Goal = QuestGoal.Instance,
                Source = QuestSource.GameSystem,
                Module = "event_executor",
                FailureRetryable = true,
                MaxRepeatCount = -1,
            });
        }

        if (result.Count > 0)
            this._logger.LogInformation("[EventScanner] 扫描到 {Count} 个可参加事件", result.Count);

        return result;
    }

    /// <summary>
    /// 通过插件接口推断 MiniGameDefinition 是否已开放（Prepared 或 Started）。
    /// 与 EventWatcherService.GetCurrentMiniGameStateAsync 共享相同逻辑。
    /// </summary>
    private async ValueTask<bool> IsEventOpenAsync(MiniGameDefinition mg)
    {
        var plugIn = this._player.GameContext?.PlugInManager
            .GetStrategy<MiniGameType, IPeriodicMiniGameStartPlugIn>(mg.Type);
        if (plugIn is null)
        {
            // 无定时策略的事件视为始终开放
            return true;
        }

        // 1) MiniGameContext 存在 → Started
        var miniGame = await plugIn.GetMiniGameContextAsync(this._player.GameContext!, mg)
            .ConfigureAwait(false);
        if (miniGame is not null)
        {
            return true;
        }

        // 2) 查询距离下次开放的时间
        var duration = await plugIn.GetDurationUntilNextStartAsync(this._player.GameContext!, mg)
            .ConfigureAwait(false);

        if (duration == TimeSpan.Zero)
        {
            return true;
        }

        // 3) 正值且 ≤ 5 分钟 → Prepared（入场窗口阶段）
        if (duration.HasValue && duration.Value > TimeSpan.Zero && duration.Value <= PreparedThreshold)
        {
            return true;
        }

        return false;
    }
}
