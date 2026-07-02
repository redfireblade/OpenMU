// <copyright file="EventHandlers.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.MiniGames;

/// <summary>
/// 事件处理器 — 处理来自 EventWatcherService/EventBus 的外部事件，
/// 更新 AI 看板和决策上下文。
/// 不执行任何游戏操作，只更新 BoardState。
/// </summary>
public sealed class EventHandlers
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly MissionBoardService _missionBoard;
    private readonly MaterialKnowledgeService _materialKnowledge;
    private readonly EventInterruptService _eventInterrupt;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventHandlers"/> class.
    /// </summary>
    public EventHandlers(
        AiPlayer player,
        IGameAdapter adapter,
        MissionBoardService missionBoard,
        MaterialKnowledgeService materialKnowledge,
        EventInterruptService eventInterrupt,
        ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._missionBoard = missionBoard;
        this._materialKnowledge = materialKnowledge;
        this._eventInterrupt = eventInterrupt;
        this._logger = logger;
    }

    /// <summary>
    /// 活动入场窗口打开 — 注入事件任务并根据 readiness 决定执行路径。
    /// </summary>
    public void OnEventOpen(EventOpenEvent evt)
    {
        this._logger.LogInformation("[EventWatcher] 🔔 {Name} Lv.{Level} 入场窗口已打开!", evt.Name, evt.GameLevel);

        var miniGameDef = this.FindMiniGameDefinition(evt.Type, evt.GameLevel);
        if (miniGameDef is null)
        {
            this._logger.LogWarning("[EventInterrupt] 未找到 MiniGameDefinition: {Type} Lv.{Level}", evt.Type, evt.GameLevel);
            return;
        }

        // Step 1: 检查当前任务是否可中断
        if (this._eventInterrupt.ShouldInterruptForEvent(this._missionBoard.BoardState.Missions.FirstOrDefault(m => m.Status == MissionStatus.Active), evt))
        {
            // 中断当前任务（标记为 Suspended，不标 Failed）
            var activeTask = this._missionBoard.BoardState.Missions.FirstOrDefault(m => m.Status == MissionStatus.Active);
            if (activeTask is not null)
            {
                activeTask.Status = MissionStatus.Suspended;
                this._logger.LogInformation("[EventInterrupt] ⏸ 暂停当前任务 {Title}", activeTask.Title);
            }

            // 设置中断上下文已被移入 EventInterruptService
            this._logger.LogInformation("[EventInterrupt] 中断触发: {Name}", evt.Name);
        }

        // Step 2: 检查事件条件
        var readiness = this._eventInterrupt.GetEventReadiness(miniGameDef);
        this._logger.LogInformation("[EventInterrupt] {Name} 入场准备状态: {Readiness}", evt.Name, readiness);

        // Step 3: 确保看板有这个事件任务（先注入，后续根据 readiness 决定是否激活）
        var eventId = $"event_{evt.Type}_{evt.GameLevel}";
        if (!this._missionBoard.BoardState.Missions.Any(m => m.Id == eventId && !m.IsDeadTask))
        {
            this._missionBoard.BoardState.Missions.Add(new MissionItem
            {
                Id = eventId,
                Title = $"{evt.Name} Lv.{evt.GameLevel}",
                Priority = 15,
                Type = MissionType.Quest,
                Category = QuestCategory.InstanceEvent,
                Goal = QuestGoal.Instance,
                Source = QuestSource.GameSystem,
                Module = "event_executor",
                FailureRetryable = true,
                MaxRepeatCount = -1,
                DailyMaxCount = evt.Type switch
                {
                    MiniGameType.DevilSquare => 3,
                    MiniGameType.BloodCastle => 3,
                    MiniGameType.ChaosCastle => 3,
                    _ => 1,
                },
            });
            this._logger.LogInformation("[EventWatcher] ➕ 注入开放事件: {Id}", eventId);
        }

        // Step 4: 根据 readiness 处理
        switch (readiness)
        {
            case EventReadiness.Ready:
                // 直接激活事件任务，下一 tick SelectCurrentTask 会选中
                var eventTask = this._missionBoard.BoardState.Missions.FirstOrDefault(m => m.Id == eventId);
                if (eventTask is not null && eventTask.Status != MissionStatus.Active)
                {
                    eventTask.Status = MissionStatus.Active;
                    this._logger.LogInformation("[EventInterrupt] ✅ 条件满足，直接激活事件任务: {Id}", eventId);
                }

                break;

            case EventReadiness.NeedVault:
                // 仓库有门票/材料 → 注入 vault_ 提取任务
                this.InjectVaultRetrieveMission(miniGameDef);
                break;

            case EventReadiness.NeedTicket:
                // 背包有材料但无门票 → 事件任务的 HandleMissingTicket 会处理合成链路
                // 激活事件任务，让 EventExecutorModule 的 HandleMissingTicket 触发合成任务
                var eventTask2 = this._missionBoard.BoardState.Missions.FirstOrDefault(m => m.Id == eventId);
                if (eventTask2 is not null && eventTask2.Status != MissionStatus.Active)
                {
                    eventTask2.Status = MissionStatus.Active;
                    this._logger.LogInformation("[EventInterrupt] 🎫 需要合成门票，激活事件任务触发合成链路: {Id}", eventId);
                }

                break;

            case EventReadiness.NeedFarm:
                // 无门票无材料 → 注入 farm_ticket 刷材料任务，然后激活事件任务
                this.InjectFarmTicketMission(miniGameDef);
                var eventTask3 = this._missionBoard.BoardState.Missions.FirstOrDefault(m => m.Id == eventId);
                if (eventTask3 is not null && eventTask3.Status != MissionStatus.Active)
                {
                    eventTask3.Status = MissionStatus.Active;
                    this._logger.LogInformation("[EventInterrupt] 🌾 注入打材料任务，激活事件任务触发 farming 链路: {Id}", eventId);
                }

                break;

            case EventReadiness.LevelTooLow:
            case EventReadiness.NotEnoughMoney:
                this._logger.LogInformation("[EventInterrupt] ⏭ {Name} 条件不足: {Readiness}", evt.Name, readiness);
                break;
        }
    }

    /// <summary>
    /// 事件进行中提醒 — 确保事件任务活跃。
    /// </summary>
    public void OnEventReminder(EventReminderEvent evt)
    {
        if (evt.MinutesLeft == 0)
        {
            this._logger.LogInformation("[EventWatcher] ⏰ {Name} Lv.{Level} 已开放, 尽快入场!", evt.Name, evt.GameLevel);
        }
        else
        {
            this._logger.LogInformation("[EventWatcher] ⏰ {Name} Lv.{Level} 进行中...", evt.Name, evt.GameLevel);
        }

        // 确保看板有事件任务（如果之前被移除）
        var eventId = $"event_{evt.Type}_{evt.GameLevel}";
        if (!this._missionBoard.BoardState.Missions.Any(m => m.Id == eventId && m.Status == MissionStatus.Active))
        {
            // 把事件任务推到 Active 层
            var existing = this._missionBoard.BoardState.Missions.FirstOrDefault(m => m.Id == eventId);
            if (existing is not null && existing.Status != MissionStatus.Active && !existing.IsDeadTask)
            {
                existing.Status = MissionStatus.Active;
                this._logger.LogInformation("[EventWatcher] 🔄 重新激活事件任务: {Id}", eventId);
            }
        }
    }

    /// <summary>
    /// 事件关闭 — 标记对应事件任务为失败（不可执行）。
    /// </summary>
    public void OnEventClosed(EventClosedEvent evt)
    {
        this._logger.LogInformation("[EventWatcher] 🔴 {Name} Lv.{Level} 已结束", evt.Name, evt.GameLevel);
        var eventId = $"event_{evt.Type}_{evt.GameLevel}";
        var existing = this._missionBoard.BoardState.Missions.FirstOrDefault(m => m.Id == eventId);
        if (existing is not null && existing.Status == MissionStatus.Active)
        {
            MarkTaskFailed(existing, FailureReason.NotExecutable);
            if (existing.IsDeadTask)
            {
                this._logger.LogInformation("[EventWatcher] 💀 事件任务 {Id} 标记为死任务 (原因=NotExecutable, 重试#{Retry})", eventId, existing.RetryCount);
            }
        }
    }

    /// <summary>
    /// 从配置文件查找匹配的 MiniGameDefinition。
    /// </summary>
    public MiniGameDefinition? FindMiniGameDefinition(MiniGameType miniGameType, int gameLevel)
    {
        var config = this._player.GameContext?.Configuration;
        if (config is null)
        {
            return null;
        }

        return config.MiniGameDefinitions
            .FirstOrDefault(d => d.Type == miniGameType && d.GameLevel == gameLevel);
    }

    /// <summary>
    /// 注入仓库取物任务 — 从仓库中取出门票或合成材料。
    /// </summary>
    public void InjectVaultRetrieveMission(MiniGameDefinition miniGameDef)
    {
        var missionId = $"vault_ticket_{miniGameDef.Type}_{miniGameDef.GameLevel}";
        if (this._missionBoard.BoardState.Missions.Any(m => m.Id == missionId))
        {
            return;
        }

        var eventId = $"event_{miniGameDef.Type}_{miniGameDef.GameLevel}";

        var vaultMission = new MissionItem
        {
            Id = missionId,
            Title = $"从仓库取{miniGameDef.Name}门票",
            Priority = 10,  // 高优先级（比事件任务 15 高，先取物再入场）
            Type = MissionType.ItemFarm,
            Category = QuestCategory.AiCustom,
            Module = "vault_executor",
            FailureRetryable = true,
            MaxRepeatCount = 3,
        };

        this._missionBoard.BoardState.Missions.Add(vaultMission);

        // 设为事件任务的前置依赖（取完门票/材料后再走 event_executor）
        var existing = this._missionBoard.BoardState.Missions.FirstOrDefault(m => m.Id == eventId);
        if (existing is not null)
        {
            existing.Dependencies = new[] { missionId };
        }

        this._logger.LogInformation("[EventInterrupt] ➕ 注入仓库取物任务: {Id}", missionId);
    }

    /// <summary>
    /// 注入打门票材料任务 — 无门票无材料时，创建 farm_ticket 任务并设为事件前驱。
    /// 用 MaterialKnowledgeService 确定掉落目标（怪物、地图、材料等级）。
    /// </summary>
    public void InjectFarmTicketMission(MiniGameDefinition miniGameDef)
    {
        var missionId = $"farm_ticket_{miniGameDef.Type}_{miniGameDef.GameLevel}";

        // 防止重复注入
        if (this._missionBoard.BoardState.Missions.Any(m => m.Id == missionId))
        {
            return;
        }

        var eventId = $"event_{miniGameDef.Type}_{miniGameDef.GameLevel}";

        // 获取玩家等级
        var playerLevel = this._player.Level;

        // 使用 MaterialKnowledgeService 计算最佳材料等级和掉落来源
        var bestDrop = this._materialKnowledge.GetTicketMaterialDropInfo(miniGameDef.Type, miniGameDef.GameLevel, playerLevel);

        var farmMission = new MissionItem
        {
            Id = missionId,
            Title = bestDrop is not null
                ? $"刷取{miniGameDef.Name}门票材料+{bestDrop.TargetLevel}"
                : $"刷取{miniGameDef.Name}门票材料",
            Priority = 11,  // 比事件任务(15)高，先打材料再入场
            Type = MissionType.ItemFarm,
            Category = QuestCategory.AiCustom,
            Module = "material_farm",
            FailureRetryable = true,
            MaxRepeatCount = 5,
            TargetType = miniGameDef.Type,
            TargetLevel = bestDrop?.TargetLevel ?? 0,
            Context = new Dictionary<string, object>
            {
                { "MiniGameType", (int)miniGameDef.Type },
                { "MiniGameLevel", miniGameDef.GameLevel },
            },
        };

        if (bestDrop is not null)
        {
            farmMission.Context["ItemGroup"] = bestDrop.ItemGroup;
            farmMission.Context["ItemNumber"] = bestDrop.ItemNumber;
            farmMission.Context["TargetLevel"] = (int)bestDrop.TargetLevel;
            farmMission.Context["MonsterNumber"] = bestDrop.MonsterNumber;
            farmMission.Context["MapNumber"] = (ushort)bestDrop.MapNumber;
        }

        this._missionBoard.BoardState.Missions.Add(farmMission);

        // 设为事件任务的前置依赖（打完材料后再走合成/入场）
        var existing = this._missionBoard.BoardState.Missions.FirstOrDefault(m => m.Id == eventId);
        if (existing is not null)
        {
            existing.Dependencies = new[] { missionId };
        }

        this._logger.LogInformation(
            "[EventInterrupt] 🌾 注入打材料任务: {Id} -> {Title}{Target}",
            missionId,
            farmMission.Title,
            bestDrop is not null ? $" (怪物#{bestDrop.MonsterNumber} @地图#{bestDrop.MapNumber})" : string.Empty);
    }

    /// <summary>
    /// 合成后材料重评估。检查目标物品是否已出现在背包(合成成功)，
    /// 否则重新注入 farm 任务。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "VSTHRD100", Justification = "Fire-and-forget: called from non-async AdvanceTask, no caller to await")]
    public async void ReevaluateMaterialsAfterCraft(MissionItem current)
    {
        try
        {
            var config = this._player.GameContext?.Configuration;
            if (config is null) return;

            var materialService = new MaterialRequirementService(
                config, this._player, this._adapter, this._logger);

            int targetGroup = 0, targetNumber = 0;
            bool isTicketCraft = current.Id.StartsWith("craft_ticket_", StringComparison.OrdinalIgnoreCase);

            if (isTicketCraft)
            {
                if (current.Context.TryGetValue("MiniGameType", out var typeObj) && typeObj is int mgType &&
                    current.Context.TryGetValue("MiniGameLevel", out var levelObj) && levelObj is int mgLevel)
                {
                    var ticketItem = config.MiniGameDefinitions
                        ?.FirstOrDefault(d => (int)d.Type == mgType && d.GameLevel == mgLevel)
                        ?.TicketItem;
                    if (ticketItem is not null)
                    {
                        targetGroup = ticketItem.Group;
                        targetNumber = ticketItem.Number;
                    }
                }
            }

            if (targetGroup == 0 && targetNumber == 0) return;

            // 1. 合成成功 → 多门票逻辑: 检查库存是否达到目标数
            var hasTicket = this._player.Inventory?.Items.Any(i =>
                i.Definition?.Group == targetGroup && i.Definition?.Number == targetNumber && i.Durability > 0) ?? false;

            if (hasTicket)
            {
                // 数背包有几张门票
                var ticketCount = this._player.Inventory?.Items.Count(i =>
                    i.Definition?.Group == targetGroup && i.Definition?.Number == targetNumber && i.Durability > 0) ?? 0;

                // 目标门票数: 根据玩家等级决定
                var playerLevel = this._adapter.GetPlayerLevel();
                var targetTicketCount = 1;
                if (playerLevel >= 200)
                    targetTicketCount = 3;
                else if (playerLevel >= 100)
                    targetTicketCount = 2;

                this._logger.LogInformation("[MaterialReeval] ✅ 合成成功:G{Group}N{Num} 背包有{Count}张(目标{Target})",
                    targetGroup, targetNumber, ticketCount, targetTicketCount);

                if (ticketCount >= targetTicketCount)
                {
                    // 门票够了 → 标记满足 + 触发入场
                    var existing = this._missionBoard.BoardState.MaterialNeeds
                        .FirstOrDefault(e => e.TargetGroup == targetGroup && e.TargetNumber == targetNumber);
                    if (existing is not null) existing.IsSatisfied = true;

                    // 激活对应的事件任务(event_Type_Level)让 EventExecutorModule 执行入场
                    if (isTicketCraft && current.Context.TryGetValue("MiniGameType", out var mt) && mt is int mgt2 &&
                        current.Context.TryGetValue("MiniGameLevel", out var ml) && ml is int mgl2)
                    {
                        var eventId = $"event_{(MiniGameType)mgt2}_{mgl2}";
                        var eventTask = this._missionBoard.BoardState.Missions
                            .FirstOrDefault(m => m.Id == eventId);
                        if (eventTask is not null && eventTask.Status != MissionStatus.Active && !eventTask.IsDeadTask)
                        {
                            eventTask.Status = MissionStatus.Active;
                            eventTask.Dependencies = Array.Empty<string>(); // 解除前置依赖
                            this._logger.LogInformation("[MaterialReeval] 激活事件任务:{Id}", eventId);
                        }
                        else if (eventTask is null)
                        {
                            // 事件任务不存在(EventWatcher 可能已经广播完毕但事件已关闭)
                            // 尝试通过 OnEventOpen 手动注入
                            var miniGameDef = config.MiniGameDefinitions
                                ?.FirstOrDefault(d => (int)d.Type == mgt2 && d.GameLevel == mgl2);
                            if (miniGameDef is not null)
                            {
                                var readiness = this._eventInterrupt.GetEventReadiness(miniGameDef);
                                if (readiness == EventReadiness.Ready)
                                {
                                    this._logger.LogInformation("[MaterialReeval] 门票已够, 调用 OnEventOpen 注入入场任务");
                                    // 通过 OnEventOpen 重新注入事件任务
                                    this.OnEventOpen(new EventOpenEvent(
                                        (MiniGameType)mgt2, mgl2, (string)miniGameDef.Name, miniGameDef.EntranceFee));
                                }
                            }
                        }
                    }
                }
                else
                {
                    // 门票还不够 → 继续刷材料或直接合成下一张
                    this._logger.LogInformation("[MaterialReeval] 门票还缺{Need}张,重置craft任务继续合成",
                        targetTicketCount - ticketCount);

                    // 检查材料是否还够再合成一次
                    var matResult = materialService.ReevaluateAfterCraft(targetGroup, targetNumber);
                    var allMaterialsAvailable = matResult is null || matResult.Materials.All(m => m.IsSufficient);

                    if (allMaterialsAvailable)
                    {
                        // 材料足够 → 直接重置craft任务,不依赖farm
                        current.Status = MissionStatus.Pending;
                        current.IsDeadTask = false;
                        current.FailureReason = null;
                        current.Dependencies = Array.Empty<string>(); // 直接合成
                        this._logger.LogInformation("[MaterialReeval] 🔄 材料足够,直接重置craft任务:{Id}", current.Id);
                    }
                    else if (matResult is not null)
                    {
                        // 材料不够 → 重新注入 farm_mat_* 任务
                        var innerMissing = matResult.Materials.FirstOrDefault(m => !m.IsSufficient);
                        if (innerMissing is not null)
                        {
                            var innerMissionId = $"farm_mat_{innerMissing.Group}_{innerMissing.Number}";
                            var innerExistingFarm = this._missionBoard.BoardState.Missions.FirstOrDefault(m => m.Id == innerMissionId);
                            if (innerExistingFarm is null)
                            {
                                var farmMission = new MissionItem
                                {
                                    Id = innerMissionId,
                                    Title = $"刷{innerMissing.Name}(合成多囤)",
                                    Priority = 20,
                                    Type = MissionType.ItemFarm,
                                    Category = QuestCategory.AiCustom,
                                    Module = "material_farm",
                                    FailureRetryable = true,
                                    MaxRepeatCount = -1,
                                    TargetLevel = innerMissing.RequiredLevel,
                                    Context = new Dictionary<string, object>
                                    {
                                        { "ItemGroup", innerMissing.Group },
                                        { "ItemNumber", innerMissing.Number },
                                        { "TargetLevel", innerMissing.RequiredLevel },
                                        { "RequiredCount", innerMissing.RequiredCount - innerMissing.Total },
                                    },
                                };
                                if (innerMissing.FarmMonsterNumber.HasValue)
                                {
                                    farmMission.Context["MonsterNumber"] = innerMissing.FarmMonsterNumber.Value;
                                    farmMission.Context["MapNumber"] = innerMissing.FarmMapNumber ?? 0;
                                }

                                this._missionBoard.BoardState.Missions.Add(farmMission);
                                this._logger.LogInformation("[MaterialReeval] ➕ 多囤材料farm:{Id}", innerMissionId);
                            }
                            else if (innerExistingFarm.Status != MissionStatus.Pending && innerExistingFarm.Status != MissionStatus.Active)
                            {
                                innerExistingFarm.Status = MissionStatus.Pending;
                                innerExistingFarm.IsDeadTask = false;
                                innerExistingFarm.FailureReason = null;
                                this._logger.LogInformation("[MaterialReeval] 🔄 重新激活farm任务:{Id}", innerMissionId);
                            }

                            // craft依赖新的farm任务
                            current.Status = MissionStatus.Pending;
                            current.IsDeadTask = false;
                            current.FailureReason = null;
                            current.Dependencies = new[] { innerMissionId };
                            this._logger.LogInformation("[MaterialReeval] 🔄 craft任务:{Id} 依赖farm:{Dep}", current.Id, innerMissionId);
                        }
                    }
                }

                return;
            }

            // 2. 合成失败 → 检查材料缺口
            var result = materialService.ReevaluateAfterCraft(targetGroup, targetNumber);
            if (result is null) return;

            var missing = result.Materials.FirstOrDefault(m => !m.IsSufficient);
            if (missing is null) return;

            // 注入 farm_mat_* 或重新激活已有的
            var missionId = $"farm_mat_{missing.Group}_{missing.Number}";
            var existingFarm = this._missionBoard.BoardState.Missions.FirstOrDefault(m => m.Id == missionId);

            if (existingFarm is null)
            {
                var farmMission = new MissionItem
                {
                    Id = missionId,
                    Title = $"刷{missing.Name}(合成失败重试)",
                    Priority = 20,
                    Type = MissionType.ItemFarm,
                    Category = QuestCategory.AiCustom,
                    Module = "material_farm",
                    FailureRetryable = true,
                    MaxRepeatCount = -1,
                    TargetLevel = missing.RequiredLevel,
                    Context = new Dictionary<string, object>
                    {
                        { "ItemGroup", missing.Group },
                        { "ItemNumber", missing.Number },
                        { "TargetLevel", missing.RequiredLevel },
                        { "RequiredCount", missing.RequiredCount - missing.Total },
                    },
                };
                if (missing.FarmMonsterNumber.HasValue)
                {
                    farmMission.Context["MonsterNumber"] = missing.FarmMonsterNumber.Value;
                    farmMission.Context["MapNumber"] = missing.FarmMapNumber ?? 0;
                }

                this._missionBoard.BoardState.Missions.Add(farmMission);
                this._logger.LogInformation("[MaterialReeval] ➕ 合成失败重注入farm:{Id}", missionId);
            }
            else if (existingFarm.Status != MissionStatus.Pending && existingFarm.Status != MissionStatus.Active)
            {
                existingFarm.Status = MissionStatus.Pending;
                existingFarm.IsDeadTask = false;
                existingFarm.FailureReason = null;
                this._logger.LogInformation("[MaterialReeval] 🔄 重新激活farm任务:{Id}", missionId);
            }

            // 3. 重新注入 craft_ticket 任务(依赖于新的 farm 任务)
            if (isTicketCraft && current.Context.TryGetValue("MiniGameType", out var mt3) && mt3 is int mgt3 &&
                current.Context.TryGetValue("MiniGameLevel", out var ml3) && ml3 is int mgl3)
            {
                var craftId = current.Id; // 复用原 ID
                var existingCraft = this._missionBoard.BoardState.Missions.FirstOrDefault(m => m.Id == craftId);
                if (existingCraft is not null && (existingCraft.IsDeadTask || existingCraft.Status == MissionStatus.Failed))
                {
                    // 重新激活 craft 任务并更新依赖
                    existingCraft.Status = MissionStatus.Pending;
                    existingCraft.IsDeadTask = false;
                    existingCraft.FailureReason = null;
                    existingCraft.Dependencies = new[] { missionId };
                    this._logger.LogInformation("[MaterialReeval] 🔄 重新激活craft任务:{Id} 依赖:{Dep}", craftId, missionId);
                }
            }
        }
        catch (Exception ex)
        {
            this._logger.LogWarning("[MaterialReeval] 异常:{Msg}", ex.Message);
        }
    }

    /// <summary>
    /// 标记任务失败，记录失败原因/重试计数/首次失败时间/死任务判定。
    /// 仅操作 BoardState，不涉及 HeartbeatService 内部状态。
    /// </summary>
    private static void MarkTaskFailed(MissionItem task, FailureReason reason)
    {
        task.Status = MissionStatus.Failed;
        task.FailureReason = reason;
        task.RetryCount++;

        if (task.FirstFailedAt is null)
            task.FirstFailedAt = DateTime.UtcNow;

        // IsDeadTask 判定
        if (!task.FailureRetryable || reason == FailureReason.NotRetryable)
        {
            task.IsDeadTask = true;
        }
        else if (task.RetryCount >= 3)
        {
            task.IsDeadTask = true;
        }
        else if (task.FirstFailedAt is not null &&
                 (DateTime.UtcNow - task.FirstFailedAt.Value).TotalDays > 365 &&
                 task.RetryCount >= 3)
        {
            task.IsDeadTask = true;
        }
    }
}
