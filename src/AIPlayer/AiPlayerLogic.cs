// <copyright file="AiPlayerLogic.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using System.Diagnostics;
using System.Linq;
using System.Threading;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.AIPlayer.Scripting;
using OAPS.AccessLayer;
using OAPS.Mind;

/// <summary>
/// Drives an <see cref="AiPlayer"/> with periodic behavior ticks.
/// Acts as module coordinator:
///   1. Refreshes <see cref="BehaviorContext.WorldState"/> each tick
///   2. Delegates to ScriptExecutor (1D) or Heartbeat (Decision mode)
///   3. Handles non-module periodic tasks (stat allocation)
/// </summary>
public sealed class AiPlayerLogic : IDisposable
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly CancellationTokenSource _cts;
    private readonly Task _loopTask;
    private readonly BehaviorContext _context;
    private readonly Scripting.ScriptExecutor? _scriptExecutor;
    private readonly Decision.HeartbeatService? _heartbeat;
    private readonly Decision.MissionBoardService? _missionBoard;
    private readonly bool _stepMode;
    private int _tickCounter;

    /// <summary>游戏引擎内挂助手（替代脚本级战斗/拾取/巡逻）。</summary>
    private AiOfflineHelper? _offlineHelper;

    /// <summary>经验学习器 — 每 tick 收集战斗/拾取/死亡数据。</summary>
    private ExperienceLearner? _experienceLearner;

    /// <summary>OAPS 认知循环 — 心智引擎主控器，每 400ms 执行感知→记忆→情感→决策循环。</summary>
    private CognitiveLoop? _cognitiveLoop;

    /// <summary>OAPS 三层记忆系统（感觉/工作/长期记忆）。</summary>
    private MemorySystem? _memorySystem;

    /// <summary>OAPS 个性与情感引擎 — 8 维个性 + OCC 情感模型。</summary>
    private PersonalityEngine? _personalityEngine;

    /// <summary>原生执行服务 — 用于 OpenMuActionExecutor 执行原子操作。</summary>
    private readonly NativeExecutionService? _nativeExec;

    /// <summary>Fugu v4.0 orchestrator — dynamic per-step routing (null if not initialized).</summary>
    private OAPS.Mind.FuguOrchestrator? _fuguOrchestrator;

    /// <summary>Fugu v4.0 script bridge — translates Fugu routing to script actions.</summary>
    private Scripting.FuguScriptBridge? _fuguBridge;

    /// <summary>Fugu v4.0 shared memory layer (set from AiPlayerManager after construction).</summary>
    private Knowledge.SharedMemoryLayer? _sharedMemory;

    /// <summary>AI behavior collector — feeds AI events into BehaviorEventStore for continuous learning.</summary>
    private AiBehaviorCollector? _behaviorCollector;

    /// <summary>Previous experience value for kill detection.</summary>
    private long _prevExperience;

    /// <summary>上次已知等级 — 用于检测升级事件。</summary>
    private int _lastKnownLevel;

    /// <summary>断线检测计数 — 连续检测到断线次数。</summary>
    private int _disconnectStreak;

    /// <summary>重连冷却：上次尝试重连时间。</summary>
    private DateTime _lastReconnectAttempt = DateTime.MinValue;

    /// <summary>重连冷却间隔。</summary>
    private static readonly TimeSpan ReconnectCooldown = TimeSpan.FromSeconds(30);

    /// <summary>断线确认阈值（连续检测到断线的 tick 次数，防误判）。</summary>
    private const int DisconnectConfirmThreshold = 3; // ~1.2秒

    // Non-module periodic task state
    private DateTime _lastStatTick = DateTime.UtcNow;

    private int SearchRange => this._scriptExecutor?.CurrentParameters?.SearchRange ?? 20;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiPlayerLogic"/> class.
    /// </summary>
    /// <param name="player">The AI player to control.</param>
    /// <param name="selector">Optional algorithm selector; uses player's default if omitted.</param>
    /// <param name="stepMode">When true, the tick loop is disabled and <see cref="TickOnceAsync"/> must be called manually. Used by the debug test client.</param>
    /// <param name="script">Optional behavior script. When provided, the ScriptExecutor is used instead of the DAG-based module executor (1D mode).</param>
    public AiPlayerLogic(AiPlayer player, AlgorithmSelector? selector = null, bool stepMode = false, BehaviorScript? script = null)
    {
        this._player = player;
        this._adapter = new GameAdapter(player);
        this._stepMode = stepMode;

        // Ensure algorithm selector is ready
        selector?.RegisterAlgorithms();

        // Create shared context — uses the SAME adapter instance
        this._context = new BehaviorContext(player)
        {
            GameAdapter = this._adapter,
        };

        // Load behavior execution engine: ScriptExecutor (1D) or Heartbeat (Decision)
        if (script is not null)
        {
            // 1D Script-driven mode: priority-chain executor replaces DAG modules
            this._scriptExecutor = new Scripting.ScriptExecutor(player, this._context, script, player.ScriptPath);
            player.Logger.LogInformation("[AiPlayerLogic] Script-driven mode: {ScriptId} v{Version}",
                script.Id, script.Version);

            // Initialize game engine's built-in auto-bot (内挂) for combat/pickup/healing.
            // Set MuHelperSettings on the player so OfflinePlayer handlers work correctly.
            var muSettings = new DefaultMuHelperSettings();
            player.MuHelperSettings = muSettings;
            this._offlineHelper = new AiOfflineHelper(player, muSettings, player.Position, player.Logger);
            player.Logger.LogInformation("[AiPlayerLogic] OfflineHelper initialized at ({X},{Y})", player.Position.X, player.Position.Y);

            // Initialize experience learning system
            if (player.ExperienceMemory is { } expMem && player.CharacterMemory is { } charMem)
            {
                this._experienceLearner = new ExperienceLearner(player, expMem, charMem, player.Logger);
                player.ExperienceLearner = this._experienceLearner;
                player.Logger.LogInformation("[AiPlayerLogic] ExperienceLearner initialized for {Char}", player.SelectedCharacter?.Name);
            }

            // Initialize native execution service for OAPS action executor
            this._nativeExec = new NativeExecutionService(player, player.Logger);
        }
        else if (script is null && player.SelectedCharacter is not null)
        {
            // Decision mode: Heartbeat + MissionBoard drives behavior autonomously
            this._nativeExec = new NativeExecutionService(player, player.Logger);
            this._missionBoard = new Decision.MissionBoardService(player, this._adapter, player.Logger);
            this._heartbeat = new Decision.HeartbeatService(player, this._context, this._missionBoard, this._adapter, player.Logger);
            player.Logger.LogInformation("[AiPlayerLogic] Decision mode: Heartbeat-driven (no script)");
        }

        // 初始化 OAPS 心智引擎（认知循环）
        this._memorySystem = new MemorySystem();
        this._personalityEngine = new PersonalityEngine();
        var oapsSensor = new OpenMuWorldSensor(player);
        var oapsExecutor = new OpenMuActionExecutor(player, this._adapter, this._nativeExec!);
        var oapsDecision = new DecisionCore(null);
        this._cognitiveLoop = new CognitiveLoop(oapsSensor, this._memorySystem, this._personalityEngine, oapsDecision);
        this._lastKnownLevel = player.Level;
        this._prevExperience = player.SelectedCharacter?.Experience ?? 0;
        player.Logger.LogInformation("[OAPS] CognitiveLoop initialized for {Char}", player.SelectedCharacter?.Name);

        // Initialize AI behavior collector (feeds events into PBO pipeline)
        if (player.BehaviorEventStore is not null)
        {
            this._behaviorCollector = new AiBehaviorCollector(
                player.BehaviorEventStore,
                player.SelectedCharacter?.Name ?? "unknown",
                player.Logger);
            this._behaviorCollector.SetInitialState(
                player.SelectedCharacter?.Experience ?? 0,
                player.Level,
                0); // Map will be set on first tick
            player.Logger.LogInformation("[AiCollector] Initialized for {Char}", player.SelectedCharacter?.Name);
        }

        // Start the adaptive tick loop
        this._cts = new CancellationTokenSource();
        this._loopTask = this.RunLoopAsync();
    }

    /// <summary>
    /// Gets or sets the target map number for cross-map navigation.
    /// When set and the player is not on this map, the AI will find
    /// and use enter gates to reach it. Cleared on arrival.
    /// Delegates to the shared <see cref="BehaviorContext"/>.
    /// </summary>
    public ushort? TargetMapNumber
    {
        get => this._context.TargetMapNumber;
        set => this._context.TargetMapNumber = value;
    }

    /// <summary>
    /// Gets the current tick counter. Increments each time <see cref="TickCoreAsync"/> runs.
    /// </summary>
    public int TickCounter => this._tickCounter;

    /// <summary>
    /// Gets a value indicating whether the background loop task is still running.
    /// Returns false if the loop has completed, faulted, or was cancelled.
    /// In step mode, returns false since no loop was started.
    /// </summary>
    public bool IsRunning => !this._loopTask.IsCompleted;

    /// <summary>
    /// Gets a value indicating whether the background loop task has faulted
    /// (crashed due to an unhandled exception).
    /// </summary>
    public bool HasCrashed => this._loopTask.IsFaulted;

    /// <summary>
    /// Gets the shared behavior context for debug inspection.
    /// </summary>
    public BehaviorContext Context => this._context;

    /// <summary>
    /// Reloads the running script. Safe to call from any thread;
    /// delegates to ScriptExecutor.ReloadScript which atomically
    /// queues the replacement for the next tick boundary.
    /// No-op in DAG module mode (no script executor).
    /// </summary>
    public void ReloadScript(Scripting.BehaviorScript script)
    {
        if (this._scriptExecutor is not null)
        {
            this._scriptExecutor.ReloadScript(script);
        }
    }

    /// <summary>
    /// Gets the HeartbeatService, or null if not in Decision mode.
    /// </summary>
    public Decision.HeartbeatService? GetHeartbeat() => this._heartbeat;

    /// <summary>Gets whether in Script-driven (1D) mode or Decision mode.</summary>
    public bool IsScriptMode => this._scriptExecutor is not null;

    /// <summary>Gets the MissionBoardService, or null if not in Decision mode.</summary>
    public Decision.MissionBoardService? GetMissionBoard() => this._missionBoard;

    /// <summary>Gets the FuguOrchestrator, or null if not initialized.</summary>
    public OAPS.Mind.FuguOrchestrator? FuguOrchestrator => _fuguOrchestrator;

    /// <summary>Gets the FuguScriptBridge, or null if not initialized.</summary>
    public Scripting.FuguScriptBridge? FuguBridge => _fuguBridge;

    /// <summary>Initializes Fugu v4.0 components. Called by AiPlayerManager after AiPlayerLogic creation.</summary>
    public void InitializeFuguComponents(Knowledge.SharedMemoryLayer sharedMemory, string? sftWeightsPath = null)
    {
        _sharedMemory = sharedMemory;
        var workerId = this._player.SelectedCharacter?.Name ?? $"ai_{System.Guid.NewGuid():N}";
        var logger = this._player.Logger;
        try
        {
            // Prefer bootstrapped SFT weights over random initialization
            var weightsPath = sftWeightsPath ?? this._player.SftWeightsPath;
            _fuguOrchestrator = new OAPS.Mind.FuguOrchestrator(weightsPath, workerId, sharedMemory, logger);
            _fuguBridge = new Scripting.FuguScriptBridge(_fuguOrchestrator, logger);
            logger.LogInformation("[Fugu] v4.0 orchestrator initialized for {Worker}{Bootstrap}",
                workerId, weightsPath is not null ? " (bootstrapped)" : "");
        }
        catch (System.Exception ex)
        {
            logger.LogWarning(ex, "[Fugu] Init failed for {Worker} (non-critical)", workerId);
        }
    }

    /// <summary>
    /// Gets the formatted execution statistics from the ScriptExecutor.
    /// Returns null if not in script-driven mode (DAG mode) or if the executor is not initialized.
    /// </summary>
    public string? GetScriptStats()
    {
        return this._scriptExecutor?.GetStats();
    }

    /// <summary>
    /// Gets the ScriptExecutor instance, or null if in DAG module mode (no script loaded).
    /// </summary>
    public Scripting.ScriptExecutor? GetScriptExecutor() => this._scriptExecutor;

    /// <summary>
    /// Gets the current behavior description from the ScriptExecutor
    /// (e.g. "战斗中", "巡逻中"). Null in DAG module mode.
    /// </summary>
    public string? CurrentBehavior => this._scriptExecutor?.CurrentBehavior;

    /// <summary>
    /// Gets the name/id of the currently loaded script from the ScriptExecutor.
    /// </summary>
    public string? ScriptName => this._scriptExecutor?.ScriptName;

    /// <summary>
    /// Gets the current position within the script from the ScriptExecutor
    /// (paragraph label or last executed node name).
    /// </summary>
    public string? ScriptPosition => this._scriptExecutor?.ScriptPosition;

    /// <summary>
    /// Gets the script lines for debug display.
    /// </summary>
    public string[]? ScriptLines => this._scriptExecutor?.ScriptLines;

    /// <summary>
    /// Gets the current line number within the script for debug display.
    /// </summary>
    public int ScriptLineNumber => this._scriptExecutor?.CurrentLineNumber ?? 0;

    /// <summary>
    /// Executes exactly one tick of AI behavior.
    /// Only valid when constructed with <c>stepMode: true</c>.
    /// </summary>
    public async ValueTask TickOnceAsync()
    {
        await this.TickAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this._cts.Cancel();
        this._cts.Dispose();
    }

    private async Task RunLoopAsync()
    {
        // Step mode: timer loop is disabled, caller drives ticks via TickOnceAsync()
        if (this._stepMode)
        {
            return;
        }

        try
        {
            while (!this._cts.Token.IsCancellationRequested)
            {
                // [D2] Adaptive interval: extend to 1000ms when degraded, 400ms normal
                var interval = 400;
                await Task.Delay(interval, this._cts.Token).ConfigureAwait(false);

                // 断线检测：在每 tick 前检查连接状态
                if (!this._player.IsConnected)
                {
                    this._disconnectStreak++;
                    if (this._disconnectStreak >= DisconnectConfirmThreshold
                        && DateTime.UtcNow - this._lastReconnectAttempt >= ReconnectCooldown)
                    {
                        this._lastReconnectAttempt = DateTime.UtcNow;
                        this._disconnectStreak = 0;
                        this._player.Logger.LogWarning("[Reconnect] AI角色 {Char} 断线, 尝试重连...",
                            this._player.SelectedCharacter?.Name);
                        await this.TryReconnectAsync(this._cts.Token).ConfigureAwait(false);
                    }

                    continue;
                }

                this._disconnectStreak = 0;
                await this.TickAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via Dispose() — no logging needed
        }
        catch (Exception ex)
        {
            this._player.Logger.LogError(ex, "[AiPlayerLogic] RunLoop task crashed for {Character}", this._player.SelectedCharacter?.Name);
        }
    }

    private async ValueTask TickAsync()
    {
        try
        {
            await this.TickCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._player.Logger.LogError(ex, "AI tick error for {Character}.", this._player.SelectedCharacter?.Name);
        }
    }

    private async ValueTask TickCoreAsync()
    {
        this._tickCounter++;
        var logger = this._player.Logger;
        this._context.BeginTickRecording(this._tickCounter, this._adapter.GetPlayerPosition());

        // ScriptExecutor mode: allow NpcDialogOpened (quest dialogs) and Dead (death recovery)
        // so is_dead/wait_respawn/just_revived script conditions can fire.
        // Decision mode (Heartbeat): allow all states, HeartbeatService handles state filtering internally
        if (this._player.PlayerState.CurrentState != GameLogic.PlayerState.EnteredWorld
            && !(this._scriptExecutor is not null && (this._player.PlayerState.CurrentState == GameLogic.PlayerState.NpcDialogOpened
                                                      || this._player.PlayerState.CurrentState == GameLogic.PlayerState.Dead))
            && this._heartbeat is null)
        {
            this.RecordEndOfTickSnapshot();
            return;
        }

        var map = this._adapter.GetCurrentMap();
        if (map is null && this._player.PlayerState.CurrentState != GameLogic.PlayerState.Dead)
        {
            this.RecordEndOfTickSnapshot();
            return;
        }

        logger.LogTrace("AI tick: entering main body, pos={pos}", this._adapter.GetPlayerPosition());

        // Periodic diagnostic summary (every ~10s = 25 ticks)
        if (this._tickCounter % 25 == 0)
        {
            var atkCount = this._context.WorldState.AttackablesInRange?.Count ?? 0;
            logger.LogInformation("[AI Diag] {Name} tick={Tick}, pos=({X},{Y}), map={Map}, walking={Walking}, targets={Targets}, hp={HP}/{MaxHP}",
                this._player.SelectedCharacter?.Name ?? "?",
                this._tickCounter,
                this._adapter.GetPlayerPosition().X, this._adapter.GetPlayerPosition().Y,
                map?.Definition.Number ?? 0,
                this._adapter.IsPlayerWalking(),
                atkCount,
                this._adapter.GetCurrentHp(),
                this._adapter.GetMaxHp());

            // [P0D] AttackablesInRange detail — shows monster composition
            var attackables = this._context.WorldState.AttackablesInRange;
            if (attackables is not null && attackables.Count > 0)
            {
                var monsterSummary = attackables
                    .OfType<Monster>()
                    .GroupBy(m => m.Definition?.Number ?? 0)
                    .Select(g => $"#{g.Key}({g.Count()})");
                logger.LogInformation("[P0D] AttackablesInRange: total={Total}, monsters=[{Monsters}]",
                    attackables.Count, string.Join(", ", monsterSummary));
            }
        }

        // 1. Refresh world state snapshot for all modules
        var timing = this._context.Timing;
        var worldSw = Stopwatch.StartNew();

        // 死亡状态下 map 可能为 null，跳过世界刷新但继续执行脚本（is_dead → wait_respawn）
        if (map is not null)
        {
            this._context.WorldState = new WorldState
            {
                CurrentMap = map,
                PlayerPosition = this._adapter.GetPlayerPosition(),
                AttackablesInRange = map.GetAttackablesInRange(this._adapter.GetPlayerPosition(), SearchRange),
            DropsInRange = map.GetDropsInRange(this._adapter.GetPlayerPosition(), SearchRange)
                .ToList(),
            IsAtSafezone = false,
            OtherPlayersInRange = map.GetAttackablesInRange(this._adapter.GetPlayerPosition(), 100)
                .OfType<Player>()
                .Where(p => p != this._player && !IsInSameParty(p))
                .Take(20)
                .ToList(),
        };
        }

        worldSw.Stop();
        timing.WorldRefreshUs = worldSw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

        // 1.5 Fugu v4.0: Record state + route decision (learns from actual behavior, does not yet override)
        if (_fuguBridge is not null && _fuguBridge.IsEnabled)
        {
            try
            {
                var pos = this._adapter.GetPlayerPosition();
                var attackables = this._context.WorldState?.AttackablesInRange;
                var drops = this._context.WorldState?.DropsInRange;
                var ctx = new Scripting.FuguStateContext
                {
                    Hp = this._adapter.GetCurrentHp(),
                    MaxHp = this._adapter.GetMaxHp(),
                    Mp = this._adapter.GetCurrentMp(),
                    MaxMp = this._adapter.GetMaxMp(),
                    Level = this._player.Level,
                    IsAtSafeZone = false,
                    FreeSlots = 64,
                    IsSurrounded = (attackables?.Count ?? 0) >= 3,
                    TargetCount = attackables?.Count ?? 0,
                    NearestMonsterLevel = (int)(attackables?.FirstOrDefault()?.Attributes?[GameLogic.Attributes.Stats.Level] ?? 0),
                    NearbyItems = drops?.Count ?? 0,
                    HotspotDistance = 255f,
                    TimeSinceLastKill = 0,
                    IsDebuffed = false,
                    PositionX = pos.X,
                    PositionY = pos.Y,
                    PersonalityTemperature = 1.0f,
                };
                _fuguBridge.Route(ctx);
            }
            catch (Exception ex)
            {
                this._player.Logger.LogDebug(ex, "[Fugu] Route step error (non-critical)");
            }
        }

        // 2. Run game engine's built-in auto-bot (OfflinePlayer 内挂 handlers)
        // Handles combat, healing, pickup, buff, repair — all at the engine level.
        // This replaces the script-level combat/pickup/patrol with battle-tested GameLogic code.
        if (this._offlineHelper is not null
            && this._player.PlayerState.CurrentState == GameLogic.PlayerState.EnteredWorld)
        {
            var offlineSw = Stopwatch.StartNew();
            await this._offlineHelper.TickAsync().ConfigureAwait(false);
            offlineSw.Stop();
            timing.OfflineHelperUs = offlineSw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
        }

        // 3. Execute behavior engine: ScriptExecutor (1D) or Heartbeat (Decision)
        // ScriptExecutor now handles HIGH-LEVEL flow only (quests, death, restock, map nav)
        if (this._scriptExecutor is not null)
        {
            var scriptSw = Stopwatch.StartNew();
            var tickResult = await this._scriptExecutor.TickAsync().ConfigureAwait(false);
            scriptSw.Stop();
            timing.ScriptExecutionUs = scriptSw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
            this._context.RecordDecision("script", scriptSw.Elapsed);
            if (tickResult.State == MUnique.OpenMU.AIPlayer.ScriptTaskState.Stuck)
                this._player.Logger.LogWarning("[AiPlayer] Script watchdog: PC={Pc} stuck", tickResult.PC);

            // Drain HeartbeatService EventBus
            if (this._heartbeat is not null)
            {
                this._heartbeat.DrainEventBus();
            }
        }
        else if (this._heartbeat is not null)
        {
            // Decision mode: Heartbeat-driven
            var hbSw = Stopwatch.StartNew();
            await this._heartbeat.BeatAsync().ConfigureAwait(false);
            hbSw.Stop();
            timing.ScriptExecutionUs = hbSw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
            this._context.RecordDecision("heartbeat", hbSw.Elapsed);
        }

        // Compute total tick duration
        timing.TotalUs = timing.WorldRefreshUs + timing.ScriptExecutionUs;
        this._context.Timing = timing;

        // 3. Non-module periodic tasks — stat allocation
        var now = DateTime.UtcNow;

        // Stat allocation (every 2 seconds — fast enough for low-level stat ramp-up)
        if (this._lastStatTick.AddSeconds(2) < now)
        {
            this._lastStatTick = now;
            await this.AllocateStatsWithGatingAsync().ConfigureAwait(false);
        }

        // Experience learner — record kills/drops/deaths each tick
        this._experienceLearner?.Tick();

        // AI Behavior Collector — feed events into PBO pipeline for continuous learning
        if (this._behaviorCollector is not null && this._tickCounter % 25 == 0)
        {
            try
            {
                var charExp = this._player.SelectedCharacter?.Experience ?? 0;
                var charLevel = this._player.Level;
                var mapId = this._adapter.GetCurrentMap()?.Definition.Number ?? 0;
                var pos = this._adapter.GetPlayerPosition();
                if (charLevel > this._lastKnownLevel) this._lastKnownLevel = charLevel;

                this._behaviorCollector.Collect(charExp, charLevel, mapId, pos.X, pos.Y);
            }
            catch (Exception ex)
            {
                this._player.Logger.LogDebug(ex, "[AiCollector] Collection error (non-critical)");
            }
        }

        // OAPS 心智引擎 tick（并行于现有行为系统）
        if (this._cognitiveLoop is not null)
        {
            DecisionResult mindResult;
            try
            {
                mindResult = await this._cognitiveLoop.ThinkAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[OAPS] CognitiveLoop.ThinkAsync failed");
                mindResult = DecisionResult.NoAction();
            }

            if (mindResult.Action != DecisionAction.Wait)
            {
                logger.LogDebug("[OAPS] CognitiveLoop decision: {Action} ({Reason})",
                    mindResult.Action, mindResult.Reason);
            }
        }

        // 检测升级 → 记录经历
        var currentLevel = this._adapter.GetPlayerLevel();
        if (currentLevel > this._lastKnownLevel)
        {
            this._lastKnownLevel = currentLevel;
            this._cognitiveLoop?.RecordExperience(new ExperienceEvent
            {
                IsLevelUp = true,
                IsSuccess = true,
                Timestamp = DateTime.UtcNow,
            });
        }

        // Record end-of-tick snapshot for debug state API.
        // Early-return paths (walking, not-entered-world, no-map) each
        // call this individually.  The main execution path reaches here.
        this.RecordEndOfTickSnapshot();
    }

    private async ValueTask AllocateStatsWithGatingAsync()
    {
        try
        {
            var character = this._player.SelectedCharacter;
            if (character is null || character.LevelUpPoints <= 0)
            {
                return;
            }

            // Use per-player BuildDirection if set, otherwise fall back to default
            var direction = this._player.BuildDirection
                ?? StatAllocationStrategy.GetDefaultDirection(character.CharacterClass?.Number ?? 0);
            var phase = StatAllocationStrategy.GetPhase(direction, this._adapter.GetPlayerLevel());
            if (phase is null)
            {
                return;
            }

            // Allocate up to 5 points per tick
            var points = character.LevelUpPoints;
            var toAllocate = Math.Min(points, 5);
            if (toAllocate <= 0)
            {
                return;
            }

            var strPoints = (ushort)(toAllocate * phase.StrWeight);
            var agiPoints = (ushort)(toAllocate * phase.AgiWeight);
            var vitPoints = (ushort)(toAllocate * phase.VitWeight);
            var enePoints = (ushort)(toAllocate * phase.EneWeight);

            // Distribute remainder to the highest-weight stat
            var allocated = strPoints + agiPoints + vitPoints + enePoints;
            if (allocated < toAllocate)
            {
                var remaining = (ushort)(toAllocate - allocated);
                var maxWeight = Math.Max(phase.StrWeight, Math.Max(phase.AgiWeight, Math.Max(phase.VitWeight, phase.EneWeight)));
                if (maxWeight == phase.StrWeight) { strPoints += remaining; }
                else if (maxWeight == phase.AgiWeight) { agiPoints += remaining; }
                else if (maxWeight == phase.VitWeight) { vitPoints += remaining; }
                else { enePoints += remaining; }
            }

            var increaseStats = new MUnique.OpenMU.GameLogic.PlayerActions.Character.IncreaseStatsAction();
        if (strPoints > 0) { await increaseStats.IncreaseStatsAsync(this._player, MUnique.OpenMU.GameLogic.Attributes.Stats.BaseStrength, strPoints).ConfigureAwait(false); }

        if (agiPoints > 0) { await increaseStats.IncreaseStatsAsync(this._player, MUnique.OpenMU.GameLogic.Attributes.Stats.BaseAgility, agiPoints).ConfigureAwait(false); }

        if (vitPoints > 0) { await increaseStats.IncreaseStatsAsync(this._player, MUnique.OpenMU.GameLogic.Attributes.Stats.BaseVitality, vitPoints).ConfigureAwait(false); }

        if (enePoints > 0) { await increaseStats.IncreaseStatsAsync(this._player, MUnique.OpenMU.GameLogic.Attributes.Stats.BaseEnergy, enePoints).ConfigureAwait(false); }
        }
        catch (Exception ex)
        {
            this._player.Logger.LogWarning(ex, "[StatAlloc] 分配属性点异常（静态构造器未就绪？）");
        }
    }

    private void RecordEndOfTickSnapshot()
    {
        this._context.EndTickRecording(
            (uint)this._adapter.GetCurrentHp(),
            (uint)this._adapter.GetMaxHp(),
            (uint)this._adapter.GetCurrentMp(),
            (uint)this._adapter.GetMaxMp(),
            this._adapter.GetPlayerLevel(),
            (ushort)(this._adapter.GetCurrentMap()?.Definition.Number ?? 0));
    }

    /// <summary>
    /// 断线重连 — 检测到连接断开后尝试重新登录。
    /// 保存进度 → 断旧连接 → 冷却等待 → 通过 AiPlayerManager 重新加载。
    /// 在当前版本中，将重连请求委托给 AiPlayerManager（通过 IAiService）。
    /// 作为兜底，RunLoop 在检测到断线后会等待冷却然后尝试重新初始化。
    /// </summary>
    private async ValueTask TryReconnectAsync(CancellationToken ct)
    {
        var characterName = this._player.SelectedCharacter?.Name;
        if (string.IsNullOrEmpty(characterName))
        {
            return;
        }

        try
        {
            this._player.Logger.LogInformation("[Reconnect] 开始断线重连: {Char}", characterName);

            // 保存当前进度
            await this._player.SaveProgressAsync().ConfigureAwait(false);

            // 断开旧连接（释放地图资源）
            await this._player.DisconnectAsync().ConfigureAwait(false);

            // 停止当前循环 — RunLoop 会 catch OperationCanceledException 并退出
            await this._cts.CancelAsync().ConfigureAwait(false);

            this._player.Logger.LogInformation("[Reconnect] 断线处理完成，上层将重新创建 AI 角色");
        }
        catch (Exception ex)
        {
            this._player.Logger.LogError(ex, "[Reconnect] 重连失败: {Char}", characterName);
        }
    }
    private bool IsInSameParty(Player other)
    {
        var myParty = this._player.Party;
        if (myParty is null) return false;
        return myParty.PartyList.Contains(other);
    }

    /// <summary>
    /// Gets a recommended hunting map based on the player's level.
    /// Scans all configured maps and calculates average monster level from MonsterSpawns.
    /// Recommends the lowest-numbered map whose average monster level is within
    /// the player's reach (player level + 5).
    /// </summary>
    /// <param name="topN">Ignored; we return the single best match.</param>
    /// <returns>The recommended map number with a score, or (0, 0) for Lorencia as fallback.</returns>
    public (ushort MapNumber, double Score) GetRecommendedMap(int topN = 3)
    {
        try
        {
            var config = this._player.GameContext?.Configuration;
            if (config?.Maps is null)
            {
                return (0, 0.0); // Lorencia fallback
            }

            var playerLevel = this._adapter.GetPlayerLevel();
            ushort bestMap = 0;
            double bestScore = 0.0;

            foreach (var mapDef in config.Maps)
            {
                if (mapDef is null || mapDef.MonsterSpawns is null || mapDef.MonsterSpawns.Count == 0)
                {
                    continue;
                }

                // Calculate average monster level for this map from spawns
                double totalLevel = 0;
                int count = 0;

                foreach (var spawn in mapDef.MonsterSpawns)
                {
                    var monster = spawn?.MonsterDefinition;
                    if (monster is null || monster.Attributes is null)
                    {
                        continue;
                    }

                    // Try to read the Level attribute via the indexer; if the monster
                    // doesn't have a Level attribute, try FirstOrDefault on the collection.
                    float monsterLevel;
                    try
                    {
                        monsterLevel = monster[Stats.Level];
                    }
                    catch (InvalidOperationException)
                    {
                        var attr = monster.Attributes.FirstOrDefault(a => a.AttributeDefinition == Stats.Level);
                        if (attr is null)
                        {
                            continue;
                        }

                        monsterLevel = attr.Value;
                    }

                    totalLevel += monsterLevel;
                    count++;
                }

                if (count == 0)
                {
                    continue;
                }

                var avgLevel = totalLevel / count;

                // Only recommend maps where average monster level is within reach
                if (avgLevel <= playerLevel + 5)
                {
                    // Score: higher is better. Prefer lower map numbers (closer to safezone).
                    // Score = 100 - monster_level_diff gives 100 for perfectly matched maps.
                    // Among equal-score maps, lower map number wins (first encountered
                    // in ascending iteration order, which is the natural map order).
                    var score = 100.0 - avgLevel;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestMap = (ushort)mapDef.Number;
                    }
                }
            }

            return (bestMap, bestScore);
        }
        catch (Exception ex)
        {
            this._player.Logger.LogWarning(ex, "[GetRecommendedMap] Failed to scan maps, falling back to Lorencia");
            return (0, 0.0);
        }
    }

}
