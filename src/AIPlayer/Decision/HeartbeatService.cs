// <copyright file="HeartbeatService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.AIPlayer.Scripting;
using MUnique.OpenMU.AIPlayer.AIStateMachine;
using MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;
using System.Diagnostics;
using System.IO;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.AIPlayer.Decision.Skills;

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
public sealed class HeartbeatService : IEventBroadcaster
{
    private readonly AiPlayer _player;
    private readonly MissionBoardService _missionBoard;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;
    private readonly NpcInteractionService _npcService;
    private readonly BehaviorContext _context;
    private readonly OAPS.Mind.DecisionCore _decisionCore; // v4.0 Fugu 三轨决策核心

    private readonly SurvivalMode _survival;
    private readonly PetHandlerModule _petHandler;
    private readonly InventoryManagerService _inventoryManager;
    private readonly ValueAssessmentService _valueAssessment;
    private readonly MarketPriceService _marketPrice;
    private readonly TransactionMonitor _transactionMonitor;
    private readonly Dictionary<string, IBehaviorSubModule> _modules = new();
    private DynamicMissionGenerator _missionGenerator;
    private GoalScheduler _goalScheduler;
    private MaterialKnowledgeService _materialKnowledge;

    /// <summary>脚本注册表 — 统一管理所有 IBehaviorSubModule 实例。</summary>
    private readonly ScriptLibrary _scriptLib;

    /// <summary>规则引擎 — 条件评估 + 脚本选择。</summary>
    private readonly RuleEngine _ruleEngine;

    /// <summary>行为日志服务 — 记录每次任务执行的全过程。</summary>
    private readonly Experience.ExperienceService _expService;

    /// <summary>事件中断决策服务 — 判断是否中断当前任务去参加事件。</summary>
    private readonly EventInterruptService _eventInterrupt;

    /// <summary>仓库服务 — 检查/存取仓库物品。</summary>
    private readonly VaultService _vaultService;

    /// <summary>装备自动换装服务 — 背包有更好装备就换上。</summary>
    private readonly EquipmentCompareService _equipmentCompare;

    /// <summary>技能自动学习服务 — 有技能书就学。</summary>
    private readonly SkillLearnService _skillLearn;

    /// <summary>SKILL 执行器 — 统一管理条件+执行脚本。</summary>
    private readonly Skills.SkillExecutor _skillExecutor;

    /// <summary>后台任务管理器 — 管理所有 Fire-and-Forget 操作。</summary>
    private readonly AITaskManager _taskManager;

    /// <summary>运行时学习者 — 记录击杀/掉落，更新 KG 边权重。</summary>
    private readonly KnowledgeGraphRuntimeLearner? _runtimeLearner;

    /// <summary>健康检查服务 — 每心跳检查内存/CPU/残留任务。</summary>
    private readonly HealthCheckService _healthCheck;

    /// <summary>规则热更新监控 — 监听 scripts-rules.json 变更。</summary>
    private readonly RuleWatcherService _ruleWatcher;

    /// <summary>技能栏管理器 — 自动设置快捷键。</summary>
    private readonly SkillBarManager _skillBar;

    /// <summary>背包维护 MVP — 多阶段背包整理。</summary>
    private readonly InventoryMaintenanceMvp _inventoryMvp;

    /// <summary>NPC 购物 MVP — 商店购买/修理。</summary>
    private readonly NpcShoppingMvp _npcShopMvp;

    /// <summary>玩家交易 MVP — 处理交易请求。</summary>
    private readonly PlayerTradeMvp _playerTradeMvp;

    /// <summary>背包维护时间戳（每30秒触发一次）。</summary>
    private DateTime _lastMaintenanceTime = DateTime.MinValue;

    /// <summary>跨地图路由规划器。</summary>
    private readonly Warp.WarpPlanner _warpPlanner;

    /// <summary>事件处理器 — 委托到 EventHandlers 类处理外部事件和材料重评估。</summary>
    private readonly EventHandlers _eventHandlers;

    /// <summary>决策系统 — 任务选择/失败/阻塞/推进逻辑。</summary>
    private readonly DecisionSystem _decisionSystem;

    /// <summary>状态机执行器 — 负责任务执行、中断、跨图移动、生存补给。</summary>
    private readonly AIStateMachineExecutor _stateMachine;

    private int _idleTicks;
    private int _noTargetStreak;
    private bool _wasDead;
    private DateTime? _deathStartTime;
    private int _taskTicks;

    // Respawn death-loop break: tracks rapid death cycles
    private int _respawnDeathStreak;
    private DateTime _lastDeathTime;
    private MissionItem? _lastTask; // 当前任务已执行 tick 数
    private int _npcDialogTicks;    // NPC 对话已持续 tick 数

    // 事件驱动状态标志
    private bool _lowHpFlag;
    private int _lastKnownHp;
    private int _lastKnownMaxHp;
    private int _lastKnownLevel;

    /// <summary>熔断阈值。</summary>
    private const int NoTargetCircuitBreaker = 30;

    /// <summary>低血量阈值 — 提升到 60% 以便在危险地图有足够反应时间。</summary>
    private const float LowHpThreshold = 0.6f;

    /// <summary>低法力阈值。</summary>
    private const float LowMpThreshold = 0.25f;

    /// <summary>AI 事件总线。</summary>
    public AiEventBus EventBus { get; } = new();

