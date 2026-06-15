// <copyright file="DynamicMissionGenerator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using Microsoft.Extensions.Logging;

/// <summary>
/// 动态任务生成协调器。
/// 每 tick 在 SelectCurrentTask 决策前被调用。
/// 组合 SystemEventScanner + ItemNeedAnalyzer。
/// 防抖：10秒扫事件，5秒扫道具。
/// 清理已完成的动态任务（Id前缀 event_ / item_ / craft_）。
/// </summary>
public sealed class DynamicMissionGenerator
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;
    private readonly BoardState _boardState;
    private readonly SystemEventScanner _eventScanner;
    private readonly ItemNeedAnalyzer _itemAnalyzer;
    private readonly ValueAssessmentService? _valueAssessment;
    private GoalScheduler? _goalScheduler;

    private DateTime _lastEventScan = DateTime.MinValue;
    private DateTime _lastItemScan = DateTime.MinValue;
    private static readonly TimeSpan EventScanInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ItemScanInterval = TimeSpan.FromSeconds(5);

    public DynamicMissionGenerator(AiPlayer player, IGameAdapter adapter, BoardState boardState, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._boardState = boardState;
        this._logger = logger;
        this._valueAssessment = null;
        this._eventScanner = new SystemEventScanner(player, adapter, boardState, logger);
        this._itemAnalyzer = new ItemNeedAnalyzer(player, adapter, logger);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicMissionGenerator"/> class with value assessment.
    /// </summary>
    public DynamicMissionGenerator(AiPlayer player, IGameAdapter adapter, BoardState boardState, ILogger logger, ValueAssessmentService valueAssessment)
        : this(player, adapter, boardState, logger)
    {
        this._valueAssessment = valueAssessment;
        this._itemAnalyzer = new ItemNeedAnalyzer(player, adapter, logger, valueAssessment);
    }

    /// <summary>
    /// 设置目标规划器引用，使目标任务注入成为 GenerateMissions 的一部分。
    /// </summary>
    public void SetGoalScheduler(GoalScheduler goalScheduler)
    {
        this._goalScheduler = goalScheduler;
    }

    /// <summary>
    /// 在 SelectCurrentTask 的 Layer 3/5 决策前调用。
    /// </summary>
    public async ValueTask GenerateMissionsAsync()
    {
        CleanupDynamicMissions();

        // Layer 0: GoalScheduler 目标任务注入
        if (this._goalScheduler is not null)
        {
            var goalMissions = this._goalScheduler.EvaluateGoals(this._adapter.GetPlayerLevel());
            foreach (var gm in goalMissions)
            {
                if (!this._boardState.Missions.Any(m => m.Id == gm.Id && (m.Status == MissionStatus.Active || m.Status == MissionStatus.Pending)))
                {
                    this._boardState.Missions.Add(gm);
                    this._logger.LogInformation("[MissionGen] + 注入目标任务: {Id} (priority={P})", gm.Id, gm.Priority);
                }
            }
        }

        if (DateTime.UtcNow - this._lastEventScan >= EventScanInterval)
        {
            var eventMissions = await this._eventScanner.ScanAsync().ConfigureAwait(false);
            foreach (var m in eventMissions)
            {
                if (!this._boardState.Missions.Any(ex => ex.Id == m.Id && (ex.Status == MissionStatus.Active || ex.Status == MissionStatus.Pending)))
                {
                    this._boardState.Missions.Add(m);
                    this._logger.LogInformation("[MissionGen] + 注入事件任务: {Id} (priority={P})", m.Id, m.Priority);
                }
            }
            this._lastEventScan = DateTime.UtcNow;
        }

        if (DateTime.UtcNow - this._lastItemScan >= ItemScanInterval)
        {
            // 常规道具任务扫描
            var itemMissions = this._itemAnalyzer.Scan();
            foreach (var m in itemMissions)
            {
                if (!this._boardState.Missions.Any(ex => ex.Id == m.Id && (ex.Status == MissionStatus.Active || ex.Status == MissionStatus.Pending)))
                {
                    this._boardState.Missions.Add(m);
                    this._logger.LogInformation("[MissionGen] + 注入道具任务: {Id} (priority={P})", m.Id, m.Priority);
                }
            }

            // ScavengeForCrafting: 深层合成需求扫描（翅膀/混沌武器/装备升级）
            // 按价值排序：翅膀(18)→门票(21)→混沌武器(25)→装备升级(27)
            var craftMissions = this._itemAnalyzer.ScavengeForCrafting();
            foreach (var m in craftMissions)
            {
                if (!this._boardState.Missions.Any(ex => ex.Id == m.Id && (ex.Status == MissionStatus.Active || ex.Status == MissionStatus.Pending)))
                {
                    this._boardState.Missions.Add(m);
                    this._logger.LogInformation("[MissionGen] + 注入合成任务: {Id} (priority={P})", m.Id, m.Priority);
                }
            }

            this._lastItemScan = DateTime.UtcNow;
        }
    }

    private void CleanupDynamicMissions()
    {
        var toRemove = this._boardState.Missions
            .Where(m => IsDynamicId(m.Id) && (m.Status == MissionStatus.Completed || m.Status == MissionStatus.Failed || m.IsDeadTask))
            .ToList();
        foreach (var m in toRemove)
        {
            this._logger.LogInformation("[MissionGen] 清理动态任务: {Id} (status={Status}, dead={Dead})", m.Id, m.Status, m.IsDeadTask);
            this._boardState.Missions.Remove(m);
        }
    }

    private static bool IsDynamicId(string id) =>
        id.StartsWith("event_") || id.StartsWith("item_") || id.StartsWith("craft_") || id.StartsWith("goal_");
}
