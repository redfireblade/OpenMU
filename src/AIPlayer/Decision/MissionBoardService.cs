// <copyright file="MissionBoardService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AIPlayer.Scripting;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;

/// <summary>
/// 任务看板初始化器。
/// AI 角色登录时调用一次 InitializeAsync 填充 BoardState，
/// 后续不刷新、不维护、不作决策。
/// 看板的数据由外部系统（HeartbeatService/AIODS）读写。
/// </summary>
public sealed class MissionBoardService
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;

    /// <summary>看板核心状态 — 大脑的事实中心。</summary>
    private readonly BoardState _boardState = new();

    private bool _initialized;

    /// <summary>标准背包格子数 (不含装备栏)。</summary>
    private const int RegularInventorySlots = 64;

    /// <summary>默认配额配置。</summary>
    private static readonly QuestCategoryQuota[] DefaultQuotas = new QuestCategoryQuota[]
    {
        new() { Category = QuestCategory.MainStory, MaxActive = 3, EffectiveLevel = 0 },
        new() { Category = QuestCategory.SideQuest, MaxActive = 5, EffectiveLevel = 150 },
        new() { Category = QuestCategory.Daily, MaxActive = 3, EffectiveLevel = 0 },
        new() { Category = QuestCategory.Random, MaxActive = 2, EffectiveLevel = 220 },
        new() { Category = QuestCategory.InstanceEvent, MaxActive = 2, EffectiveLevel = 0 },
    };

    public MissionBoardService(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
    }

    /// <summary>获取看板核心状态 — 世界事件 + 任务 + 角色快照。</summary>
    public BoardState BoardState => this._boardState;

    /// <summary>是否已初始化。</summary>
    public bool IsInitialized => this._initialized;

    /// <summary>
    /// AI 角色登录时调用一次：扫 GameConfiguration 填任务看板。
    /// 后续不会再被调用。任务进度通过 HeartbeatService 与 NPC 交互同步。
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        if (this._initialized)
        {
            this._logger.LogDebug("[MissionBoard] 看板已初始化，跳过");
            return;
        }

        var config = this._player.GameContext?.Configuration;
        if (config is null)
        {
            this._logger.LogWarning("[MissionBoard] 无法读取 GameConfiguration，看板为空");
            return;
        }

        var level = this._adapter.GetPlayerLevel();
        var charClass = this._player.SelectedCharacter?.CharacterClass;
        var charClassNumber = charClass?.Number ?? 0;
        var questStates = this._player.SelectedCharacter?.QuestStates;
        var allQuests = config.Monsters.SelectMany(m => m.Quests).ToList();

        // ===== Phase 1: 游戏 Quest → MissionItem（扩展分类字段） =====
        var priorityMap = new Dictionary<QuestCategory, int>
        {
            { QuestCategory.MainStory, 10 },
            { QuestCategory.SideQuest, 20 },
            { QuestCategory.Daily, 30 },
            { QuestCategory.Random, 40 },
        };

        foreach (var q in allQuests)
        {
            if (level < q.MinimumCharacterLevel) continue;
            if (q.MaximumCharacterLevel > 0 && level > q.MaximumCharacterLevel) continue;
            if (q.QualifiedCharacter is not null && q.QualifiedCharacter.Number != charClassNumber) continue;
            if (IsQuestCompletelyFinished(q, questStates)) continue;

            var category = MapQuestCategory(q.Group);
            var priority = priorityMap.TryGetValue(category, out var p) ? p : 50;

            this._boardState.Missions.Add(new MissionItem
            {
                Id = $"{category}_g{q.Group}n{q.Number}",
                Title = q.Name.ToString() ?? $"任务 G{q.Group}#{q.Number}",
                Priority = priority,
                Type = MissionType.Quest,
                QuestDef = q,
                QuestGroup = q.Group,
                QuestNumber = q.Number,
                Repeatable = q.Repeatable,
                Module = "quest_executor",
                TriggerEvents = new[] { "quest_state_changed", "level_up" },
                Category = category,
                Goal = DetectQuestGoal(q),
                Source = QuestSource.GameSystem,
                MaxLevel = q.MaximumCharacterLevel,
                FailureRetryable = true,
                MaxRepeatCount = q.Repeatable ? -1 : 0,
            });

            // G18 蜘蛛任务是日常可重复，一天最多打5轮
            if (q.Group == 18)
            {
                var lastAdded = this._boardState.Missions[^1];
                lastAdded.DailyMaxCount = 5;
                this._logger.LogDebug("[MissionBoard] 📅 G18 任务 {Id} 每日上限5次", lastAdded.Id);
            }
        }

        // ===== Phase 2: MiniGame 事件注入（由 DynamicMissionGenerator 和 EventWatcherService 动态管理） =====
        // 启动时不注入事件任务。事件任务在活动开放时(Prepared/Started)由 SystemEventScanner 每 10 秒扫描注入，
        // 或在 EventWatcherService 检测到状态变化时通过 EventOpenEvent 注入。
        // 这防止了 NotStarted 事件被错误选中执行入场流程。

        // ===== Phase 3: AI 自定义/群体任务（预留） =====
        // TODO: AIODS 发布任务时添加

        // ===== Phase 4: 等级配额过滤 =====
        var filtered = this.ApplyQuota(this._boardState.Missions, level);
        this._boardState.Missions = filtered;

        // ===== Phase 5: 兜底生存刷怪 -- 已移到 DynamicMissionGenerator Layer 6 =====

        // ===== Phase 6: 材料需求知识初始化 =====
        try
        {
            var materialService = new MaterialRequirementService(
                config, this._player, this._adapter, this._logger);
            var needs = materialService.AnalyzeAll();
            this._boardState.MaterialNeeds = needs;

            // 对于材料不满足的，注入 farm_* 任务（每个材料缺口的第一个材料前驱）
            foreach (var need in needs)
            {
                if (need.Materials.All(m => m.IsSufficient))
                {
                    continue; // 材料充足，无需任务
                }

                // 找第一个不充足的材料
                var missing = need.Materials.FirstOrDefault(m => !m.IsSufficient);
                if (missing is null)
                {
                    continue;
                }

                if (missing.Status != MaterialStatus.NeedFarm &&
                    missing.Status != MaterialStatus.NeedVault)
                {
                    continue;
                }

                var missionId = $"farm_mat_{missing.Group}_{missing.Number}";
                if (this._boardState.Missions.Any(m => m.Id == missionId))
                {
                    continue;
                }

                // 门票材料额外多打: 按玩家等级决定要打多少份
                var baseNeed = missing.RequiredCount - missing.Total;
                var extraCount = baseNeed;
                if (missing.RequiredLevel > 0 && level >= 200)
                    extraCount = Math.Max(baseNeed, 3);  // 高等级玩家多囤
                else if (missing.RequiredLevel > 0 && level >= 100)
                    extraCount = Math.Max(baseNeed, 2);  // 中等等级适量囤

                // 只注入有具体掉落来源的任务（跳过全局掉落物，如宝石类）
                if (!missing.FarmMonsterNumber.HasValue)
                {
                    this._logger.LogDebug(
                        "[MissionBoard] 跳过材料任务 {Group}_{Number}: 无具体怪物掉落来源",
                        missing.Group, missing.Number);
                    continue;
                }

                var farmMission = new MissionItem
                {
                    Id = missionId,
                    Title = $"刷{missing.Name}",
                    Priority = 20, // 中等优先级
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
                        { "RequiredCount", extraCount },
                    },
                };

                if (missing.FarmMonsterNumber.HasValue)
                {
                    farmMission.Context["MonsterNumber"] = missing.FarmMonsterNumber.Value;
                    farmMission.Context["MapNumber"] = missing.FarmMapNumber ?? 0;
                }

                this._boardState.Missions.Add(farmMission);
                var extraLog = extraCount > baseNeed ? $", 多囤{extraCount - baseNeed}张" : "";
                this._logger.LogInformation(
                    "[MissionBoard] 📋 材料任务: {Id} -> {Title} (等级{Lv}, 缺{Need}个{Extra})",
                    missionId,
                    farmMission.Title,
                    missing.RequiredLevel,
                    extraCount,
                    extraLog);
            }

            // 对于门票类合成需求，同时注入 craft_ticket_* 任务
            foreach (var need in needs)
            {
                if (need.Materials.All(m => m.IsSufficient) || need.IsSatisfied)
                    continue;

                // 从 need 查 MiniGameDefinition
                var miniGameDef = config.MiniGameDefinitions
                    .FirstOrDefault(d => d.TicketItem?.Group == need.TargetGroup
                                      && d.TicketItem?.Number == need.TargetNumber);
                if (miniGameDef is null)
                    continue;

                var craftId = $"craft_ticket_{miniGameDef.Type}_{miniGameDef.GameLevel}";
                if (this._boardState.Missions.Any(m => m.Id == craftId))
                    continue;

                // 找到对应的 farm 任务 ID
                var farmIds = need.Materials
                    .Where(m => !m.IsSufficient && (m.Status == MaterialStatus.NeedFarm || m.Status == MaterialStatus.NeedVault))
                    .Select(m => $"farm_mat_{m.Group}_{m.Number}")
                    .ToArray();

                var craftMission = new MissionItem
                {
                    Id = craftId,
                    Title = $"合成{miniGameDef.Name}门票(Lv.{miniGameDef.GameLevel})",
                    Priority = 18, // 比 farm 高比事件低
                    Type = MissionType.ItemFarm,
                    Category = QuestCategory.InstanceEvent,
                    Module = "crafting_executor",
                    FailureRetryable = true,
                    MaxRepeatCount = 5,
                    TargetType = miniGameDef.Type,
                    TargetLevel = miniGameDef.TicketItemLevel,
                    Dependencies = farmIds, // 所有 farm 任务完成后才能合成
                    Context = new Dictionary<string, object>
                    {
                        { "MiniGameType", (int)miniGameDef.Type },
                        { "MiniGameLevel", miniGameDef.GameLevel },
                        { "TicketItemGroup", miniGameDef.TicketItem?.Group ?? 0 },
                        { "TicketItemNumber", miniGameDef.TicketItem?.Number ?? 0 },
                    },
                };

                // 副本门票合成任务的每日可用次数限制
                craftMission.DailyMaxCount = miniGameDef.Type switch
                {
                    MiniGameType.DevilSquare => 3,
                    MiniGameType.BloodCastle => 3,
                    MiniGameType.ChaosCastle => 3,
                    _ => 0,
                };

                this._boardState.Missions.Add(craftMission);
                this._logger.LogInformation("[MissionBoard] 📋 craft任务: {Id} -> {Title} (依赖:{Deps})",
                    craftId, craftMission.Title, string.Join(",", farmIds));
            }

            this._logger.LogInformation(
                "[MissionBoard] 📋 材料需求分析完成: {Count} 项目标",
                needs.Count);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(
                "[MissionBoard] 材料需求分析跳过: {Msg}", ex.Message);
        }

        this._boardState.Missions.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        // 同步角色状态
        this.SyncPlayerState();

        this._initialized = true;

        // 从游戏服务器读取已有任务进度写上看板
        this.SyncActiveQuestToBoard();

        this._logger.LogInformation(
            "[MissionBoard] 看板初始化: {Count} 项, 角色等级 {Lv}",
            this._boardState.Missions.Count, level);
    }

    /// <summary>Group → QuestCategory 映射。Season 6 实际配置中仅有 Group 0(主线) 和 Group 18(日常)。</summary>
    private static QuestCategory MapQuestCategory(short group) => group switch
    {
        0 => QuestCategory.MainStory,
        18 => QuestCategory.Daily,
        _ => QuestCategory.SideQuest,
    };

    /// <summary>从 QuestDefinition 自动检测任务目的。</summary>
    private static QuestGoal DetectQuestGoal(QuestDefinition quest)
    {
        var goal = QuestGoal.Kill;

        if (quest.RequiredMonsterKills is { Count: > 0 })
            goal |= QuestGoal.Kill;

        if (quest.RequiredItems is { Count: > 0 })
            goal |= QuestGoal.Collect;

        if (quest.RequiresClientAction)
            goal |= QuestGoal.Dialogue;

        if (quest.Rewards is not null)
        {
            foreach (var r in quest.Rewards)
            {
                if (r.RewardType is QuestRewardType.CharacterEvolutionFirstToSecond
                    or QuestRewardType.CharacterEvolutionSecondToThird)
                {
                    goal |= QuestGoal.ClassAdvancement;
                    break;
                }
            }
        }

        return goal;
    }

    /// <summary>按类别配额过滤任务列表。</summary>
    private List<MissionItem> ApplyQuota(List<MissionItem> items, int level)
    {
        var result = new List<MissionItem>(items.Count);
        var categoryCounts = new Dictionary<QuestCategory, int>();

        foreach (var item in items)
        {
            var quota = DefaultQuotas.FirstOrDefault(q => q.Category == item.Category);
            if (quota is not null && level >= quota.EffectiveLevel)
            {
                categoryCounts.TryGetValue(item.Category, out var count);
                if (count >= quota.MaxActive)
                {
                    this._logger.LogDebug("[MissionBoard] 配额跳过: {Title} (类别={Category}, 已达上限{Max})",
                        item.Title, item.Category, quota.MaxActive);
                    continue;
                }
                categoryCounts[item.Category] = count + 1;
            }
            result.Add(item);
        }

        return result;
    }

    /// <summary>
    /// 每 tick 从 IGameAdapter 同步角色当前状态到 BoardState.PlayerState。
    /// 供决策层零成本读取。
    /// </summary>
    public void SyncPlayerState()
    {
        var hp = this._adapter.GetCurrentHp();
        var maxHp = this._adapter.GetMaxHp();
        var mp = this._adapter.GetCurrentMp();
        var maxMp = this._adapter.GetMaxMp();
        var pos = this._adapter.GetPlayerPosition();
        var map = this._adapter.GetCurrentMap();

        this._boardState.PlayerState.HpPercent = maxHp > 0 ? (int)((float)hp / maxHp * 100) : 0;
        this._boardState.PlayerState.MpPercent = maxMp > 0 ? (int)((float)mp / maxMp * 100) : 0;
        this._boardState.PlayerState.Position = pos;
        this._boardState.PlayerState.MapNumber = (ushort)(map?.Definition?.Number ?? 0);
        this._boardState.PlayerState.InventoryFullness = this.CalculateInventoryFullness();

        // 安全区检测 — AiMap removed, always false
        this._boardState.PlayerState.IsInSafeZone = false;

        this._boardState.PlayerState.IsWalking = this._adapter.IsPlayerWalking();
        this._boardState.PlayerState.Level = this._adapter.GetPlayerLevel();
    }

    /// <summary>
    /// 从游戏服务器查询当前活跃任务，同步到看板。
    /// 不推倒看板，只更新对应条目的状态、进度和阶段树。
    /// 跳过 IsDeadTask 的条目——看板的死任务判定优先级高于游戏服务器同步。
    /// </summary>
    public void SyncActiveQuestToBoard()
    {
        var activeQuests = this._adapter.GetActiveQuests();
        foreach (var aq in activeQuests)
        {
            var entry = this._boardState.Missions.FirstOrDefault(m =>
                m.QuestGroup == aq.Group && m.QuestNumber == aq.Number && m.Type == MissionType.Quest);
            if (entry is null) continue;

            // AR-24: 看板纯数据 — 死任务的判定由 HeartbeatService 的决策层负责,
            // 游戏服务器的活跃状态不应覆盖看板已做出的失败/死亡判定
            if (entry.IsDeadTask) continue;

            entry.Status = MissionStatus.Active;
            this.BuildStagesForEntry(entry, aq);
        }
    }

    /// <summary>
    /// 为看板条目构建任务阶段树 (accept → hunt → submit)。
    /// 根据游戏服务器当前任务进度设置阶段状态。
    /// </summary>
    public void BuildStagesForEntry(MissionItem entry, ActiveQuestInfo aq)
    {
        if (entry.Stages.Count > 0) return;

        var stages = new List<TaskStage>();

        // stage 0: 接任务 — 有 ActiveQuest 说明已完成
        stages.Add(new TaskStage
        {
            Id = "accept",
            Label = "接任务",
            Status = TaskStageStatus.Completed,
            Action = "walk_to_quest_npc",
            Context = new Dictionary<string, object>
            {
                { "questNpcNumber", entry.QuestDef?.QuestGiver?.Number ?? 0 },
                { "questGroup", entry.QuestGroup },
                { "questNumber", entry.QuestNumber },
            },
        });

        // stage 1: 狩猎 (如有杀怪要求)
        if (aq.RequiredKills is { Count: > 0 })
        {
            var killProgress = new List<KillProgress>();
            foreach (var kr in aq.RequiredKills)
            {
                killProgress.Add(new KillProgress
                {
                    MonsterNumber = kr.MonsterNumber,
                    MonsterName = kr.MonsterName,
                    CurrentKills = kr.Current,
                    RequiredKills = kr.Required,
                });
            }

            entry.KillProgress = killProgress;

            var allCompleted = killProgress.All(k => k.CurrentKills >= k.RequiredKills);
            stages.Add(new TaskStage
            {
                Id = "hunt",
                Label = "狩猎",
                Status = allCompleted ? TaskStageStatus.Completed : TaskStageStatus.Wip,
                Action = "find_nearest_monster",
            });
        }

        // stage 2: 提交任务
        stages.Add(new TaskStage
        {
            Id = "submit",
            Label = "提交任务",
            Status = TaskStageStatus.Pending,
            Action = "walk_to_quest_npc",
            Context = new Dictionary<string, object>
            {
                { "questNpcNumber", entry.QuestDef?.QuestGiver?.Number ?? 0 },
                { "questGroup", entry.QuestGroup },
                { "questNumber", entry.QuestNumber },
            },
        });

        entry.Stages = stages;
    }

    /// <summary>
    /// 计算背包占用率。
    /// 统计常规背包格 (slot &gt; 11) 的已占用比例。
    /// </summary>
    private float CalculateInventoryFullness()
    {
        var inv = this._player.Inventory;
        if (inv is null) return 0f;

        var occupied = 0;
        foreach (var item in inv.Items)
        {
            if (item.ItemSlot > InventoryConstants.LastEquippableItemSlotIndex)
            {
                occupied++;
            }
        }

        return (float)occupied / RegularInventorySlots;
    }

    /// <summary>
    /// Clears all missions from the board state.
    /// Called when the AI needs to reset its mission plan (e.g., during
    /// death-loop escalation) so it re-evaluates from scratch rather than
    /// running back to a dangerous hotspot.
    /// </summary>
    public void ClearAllMissions()
    {
        this._boardState.Missions.Clear();
        this._boardState.WorldEvents.Clear();
        this._logger.LogInformation("[MissionBoard] 已清空看板任务 — 死亡循环退避");
    }

    private bool IsQuestCompletelyFinished(QuestDefinition quest, ICollection<CharacterQuestState>? questStates)
    {
        if (questStates is null || questStates.Count == 0) return false;
        var state = questStates.FirstOrDefault(qs => qs.Group == quest.Group);
        if (state?.LastFinishedQuest is null) return false;
        if (state.LastFinishedQuest.Number != quest.Number) return false;

        // 不可重复且已完成 → 算完成
        if (!quest.Repeatable) return true;

        // 可重复任务：如果 LastFinished ≥ quest 且 CharacterQuestState 的 ActiveQuest 不等于 quest → 不重复出现
        // 但如果 ActiveQuest == quest（游戏中已激活），应该出现在看板
        return false;
    }
}