    /// <summary>
    /// Drain EventBus queue, dispatching queued events to registered handlers.
    /// Called externally by AiPlayerLogic after ScriptExecutor.TickAsync so that
    /// DeathEvent/RespawnEvent from ScriptExecutor reach OnDeath/OnRespawn
    /// without requiring BeatAsync to run.
    /// </summary>
    public void DrainEventBus() => this.EventBus.DrainEvents();

    /// <summary>获取看板状态（供外部 API 访问测试结果等）。</summary>
    public BoardState BoardState => this._missionBoard.BoardState;

    /// <summary>热重载规则（供外部 API 调用）。</summary>
    public void ReloadRules() => this._ruleWatcher.Reload();

    public IBehaviorSubModule? ActiveModule => this._stateMachine.ActiveModule;
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

        // Initialize RuntimeLearner from the static KnowledgeGraph holder.
        // This records kills and drops to continuously improve KG edge weights.
        if (KnowledgeGraphHolder.Graph is { } kg)
        {
            this._runtimeLearner = new KnowledgeGraphRuntimeLearner(kg, logger);

            // F13: Upsert worker profile in KG for swarm task assignment
            try
            {
                var wp = new Knowledge.KnowledgeGraph.WorkerProfileNode(
                    kg, logger as Microsoft.Extensions.Logging.ILogger<Knowledge.KnowledgeGraph.WorkerProfileNode>
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Knowledge.KnowledgeGraph.WorkerProfileNode>.Instance);
                var name = player.SelectedCharacter?.Name ?? "unknown";
                wp.UpsertProfile(name, "combat", 0.5f);
                wp.UpsertProfile(name, "survival", 0.5f);
            }
            catch { /* non-critical */ }
        }

        this._marketPrice = new MarketPriceService(logger);
        this._valueAssessment = new ValueAssessmentService(player, logger, this._marketPrice);
        this._transactionMonitor = new TransactionMonitor(this._marketPrice, logger);
        this._inventoryManager = new InventoryManagerService(player, adapter, logger, this._valueAssessment);
        this._materialKnowledge = new MaterialKnowledgeService(player.GameContext!.Configuration, logger);

        // ScriptLibrary 统一管理所有 IBehaviorSubModule 实例
        this._scriptLib = new ScriptLibrary(
            player, adapter, this._missionBoard.BoardState, context, logger,
            this._materialKnowledge, this._valueAssessment);

        // 提取直接在 BeatAsync 中使用的模块引用
        this._survival = (SurvivalMode)this._scriptLib.Get("survival")!;
        this._petHandler = (PetHandlerModule)this._scriptLib.Get("pet_handler")!;

        // 填充 _modules 字典（向下兼容 DecisionSystem / AIStateMachineExecutor）
        foreach (var kvp in this._scriptLib.GetAll())
        {
            this._modules[kvp.Key] = kvp.Value;
        }

        this._missionGenerator = new DynamicMissionGenerator(player, adapter, this._missionBoard.BoardState, logger, this._valueAssessment);
        this._goalScheduler = new GoalScheduler(player, adapter, logger);
        this._missionGenerator.SetGoalScheduler(this._goalScheduler);

        // 规则引擎初始化
        this._ruleEngine = new RuleEngine(this._scriptLib, logger);
        this._decisionCore = new OAPS.Mind.DecisionCore(logger);

        // 行为日志服务（引用群体经验服务）
        this._expService = this._context.ExpService ?? new Decision.Experience.ExperienceService(AppContext.BaseDirectory, logger);

        this._decisionSystem = new DecisionSystem(
            player, adapter, this._missionBoard, context,
            this._missionGenerator, this._goalScheduler,
            this._inventoryManager, this._marketPrice,
            (IReadOnlyDictionary<string, IBehaviorSubModule>)this._modules.AsReadOnly(), logger);

        this._eventInterrupt = new EventInterruptService(player, logger);
        this._vaultService = new VaultService(player, logger);
        this._equipmentCompare = new EquipmentCompareService(player, adapter, logger);
        this._skillLearn = new SkillLearnService(player, adapter, logger);

        // 新 SKILL/MVP 实例化
        this._skillBar = new SkillBarManager(player, logger);
        this._inventoryMvp = new InventoryMaintenanceMvp(player, this._inventoryManager, logger);
        this._npcService = new NpcInteractionService(player, logger);
        this._npcShopMvp = new NpcShoppingMvp(player, adapter, this._npcService, logger);
        this._playerTradeMvp = new PlayerTradeMvp(player, logger);

        // SKILL 执行器 — 注册所有条件+执行脚本
        this._skillExecutor = new Skills.SkillExecutor(
            new Skills.ISkill[]
            {
                new Skills.SurvivalHpSkill(logger),
                new Skills.InventoryCleanupSkill(this._inventoryManager, logger),
                new Skills.AutoEquipSkill(this._equipmentCompare, logger),
                new Skills.LearnSkillSkill(this._skillLearn, logger),
            },
            logger);

        // 后台任务管理器 — Fire-and-Forget 操作
        this._taskManager = new AITaskManager(logger);

        // 健康检查 — 内存/CPU/残留任务清理
        this._healthCheck = new HealthCheckService(logger);

        // 规则热更新监控 — 监听 scripts-rules.json 变更
        var rulesFilePath = Path.Combine(AppContext.BaseDirectory, "Decision", "scripts-rules.json");
        this._ruleWatcher = new RuleWatcherService(rulesFilePath, json => this._ruleEngine.ReloadFromJson(json), logger);

        this._warpPlanner = new Warp.WarpPlanner(player.GameContext!.Configuration, logger);

