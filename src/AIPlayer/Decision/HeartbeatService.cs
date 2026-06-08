// <copyright file="HeartbeatService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.AIPlayer.Scripting;

/// <summary>
/// 心跳服务 — AI 角色的决策引擎。
///
/// 职责：
///   1. 登录时调 MissionBoardService.InitializeAsync 初始化看板
///   2. 每 tick 读 BoardState → 决策下一阶段 → 执行 → 写回看板
///   3. 事件驱动：接收游戏事件 → 更新 BoardState 任务进度
///
/// 不再调用 MissionBoardService 的任何服务方法（InitializeAsync 除外）。
/// SelectNext/Advance 逻辑迁入决策系统自身。
/// </summary>
public sealed class HeartbeatService
{
    private readonly AiPlayer _player;
    private readonly MissionBoardService _missionBoard;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;

    private readonly SurvivalMode _survival;
    private readonly ItemFarmModule _itemFarm;
    private readonly Dictionary<string, IBehaviorSubModule> _modules = new();

    /// <summary>看板脚本执行器 — 当前活跃任务的 ScriptExecutor。</summary>
    private ScriptExecutor? _scriptExecutor;

    /// <summary>中断上下文。</summary>
    private InterruptContext? _pendingInterrupt;

    private int _idleTicks;
    private int _noTargetStreak;
    private bool _wasDead;
    private int _taskTicks;
    private MissionItem? _lastTask; // 当前任务已执行 tick 数

    // 事件驱动状态标志
    private bool _lowHpFlag;
    private int _lastKnownHp;
    private int _lastKnownMaxHp;
    private int _lastKnownLevel;

    /// <summary>熔断阈值。</summary>
    private const int NoTargetCircuitBreaker = 30;

    /// <summary>低血量阈值。</summary>
    private const float LowHpThreshold = 0.4f;

    /// <summary>AI 事件总线。</summary>
    public AiEventBus EventBus { get; } = new();

    public IBehaviorSubModule? ActiveModule { get; private set; }
    public string State => this._activeState;
    private string _activeState = "初始化";

    private void SetActiveState(string state)
    {
        this._activeState = state;
        this._missionBoard.BoardState.PlayerState.CurrentActivity = state;
    }

    public HeartbeatService(AiPlayer player, MissionBoardService missionBoard, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._missionBoard = missionBoard;
        this._adapter = adapter;
        this._logger = logger;

        this._survival = new SurvivalMode(player, adapter, logger);
        this._itemFarm = new ItemFarmModule(player, adapter, logger);

        this._modules["quest_executor"] = new QuestExecutor(player, adapter, logger);
        this._modules["item_farm"] = this._itemFarm;
        this._modules["survival"] = this._survival;

        this._lastKnownHp = adapter.GetCurrentHp();
        this._lastKnownMaxHp = adapter.GetMaxHp();
        this._lastKnownLevel = adapter.GetPlayerLevel();

        // 注册事件处理器
        this.EventBus.Subscribe<HpLowEvent>(OnHpLow);
        this.EventBus.Subscribe<DeathEvent>(OnDeath);
        this.EventBus.Subscribe<RespawnEvent>(OnRespawn);
        this.EventBus.Subscribe<MonsterKilledEvent>(OnMonsterKilled);
        this.EventBus.Subscribe<LevelUpEvent>(OnLevelUp);
        this.EventBus.Subscribe<QuestStateChangedEvent>(OnQuestStateChanged);
        this.EventBus.Subscribe<DamagedEvent>(OnDamaged);
        this.EventBus.Subscribe<StuckEvent>(OnStuck);
        this.EventBus.Subscribe<QuestItemDroppedEvent>(OnQuestItemDropped);
        this.EventBus.Subscribe<NpcInRangeEvent>(OnNpcInRange);

        if (adapter is GameAdapter ga)
        {
            ga.EventPublisher = this.EventBus.Publish;
        }
    }

