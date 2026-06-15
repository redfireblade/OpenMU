// <copyright file="EventExecutorModule.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using System.Globalization;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions.MiniGames;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 副本活动执行模块 — 实现迷你游戏入场流程状态机。
/// 状态机: 检查门票 → 走到入场区域 → 调用 EnterMiniGameAction 入场。
/// 当前版本：直接调用 EnterMiniGameAction（服务端负责门票检查/等级验证/传送），
/// 跳过了 NPC 对话步骤，因为 EnterMiniGameAction 不依赖 NPC 打开对话框.
/// 未来可通过 MiniGameDefinition.TicketItem 的入场NPC映射做 NPC 端对话.
/// </summary>
public sealed class EventExecutorModule : IBehaviorSubModule
{
    /// <summary>自动搜索门票的槽位标记值（EnterMiniGameAction会遍历背包查找门票）.</summary>
    private const byte AutoSearchTicketSlot = 0xFF;

    /// <summary>NPC 交互的最小距离.</summary>
    private const float NpcInteractionDistance = 3f;

    /// <summary>事件ID最小段数（格式: event_Type_Level）.</summary>
    private const int MiniGameIdMinParts = 3;

    /// <summary>门票校验中 level 对比的系数偏移.</summary>
    private const int TicketLevelComparisonOffset = 2;

    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;
    private readonly EnterMiniGameAction _enterAction = new();
    private readonly BoardState _boardState;
    private readonly MaterialKnowledgeService _materialKnowledge;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventExecutorModule"/> class.
    /// </summary>
    /// <param name="player">The AI player.</param>
    /// <param name="adapter">The game adapter.</param>
    /// <param name="boardState">The mission board state for dependency injection.</param>
    /// <param name="logger">The logger.</param>
    public EventExecutorModule(AiPlayer player, IGameAdapter adapter, BoardState boardState, ILogger logger, MaterialKnowledgeService materialKnowledge)
    {
        this._player = player;
        this._adapter = adapter;
        this._boardState = boardState;
        this._logger = logger;
        this._materialKnowledge = materialKnowledge;
    }

    /// <inheritdoc />
    public string ModuleId => "event_executor";

    /// <summary>
    /// 执行副本入场的一步心跳.
    /// </summary>
    /// <param name="item">任务条目，Id 格式为 "event_{MiniGameType}_{GameLevel}".</param>
    /// <returns>当前步骤的执行结果.</returns>
    public async ValueTask<StepResult> ExecuteStepAsync(MissionItem item)
    {
        if (!TryParseMiniGameId(item.Id, out var miniGameType, out var gameLevel))
        {
            this._logger.LogWarning("[EventExec] 无法解析事件ID: {Id}", item.Id);
            return StepResult.Failed;
        }

        this._logger.LogInformation(
            "[EventExec] 开始处理事件 {Type} Lv.{Level}",
            miniGameType,
            gameLevel);

        if (this._player.CurrentMiniGame is not null)
        {
            this._logger.LogDebug(
                "[EventExec] 玩家已在副本中 ({Type})，等待结束",
                miniGameType);
            return StepResult.InProgress;
        }

        var miniGameDef = this.FindMiniGameDefinition(miniGameType, gameLevel);
        if (miniGameDef is null)
        {
            return StepResult.Failed;
        }

        byte ticketSlot = AutoSearchTicketSlot;
        // Phase 3: 门票和金币检查
        // 先检查门票（不足时注入合成的条件任务链）
        if (miniGameDef.TicketItem is not null)
        {
            if (!this.TryCheckTicket(miniGameDef, out ticketSlot))
            {
                var result = this.HandleMissingTicket(miniGameDef, item);
                if (result == StepResult.InProgress)
                {
                    this._logger.LogInformation(
                        "[EventExec] 缺少门票, 已注入前置任务 {Type} Lv.{Level}",
                        miniGameType,
                        gameLevel);
                    return StepResult.InProgress;
                }

                if (result == StepResult.Failed)
                {
                    this._logger.LogWarning(
                        "[EventExec] 门票合成已完成({Type} Lv.{Level})但背包未找到门票，无法入场",
                        miniGameType,
                        gameLevel);
                    return StepResult.Failed;
                }

                this._logger.LogWarning(
                    "[EventExec] 缺少 {Type} Lv.{Level} 的入场门票",
                    miniGameType,
                    gameLevel);
                return StepResult.Failed;
            }
        }

        if (!this.TryCheckEntranceFee(miniGameDef))
        {
            return StepResult.Failed;
        }

        this._logger.LogInformation(
            "[EventExec] 调用 EnterMiniGameAction 进入 {Type} Lv.{Level}, ticketSlot={Slot}",
            miniGameType,
            gameLevel,
            ticketSlot);

        return await this.TryEnterAsync(miniGameType, gameLevel, ticketSlot).ConfigureAwait(false);
    }

