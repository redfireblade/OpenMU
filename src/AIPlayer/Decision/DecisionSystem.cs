// <copyright file="DecisionSystem.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.AIPlayer.Scripting;
using MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// 决策系统 — 从 HeartbeatService 中提取的所有决策/任务管理逻辑。
///
/// 职责：
///   1. 全看板6层扫描选择当前要执行的任务
///   2. 失败/阻塞/恢复管理
///   3. 任务推进和脚本构建
///   4. 热点推导和任务脚本工厂
/// </summary>
public sealed class DecisionSystem
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly MissionBoardService _missionBoard;
    private readonly BehaviorContext _context;
    private readonly DynamicMissionGenerator _missionGenerator;
    private readonly GoalScheduler _goalScheduler;
    private readonly InventoryManagerService _inventoryManager;
    private readonly MarketPriceService _marketPrice;
    private readonly IReadOnlyDictionary<string, IBehaviorSubModule> _modules;
    private readonly ILogger _logger;

    // Internal decision state (mirrors HeartbeatService fields for reset/signaling)
    private MissionItem? _lastTask;
    private ScriptExecutor? _scriptExecutor;
    private int _idleTicks;
    private int _taskTicks;
    private int _noTargetStreak;

    /// <summary>
    /// Initializes a new instance of the <see cref="DecisionSystem"/> class.
    /// </summary>
    public DecisionSystem(
        AiPlayer player,
        IGameAdapter adapter,
        MissionBoardService missionBoard,
        BehaviorContext context,
        DynamicMissionGenerator missionGenerator,
        GoalScheduler goalScheduler,
        InventoryManagerService inventoryManager,
        MarketPriceService marketPrice,
        IReadOnlyDictionary<string, IBehaviorSubModule> modules,
        ILogger logger,
        IKnowledgeGraphQuery? knowledgeGraphQuery = null)
    {
        this._player = player;
        this._adapter = adapter;
        this._missionBoard = missionBoard;
        this._context = context;
        this._missionGenerator = missionGenerator;
        this._goalScheduler = goalScheduler;
        this._inventoryManager = inventoryManager;
        this._marketPrice = marketPrice;
        this._modules = modules;
        this._logger = logger;
        this.KnowledgeGraphQuery = knowledgeGraphQuery;
    }

    /// <summary>
    /// Gets the knowledge graph query service for KG-aware condition evaluation.
    /// When null, KG-based conditions (e.g., <c>kg_has_dependencies</c>) return <c>false</c>.
    /// </summary>
    public IKnowledgeGraphQuery? KnowledgeGraphQuery { get; }

    // ===================== 决策核心 — 六层全看板扫描 =====================

    /// <summary>
    /// 每 tick 全看板6层扫描，选择当前要执行的任务。
    ///
    /// Layer 1: Active — 正在进行的，继续执行
    /// Layer 1.5: 检查阻塞任务是否恢复
    /// Layer 2: Failed — 失败的，判断是否可重试
    /// Layer 3: 注入系统事件+道具需求任务
    /// Layer 4: Pending — 新任务，检查依赖条件
    /// Layer 5: Fallback 生存兜底
    /// </summary>
    public async ValueTask<MissionItem?> SelectCurrentTaskAsync()
    {
        var board = this._missionBoard.BoardState;

        // Layer 1: Active
        var active = board.Missions.FirstOrDefault(m => m.Status == MissionStatus.Active);
        if (active is not null) return active;

        // Layer 1.5: Unblock
        this.TryUnblockTasks();

        // Layer 2: Failed retryable
        var retryable = this.SelectRetryableFailedTask(board);
        if (retryable is not null) return retryable;

        // Layer 3: 注入系统事件+道具需求任务
        await this._missionGenerator.GenerateMissionsAsync().ConfigureAwait(false);
        board.Missions.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        // Layer 4: Pending
        var pending = this.SelectPendingTask(board);
        if (pending is not null) return pending;

        // Layer 5: Fallback survival
        return this.GetOrCreateFallbackTask(board);
    }

    /// <summary>
    /// Layer 2: 从 Failed 任务中选出一个可重试的。
    /// 跳过规则：NotRetryable / IsDeadTask / 超3次 / 超1年。
    /// 可重试 → 重置回 Active 开始执行。
    /// </summary>
    private MissionItem? SelectRetryableFailedTask(BoardState board)
    {
        foreach (var task in board.Missions)
        {
            if (task.Status != MissionStatus.Failed)
                continue;

            // 不可重试原因 → 跳过
            if (task.FailureReason == FailureReason.NotRetryable)
            {
                task.IsDeadTask = true;
                continue;
            }

            // 已标记死任务 → 跳过
            if (task.IsDeadTask)
            {
                // Fix G: Goal 任务死掉时同步通知 GoalScheduler（兜底保护）
                if (task.Id.StartsWith("goal_"))
                {
                    this._goalScheduler.MarkGoalFailed(task.Id);
                }
                continue;
            }

            // 重试 >= 3 次 → 标死任务，跳过
            if (task.RetryCount >= 3)
            {
                this._logger.LogInformation("[HB] 💀 任务 {Title} 已重试 {N} 次，标记 IsDeadTask", task.Title, task.RetryCount);
                task.IsDeadTask = true;
                continue;
            }

            // 首次失败超 1 年且重试 >= 3 次 → 标死任务，跳过
            if (task.FirstFailedAt is not null &&
                (DateTime.UtcNow - task.FirstFailedAt.Value).TotalDays > 365 &&
                task.RetryCount >= 3)
            {
                this._logger.LogInformation("[HB] 💀 任务 {Title} 失败超 1 年且重试 {N} 次，标记 IsDeadTask", task.Title, task.RetryCount);
                task.IsDeadTask = true;
                continue;
            }

            // 不可重接 → 跳过 (Layer 2 不做标记，系统层面不再尝试)
            if (!task.FailureRetryable)
                continue;

            // 跳过当日已达上限的任务
            if (task.IsDailyLimitReached)
            {
                this._logger.LogDebug("[HB] ⏭ 重试任务 {Title} 当日已满 ({Count}/{Max})",
                    task.Title, task.DailyCount, task.DailyMaxCount);
                continue;
            }

            // AR-29: 重试前检查阻塞条件
            if (task.QuestDef is not null)
            {
                if (task.QuestDef.RequiredStartMoney > 0 && this._player.Money < task.QuestDef.RequiredStartMoney)
                {
                    this._logger.LogInformation("[HB] ⏸ 重试任务 {Title} 金币不足，标记 Blocked 而非 Failed", task.Title);
                    task.Status = MissionStatus.Blocked;
                    task.BlockedReason = BlockedReason.PrerequisiteNotMet;
                    continue;  // 不计重试
                }
                if (task.QuestDef.MinimumCharacterLevel > 0 && this._player.Level < task.QuestDef.MinimumCharacterLevel)
                {
                    this._logger.LogInformation("[HB] ⏸ 重试任务 {Title} 等级不足，标记 Blocked 而非 Failed", task.Title);
                    task.Status = MissionStatus.Blocked;
                    task.BlockedReason = BlockedReason.PrerequisiteNotMet;
                    continue;  // 不计重试
                }
            }

            // ✅ 可重试 → 激活
            this._logger.LogInformation("[HB] 🔄 重试任务 {Title} (重试 #{N})", task.Title, task.RetryCount + 1);
            task.Status = MissionStatus.Active;
            task.FailureReason = null;
            this.BuildScriptForTask(task);
            return task;
        }

        return null;
    }

    /// <summary>
    /// Layer 3: 从 Pending 任务中选出一个依赖条件满足的可执行任务。
    /// </summary>
    private MissionItem? SelectPendingTask(BoardState board)
    {
        foreach (var task in board.Missions)
        {
            if (task.Status != MissionStatus.Pending)
                continue;

            // 检查依赖：所有前置任务必须已完成
            if (task.Dependencies.Length > 0 &&
                task.Dependencies.Any(dep => board.Missions.Any(mm => mm.Id == dep && mm.Status != MissionStatus.Completed)))
                continue;

            // 跳过当日已达上限的任务
            if (task.IsDailyLimitReached)
            {
                this._logger.LogDebug("[HB] ⏭ 跳过任务 {Title} 当日已满 ({Count}/{Max})",
                    task.Title, task.DailyCount, task.DailyMaxCount);
                continue;
            }

            // AR-29: 阻塞检测 — 在 BuildScriptForTask 之前拦截
            if (task.QuestDef is not null)
            {
                if (task.QuestDef.RequiredStartMoney > 0 && this._player.Money < task.QuestDef.RequiredStartMoney)
                {
                    this._logger.LogInformation("[HB] ⏸ 任务 {Title} 金币不足 ({Money}/{Need})，跳过激活",
                        task.Title, this._player.Money, task.QuestDef.RequiredStartMoney);
                    task.Status = MissionStatus.Blocked;
                    task.BlockedReason = BlockedReason.PrerequisiteNotMet;
                    continue;
                }
                if (task.QuestDef.MinimumCharacterLevel > 0 && this._player.Level < task.QuestDef.MinimumCharacterLevel)
                {
                    this._logger.LogInformation("[HB] ⏸ 任务 {Title} 等级不足 ({Level}/{Need})，跳过激活",
                        task.Title, this._player.Level, task.QuestDef.MinimumCharacterLevel);
                    task.Status = MissionStatus.Blocked;
                    task.BlockedReason = BlockedReason.PrerequisiteNotMet;
                    continue;
                }
            }

            // 满足条件 → 激活
            this._logger.LogInformation("[HB] ➡️ 激活新任务 {Title}", task.Title);
            task.Status = MissionStatus.Active;
            this.BuildScriptForTask(task);
            return task;
        }

        return null;
    }

    /// <summary>
    /// Layer 5: 创建或获取生存兜底任务。
    /// 如果看板已有非完成/非死亡的 survival 任务，激活它。
    /// 否则新建一个 survival 任务并激活。
    /// </summary>
    private MissionItem? GetOrCreateFallbackTask(BoardState board)
    {
        var survival = board.Missions.FirstOrDefault(m =>
            m.Id == "survival" && m.Status != MissionStatus.Completed && !m.IsDeadTask);
        if (survival is not null)
        {
            survival.Status = MissionStatus.Active;
            this._logger.LogInformation("[HB] Layer 6: 使用现有生存兜底");
            return survival;
        }

        var newSurvival = new MissionItem
        {
            Id = "survival",
            Title = "生存模式 -- 自由刷怪/练级",
            Priority = 999,
            Type = MissionType.Survival,
            Category = QuestCategory.Survival,
            Module = "survival",
            FailureRetryable = true,
            MaxRepeatCount = -1,
        };
        board.Missions.Add(newSurvival);
        newSurvival.Status = MissionStatus.Active;
        this._logger.LogInformation("[HB] Layer 6: 创建新的生存兜底任务");
        return newSurvival;
    }

    /// <summary>
    /// 标记任务失败，记录失败原因/重试计数/首次失败时间/死任务判定。
    /// </summary>
    public void MarkTaskFailed(MissionItem task, FailureReason reason)
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

        if (task.IsDeadTask)
        {
            this._logger.LogInformation("[HB] 💀 任务 {Title} 标记为死任务 (原因={Reason}, 重试#{Retry})",
                task.Title, reason, task.RetryCount);
        }

        // Fix F: Goal 任务死掉时通知 GoalScheduler 跳过
        if (task.IsDeadTask && task.Id.StartsWith("goal_"))
        {
            this._goalScheduler.MarkGoalFailed(task.Id);
            this._logger.LogInformation("[HB] Goal 任务 {Id} 永久失败, 通知 GoalScheduler", task.Id);
        }

        this._taskTicks = 0;
        this._lastTask = null;
        this._scriptExecutor = null;
    }

    /// <summary>
    /// 标记任务阻塞 — 条件不足时暂挂，不累计重试计数/不标死任务。
    /// Blocked ≠ Failed: 不影响重试计数、不标 IsDeadTask、不积累超时。
    /// </summary>
    public void MarkTaskBlocked(MissionItem task, BlockedReason reason)
    {
        task.Status = MissionStatus.Blocked;
        task.BlockedReason = reason;
        task.BlockedEverChecked = false;
        this._taskTicks = 0;
        this._lastTask = null;
        this._scriptExecutor = null;
        this._logger.LogInformation("[HB] ⏸ 任务 {Title} 阻塞 (原因={Reason})", task.Title, reason);
    }

    /// <summary>
    /// 每 tick 检查阻塞任务的条件是否恢复。
    /// 只读系统 API（Money/Level），不执行脚本。
    /// </summary>
    private void TryUnblockTasks()
    {
        var board = this._missionBoard.BoardState;
        foreach (var task in board.Missions)
        {
            if (task.Status != MissionStatus.Blocked) continue;
            if (task.BlockedReason != BlockedReason.PrerequisiteNotMet) continue;
            if (task.QuestDef is null) continue;

            var money = this._player.Money;
            var level = this._player.Level;

            bool stillBlocked = false;

            // 金币恢复检查
            if (task.QuestDef.RequiredStartMoney > 0 && money < task.QuestDef.RequiredStartMoney)
            {
                if (!task.BlockedEverChecked)
                {
                    this._logger.LogInformation("[HB] 任务 {Title} 仍阻塞: 金币不足 ({Money}/{Need})",
                        task.Title, money, task.QuestDef.RequiredStartMoney);
                }
                stillBlocked = true;
            }

            // 等级检查
            if (!stillBlocked && task.QuestDef.MinimumCharacterLevel > level)
            {
                if (!task.BlockedEverChecked)
                {
                    this._logger.LogInformation("[HB] 任务 {Title} 仍阻塞: 等级不足 ({Level}/{Need})",
                        task.Title, level, task.QuestDef.MinimumCharacterLevel);
                }
                stillBlocked = true;
            }

            task.BlockedEverChecked = true;

            if (!stillBlocked)
            {
                // 条件满足 → 恢复为 Pending（让决策层重新激活）
                task.Status = MissionStatus.Pending;
                task.BlockedReason = null;
                task.BlockedEverChecked = false;
                this._logger.LogInformation("[HB] ✅ 任务 {Title} 条件恢复，重新加入候选", task.Title);
            }
        }
    }

    /// <summary>推进任务:标记完成。不含材料重评估（由心跳层负责）。</summary>
    public void AdvanceTask(MissionItem current)
    {
        current.Status = MissionStatus.Completed;
        // 可重复任务递增计数
        if (current.MaxRepeatCount > 0)
        {
            current.RepeatCount++;
        }

        // 每日计数
        if (current.DailyMaxCount > 0)
        {
            var today = DateTime.UtcNow.Date;
            if (current.LastExecutedDay != today)
            {
                current.DailyCount = 0; // 跨天重置
                current.LastExecutedDay = today;
            }

            current.DailyCount++;
            this._logger.LogInformation("[HB] 📅 任务 {Id} 当日完成 {Count}/{Max} 次",
                current.Id, current.DailyCount, current.DailyMaxCount);
        }

        // 可重复且未达每日上限和总次数上限 → 重置为 Pending 继续执行
        // 注意: DailyCount 保留实际完成次数，SelectPendingTask/SelectRetryableFailedTask
        // 会通过 IsDailyLimitReached 正确跳过已达上限的任务。
        var canRepeatMore = current.MaxRepeatCount switch
        {
            0 => false,                           // 不可重复
            -1 => true,                           // 无限重复
            > 0 when current.RepeatCount >= current.MaxRepeatCount => false, // 总次数用完
            _ => true,                            // 还有剩余次数
        };
        if (canRepeatMore && !current.IsDailyLimitReached)
        {
            current.Status = MissionStatus.Pending;
        }

        var board = this._missionBoard.BoardState;
        if (!string.IsNullOrEmpty(current.Id) && !board.CompletedMissionIds.Contains(current.Id))
            board.CompletedMissionIds.Add(current.Id);
        this._idleTicks = 0;
        this._noTargetStreak = 0;
        this._taskTicks = 0;
        this._lastTask = null;
        this._scriptExecutor = null; // 切换任务时释放旧 ScriptExecutor
    }

    /// <summary>
    /// Evaluates a KG-aware condition string for decision-making.
    /// Supported conditions:
    /// <c>kg_has_dependencies</c> — checks if the target node has prerequisites via <see cref="DependencyDirection.Backward"/>.
    /// <c>kg_dependencies_met</c> — checks if all required ingredients are present in inventory.
    /// </summary>
    /// <param name="condition">The condition name (e.g., <c>kg_has_dependencies</c>).</param>
    /// <param name="parameter">Optional parameter; expected to be a <see cref="NodeId"/> for KG conditions.</param>
    /// <returns><c>true</c> if the condition is satisfied; <c>false</c> if KG is unavailable or the condition is unknown.</returns>
    public bool EvaluateKgCondition(string condition, object? parameter)
    {
        if (this.KnowledgeGraphQuery is null)
        {
            return false;
        }

        switch (condition)
        {
            case "kg_has_dependencies":
            {
                if (parameter is not NodeId targetNode)
                {
                    return false;
                }

                var chain = this.KnowledgeGraphQuery.ResolveDependencies(targetNode, DependencyDirection.Backward);
                return chain.Steps.Count > 0;
            }

            case "kg_dependencies_met":
            {
                if (parameter is not NodeId targetNode)
                {
                    return false;
                }

                var chain = this.KnowledgeGraphQuery.ResolveDependencies(targetNode, DependencyDirection.Backward);
                if (chain.Steps.Count == 0)
                {
                    return true;
                }

                foreach (var step in chain.Steps)
                {
                    if (step.NodeId.Type != NodeType.Item)
                    {
                        continue;
                    }

                    var domainId = step.NodeId.DomainId;
                    var group = (int)(domainId >> 32);
                    var number = (int)(domainId & 0xFFFFFFFF);

                    if (this._player.Inventory is null ||
                        !this._player.Inventory.Items.Any(i =>
                            i.Definition?.Group == group && i.Definition?.Number == number))
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>为任务的当前阶段生成/切换脚本。</summary>
    private void BuildScriptForTask(MissionItem task)
    {
        // 先构建阶段树（如果没有）
        if (task.Stages.Count == 0 && task.QuestDef is not null)
        {
            var activeQuests = this._adapter.GetActiveQuests();
            var aq = activeQuests.FirstOrDefault(q => q.Group == task.QuestGroup && q.Number == task.QuestNumber);
            if (aq is not null)
            {
                this._missionBoard.BuildStagesForEntry(task, aq);
            }
        }

        // 根据任务类型构建段落脚本
        if (task.Type == MissionType.Quest && task.QuestDef is not null)
        {
            var hotspots = this.BuildHotspotsFromQuestDef(task.QuestDef);

            task.Script = BuildQuestScript(
                task.QuestGroup, task.QuestNumber,
                task.QuestDef.QuestGiver?.Number,
                hotspots, task.QuestDef.RequiredMonsterKills is { Count: > 0 });
        }
    }

    /// <summary>从 QuestDef 的 RequiredMonsterKills 推导狩猎热点坐标。</summary>
    private List<HotspotDef> BuildHotspotsFromQuestDef(QuestDefinition qdef)
    {
        if (qdef.RequiredMonsterKills is not { Count: > 0 })
            return new List<HotspotDef>();

        var config = this._player.GameContext?.Configuration;
        if (config is null)
            return new List<HotspotDef>();

        var results = new List<HotspotDef>();
        foreach (var kr in qdef.RequiredMonsterKills)
        {
            var monsterDef = kr.Monster;
            if (monsterDef is null)
                continue;

            // 在所有地图的 MonsterSpawns 中找这个怪的所有刷出区域
            foreach (var mapDef in config.Maps)
            {
                if (mapDef.MonsterSpawns is null)
                    continue;

                var spawns = mapDef.MonsterSpawns
                    .Where(s => s.MonsterDefinition?.Number == monsterDef.Number)
                    .ToList();
                if (spawns.Count == 0)
                    continue;

                // 随机选一个刷点
                var spawn = spawns[Random.Shared.Next(spawns.Count)];
                var centerX = (byte)((spawn.X1 + spawn.X2) / 2);
                var centerY = (byte)((spawn.Y1 + spawn.Y2) / 2);
                var mapName = mapDef.Name.ToString() ?? $"Map#{mapDef.Number}";
                this._logger.LogInformation("[P0D] Hotspot: monster #{Monster} '{Name}' -> map={Map}, spawn area ({X1},{Y1})-({X2},{Y2}), center=({Cx},{Cy})",
                    monsterDef.Number, monsterDef.Designation, mapName,
                    spawn.X1, spawn.Y1, spawn.X2, spawn.Y2, centerX, centerY);
                results.Add(new HotspotDef
                {
                    X = centerX,
                    Y = centerY,
                    X1 = spawn.X1,
                    X2 = spawn.X2,
                    Y1 = spawn.Y1,
                    Y2 = spawn.Y2,
                    MapNumber = (ushort)mapDef.Number,
                    Name = $"{monsterDef.Designation}({mapName})",
                });

                // 每个怪物类型只选一个热点（随机的那一个已添加）
                break;
            }
        }

        this._logger.LogInformation("[P0D] BuildHotspotsFromQuestDef: qdef G{Group}#{Num}, monsterKills={KillCount}, results={ResultCount}, fallback={IsFallback}",
            qdef.Group, qdef.Number,
            qdef.RequiredMonsterKills?.Count ?? 0,
            results.Count,
            results.Count == 0 ? "YES (0,0)" : "NO");

        // 如果一个 spawn 都找不到, 用兜底占位（供 Blocked 判断）
        if (results.Count == 0)
        {
            this._logger.LogInformation("[HB] Quest {Group}#{Number} 杀怪需求在 MonsterSpawns 中找不到对应刷点, 使用兜底占位",
                qdef.Group, qdef.Number);
            results.Add(new HotspotDef { X = 0, Y = 0, Name = "兜底占位", IsNoSpawnFallback = true });
        }

        return results;
    }

    // ===================== 构建脚本 =====================

    private static BehaviorScript BuildQuestScript(
        short questGroup, short questNumber, short? questNpcNumber,
        List<HotspotDef> hotspots, bool hasKillRequirement)
    {
        var huntNodes = new List<PriorityNode>
        {
            // 0: 条件满足 → 提交
            new() { Name = "quest_check", Condition = "quest_conditions_met", Action = "goto @submit_quest" },
            // 1: 跨地图 warp — 如果所有热点都在另一张地图，直接warp过去
            new() { Name = "warp_to_hunt_map", Condition = "not_on_hunt_map", Action = "warp_to_hunt_map" },
            // 2: 无目标 → 索敌
            new() { Name = "find_target", Condition = "no_target", Action = "find_nearest_monster" },
            // 3: 在攻击范围 → 攻击
            new() { Name = "attack", Condition = "has_target_in_range", Action = "attack_target" },
            // 4: 不在攻击范围 → 走近
            new() { Name = "approach", Condition = "has_target_not_in_range", Action = "walk_to_target" },
            // 5: 多次无怪 → 去下一个热点
            new() { Name = "relocate_if_away", Condition = "no_monsters_recently", Action = "relocate_to_hotspot" },
            // 6: 兜底巡逻
            new() { Name = "no_target_patrol", Condition = "always", Action = "random_patrol" },
        };

        return new BehaviorScript
        {
            Id = $"board_{questGroup}_{questNumber}",
            Parameters = new ScriptParameters
            {
                QuestGroup = questGroup,
                QuestNumber = questNumber,
                QuestNpcNumber = questNpcNumber,
                HpThreshold = 0.35f,
                SearchRange = 60,
                NoMonsterTicksLimit = 30,
                Hotspots = hotspots,
            },
            Paragraphs = new List<ScriptParagraph>
            {
                new()
                {
                    Label = "start",
                    Nodes = new List<PriorityNode>
                    {
                        new() { Name = "hp_check", Condition = "hp_below_threshold", Action = "use_hp_potion" },
                        new() { Name = "submit_ready", Condition = "quest_conditions_met", Action = "goto @submit_quest" },
                        new() { Name = "accept_if_none", Condition = "no_active_quest", Action = "goto @accept_quest" },
                        new() { Name = "has_quest_go_hunt", Condition = "has_active_quest", Action = "goto @hunt" },
                        new() { Name = "enter_hunt", Condition = "always", Action = "goto @hunt" },
                    },
                },
                new()
                {
                    Label = "accept_quest",
                    Nodes = new List<PriorityNode>
                    {
                        new() { Name = "walk_to_npc", Condition = "always", Action = "walk_to_quest_npc", Parameters = new ScriptParameters { QuestNpcNumber = questNpcNumber } },
                        new() { Name = "check_accepted", Condition = "can_accept_quest", Action = "start_quest" },
                        new() { Name = "close_dialog", Condition = "always", Action = "close_npc_dialog" },
                        new() { Name = "go_hunt", Condition = "always", Action = "goto @hunt" },
                    },
                },
                new() { Label = "hunt", Nodes = huntNodes },
                new()
                {
                    Label = "submit_quest",
                    Nodes = new List<PriorityNode>
                    {
                        new() { Name = "walk_to_npc", Condition = "always", Action = "walk_to_quest_npc" },
                        new() { Name = "check_ready", Condition = "quest_completable", Action = "complete_quest" },
                        new() { Name = "close_dialog", Condition = "always", Action = "close_npc_dialog" },
                        new() { Name = "done", Condition = "always", Action = "stop" },
                    },
                },
            },
        };
    }
}