    /// <summary>心跳 — 每 400ms 左右调用一次。</summary>
    public async ValueTask BeatAsync()
    {
        // 看板未初始化 → 登录时首次初始化
        if (!this._missionBoard.IsInitialized)
        {
            await this._missionBoard.InitializeAsync().ConfigureAwait(false);
            this._logger.LogInformation("[HB] 看板初始化完成");
        }

        this._logger.LogDebug("[HB] Beat: state={State}", this._activeState);

        // === 0) Drain 事件队列 + 同步角色状态 ===
        this.EventBus.DrainEvents();
        this._missionBoard.SyncPlayerState();

        var hp = this._adapter.GetCurrentHp();
        var maxHp = this._adapter.GetMaxHp();
        this.DetectStateChanges(hp, maxHp);

        // === 1) 死亡检测 ===
        if (hp <= 0)
        {
            this._wasDead = true;
            this.SetActiveState("已死亡");
            return;
        }

        if (this._wasDead)
        {
            this._wasDead = false;
            this._idleTicks = 0;
            this._noTargetStreak = 0;
            this._lowHpFlag = false;
            this._logger.LogInformation("[HB] ✅ 角色复活，重置决策状态");
            this.SetActiveState("复活恢复");
        }

        // === 2) 行走 / NPC 对话跳过 ===
        if (this._adapter.IsPlayerWalking()) { this.SetActiveState("行走中"); return; }
        if (this._player.PlayerState.CurrentState == PlayerState.NpcDialogOpened) { this.SetActiveState("NPC对话"); return; }

        // === 3) 低血量恢复 ===
        if (this._lowHpFlag || (maxHp > 0 && (float)hp / maxHp < LowHpThreshold))
        {
            this._lowHpFlag = false;
            this._logger.LogDebug("[HB] ❤️ 血量 {Hp}/{MaxHp} 偏低", hp, maxHp);
            this.SetActiveState("低血恢复");
            var recoveryItem = new MissionItem { Id = "hp_recovery", Title = "低血量恢复", Priority = 0, Type = MissionType.Survival, Module = "survival" };
            var recoveryResult = await this._survival.ExecuteStepAsync(recoveryItem).ConfigureAwait(false);
            if (recoveryResult == StepResult.Completed)
                this._logger.LogInformation("[HB] ❤️ 血量恢复完成");
            if (this._adapter.IsPlayerWalking()) return;
        }

        // === 4) 中断处理 ===
        if (this._pendingInterrupt is not null)
        {
            await this.HandleInterruptAsync().ConfigureAwait(false);
            return;
        }

        // === 5) NoTarget 熔断 ===
        if (this._noTargetStreak >= NoTargetCircuitBreaker)
        {
            this._logger.LogWarning("[HB] 🔥 NoTarget 熔断: {Streak}次", this._noTargetStreak);
            this._noTargetStreak = 0;
            this.SetActiveState("NoTarget熔断-生存模式");
            var patrolItem = new MissionItem { Id = "notarget_patrol", Title = "NoTarget 熔断 — 生存巡逻", Priority = 0, Type = MissionType.Survival, Module = "survival" };
            await this._survival.ExecuteStepAsync(patrolItem).ConfigureAwait(false);
            return;
        }

        // === 6) 决策：选当前任务 ===
        var current = this.SelectCurrentTask();
        if (current is null)
        {
            this.SetActiveState("今日任务全部完成");
            this._idleTicks++;
            return;
        }

        // === 7) 同步游戏活跃任务到看板（确保进度是最新的） ===
        this._missionBoard.SyncActiveQuestToBoard();

        // 检测任务切换
        var sameTask = current == this._lastTask;
        if (!sameTask)
        {
            this._taskTicks = 0;
            this._lastTask = current;
        }

        // === 8) 执行当前任务的当前阶段 ===
        this._taskTicks++;
        this.SetActiveState(current.Title);

        // 任务级超时：一个任务执行太长时间也没推进 → 跳过
        const int maxTaskTicks = 2500; // ~16 分钟 @ 400ms/tick
        if (this._taskTicks > maxTaskTicks)
        {
            this._logger.LogWarning("[HB] ⏰ 任务 {Title} 执行超时 ({Ticks} ticks)", current.Title, this._taskTicks);
            this.MarkTaskFailed(current, FailureReason.Timeout);
            return;
        }

        // 快速跳过：不可执行的任务（无活跃 Quest 且无杀怪需求的系统任务、副本事件等）
        if (current.Category is QuestCategory.InstanceEvent or QuestCategory.Survival ||
            (current.Type == MissionType.Quest && current.QuestDef is not null))
        {
            // Survival 任务是兜底，不放这里跳过
            if (current.Category == QuestCategory.InstanceEvent)
            {
                this._logger.LogInformation("[HB] ⏭ 事件任务 {Title} 暂不执行 (副本模块未实现)", current.Title);
                this.MarkTaskFailed(current, FailureReason.InstanceModuleMissing);
                return;
            }

            if (current.Type == MissionType.Quest && current.QuestDef is not null)
            {
                // 有脚本的任务放行 — ScriptExecutor 的 no_active_quest 条件会自动走 accept_quest 段落
                if (current.Script is not null)
                {
                    // 不做快速跳过，让脚本流程自然运行
                }
                else
                {
                    var activeQuests = this._adapter.GetActiveQuests();
                    var hasActive = activeQuests.Any(q => q.Group == current.QuestGroup && q.Number == current.QuestNumber);
                    if (!hasActive && (current.QuestDef.RequiredMonsterKills is null || current.QuestDef.RequiredMonsterKills.Count == 0))
                    {
                        if (current.Stages.Count == 0 || current.Stages.All(s => s.Status == TaskStageStatus.Pending))
                        {
                            this._logger.LogInformation("[HB] ⏭ 任务 {Title} 无活跃 Quest 且无杀怪需求, 跳过 (不可执行)", current.Title);
                            this.MarkTaskFailed(current, FailureReason.NotExecutable);
                            return;
                        }
                    }
                }
            }
        }

        var stageResult = await this.ExecuteCurrentStageAsync(current).ConfigureAwait(false);
        switch (stageResult)
        {
            case StageResult.Completed:
                this._logger.LogInformation("[HB] ✅ {Title} (stage={Stage}) 完成", current.Title, current.CurrentStageIndex);
                this.AdvanceTask(current);
                break;
            case StageResult.InProgress:
                this._noTargetStreak = 0;
                break;
            case StageResult.Failed:
                this._logger.LogWarning("[HB] ❌ {Title} 失败", current.Title);
                this.MarkTaskFailed(current, current.FailureRetryable ? FailureReason.NotExecutable : FailureReason.NotRetryable);
                break;
            case StageResult.NoTarget:
                this._idleTicks++;
                this._noTargetStreak++;
                if (this._idleTicks > 15)
                {
                    this._logger.LogInformation("[HB] ⏭ {Title} 长时间无目标(idle={Idle})，标记失败可重试", current.Title, this._idleTicks);
                    this.MarkTaskFailed(current, FailureReason.NoTarget);
                    this._idleTicks = 0;
                    this._noTargetStreak = 0;
                }
                break;
        }
    }