    /// <summary>
    /// 将活动入场事件任务从 HeartbeatService 发送到 EventWatcher 的 EventOpenEvent 链路。
    /// 检查活动是否已开放（TryCheckMiniGameOpenAsync），未开放时设置 TargetMap 供传送。
    /// </summary>
    private async ValueTask<bool> TryEnsureEventOpenAsync(MiniGameType miniGameType, int gameLevel)
    {
        var miniGameDef = this.FindMiniGameDefinition(miniGameType, gameLevel);
        if (miniGameDef is null) return false;

        // 检查活动开放状态
        return await this.TryCheckMiniGameOpenAsync(miniGameDef).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析事件ID格式 "event_{Type}_{Level}"，例如 "event_BloodCastle_2".
    /// </summary>
    /// <param name="id">事件ID字符串.</param>
    /// <param name="miniGameType">解析出的迷你游戏类型.</param>
    /// <param name="gameLevel">解析出的游戏等级.</param>
    /// <returns>解析是否成功.</returns>
    private static bool TryParseMiniGameId(string id, out MiniGameType miniGameType, out int gameLevel)
    {
        miniGameType = MiniGameType.Undefined;
        gameLevel = 0;

        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        var parts = id.Split('_');
        if (parts.Length < MiniGameIdMinParts
            || !string.Equals(parts[0], "event", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var typeStr = string.Join("_", parts.Skip(1).Take(parts.Length - TicketLevelComparisonOffset));
        if (!Enum.TryParse<MiniGameType>(typeStr, ignoreCase: true, out miniGameType))
        {
            return false;
        }

        return int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out gameLevel);
    }

    /// <summary>
    /// 在背包中遍历寻找匹配门票定义的物品.
    /// </summary>
    private static bool TryFindTicketInInventory(
        MUnique.OpenMU.GameLogic.IInventoryStorage inventory,
        ItemDefinition ticketDef,
        MiniGameDefinition miniGameDef,
        out byte ticketSlot)
    {
        ticketSlot = AutoSearchTicketSlot;
        foreach (var item in inventory.Items)
        {
            if (item.Definition is null)
            {
                continue;
            }

            if (item.Definition == ticketDef
                && item.Durability > 0
                && item.Level == miniGameDef.TicketItemLevel)
            {
                ticketSlot = (byte)item.ItemSlot;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 从配置文件查找匹配的 MiniGameDefinition.
    /// </summary>
    private MiniGameDefinition? FindMiniGameDefinition(MiniGameType miniGameType, int gameLevel)
    {
        var config = this._player.GameContext?.Configuration;
        if (config is null)
        {
            this._logger.LogWarning("[EventExec] GameConfiguration 为空");
            return null;
        }

        var miniGameDef = config.MiniGameDefinitions
            .FirstOrDefault(d => d.Type == miniGameType && d.GameLevel == gameLevel);

        if (miniGameDef is null)
        {
            this._logger.LogWarning(
                "[EventExec] 未找到 MiniGameDefinition: {Type} Lv.{Level}",
                miniGameType,
                gameLevel);
        }

        return miniGameDef;
    }

    /// <summary>
    /// 检查门票是否充足。如果 MiniGameDefinition 不需要门票则直接通过.
    /// </summary>
    private bool TryCheckTicket(MiniGameDefinition miniGameDef, out byte ticketSlot)
    {
        ticketSlot = AutoSearchTicketSlot;

        var ticketDef = miniGameDef.TicketItem;
        if (ticketDef is null)
        {
            return true;
        }

        var inventory = this._player.Inventory;
        if (inventory is null)
        {
            return false;
        }

        // 直接遍历 inventory.Items 找匹配门票
        if (TryFindTicketInInventory(inventory, ticketDef, miniGameDef, out ticketSlot))
        {
            return true;
        }

        // 也检查 FindItemsByDefinition 方法（EnterMiniGameAction 内部使用的逻辑）
        var foundItems = inventory.FindItemsByDefinition(ticketDef);
        foreach (var item in foundItems)
        {
            if (item.Durability > 0 && item.Level == miniGameDef.TicketItemLevel)
            {
                ticketSlot = (byte)item.ItemSlot;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 检查入场费用是否足够.
    /// </summary>
    private bool TryCheckEntranceFee(MiniGameDefinition miniGameDef)
    {
        if (miniGameDef.EntranceFee > 0 && this._player.Money < miniGameDef.EntranceFee)
        {
            this._logger.LogWarning(
                "[EventExec] 金钱不足({Money} < {Fee})，无法进入 {Type} Lv.{Level}",
                this._player.Money,
                miniGameDef.EntranceFee,
                miniGameDef.Type,
                miniGameDef.GameLevel);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 检查迷你游戏活动是否已开放（基于 IPeriodicMiniGameStartPlugIn 定时策略）。
    /// Shared map 活动（如 BloodCastle）有定时窗口；无策略时视为始终开放。
    /// 关键改进：距开放时间 ≤ 60 秒时视为开放，避免 PeriodicTask 状态缓存
    /// （NextRunUtc 持久化）与真实时间窗口不一致导致的事件状态误判。
    /// </summary>
    private async ValueTask<bool> TryCheckMiniGameOpenAsync(MiniGameDefinition miniGameDef)
    {
        var plugIn = this._player.GameContext?.PlugInManager
            .GetStrategy<MiniGameType, IPeriodicMiniGameStartPlugIn>(miniGameDef.Type);
        if (plugIn is null)
        {
            this._logger.LogDebug("[EventExec] 无定时策略 {Type}, 假设始终开放", miniGameDef.Type);
            return true;
        }

        // 查询距离下次开放时间
        var duration = await plugIn.GetDurationUntilNextStartAsync(
                this._player.GameContext!, miniGameDef)
            .ConfigureAwait(false);

        if (duration == TimeSpan.Zero)
        {
            this._logger.LogInformation("[EventExec] {Type} Lv.{Level} 当前开放!", miniGameDef.Type, miniGameDef.GameLevel);
            return true;
        }

        // 距开放小于 60 秒 → 假设开放，避免 PeriodicTask 状态缓存与时间窗口的微小偏差
        if (duration is null || duration.Value.TotalSeconds <= 60)
        {
            this._logger.LogInformation("[EventExec] {Type} Lv.{Level} 距开放<60s, 假设开放",
                miniGameDef.Type, miniGameDef.GameLevel);
            return true;
        }

        // 超过 60 秒 → 未开放
        var remain = (int)duration.Value.TotalMinutes;
        this._logger.LogInformation("[EventExec] {Type} Lv.{Level} 活动未开放(>{Remain}分钟), 跳过",
            miniGameDef.Type, miniGameDef.GameLevel, remain);
        return false;
    }

    /// <summary>
    /// 调用 EnterMiniGameAction 尝试进入副本.
    /// </summary>
    private async ValueTask<StepResult> TryEnterAsync(MiniGameType miniGameType, int gameLevel, byte ticketSlot)
    {
        await this._enterAction.TryEnterMiniGameAsync(
                this._player,
                miniGameType,
                gameLevel,
                ticketSlot)
            .ConfigureAwait(false);

        if (this._player.CurrentMiniGame is not null)
        {
            this._logger.LogInformation(
                "[EventExec] 成功进入 {Type} Lv.{Level}",
                miniGameType,
                gameLevel);
            return StepResult.Completed;
        }

        this._logger.LogWarning(
            "[EventExec] 进入 {Type} Lv.{Level} 失败（等级/门票/时间条件不满足）",
            miniGameType,
            gameLevel);
        return StepResult.Failed;
    }

    /// <summary>
    /// 门票缺失处理：检查合成材料 → 注入合成任务或打材料任务。
    /// </summary>
    private StepResult HandleMissingTicket(MiniGameDefinition miniGameDef, MissionItem eventItem)
    {
        var craftMissionId = $"craft_ticket_{miniGameDef.Type}_{miniGameDef.GameLevel}";
        var existingCraftMission = this._boardState.Missions.FirstOrDefault(m => m.Id == craftMissionId);

        // 已有合成任务且已完成 → 但背包仍无门票 → 合成失败/材料被消耗但没出票
        if (existingCraftMission is not null && existingCraftMission.Status == MissionStatus.Completed)
        {
            return StepResult.Failed;
        }

        // 已有合成任务正在执行中 → 等待
        if (existingCraftMission is not null)
        {
            return StepResult.InProgress;
        }

        // 还没有任务 → 根据材料有无注入对应任务
        if (this.CheckTicketCraftingMaterials(miniGameDef))
        {
            this.EnsureTicketCraftingMissionExists(miniGameDef, eventItem);
            this._logger.LogInformation(
                "[EventExec] 有门票材料无门票, 生成合成任务处理 {Type} Lv.{Level}",
                miniGameDef.Type,
                miniGameDef.GameLevel);
        }
        else
        {
            this.EnsureTicketMaterialFarmingMissionExists(miniGameDef, eventItem);
            this._logger.LogInformation(
                "[EventExec] 无门票无材料, 生成打材料任务 {Type} Lv.{Level}",
                miniGameDef.Type,
                miniGameDef.GameLevel);
        }

        // 将事件任务降为 Pending, 让链路自动切换到新注入的任务
        eventItem.Status = MissionStatus.Pending;
        return StepResult.InProgress;
    }

    /// <summary>
    /// 检查背包是否有门票合成材料。
    /// 各副本的材料定义：
    ///   BloodCastle: Scroll of Archangel(13,16) + Blood Bone(13,17) + Jewel of Chaos(12,15)
    ///   DevilSquare: Devil's Eye(14,17) + Devil's Key(14,18) + Jewel of Chaos(12,15)
    /// </summary>
    private bool CheckTicketCraftingMaterials(MiniGameDefinition miniGameDef)
    {
        if (miniGameDef.TicketItem is null)
        {
            return false;
        }

        var ticketLevel = miniGameDef.TicketItemLevel;

        // 混沌宝石所有人都需要: Group=12, Number=15
        if (!HasItemByGroupNumber(12, 15, 0))
        {
            return false;
        }

        // 根据事件类型检查对应的两个材料
        return miniGameDef.Type switch
        {
            MiniGameType.BloodCastle =>
                HasItemByGroupNumber(13, 16, ticketLevel)   // Scroll of Archangel
                && HasItemByGroupNumber(13, 17, ticketLevel), // Blood Bone
            MiniGameType.DevilSquare =>
                HasItemByGroupNumber(14, 17, ticketLevel)   // Devil's Eye
                && HasItemByGroupNumber(14, 18, ticketLevel), // Devil's Key
            _ => false,
        };
    }

    /// <summary>
    /// 在玩家背包中查找指定 Group/Number/Level 的物品。
    /// </summary>
    private bool HasItemByGroupNumber(int group, int number, int level)
    {
        var inv = this._player.Inventory;
        if (inv is null) return false;
        return inv.Items.Any(i =>
            i.Definition?.Group == group && i.Definition?.Number == number && i.Level == level);
    }

    /// <summary>
    /// 注入门票合成任务到看板，并将它设为事件任务的前置依赖。
    /// </summary>
    private void EnsureTicketCraftingMissionExists(MiniGameDefinition miniGameDef, MissionItem eventItem)
    {
        var missionId = $"craft_ticket_{miniGameDef.Type}_{miniGameDef.GameLevel}";

        // 防止重复注入
        if (this._boardState.Missions.Any(m => m.Id == missionId))
        {
            return;
        }

        var craftingMission = new MissionItem
        {
            Id = missionId,
            Title = $"合成{miniGameDef.Name}门票",
            Priority = 12,           // 比事件任务(15)高, 先合门票再入场
            Type = MissionType.ItemFarm,
            Category = QuestCategory.AiCustom,
            Module = "crafting_executor",
            FailureRetryable = true,
            MaxRepeatCount = -1,
            Context = new Dictionary<string, object>
            {
                { "TicketItemGroup", miniGameDef.TicketItem?.Group ?? 0 },
                { "TicketItemNumber", miniGameDef.TicketItem?.Number ?? 0 },
            },
        };

        this._boardState.Missions.Add(craftingMission);
        this._logger.LogInformation(
            "[EventExec] 注入合成任务 {Id} -> {Title}",
            missionId,
            craftingMission.Title);

        // 设为事件任务的依赖
        eventItem.Dependencies = new[] { missionId };
    }

    /// <summary>
    /// 注入打门票材料任务到看板，并将它设为事件任务的前置依赖。
    /// 使用 MaterialKnowledgeService.GetTicketMaterialDropInfo 确定目标怪物、地图和材料等级。
    /// </summary>
    private void EnsureTicketMaterialFarmingMissionExists(MiniGameDefinition miniGameDef, MissionItem eventItem)
    {
        var missionId = $"farm_ticket_{miniGameDef.Type}_{miniGameDef.GameLevel}";

        // 防止重复注入
        if (this._boardState.Missions.Any(m => m.Id == missionId))
        {
            return;
        }

        // 获取玩家等级
        var playerLevel = this._adapter.GetPlayerLevel();

        // 使用 MaterialKnowledgeService 计算最佳材料等级和掉落来源
        var bestDrop = this._materialKnowledge.GetTicketMaterialDropInfo(miniGameDef.Type, miniGameDef.GameLevel, playerLevel);
        if (bestDrop is null)
        {
            this._logger.LogWarning(
                "[EventExec] GetTicketMaterialDropInfo 返回 null，无法确定刷取目标 (Type={Type} Lv={GameLevel} PlayerLv={PlayerLv})",
                miniGameDef.Type,
                miniGameDef.GameLevel,
                playerLevel);
            return;
        }

        // 从材料知识服务获取需要刷的材料列表
        var materials = this._materialKnowledge.GetTicketCraftingMaterials(miniGameDef.Type, miniGameDef.GameLevel);

        // 对每个缺少的材料（跳过混沌宝石），创建独立的 farm 任务
        if (materials.Count > 0)
        {
            var dependencies = new List<string>();

            foreach (var mat in materials)
            {
                // 跳过混沌宝石（全局掉落，不需要专门刷取）
                if (mat.Group == 12 && mat.Number == 15)
                {
                    continue;
                }

                // 跳过背包中已有的材料（检查对应等级）
                var inv = this._player.Inventory;
                var hasMaterial = inv?.Items.Any(i =>
                    i.Definition?.Group == mat.Group
                    && i.Definition?.Number == mat.Number
                    && i.Durability > 0
                    && i.Level == bestDrop.TargetLevel) == true;

                if (hasMaterial)
                {
                    continue;
                }

                // 查找该材料对应等级（bestDrop.TargetLevel）的掉落来源
                var dropSources = this._materialKnowledge.GetDropSources(mat.Group, mat.Number);
                var matchedSource = dropSources.FirstOrDefault(s => s.ItemLevel == bestDrop.TargetLevel);
                if (matchedSource is null)
                {
                    // Fallback: 使用 bestDrop 的怪物和地图
                    this._logger.LogWarning(
                        "[EventExec] 材料 {Name}({Group},{Number}) 无 Lv.{Level} 等级匹配的掉落来源，使用 bestDrop 的默认值",
                        mat.Name, mat.Group, mat.Number, bestDrop.TargetLevel);
                }

                var source = matchedSource ?? new DropSourceInfo(
                    MonsterNumber: bestDrop.MonsterNumber,
                    MonsterName: bestDrop.MonsterName,
                    MapNumber: bestDrop.MapNumber,
                    MapName: bestDrop.MapName,
                    ItemLevel: bestDrop.TargetLevel);

                var matMissionId = $"{missionId}_{mat.Group}_{mat.Number}";

                var farmMission = new MissionItem
                {
                    Id = matMissionId,
                    Title = $"刷取{mat.Name}+{bestDrop.TargetLevel}",
                    Priority = 11,
                    Type = MissionType.ItemFarm,
                    Category = QuestCategory.AiCustom,
                    Module = "material_farm",
                    FailureRetryable = true,
                    MaxRepeatCount = 5,
                    Context = new Dictionary<string, object>
                    {
                        { "ItemGroup", mat.Group },
                        { "ItemNumber", mat.Number },
                        { "TargetLevel", (int)bestDrop.TargetLevel },
                        { "MonsterNumber", source.MonsterNumber },
                        { "MapNumber", (ushort)source.MapNumber },
                        { "RequiredCount", mat.RequiredCount },
                    },
                };

                this._boardState.Missions.Add(farmMission);
                dependencies.Add(matMissionId);

                this._logger.LogInformation(
                    "[EventExec] 注入打材料任务 {Id} -> 刷取{Name}+{Level} (怪物#{Monster} @地图#{Map})",
                    matMissionId,
                    mat.Name,
                    bestDrop.TargetLevel,
                    source.MonsterNumber,
                    source.MapNumber);
            }

            if (dependencies.Count > 0)
            {
                eventItem.Dependencies = dependencies.ToArray();
            }
            else
            {
                // 所有材料都有了（包括对应等级）→ 直接升级为合成任务
                this.EnsureTicketCraftingMissionExists(miniGameDef, eventItem);
            }
        }
        else
        {
            // 不知道需要什么材料 → 用旧的 survival 方式
            var farmingMission = new MissionItem
            {
                Id = missionId,
                Title = $"收集{miniGameDef.Name}门票材料",
                Priority = 11,
                Type = MissionType.Survival,
                Category = QuestCategory.AiCustom,
                Module = "survival",
                FailureRetryable = true,
                MaxRepeatCount = 3,
            };

            this._boardState.Missions.Add(farmingMission);
            this._logger.LogInformation(
                "[EventExec] 注入打材料(fallback)任务 {Id} -> {Title}",
                missionId,
                farmingMission.Title);

            eventItem.Dependencies = new[] { missionId };
        }
    }
}