        // 状态机执行器 — 承载所有任务执行和生存补给逻辑
        this._stateMachine = new AIStateMachineExecutor(
            player,
            adapter,
            this._context,
            this._modules.AsReadOnly(),
            this._npcService,
            this._warpPlanner,
            logger);

        this._eventHandlers = new EventHandlers(
            player, adapter, this._missionBoard, this._materialKnowledge,
            this._eventInterrupt, logger);
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
        this.EventBus.Subscribe<EventOpenEvent>(evt => ((IEventBroadcaster)this).OnEventOpen(evt.Type, evt.GameLevel, evt.Name, evt.EntranceFee));
        this.EventBus.Subscribe<EventReminderEvent>(evt => ((IEventBroadcaster)this).OnEventReminder(evt.Type, evt.GameLevel, evt.Name, evt.MinutesLeft));
        this.EventBus.Subscribe<EventClosedEvent>(evt => ((IEventBroadcaster)this).OnEventClosed(evt.Type, evt.GameLevel, evt.Name));

        if (adapter is GameAdapter ga)
        {
            ga.EventPublisher = this.EventBus.Publish;
            ga.ChatMessageReceived += (sender, msg, type) =>
            {
                if (type == ChatMessageType.Normal)
                {
                    this._transactionMonitor.ProcessChatMessage(sender, msg);
                    this._playerTradeMvp.HandleChatMessage(sender, msg);
                }
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

            // 首次启动时自动买药 — 这会在 NPC 购物 MVP 空闲时执行
            if (!this._npcShopMvp.IsShopping)
            {
                this._npcShopMvp.BeginShopping(NpcShoppingMvp.ShopType.Potions);
                this._logger.LogInformation("[HB] 自动买药已触发");
            }
        }

        // === Test Runner: 检测挂起的测试 ===
        // 两阶段执行：
        //   心跳1: executed=0 → 执行操作，设 executed=1
        //   心跳2: executed=1 → 读取结果，设 completed=true（等待 test-result API 读取后清除）
        var pendingTest = this._missionBoard.BoardState.PendingTest;
        if (pendingTest is not null)
        {
            var testType = pendingTest.GetValueOrDefault("type") as string;
            var executed = pendingTest.TryGetValue("executed", out var execObj) && execObj is int execCount ? execCount : 0;

            if (executed > 0)
            {
                // 已执行过 — 检查是否需要写入最终结果
                if (pendingTest.GetValueOrDefault("status") as string == "check_result")
                {
                    // 第二次进入 → 检查结果
                    if (testType == "auto_equip")
                    {
                        var inv = this._player.Inventory;
                        var equipSlots = inv?.Items
                            .Where(i => i.ItemSlot <= 11)
                            .Select(i => (i.Definition?.Name.ToString() ?? "?"))
                            .ToList() ?? new();
                        pendingTest["result"] = string.Join(",", equipSlots);
                        pendingTest["swordInBag"] = inv?.Items.Any(i =>
                            i.Definition?.Group == 0 && i.Definition?.Number == 16 && i.ItemSlot > 11) == true;
                        pendingTest["completed"] = true;
                        this._logger.LogInformation("[TestRunner] auto_equip 完成: swordInBag={InBag}", pendingTest["swordInBag"]);
                    }
                    else if (testType == "learn_skill")
                    {
                        var inv = this._player.Inventory;
                        var skillId = pendingTest.GetValueOrDefault("skillId") is int si ? si : 0;
                        var slot = pendingTest.GetValueOrDefault("skillBookSlot") is int sl ? sl : 0;
                        var bookStillInBag = inv?.Items.Any(i =>
                            i.Definition?.Group == 15 && i.ItemSlot == slot) == true;
                        var skillLearned = this._player.SkillList?.ContainsSkill((ushort)skillId) == true;

                        pendingTest["bookConsumed"] = !bookStillInBag;
                        pendingTest["skillLearned"] = skillLearned;
                        pendingTest["completed"] = true;
                        this._logger.LogInformation("[TestRunner] learn_skill 完成: bookConsumed={Consumed}, skillLearned={Learned}",
                            !bookStillInBag, skillLearned);
                    }
                    else if (testType == "survival_hp")
                    {
                        var inv = this._player.Inventory;
                        var potionSlot = pendingTest.GetValueOrDefault("potionSlot") is int ps ? ps : 0;
                        var potionConsumed = inv?.Items.Any(i => i.ItemSlot == potionSlot && i.Durability > 0) != true;
                        var initialHp = pendingTest.GetValueOrDefault("initialHp") is int ih ? ih : 0;
                        var currentHp = this._adapter.GetCurrentHp();
                        var hpImproved = currentHp > initialHp;

                        pendingTest["potionConsumed"] = potionConsumed;
                        pendingTest["currentHp"] = currentHp;
                        pendingTest["hpImproved"] = hpImproved;
                        pendingTest["completed"] = true;
                        this._logger.LogInformation("[TestRunner] survival_hp 完成: potionConsumed={Consumed}, HP={Hp}(was {Init})",
                            potionConsumed, currentHp, initialHp);
                    }
                    else if (testType == "rule_engine_chain")
                    {
                        var inv = this._player.Inventory;
                        var equipSlots = inv?.Items
                            .Where(i => i.ItemSlot <= 11)
                            .Select(i => (i.Definition?.Name.ToString() ?? "?"))
                            .ToList() ?? new();
                        var swordInBag = inv?.Items.Any(i =>
                            i.Definition?.Group == 0 && i.Definition?.Number == 16 && i.ItemSlot > 11) == true;
                        var freeSlotCount = this._inventoryManager.GetFreeSlotCount();
                        var skillBookConsumed = inv?.Items.Any(i =>
                            i.Definition?.Group == 15) != true;

                        pendingTest["equipped"] = string.Join(",", equipSlots);
                        pendingTest["swordInBag"] = swordInBag;
                        pendingTest["freeSlots"] = freeSlotCount;
                        pendingTest["skillBookConsumed"] = skillBookConsumed;
                        pendingTest["completed"] = true;
                        this._logger.LogInformation("[TestRunner] rule_engine_chain 完成: equipped={Eq}, freeSlots={Fs}, skillConsumed={Sc}",
                            string.Join(",", equipSlots), freeSlotCount, skillBookConsumed);
                    }
				}
				// completed 为 true → 跳过（等待 test-result API 读取后清除 PendingTest）
				if (pendingTest.TryGetValue("completed", out var compObj) && compObj is true)
				{
					goto AfterPendingTest;
				}
				// status=check_result 但未 completed → 继续等下一 tick
				goto AfterPendingTest;
			}

            // 首次执行
            this._logger.LogInformation("[TestRunner] 执行挂起测试: {Type}", testType);

            if (testType == "auto_equip")
            {
                // 先清理装备位的药水/消耗品，避免引擎在心跳间隙自动补给药水干扰换装判定
                var invClean = this._player.Inventory;
                if (invClean is not null)
                {
                    var potionItems = invClean.Items
                    .Where(i => i.ItemSlot <= InventoryConstants.LastEquippableItemSlotIndex
                                && i.Definition?.ItemSlot is null)
                    .ToList();
                    foreach (var p in potionItems)
                    {
                    invClean.ItemStorage.Items.Remove(p);
                    }
                    if (potionItems.Count > 0)
                    {
                        await this._player.SaveProgressAsync().ConfigureAwait(false);
                        this._logger.LogInformation("[TestRunner] auto_equip: 已清理 {Count} 件装备位药水/消耗品", potionItems.Count);
                    }
                }

                if (this._equipmentCompare is not null)
                {
                    await this._equipmentCompare.AutoEquipIfBetterAsync().ConfigureAwait(false);
                    // 设 executed=1 + status=check_result，下次心跳再读取结果
                    pendingTest["executed"] = 1;
                    pendingTest["status"] = "check_result";
                    this._logger.LogInformation("[TestRunner] auto_equip 已执行，等待下次心跳检查装备结果");
                    goto AfterPendingTest;
                }
            }
            else if (testType == "inventory_cleanup")
            {
                var beforeFree = this._inventoryManager.GetFreeSlotCount();
                await this._inventoryManager.ForceCleanupAsync().ConfigureAwait(false);
                var afterFree = this._inventoryManager.GetFreeSlotCount();
                pendingTest["executed"] = 1;
                pendingTest["beforeFree"] = beforeFree;
                pendingTest["afterFree"] = afterFree;
                pendingTest["freeSlotIncrease"] = afterFree - beforeFree;
                pendingTest["completed"] = true;
                this._logger.LogInformation("[TestRunner] inventory_cleanup 完成: {Before}->{After}", beforeFree, afterFree);
            }
            else if (testType == "learn_skill")
            {
                if (this._skillLearn is not null)
                {
                    var learned = await this._skillLearn.TryLearnSkillsAsync().ConfigureAwait(false);
                    pendingTest["executed"] = 1;
                    pendingTest["status"] = "check_result";
                    pendingTest["immediateLearned"] = learned;
                    this._logger.LogInformation("[TestRunner] learn_skill 已执行(learned={Learned})，等待下次心跳验证", learned);
                    goto AfterPendingTest;
                }
            }
            else if (testType == "survival_hp")
            {
                // 直接模仿心跳的 survival_hp 逻辑：找药水、喝药
                var inv = this._player.Inventory;
                var beforeHp = this._adapter.GetCurrentHp();
                var hpPotion = inv?.Items.FirstOrDefault(i =>
                    i.Definition?.Group == 14 && i.Durability > 0);
                if (hpPotion is not null)
                {
                    await this._adapter.ConsumeItemAsync(hpPotion.ItemSlot).ConfigureAwait(false);
                    this._logger.LogInformation("[TestRunner] survival_hp: 已喝药水(Slot={Slot}, HP={Hp}->{After})",
                        hpPotion.ItemSlot, beforeHp, this._adapter.GetCurrentHp());
                }
                else
                {
                    this._logger.LogWarning("[TestRunner] survival_hp: 背包中无药水");
                }
                pendingTest["executed"] = 1;
                pendingTest["status"] = "check_result";
                pendingTest["beforeHp"] = beforeHp;
                goto AfterPendingTest;
            }
            else if (testType == "rule_engine_chain")
            {
                // 执行一轮规则引擎评估（只匹配最高优先级规则）
                var chainResult = this._ruleEngine.Evaluate(this._player, this._adapter);
                if (chainResult is not null)
                {
                    pendingTest["matchedRule"] = chainResult.Rule.RuleId;
                    pendingTest["matchedPriority"] = chainResult.Rule.Priority;
                    pendingTest["matchedDescription"] = chainResult.Rule.Description;

                    // 规则→脚本→执行器：Rule.ScriptId → ScriptLibrary → module.ExecuteStepAsync
                    // 规则指向 ScriptLibrary 中的模块ID（combat/survival/inventory/quest）
                    var mission = chainResult.GeneratedMission;
                    var module = this._scriptLib.Get(chainResult.Rule.ScriptId);
                    if (module is not null && mission is not null)
                    {
                        var result = await module.ExecuteStepAsync(mission).ConfigureAwait(false);
                        this._context.RecordDecision(chainResult.Rule.ScriptId, TimeSpan.Zero);
                        this._logger.LogDebug("[HB] Rule→Script: {RuleId} → {ScriptId} result={Result}",
                            chainResult.Rule.RuleId, chainResult.Rule.ScriptId, result);
                    }
                    else
                    {
                        this._logger.LogDebug("[HB] 规则 {RuleId}: ScriptId={ScriptId} module={HasModule} mission={HasMission}",
                            chainResult.Rule.RuleId, chainResult.Rule.ScriptId,
                            module is not null, mission is not null);
                    }

                    this._logger.LogInformation("[TestRunner] rule_engine_chain: 匹配规则 {RuleId}(Priority={P})", chainResult.Rule.RuleId, chainResult.Rule.Priority);
                }
                else
                {
                    pendingTest["matchedRule"] = "none";
                    pendingTest["matchedPriority"] = -1;
                    this._logger.LogWarning("[TestRunner] rule_engine_chain: 无规则匹配");
                }

                pendingTest["executed"] = 1;
                pendingTest["status"] = "check_result";
                goto AfterPendingTest;
            }
        }

    AfterPendingTest:

        this._logger.LogDebug("[HB] Beat: state={State}", this._activeState);

        // === 0) Drain 事件队列 + 同步角色状态 ===
        this.EventBus.DrainEvents();
        if (this._adapter is GameAdapter ga)
        {
            ga.DrainChatMessages();
        }

        this._missionBoard.SyncPlayerState();

        var hp = this._adapter.GetCurrentHp();
        var maxHp = this._adapter.GetMaxHp();
        this.DetectStateChanges(hp, maxHp);

        // === 1) 死亡检测 + 自动重生 fallback ===
        if (hp <= 0 || !this._player.IsAlive)
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

        // 强制复活检测：当 HP > 0 但 IsAlive=false 或 PlayerState 卡在 Dead 时
        // （例如通过 API set-hp 强行补血后，游戏引擎未触发重生流程）
        if (this._wasDead || !this._player.IsAlive ||
            this._player.PlayerState.CurrentState == GameLogic.PlayerState.Dead ||
            this._player.PlayerState.CurrentState.IsDisconnectedOrFinished())
        {
            this._wasDead = false;
            this._deathStartTime = null;
            this._player.IsAlive = true;

            // 尝试恢复 PlayerState
            if (this._player.PlayerState.CurrentState == GameLogic.PlayerState.Dead ||
                this._player.PlayerState.CurrentState.IsDisconnectedOrFinished())
            {
                await this._player.PlayerState.TryAdvanceToAsync(GameLogic.PlayerState.EnteredWorld).ConfigureAwait(false);
            }
            this._wasDead = false;
            this._idleTicks = 0;
            this._noTargetStreak = 0;
            this._lowHpFlag = false;
            this._logger.LogInformation("[HB] ✅ 角色复活，重置决策状态");
            this.SetActiveState("复活恢复");

            // === 1.5) 复活后补给：买药 + 修装备 ===
            // 复活后传送回安全区，旁边通常有 Potion Girl(226)
            // 先尝试补给，不影响下一个 tick 继续执行任务
            await this.PostRespawnSupplyAsync().ConfigureAwait(false);
        }

        // === 1.6) 宠物处理：暗黑骑士行为管理 ===
        await this._petHandler.ExecuteStepAsync(new MissionItem()).ConfigureAwait(false);

        // === 1.7) 跨地图传送检测（优先级高于行走检测）===
        // CraftingModule / EventExecutorModule 返回 NoTarget 时标记目标地图，
        // 决策系统会切换到跨地图传送任务。需要在行走检测之前执行，
        // 否则 AI 在巡逻行走时永远不会触发跨图传送。
        if (this._context.TargetMapNumber.HasValue)
        {
            var targetMap = this._context.TargetMapNumber.Value;
            var arrived = await this._stateMachine.ExecuteWarpAsync(targetMap).ConfigureAwait(false);
            if (!arrived)
            {
                this.SetActiveState($"传送至地图 #{targetMap}");
                return;
            }
            this._logger.LogInformation("[HB] ✅ 到达目标地图 #{Map}，继续执行任务", targetMap);
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

            if (!this._stateMachine.HasActiveScript)
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

        // === 3) 低血量恢复 + 嗑药 ===
        // 先在死亡/行走之前主动嗑药（无论当前什么状态）
        await this.TryConsumePotionsAsync().ConfigureAwait(false);

        // 注：不再调用 _survival.ExecuteStepAsync 喝红
        // 统一由 Step 5.6 的 survival_hp 规则管理喝红动作
        // 这里只打日志，不做实际执行（避免两套逻辑冲突）
        if (this._lowHpFlag || (maxHp > 0 && (float)hp / maxHp < LowHpThreshold))
        {
            this._lowHpFlag = false;
            this._logger.LogDebug("[HB] ❤️ 血量 {Hp}/{MaxHp} 偏低 — 由规则引擎处理喝红", hp, maxHp);
            this.SetActiveState("低血恢复");
            if (this._adapter.IsPlayerWalking()) return;
        }

        // === 4) 中断处理 ===
        if (this._stateMachine.PendingInterrupt is not null)
        {
            await this.HandleInterruptAsync().ConfigureAwait(false);
            return;
        }

        // === 5) NoTarget 熔断（跨地图传送检测已移到 1.7） ===

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

        // === 5.5) 健康检查 + 后台任务轮询（先清理再发射新任务） ===
        var health = this._healthCheck.Check(this._taskManager);
        if (health.GcTriggered || health.StaleTasksCleaned > 0)
        {
            this._logger.LogDebug("[HB] 健康检查: Mem={Mem}MB Tasks={Active} GC={Gc} Cleaned={Clean}",
                health.MemoryMB, health.ActiveBackgroundTasks, health.GcTriggered, health.StaleTasksCleaned);
        }

        // 轮询已完成的后台任务结果
        var completedTasks = this._taskManager.PollResults();
        foreach (var result in completedTasks)
        {
            this._logger.LogDebug("[HB] 后台任务完成: {Type} Status={Status} ({Duration:F1}s)",
                result.TaskType, result.Status, result.Duration.TotalSeconds);
        }

        // === 5.55) 技能栏更新（同步、非阻塞） ===
        this._skillBar.UpdateSkillBar();

        // === 5.56) MVP: 背包维护（每 30 秒执行一次） ===
        if (this._inventoryMvp.IsActive() && this._npcShopMvp.IsShopping)
        {
            // 让购物先完成
        }
        else if (this._inventoryMvp.IsActive())
        {
            await this._inventoryMvp.ExecuteAsync().ConfigureAwait(false);
        }
        else if ((DateTime.UtcNow - this._lastMaintenanceTime).TotalSeconds > 30)
        {
            this._inventoryMvp.BeginMaintenance();
            this._lastMaintenanceTime = DateTime.UtcNow;
            this._logger.LogDebug("[HB] 触发背包维护");
        }

        // === 5.57) MVP: NPC 购物（激活时执行） ===
        if (this._npcShopMvp.IsShopping)
        {
            await this._npcShopMvp.ExecuteAsync().ConfigureAwait(false);
        }

        // === 5.6) SKILL 执行器 — Fire-and-Forget 模式 ===
        // 内部按 Priority 遍历所有 SKILL: survival_hp(1) → inventory_cleanup(10) → auto_equip(20) → learn_skill(25)
        // 发射到后台 AITaskManager，心跳不等待
        if (this._skillExecutor.FireAndForget(this._player, this._adapter, this._taskManager).Count > 0)
        {
            // 有 SKILL 已发射到后台执行，但心跳继续进入决策系统
            // 不像旧的同步模式那样 return 阻塞 tick
        }

        // === 6) v4.0 决策：规则库→规则集→脚本序列→执行 ===
        // Kanban 看板任务阶段 → RuleEngine.EvaluateAll → 规则集
        // → 每个规则加载对应 ScriptLibrary 脚本 → 形成执行序列
        var ruleSet = this._decisionCore.DecideAsync(
            this._player, this._adapter, this._ruleEngine, this._scriptLib);

        // 执行规则集对应的脚本序列
        // 规则→脚本：Rule.ScriptId指JSON脚本文件 → 通过ScriptExecutor加载→执行
        // 规则→模块：如果ScriptLibrary有对应C#模块 → 通过模块.ExecuteStepAsync
        foreach (var (match, module) in ruleSet)
        {
            if (module is not null && match.GeneratedMission is not null)
            {
                // C#模块路径（combat/survival/inventory等）
                try
                {
                    await module.ExecuteStepAsync(match.GeneratedMission).ConfigureAwait(false);
                    this._context.RecordDecision(match.Rule.ScriptId, TimeSpan.Zero);
                }
                catch (Exception ex)
                {
                    this._logger.LogWarning(ex, "[HB] 规则 {RuleId} 模块执行失败", match.Rule.RuleId);
                }
            }
            else if (match.Rule.ScriptId.StartsWith("learned_"))
            {
                // JSON脚本路径（learned_fashi01_13等）
                var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "learned", $"{match.Rule.ScriptId}.json");
                if (File.Exists(scriptPath))
                {
                    try
                    {
                        var script = Scripting.ScriptExecutor.LoadFromFile(scriptPath);
                        if (script is not null && this._player.Logic is not null)
                        {
                            this._player.Logic.ReloadScript(script);
                            this._context.RecordDecision(match.Rule.ScriptId, TimeSpan.Zero);
                            this._logger.LogInformation("[HB] 规则 {RuleId} → 加载脚本 {ScriptId}", match.Rule.RuleId, match.Rule.ScriptId);
                        }
                    }
                    catch (Exception ex)
                    {
                        this._logger.LogWarning(ex, "[HB] 规则 {RuleId} 脚本加载失败", match.Rule.RuleId);
                    }
                }
            }
        }

        var current = await this._decisionSystem.SelectCurrentTaskAsync().ConfigureAwait(false);
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

        // === 8) 执行当前任务的当前阶段（Fire-and-Forget 到后台） ===
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

        // 检查该任务是否已有后台任务在运行（防止重复发射）
        var missionTaskType = $"mission_{current.Id}";
        if (this._taskManager.HasActiveTask(missionTaskType))
        {
            this._logger.LogDebug("[HB] {Title} 已在后台执行，跳过本次发射", current.Title);
            return;
        }

        // 快速跳过+任务切换检测（这部分需要同步做）
        if (current.Category is QuestCategory.Survival ||
            (current.Type == MissionType.Quest && current.QuestDef is not null && current.Category != QuestCategory.InstanceEvent))
        {
            if (current.Type == MissionType.Quest && current.QuestDef is not null)
            {
                if (current.Script is not null)
                {
                    // 有脚本的任务放行
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

        // 发射到后台执行，心跳不等待
        this._taskManager.RunTask(
            missionTaskType,
            async ct =>
            {
                var stageResult = await this._stateMachine.ExecuteStepAsync(current).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                // 记录决策到 BehaviorContext
                this._context.RecordDecision(current.Module, TimeSpan.Zero);

                switch (stageResult)
                {
                    case StepResult.Completed:
                        this._logger.LogInformation("[HB] ✅ {Title} (stage={Stage}) 完成", current.Title, current.CurrentStageIndex);
                        this.RecordActionLog(current, Experience.BehaviorResult.Completed);
                        this.AdvanceTask(current);
                        break;
                    case StepResult.InProgress:
                        break;
                    case StepResult.Failed:
                        this._logger.LogWarning("[HB] ❌ {Title} 失败", current.Title);
                        var failReason = current.Category == QuestCategory.InstanceEvent
                            ? FailureReason.InstanceModuleMissing
                            : FailureReason.NotExecutable;
                        this.RecordActionLog(current, Experience.BehaviorResult.Failed);
                        this.MarkTaskFailed(current, current.FailureRetryable ? failReason : FailureReason.NotRetryable);
                        break;
                    case StepResult.NoTarget:
                        if (this._context.TargetMapNumber.HasValue)
                        {
                            this._logger.LogInformation("[HB] 🗺 {Title} 需要跨地图到 #{Target}，切换到传送",
                                current.Title, this._context.TargetMapNumber.Value);
                            break;
                        }
                        this._idleTicks++;
                        this._noTargetStreak++;
                        if (this._idleTicks > 15)
                        {
                            this._logger.LogInformation("[HB] ⏭ {Title} 长时间无目标(idle={Idle})，标记失败可重试",
                                current.Title, this._idleTicks);
                            this.MarkTaskFailed(current, FailureReason.NoTarget);
                            this._idleTicks = 0;
                            this._noTargetStreak = 0;
                        }
                        break;
                }
            },
            TimeSpan.FromSeconds(15));

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

        // Periodically apply learned drop rates to KG (every ~100s = 250 ticks)
        if (this._taskTicks > 0 && this._taskTicks % 250 == 0)
        {
            if (this._runtimeLearner is { } rl)
            {
                var adjusted = rl.ApplyLearnedDropRates();
                if (adjusted > 0)
                {
                    this._logger.LogInformation("[RuntimeLearner] Applied {Count} drop rate adjustments to KG", adjusted);
                }
            }
        }
    }

    // ===================== 决策委托 =====================

    /// <summary>推进任务:合成任务触发材料重评估,其余委托决策系统。</summary>
    private void AdvanceTask(MissionItem current)
    {
        // 合成任务的材料重评估（决策层不做，心跳层负责）
        if (current.Id.StartsWith("craft_", StringComparison.OrdinalIgnoreCase))
        {
            this.ReevaluateMaterialsAfterCraft(current);
        }

        this._decisionSystem.AdvanceTask(current);
    }

    private void MarkTaskFailed(MissionItem task, FailureReason reason) => this._decisionSystem.MarkTaskFailed(task, reason);

    private void MarkTaskBlocked(MissionItem task, BlockedReason reason) => this._decisionSystem.MarkTaskBlocked(task, reason);

    /// <summary>
    /// 合成后材料重评估。检查目标物品是否已出现在背包(合成成功),
    /// 否则重新注入 farm 任务。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "VSTHRD100", Justification = "Fire-and-forget: called from non-async AdvanceTask, no caller to await")]
    private void ReevaluateMaterialsAfterCraft(MissionItem current) => this._eventHandlers.ReevaluateMaterialsAfterCraft(current);

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
        this._stateMachine.PendingInterrupt = new InterruptContext
        {
            Reason = reason,
        };
        this._logger.LogInformation("[HB] 中断: {Reason}", reason);
    }

    private async ValueTask HandleInterruptAsync() => await this._stateMachine.HandleInterruptAsync();

    /// <summary>
    /// 使用 NPC 对话关闭动作关闭当前对话框。
    /// </summary>
    private async ValueTask CloseNpcDialogAsync() => await this._stateMachine.CloseNpcDialogAsync();

    /// <summary>
    /// 嗑药：委托给状态机执行器。
    /// </summary>
    private async ValueTask TryConsumePotionsAsync() => await this._stateMachine.TryConsumePotionsAsync();

    /// <summary>
    /// 复活补给：委托给状态机执行器。
    /// </summary>
    private async ValueTask PostRespawnSupplyAsync() => await this._stateMachine.PostRespawnSupplyAsync();

    /// <summary>
    /// 手动复活：委托给状态机执行器。
    /// </summary>
    private async ValueTask RespawnPlayerAsync() => await this._stateMachine.RespawnPlayerAsync();

    /// <summary>
    /// 记录行为日志。MissionItem 执行完成或失败后调用。
    /// 日志 = 规则原料，聚合后产出规则候选。
    /// </summary>
    private void RecordActionLog(MissionItem mission, Experience.BehaviorResult result)
    {
        try
        {
            var player = this._player;
            var log = new Experience.ActionLog
            {
                AiRoleName = player.Name ?? "unknown",
                AiLevel = player.Level,
                AiClass = player.SelectedCharacter?.CharacterClass?.Name.ValueInNeutralLanguage ?? "unknown",
                BehaviorType = mission.Category switch
                {
                    QuestCategory.InstanceEvent => Experience.BehaviorType.MiniGame,
                    QuestCategory.Survival => Experience.BehaviorType.Hunting,
                    _ => Experience.BehaviorType.Quest,
                },
                MapId = this._adapter.GetCurrentMap()?.MapId ?? 0,
                Result = result,
                DurationSeconds = (int)(DateTime.UtcNow - this._lastBeatTime).TotalSeconds,
                WorthRepeating = result == Experience.BehaviorResult.Completed,
            };
            this._expService.RecordBehavior(log);
        }
        catch
        {
            // 日志记录失败不影响主循环
        }
    }

    private DateTime _lastBeatTime = DateTime.UtcNow;

    /// <summary>
    /// 经验服务引用（由外部注入）。
    /// </summary>
    internal Experience.ExperienceService ExpService => this._expService;

    #region 事件处理器

    private void OnHpLow(HpLowEvent evt) { this._lowHpFlag = true; this.SetActiveState("低血事件"); this._logger.LogInformation("[Event] ❤️ 血量过低: {Hp}/{MaxHp}", evt.Hp, evt.MaxHp); }
    private void OnDeath(DeathEvent evt)
    {
        this._wasDead = true;
        this.SetActiveState("已死亡");
        this._lowHpFlag = false;
        this._logger.LogInformation("[Event] 💀 角色死亡");

        // Track rapid death streak for respawn loop-breaking
        var now = DateTime.UtcNow;
        if ((now - this._lastDeathTime).TotalSeconds < 30)
        {
            this._respawnDeathStreak++;
            this._logger.LogWarning("[DeathLoop] 快速死亡 #{Streak}，距上次死亡 {Sec}s",
                this._respawnDeathStreak, (now - this._lastDeathTime).TotalSeconds.ToString("F1"));
        }
        else
        {
            this._respawnDeathStreak = 1;
        }

        this._lastDeathTime = now;
    }
    private void OnRespawn(RespawnEvent evt)
    {
        this._wasDead = false;
        this._deathStartTime = null;
        this._idleTicks = 0;
        this._noTargetStreak = 0;
        this._lowHpFlag = false;
        this.SetActiveState("复活恢复");
        this._logger.LogInformation("[Event] 🔄 角色复活于 ({X},{Y})", evt.Position.X, evt.Position.Y);

        // Death-loop break: if the AI has died 3+ times in rapid succession,
        // force a retreat to a safe map (Devias #3) and clear all missions
        // so the AI re-evaluates from scratch instead of going back to the
        // dangerous hotspot.
        if (this._respawnDeathStreak >= 3)
        {
            this._logger.LogWarning("[DeathLoop] 检测到死亡循环 ({Streak}次)，强制退回安全地图 #3 (Devias)",
                this._respawnDeathStreak);
            this._context.TargetMapNumber = 3;
            this._missionBoard.ClearAllMissions();
            this._respawnDeathStreak = 0; // Reset after escalation
        }
    }

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
        this._respawnDeathStreak = 0; // Successful kill breaks the death loop

        // Feed into RuntimeLearner for KG weight updates
        this._runtimeLearner?.RecordMonsterKill((short)evt.MonsterNumber);
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

    private void OnEventOpen(EventOpenEvent evt) => this._eventHandlers.OnEventOpen(evt);

    /// <summary>
    /// 注入仓库取物任务 — 从仓库中取出门票或合成材料。
    /// </summary>
    private void InjectVaultRetrieveMission(MiniGameDefinition miniGameDef) => this._eventHandlers.InjectVaultRetrieveMission(miniGameDef);

    /// <summary>
    /// 注入打门票材料任务 — 无门票无材料时，创建 farm_ticket 任务并设为事件前驱。
    /// 用 MaterialKnowledgeService 确定掉落目标（怪物、地图、材料等级）。
    /// </summary>
    private void InjectFarmTicketMission(MiniGameDefinition miniGameDef) => this._eventHandlers.InjectFarmTicketMission(miniGameDef);

    /// <summary>
    /// 从配置文件查找匹配的 MiniGameDefinition。
    /// </summary>
    private MiniGameDefinition? FindMiniGameDefinition(MiniGameType miniGameType, int gameLevel) => this._eventHandlers.FindMiniGameDefinition(miniGameType, gameLevel);

    private void OnEventReminder(EventReminderEvent evt) => this._eventHandlers.OnEventReminder(evt);

    private void OnEventClosed(EventClosedEvent evt) => this._eventHandlers.OnEventClosed(evt);

    // ===================== IEventBroadcaster Explicit Implementation =====================

    /// <inheritdoc />
    void IEventBroadcaster.OnEventOpen(MiniGameType type, int gameLevel, string name, int entranceFee)
    {
        this.OnEventOpen(new EventOpenEvent(type, gameLevel, name, entranceFee));
    }

    /// <inheritdoc />
    void IEventBroadcaster.OnEventReminder(MiniGameType type, int gameLevel, string name, int minutesLeft)
    {
        this.OnEventReminder(new EventReminderEvent(type, gameLevel, name, minutesLeft));
    }

    /// <inheritdoc />
    void IEventBroadcaster.OnEventClosed(MiniGameType type, int gameLevel, string name)
    {
        this.OnEventClosed(new EventClosedEvent(type, gameLevel, name));
    }

    /// <summary>
    /// 从共享规则引擎同步学习规则 — OAPS 旁观者系统桥梁。
    /// 由 AiPlayerManager 在每个 SyncLearning 周期后调用。
    /// </summary>
    public void SyncLearnedRules(RuleEngine sharedEngine)
    {
        foreach (var rule in sharedEngine.Rules)
        {
            if (rule.AutoGenerated && !this._ruleEngine.Rules.Any(r => r.ScriptId == rule.ScriptId))
            {
                this._ruleEngine.AddSingleLearnedRule(rule);
            }
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