    // ===================== 决策核心 — 三层全看板扫描 =====================

    /// <summary>
    /// 每 tick 全看板三层扫描，选择当前要执行的任务。
    ///
    /// Layer 1: Active — 正在进行的，继续执行
    /// Layer 2: Failed — 失败的，判断是否可重试
    /// Layer 3: Pending — 新任务，检查依赖条件
    /// </summary>
    private MissionItem? SelectCurrentTask()
    {
        var board = this._missionBoard.BoardState;

        // Layer 1: 找到第一个 Active 任务 → 继续执行
        var active = board.Missions.FirstOrDefault(m => m.Status == MissionStatus.Active);
        if (active is not null)
            return active;

        // Layer 2: 扫描 Failed 任务 → 评估可重试性
        var retryable = this.SelectRetryableFailedTask(board);
        if (retryable is not null)
            return retryable;

        // Layer 3: 扫描 Pending 任务 → 检查依赖
        var pending = this.SelectPendingTask(board);
        if (pending is not null)
            return pending;

        return null;
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
                continue;

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

            // 满足条件 → 激活
            this._logger.LogInformation("[HB] ➡️ 激活新任务 {Title}", task.Title);
            task.Status = MissionStatus.Active;
            this.BuildScriptForTask(task);
            return task;
        }

