// <copyright file="HeartbeatService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.AIPlayer.Scripting;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Views;

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
    private readonly NpcInteractionService _npcService;
    private readonly BehaviorContext _context;

    private readonly SurvivalMode _survival;
    private readonly ItemFarmModule _itemFarm;
    private readonly InventoryManagerService _inventoryManager;
    private readonly ValueAssessmentService _valueAssessment;
    private readonly MarketPriceService _marketPrice;
    private readonly TransactionMonitor _transactionMonitor;
    private readonly Dictionary<string, IBehaviorSubModule> _modules = new();
    private DynamicMissionGenerator _missionGenerator;
    private GoalScheduler _goalScheduler;
    private CraftingModule _crafting;
    private EventExecutorModule _eventExecutor;
    private ItemPickupManager _itemPickupManager;

    /// <summary>事件活动广播器 — 监听活动事件开放/关闭状态。</summary>
    private readonly EventWatcherService _eventWatcher;

    /// <summary>事件中断决策服务 — 判断是否中断当前任务去参加事件。</summary>
    private readonly EventInterruptService _eventInterrupt;

    /// <summary>仓库服务 — 检查/存取仓库物品。</summary>
    private readonly VaultService _vaultService;

    /// <summary>跨地图路由规划器。</summary>
    private readonly Warp.WarpPlanner _warpPlanner;

    /// <summary>看板脚本执行器 — 当前活跃任务的 ScriptExecutor。</summary>
    private ScriptExecutor? _scriptExecutor;

    /// <summary>中断上下文。</summary>
    private InterruptContext? _pendingInterrupt;

    private int _idleTicks;
    private int _noTargetStreak;
    private bool _wasDead;
    private DateTime? _deathStartTime;
    private int _taskTicks;
    private MissionItem? _lastTask; // 当前任务已执行 tick 数
    private int _npcDialogTicks;    // NPC 对话已持续 tick 数

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

    public HeartbeatService(AiPlayer player, BehaviorContext context, MissionBoardService missionBoard, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._context = context;
        this._missionBoard = missionBoard;
        this._adapter = adapter;
        this._logger = logger;

        this._survival = new SurvivalMode(player, adapter, logger);
        this._itemFarm = new ItemFarmModule(player, adapter, logger);
        this._marketPrice = new MarketPriceService(logger);
        this._valueAssessment = new ValueAssessmentService(player, logger, this._marketPrice);
        this._transactionMonitor = new TransactionMonitor(this._marketPrice, logger);
        this._inventoryManager = new InventoryManagerService(player, adapter, logger, this._valueAssessment);

        this._modules["quest_executor"] = new QuestExecutor(player, adapter, logger);
        this._modules["item_farm"] = this._itemFarm;
        this._modules["survival"] = this._survival;

        this._crafting = new CraftingModule(player, adapter, logger);
        this._eventExecutor = new EventExecutorModule(player, adapter, this._missionBoard.BoardState, logger);
        this._missionGenerator = new DynamicMissionGenerator(player, adapter, this._missionBoard.BoardState, logger, this._valueAssessment);
        this._goalScheduler = new GoalScheduler(player, adapter, logger);
        this._missionGenerator.SetGoalScheduler(this._goalScheduler);
        this._modules["crafting_executor"] = this._crafting;
        this._modules["event_executor"] = this._eventExecutor;
        this._modules["vault_executor"] = new VaultModule(player, adapter, logger);
        this._itemPickupManager = new ItemPickupManager(player, adapter, context, logger, this._valueAssessment);
        this._modules["item_pickup_manager"] = this._itemPickupManager;

        this._eventWatcher = new EventWatcherService(player, this.EventBus, logger);

        this._eventInterrupt = new EventInterruptService(player, logger);
        this._vaultService = new VaultService(player, logger);

        this._npcService = new NpcInteractionService(player, logger);
        this._warpPlanner = new Warp.WarpPlanner(player.GameContext!.Configuration, logger);
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
        this.EventBus.Subscribe<EventOpenEvent>(OnEventOpen);
        this.EventBus.Subscribe<EventReminderEvent>(OnEventReminder);
        this.EventBus.Subscribe<EventClosedEvent>(OnEventClosed);

        if (adapter is GameAdapter ga)
        {
            ga.EventPublisher = this.EventBus.Publish;
            ga.ChatMessageReceived += (sender, msg, type) =>
            {
                if (type == ChatMessageType.Normal)
                    this._transactionMonitor.ProcessChatMessage(sender, msg);
            };
        }
    }

    /// <summary>心跳 — 每 400ms 左右调用一次。</summary>
    public async ValueTask BeatAsync()
    {
        // 看板未初始化 → 登录时首次初始化
        if (!this._missionBoard.IsInitialized)
        {
            await this._missionBoard.InitializeAsync().ConfigureAwait(false);
            this._goalScheduler.InitializeDefaultGoals();
            this._logger.LogInformation("[HB] 看板初始化完成，目标规划器已初始化");
        }

        this._logger.LogDebug("[HB] Beat: state={State}", this._activeState);

        // === 0) Drain 事件队列 + 同步角色状态 ===
        this.EventBus.DrainEvents();
        if (this._adapter is GameAdapter ga)
        {
            ga.DrainChatMessages();
        }

        // 扫描活动事件状态 → 发布状态变更事件（下一 tick 的 DrainEvents 处理）
        await this._eventWatcher.CheckEventsAsync().ConfigureAwait(false);

        this._missionBoard.SyncPlayerState();

        var hp = this._adapter.GetCurrentHp();
        var maxHp = this._adapter.GetMaxHp();
        this.DetectStateChanges(hp, maxHp);

        // === 1) 死亡检测 + 自动重生 fallback ===
        if (hp <= 0)
        {
            this._wasDead = true;
            this._deathStartTime ??= DateTime.UtcNow;
            this.SetActiveState("已死亡");

            // 等待游戏引擎自动重生（~3秒），5秒后仍无复活 → 手动触发
            var deathDuration = DateTime.UtcNow - this._deathStartTime.Value;
            if (deathDuration.TotalSeconds >= 5)
            {
                this._logger.LogWarning("[HB] 💀 游戏引擎未在 {Seconds:F1}s 内自动重生，手动触发复活", deathDuration.TotalSeconds);
                await this.RespawnPlayerAsync().ConfigureAwait(false);
                return;
            }

            return;
        }

        this._deathStartTime = null;

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

        // NPC 对话打开时，如果有活跃的 ScriptExecutor → 放行让脚本执行 quest 操作
        // 否则跳过（无脚本时的 NPC 对话无意义，防止卡死）
        // 另设超时保护：NPC 对话持续超过 40 tick(~16秒)时自动关闭
        if (this._player.PlayerState.CurrentState == PlayerState.NpcDialogOpened)
        {
            this._npcDialogTicks++;
            if (this._npcDialogTicks > 40)
            {
                this._logger.LogWarning("[HB] ⏰ NPC 对话超时 ({Ticks}tick)，自动关闭", this._npcDialogTicks);
                await this.CloseNpcDialogAsync().ConfigureAwait(false);
                this._npcDialogTicks = 0;
                return;
            }

            if (this._scriptExecutor is null)
            {
                this.SetActiveState("NPC对话");
                return;
            }
            // 有脚本执行器时放行，让 ScriptExecutor 处理 quest accept/submit
        }
        else
        {
            this._npcDialogTicks = 0;
        }

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

        // === 5) NoTarget 熔断 + 跨地图传送检测 ===
        // CraftingModule / EventExecutorModule 返回 NoTarget 时标记目标地图，
        // 决策系统会切换到跨地图传送任务
        if (this._context.TargetMapNumber.HasValue)
        {
            var targetMap = this._context.TargetMapNumber.Value;
            var arrived = await this.ExecuteCrossMapWarpAsync(targetMap).ConfigureAwait(false);
            if (!arrived)
            {
                this.SetActiveState($"传送至地图 #{targetMap}");
                return;
            }
            this._logger.LogInformation("[HB] ✅ 到达目标地图 #{Map}，继续执行任务", targetMap);
        }

        if (this._noTargetStreak >= NoTargetCircuitBreaker)
        if (this._noTargetStreak >= NoTargetCircuitBreaker)
        {
            this._logger.LogWarning("[HB] 🔥 NoTarget 熔断: {Streak}次", this._noTargetStreak);
            this._noTargetStreak = 0;
            this.SetActiveState("NoTarget熔断-生存模式");
            var patrolItem = new MissionItem { Id = "notarget_patrol", Title = "NoTarget 熔断 — 生存巡逻", Priority = 0, Type = MissionType.Survival, Module = "survival" };
            await this._survival.ExecuteStepAsync(patrolItem).ConfigureAwait(false);
            return;
        }

        // === 5.5) 背包检测：满包时回城贩卖 ===
        if (this._inventoryManager.NeedsInventoryCleanup())
        {
            this._logger.LogInformation("[HB] 🎒 背包满({Fullness:F1}%), 启动回城贩卖", this._inventoryManager.Fullness * 100);
            await this._inventoryManager.CleanupInventoryAsync().ConfigureAwait(false);
            return;
        }

        // === 6) 决策：选当前任务 ===
        var current = await this.SelectCurrentTaskAsync().ConfigureAwait(false);
        if (current is null)
        {
            this._logger.LogDebug("[HB] SelectCurrentTask 无任务, 等待下一 tick");
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

        // 快速跳过：不可执行的任务（无活跃 Quest 且无杀怪需求的系统任务）
        // InstanceEvent 类任务跳过此检测（由 EventExecutorModule 模块执行）
        if (current.Category is QuestCategory.Survival ||
            (current.Type == MissionType.Quest && current.QuestDef is not null && current.Category != QuestCategory.InstanceEvent))
        {
            // Survival 任务是兜底，不放这里跳过

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
                // InstanceEvent 类任务用 InstanceModuleMissing，不占用 NotExecutable 重试槽位
                var failReason = current.Category == QuestCategory.InstanceEvent
                    ? FailureReason.InstanceModuleMissing
                    : FailureReason.NotExecutable;
                this.MarkTaskFailed(current, current.FailureRetryable ? failReason : FailureReason.NotRetryable);
                break;
            case StageResult.NoTarget:
                // 模块返回 NoTarget 表示需要跨地图移动
                // CraftingModule 在 NPC 不在当前地图时会返回 NoTarget 并设置 TargetMap
                // EventExecutorModule 在活动不在当前地图时同样返回 NoTarget
                if (this._context.TargetMapNumber.HasValue)
                {
                    this._logger.LogInformation("[HB] 🗺 {Title} 需要跨地图到 #{Target}，切换到传送", current.Title, this._context.TargetMapNumber.Value);
                    break; // 让下一 tick 的跨地图检测执行传送
                }

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

        // === 9) 兜底检测：所有狩猎热点均为兜底占位（无实际怪物刷点）→ Blocked ===
        if (this._idleTicks > 10 &&
            current.Script?.Parameters.Hotspots is { Count: > 0 } hotspots &&
            hotspots.All(h => h.IsNoSpawnFallback))
        {
            this._logger.LogInformation(
                "[HB] ⏸ {Title} 所有狩猎热点均为兜底占位（无实际怪物刷点），标记 MonsterNotAvailable",
                current.Title);
            this.MarkTaskBlocked(current, BlockedReason.MonsterNotAvailable);
            return;
        }

        // 市场偏移衰减（每60tick ≈ 24秒）
        if (this._taskTicks > 0 && this._taskTicks % 60 == 0)
        {
            this._marketPrice.DecayOffsets();
        }
    }

    // ===================== 决策核心 — 三层全看板扫描 =====================

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
    private async ValueTask<MissionItem?> SelectCurrentTaskAsync()
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
    private void MarkTaskBlocked(MissionItem task, BlockedReason reason)
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

        // 脚本优先：有脚本时不管哪个阶段都用 ScriptExecutor
        // ScriptExecutor 的段落脚本会处理 accept/hunt/submit 的全自动流转
        if (task.Script is not null)
        {
            return await this.ExecuteScriptAsync(task).ConfigureAwait(false);
        }

        // 无脚本时的狩猎阶段：用模块执行
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
            // 使用 AiPlayerLogic 的主 BehaviorContext — WorldState 每 tick 被刷新
            this._scriptExecutor = new ScriptExecutor(this._player, this._context, item.Script!);
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

    /// <summary>
    /// 手动复活 AI 玩家：当游戏引擎自动重生（~3秒）失败时作为 fallback。
    /// 通过 WarpToSafezoneAsync 将玩家传送到安全区，触发游戏引擎的复活流程。
    /// </summary>
    /// <summary>
    /// 使用 NPC 对话关闭动作关闭当前对话框。
    /// </summary>
    private async ValueTask CloseNpcDialogAsync()
    {
        var closeAction = new GameLogic.PlayerActions.CloseNpcDialogAction();
        await closeAction.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
    }

    /// <summary>
    /// 跨地图移动：使用 WarpPlanner 计算路线并执行多跳传送。
    /// 如果当前有活跃路由，推进路由步骤。否则用 WarpPlanner 算新路由。
    /// 路由完成后将 TargetMap 置 null。
    /// </summary>
    private async ValueTask<bool> ExecuteCrossMapWarpAsync(ushort targetMap)
    {
        var currentMapNum = (ushort)(this._adapter.GetCurrentMap()?.Definition.Number ?? 0);
        if (currentMapNum == targetMap)
        {
            this._context.TargetMapNumber = null;
            return true;
        }

        // 如果当前有活跃路由，先推进
        if (this._context.ActiveWarpRoute is not null)
        {
            var stepIdx = this._context.ActiveWarpStepIndex;
            var steps = this._context.ActiveWarpRoute.Steps;

            if (stepIdx >= steps.Count)
            {
                // 路由完成
                this._context.ActiveWarpRoute = null;
                this._context.ActiveWarpStepIndex = 0;
                this._context.TargetMapNumber = null;
                this._logger.LogInformation("[WarpRoute] ✅ 跨图传送完成（目标地图 #{Map}）", targetMap);
                return true;
            }

            // 检查当前是否在地图传送等待中
            if (this._context.WarpInProgress)
            {
                // 等待地图切换完成
                if (this._adapter.GetCurrentMap() is not null)
                {
                    this._context.WarpInProgress = false;
                    this._context.ActiveWarpStepIndex++;
                }
                return false;
            }

            // 执行下一步
            await this.ExecuteWarpStepAsync(steps[stepIdx]).ConfigureAwait(false);
            return false;
        }

        // 没有活跃路由 → 计算新路由
        var route = this._warpPlanner.ComputeRoute(
            (short)currentMapNum,
            (short)targetMap,
            this._adapter.GetPlayerLevel(),
            this._player.Money);

        if (route is null || !route.IsFeasible)
        {
            this._logger.LogWarning("[WarpRoute] 无法找到从地图 #{From} 到 #{To} 的可行路线",
                currentMapNum, targetMap);
            return false;
        }

        this._context.ActiveWarpRoute = route;
        this._context.ActiveWarpStepIndex = 0;
        this._context.TargetMapNumber = targetMap;
        this._logger.LogInformation("[WarpRoute] 开始跨图传送: {From}→{To} ({Steps}步, 金币={Gold})",
            currentMapNum, targetMap, route.Steps.Count, route.TotalGoldCost);
        return false;
    }

    /// <summary>
    /// 执行路由单步（门传送或传送菜单）。
    /// 复用 ScriptExecutor 的 ExecuteGateStepAsync / ExecuteWarpMenuStepAsync 逻辑。
    /// </summary>
    private async ValueTask ExecuteWarpStepAsync(Warp.WarpStep step)
    {
        if (step.Method == Warp.WarpEdgeType.Gate)
        {
            // 门传送：走到门中心 → 进门
            var currentMap = this._adapter.GetCurrentMap();
            if (currentMap is null) return;

            var pos = this._adapter.GetPlayerPosition();
            var atGate = Math.Abs(pos.X - step.GateCenter.X) <= 3
                      && Math.Abs(pos.Y - step.GateCenter.Y) <= 3;

            if (!atGate)
            {
                await this._adapter.WalkToAsync(step.GateCenter, currentMap).ConfigureAwait(false);
                return;
            }

            if (step.EnterGate is not null)
            {
                var warpAction = new GameLogic.PlayerActions.WarpGateAction();
                await warpAction.EnterGateAsync(this._player, step.EnterGate).ConfigureAwait(false);
                this._context.WarpInProgress = true;
            }
        }
        else if (step.WarpInfo is not null)
        {
            // 传送菜单：直接使用 WarpAction
            var warpAction = new GameLogic.PlayerActions.WarpAction();
            await warpAction.WarpToAsync(this._player, step.WarpInfo).ConfigureAwait(false);
            this._context.WarpInProgress = true;
        }
    }

    private async ValueTask RespawnPlayerAsync()
    {
        try
        {
            this._logger.LogWarning("[HB] 💀 执行手动复活...");
            await this._player.WarpToSafezoneAsync().ConfigureAwait(false);
            this._logger.LogInformation("[HB] ✅ WarpToSafezoneAsync 完成");

            // WarpToSafezoneAsync 只传送不恢复HP — 手动恢复满属性
            foreach (var regen in Stats.IntervalRegenerationAttributes)
            {
                this._player.Attributes![regen.CurrentAttribute] = this._player.Attributes[regen.MaximumAttribute];
            }
            this._player.IsAlive = true;

            // 重置 ScriptExecutor 状态，确保复活后能正常处理任务
            this._scriptExecutor = null;

            // 如果传送后 CurrentMap 为 null（等待客户端确认），直接确认地图变换
            if (this._player.CurrentMap is null)
            {
                await this._player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
                this._logger.LogInformation("[HB] ✅ ClientReadyAfterMapChangeAsync 完成（手动复活）");
            }

            this._deathStartTime = null;
            this._wasDead = false;
            this._idleTicks = 0;
            this._noTargetStreak = 0;
            this._lowHpFlag = false;
            this._logger.LogWarning("[HB] 💀 手动复活流程完成，HP/MP 已恢复");
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "[HB] 💀 手动复活异常");
        }
    }

    #region 事件处理器

    private void OnHpLow(HpLowEvent evt) { this._lowHpFlag = true; this.SetActiveState("低血事件"); this._logger.LogInformation("[Event] ❤️ 血量过低: {Hp}/{MaxHp}", evt.Hp, evt.MaxHp); }
    private void OnDeath(DeathEvent evt) { this._wasDead = true; this.SetActiveState("已死亡"); this._lowHpFlag = false; this._logger.LogInformation("[Event] 💀 角色死亡"); }
    private void OnRespawn(RespawnEvent evt) { this._wasDead = false; this._deathStartTime = null; this._idleTicks = 0; this._noTargetStreak = 0; this._lowHpFlag = false; this.SetActiveState("复活恢复"); this._logger.LogInformation("[Event] 🔄 角色复活于 ({X},{Y})", evt.Position.X, evt.Position.Y); }

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
        this._goalScheduler.ForceReevaluation();
    }

    private void OnNpcInRange(NpcInRangeEvent evt)
    {
        this._logger.LogDebug("[Event] 🗺️ NPC #{Num}「{Name}」进入感知范围", evt.NpcNumber, evt.NpcName);
    }

    private void OnEventOpen(EventOpenEvent evt)
    {
        this._logger.LogInformation("[EventWatcher] 🔔 {Name} Lv.{Level} 入场窗口已打开!", evt.Name, evt.GameLevel);

        var miniGameDef = this.FindMiniGameDefinition(evt.Type, evt.GameLevel);
        if (miniGameDef is null)
        {
            this._logger.LogWarning("[EventInterrupt] 未找到 MiniGameDefinition: {Type} Lv.{Level}", evt.Type, evt.GameLevel);
            return;
        }

        // Step 1: 检查当前任务是否可中断
        if (this._eventInterrupt.ShouldInterruptForEvent(this._lastTask, evt))
        {
            // 中断当前任务（标记为 Suspended，不标 Failed）
            if (this._lastTask is not null && this._lastTask.Status == MissionStatus.Active)
            {
                this._lastTask.Status = MissionStatus.Suspended;
                this._logger.LogInformation("[EventInterrupt] ⏸ 暂停当前任务 {Title}", this._lastTask.Title);
            }

            // 设置中断上下文，下一 tick 的 === 4) 中断处理 阶段会自然处理
            this.Interrupt($"事件开放: {evt.Name}");
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
                // 无门票无材料 → 不参与，事件任务自然失败
                this._logger.LogInformation("[EventInterrupt] ⏭ {Name} 无门票无材料，跳过参与", evt.Name);
                break;

            case EventReadiness.LevelTooLow:
            case EventReadiness.NotEnoughMoney:
                this._logger.LogInformation("[EventInterrupt] ⏭ {Name} 条件不足: {Readiness}", evt.Name, readiness);
                break;
        }
    }

    /// <summary>
    /// 注入仓库取物任务 — 从仓库中取出门票或合成材料。
    /// </summary>
    private void InjectVaultRetrieveMission(MiniGameDefinition miniGameDef)
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
    /// 从配置文件查找匹配的 MiniGameDefinition。
    /// </summary>
    private MiniGameDefinition? FindMiniGameDefinition(MiniGameType miniGameType, int gameLevel)
    {
        var config = this._player.GameContext?.Configuration;
        if (config is null)
        {
            return null;
        }

        return config.MiniGameDefinitions
            .FirstOrDefault(d => d.Type == miniGameType && d.GameLevel == gameLevel);
    }

    private void OnEventReminder(EventReminderEvent evt)
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

    private void OnEventClosed(EventClosedEvent evt)
    {
        this._logger.LogInformation("[EventWatcher] 🔴 {Name} Lv.{Level} 已结束", evt.Name, evt.GameLevel);
        var eventId = $"event_{evt.Type}_{evt.GameLevel}";
        var existing = this._missionBoard.BoardState.Missions.FirstOrDefault(m => m.Id == eventId);
        if (existing is not null && existing.Status == MissionStatus.Active)
        {
            this.MarkTaskFailed(existing, FailureReason.NotExecutable);
        }
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