        return null;
    }

    /// <summary>
    /// 标记任务失败，记录失败原因/重试计数/首次失败时间/死任务判定。
    /// </summary>
    private void MarkTaskFailed(MissionItem task, FailureReason reason)
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

        this._taskTicks = 0;
        this._lastTask = null;
        this._scriptExecutor = null;
    }

    /// <summary>推进任务：标记完成，不操作索引（三层扫描自然推进）。</summary>
    private void AdvanceTask(MissionItem current)
    {
        current.Status = MissionStatus.Completed;
        // 可重复任务递增计数
        if (current.MaxRepeatCount > 0)
        {
            current.RepeatCount++;
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

    /// <summary>为任务的当前阶段生成/切换脚本。</summary>
    private void BuildScriptForTask(MissionItem task)
    {
        if (task.Script is not null) return; // 已有脚本

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
            var hotspots = task.QuestDef.RequiredMonsterKills?.Count > 0
                ? new List<HotspotDef> { new() { X = 200, Y = 150, Name = "狩猎区" } }
                : new List<HotspotDef>();

            task.Script = BuildQuestScript(
                task.QuestGroup, task.QuestNumber,
                task.QuestDef.QuestGiver?.Number,
                hotspots, task.QuestDef.RequiredMonsterKills is { Count: > 0 });
        }
    }

    /// <summary>执行当前任务的当前阶段。</summary>
    private async ValueTask<StageResult> ExecuteCurrentStageAsync(MissionItem task)
    {
        // 没有阶段树或所有阶段完成 → 整任务完成
        if (task.Stages.Count == 0 || task.CurrentStageIndex >= task.Stages.Count)
        {
            // 脚本模式
            if (task.Script is not null)
            {
                var scriptResult = await this.ExecuteScriptAsync(task).ConfigureAwait(false);
                return scriptResult;
            }

            // 无阶段树也无脚本 → 模块模式
            if (!this._modules.TryGetValue(task.Module, out var module))
            {
                this._logger.LogWarning("[HB] 未知模块: {Mod}", task.Module);
                return StageResult.Failed;
            }

            this.ActiveModule = module;
            return await module.ExecuteStepAsync(task).ConfigureAwait(false) switch
            {
                StepResult.Completed => StageResult.Completed,
                StepResult.InProgress => StageResult.InProgress,
                StepResult.Failed => StageResult.Failed,
                StepResult.NoTarget => StageResult.NoTarget,
                _ => StageResult.InProgress,
            };
        }

        // 有阶段树 → 按阶段执行
        var stage = task.Stages[task.CurrentStageIndex];
        this._logger.LogDebug("[HB] ▶ {Title} stage={StageLabel}({StageIdx})", task.Title, stage.Label, task.CurrentStageIndex);

        // 狩猎阶段：用模块执行
        if (stage.Id == "hunt" && this._modules.TryGetValue(task.Module, out var huntModule))
        {
            this.ActiveModule = huntModule;
            var result = await huntModule.ExecuteStepAsync(task).ConfigureAwait(false);
            if (result == StepResult.Completed)
            {
                stage.Status = TaskStageStatus.Completed;
                task.CurrentStageIndex++;
                this._logger.LogInformation("[HB] ✅ stage={Stage} 完成 → 进入下一阶段", stage.Label);
                return StageResult.InProgress; // 继续下一阶段
            }
            return StageResult.InProgress;
        }

        // 非狩猎阶段：用脚本执行
        if (task.Script is not null)
        {
            return await this.ExecuteScriptAsync(task).ConfigureAwait(false);
        }

        // fallback: 模块模式
        if (!this._modules.TryGetValue(task.Module, out var mod))
            return StageResult.Failed;
        this.ActiveModule = mod;
        return await mod.ExecuteStepAsync(task).ConfigureAwait(false) switch
        {
            StepResult.Completed => StageResult.Completed,
            StepResult.InProgress => StageResult.InProgress,
            StepResult.Failed => StageResult.Failed,
            StepResult.NoTarget => StageResult.NoTarget,
            _ => StageResult.InProgress,
        };
    }

    /// <summary>用 ScriptExecutor 执行脚本一步。</summary>
    private async ValueTask<StageResult> ExecuteScriptAsync(MissionItem item)
    {
        if (item.Script is null) return StageResult.Failed;

        var scriptId = item.Script.Id;
        var lastScriptId = this._scriptExecutor?.Script?.Id;

        if (this._scriptExecutor is null && item.Script is not null)
        {
            var context = new BehaviorContext(this._player)
            {
                GameAdapter = this._adapter,
                NpcService = new NpcInteractionService(this._player, this._logger),
            };
            var stateMachine = this._player.StateMachine;
            this._scriptExecutor = new ScriptExecutor(this._player, context, item.Script!);
        }
        else if (item.Script is not null && scriptId != lastScriptId)
        {
            this._scriptExecutor!.ReloadScript(item.Script);
        }

        var tickResult = await this._scriptExecutor!.TickAsync().ConfigureAwait(false);
        if (tickResult.State == ScriptTaskState.Completed)
        {
            // 脚本完成 → 标记当前阶段完成并推进
            if (item.CurrentStageIndex < item.Stages.Count)
            {
                item.Stages[item.CurrentStageIndex].Status = TaskStageStatus.Completed;
                item.CurrentStageIndex++;
            }
            // 没有更多阶段 → 整个任务完成
            if (item.CurrentStageIndex >= item.Stages.Count)
                return StageResult.Completed;
            return StageResult.InProgress; // 继续下阶段
        }
        if (tickResult.State == ScriptTaskState.Stuck)
            return StageResult.Failed;
        return StageResult.InProgress;
    }

    // ===================== 构建脚本 =====================

    private static BehaviorScript BuildQuestScript(
        short questGroup, short questNumber, short? questNpcNumber,
        List<HotspotDef> hotspots, bool hasKillRequirement)
    {
        // 先判断是否有杀怪要求
        var huntNodes = new List<PriorityNode>
        {
            new() { Name = "quest_check", Condition = "quest_conditions_met", Action = "goto @submit_quest" },
        };

        if (hasKillRequirement)
        {
            huntNodes.Add(new() { Name = "relocate_if_away", Condition = "not_at_hotspot", Action = "relocate_to_hotspot" });
        }

        huntNodes.Add(new() { Name = "find_target", Condition = "no_target", Action = "find_nearest_monster" });
        huntNodes.Add(new() { Name = "attack", Condition = "has_target_in_range", Action = "attack_target" });
        huntNodes.Add(new() { Name = "approach", Condition = "has_target_not_in_range", Action = "walk_to_target" });
        huntNodes.Add(new() { Name = "no_target_patrol", Condition = "always", Action = "random_patrol" });

        return new BehaviorScript
        {
            Id = $"board_{questGroup}_{questNumber}",
            Parameters = new ScriptParameters
            {
                QuestGroup = questGroup,
                QuestNumber = questNumber,
                QuestNpcNumber = questNpcNumber,
                HpThreshold = 0.35f,
                SearchRange = 20,
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
                        new() { Name = "check_ready", Condition = "can_accept_quest", Action = "complete_quest" },
                        new() { Name = "close_dialog", Condition = "always", Action = "close_npc_dialog" },
                        new() { Name = "done", Condition = "always", Action = "stop" },
                    },
                },
            },
        };
    }

    // ===================== 被动状态检测 =====================

    private void DetectStateChanges(int hp, int maxHp)
    {
        if (hp != this._lastKnownHp || maxHp != this._lastKnownMaxHp)
        {
            if (hp <= 0 && this._lastKnownHp > 0)
                this.EventBus.Publish(new DeathEvent(null));
            else if (hp > 0 && this._lastKnownHp <= 0)
                this.EventBus.Publish(new RespawnEvent(this._adapter.GetPlayerPosition()));
            else if (maxHp > 0 && (float)hp / maxHp < LowHpThreshold
                     && (this._lastKnownMaxHp <= 0 || (float)this._lastKnownHp / this._lastKnownMaxHp >= LowHpThreshold))
                this.EventBus.Publish(new HpLowEvent(hp, maxHp));

            this._lastKnownHp = hp;
            this._lastKnownMaxHp = maxHp;
        }

        var level = this._adapter.GetPlayerLevel();
        if (level != this._lastKnownLevel)
        {
            this.EventBus.Publish(new LevelUpEvent(level));
            this._lastKnownLevel = level;
        }
    }

    public void Interrupt(string reason)
    {
        this._pendingInterrupt = new InterruptContext
        {
            Reason = reason,
        };
        this._logger.LogInformation("[HB] 中断: {Reason}", reason);
    }

    private async ValueTask HandleInterruptAsync()
    {
        var ctx = this._pendingInterrupt!;
        this.SetActiveState($"中断: {ctx.Reason}");

        var temp = new MissionItem { Id = $"intr_{ctx.Reason}", Title = ctx.Reason, Priority = 0, Type = MissionType.Emergency, Module = "survival" };
        var result = await this._survival.ExecuteStepAsync(temp).ConfigureAwait(false);
        if (result != StepResult.InProgress)
        {
            this._pendingInterrupt = null;
            this._logger.LogInformation("[HB] 中断处理完毕");
        }
    }

    #region 事件处理器

    private void OnHpLow(HpLowEvent evt) { this._lowHpFlag = true; this.SetActiveState("低血事件"); this._logger.LogInformation("[Event] ❤️ 血量过低: {Hp}/{MaxHp}", evt.Hp, evt.MaxHp); }
    private void OnDeath(DeathEvent evt) { this._wasDead = true; this.SetActiveState("已死亡"); this._lowHpFlag = false; this._logger.LogInformation("[Event] 💀 角色死亡"); }
    private void OnRespawn(RespawnEvent evt) { this._wasDead = false; this._idleTicks = 0; this._noTargetStreak = 0; this._lowHpFlag = false; this.SetActiveState("复活恢复"); this._logger.LogInformation("[Event] 🔄 角色复活于 ({X},{Y})", evt.Position.X, evt.Position.Y); }

    private void OnDamaged(DamagedEvent evt)
    {
        if (this._lastKnownMaxHp > 0 && (float)evt.Damage / this._lastKnownMaxHp > 0.3f)
            this._lowHpFlag = true;
    }

    private void OnStuck(StuckEvent evt)
    {
        this._logger.LogWarning("[Event] ⛔ 角色卡住, 触发中断");
        this.Interrupt("角色卡住");
    }

    private void OnMonsterKilled(MonsterKilledEvent evt)
    {
        this._logger.LogDebug("[Event] 💀 击杀 #{Monster}「{Name}」", evt.MonsterNumber, evt.MonsterName);
        this._noTargetStreak = 0;
    }

    private void OnQuestStateChanged(QuestStateChangedEvent evt)
    {
        this._logger.LogInformation("[Event] 📋 任务状态变更: G{Group}N{Number}", evt.Group, evt.Number);
        // 事件后同步游戏状态到看板
        this._missionBoard.SyncActiveQuestToBoard();
    }

    private void OnQuestItemDropped(QuestItemDroppedEvent evt)
    {
        this._logger.LogInformation("[Event] 📦 任务道具掉落: 「{Name}」(G{Group}N{Number})", evt.ItemName, evt.ItemGroup, evt.ItemNumber);
    }

    private void OnLevelUp(LevelUpEvent evt)
    {
        this._logger.LogInformation("[Event] 🎉 升级到 Lv{Level}!", evt.NewLevel);
    }

    private void OnNpcInRange(NpcInRangeEvent evt)
    {
        this._logger.LogDebug("[Event] 🗺️ NPC #{Num}「{Name}」进入感知范围", evt.NpcNumber, evt.NpcName);
    }

    #endregion
}

/// <summary>阶段执行结果。</summary>
internal enum StageResult
{
    Completed,
    InProgress,
    Failed,
    NoTarget,
}
