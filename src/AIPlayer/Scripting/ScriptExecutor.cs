// <copyright file="ScriptExecutor.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Scripting;

using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Orchestrator;
using MUnique.OpenMU.GameLogic.PlayerActions.Quests;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.AIPlayer.Warp;

/// <summary>
/// Priority-chain script executor — the 1D runtime for AI behavior.
///
/// Replaces the DAG-based module executor with a simple priority chain:
/// each tick, the first node whose condition is satisfied executes.
/// This mirrors how game bots work: HP check → MP check → death check →
/// inventory check → combat → patrol.
///
/// All actions call game APIs directly (no hooking needed — we have source access).
/// </summary>
public sealed class ScriptExecutor
{
    private readonly AiPlayer _player;
    private readonly NativeExecutionService _nativeExec;
    private readonly BehaviorContext _context;
    private readonly ILogger _logger;

    // 日志角色标签 — 在构造时设置，所有日志自动包含角色名
    private string _charTag;
    // Active script reference — can be atomically replaced via ReloadScript
    private BehaviorScript _script;

    // Pending script replacement, checked at the start of each TickAsync()
    private BehaviorScript? _pendingReload;

    // Whether hot-reload checks are enabled
    private bool _enableHotReload = true;

    // Action helpers (reuse existing game action objects)
    private readonly CloseNpcDialogAction _closeNpcDialog = new();

    // NpcInteractionService for merchant/quest NPC interactions
    private readonly NpcInteractionService _npcService;

    // Cached resolved parameters (global merged with per-node overrides)
    private readonly Dictionary<string, ScriptParameters> _nodeParams = new();

    // Cooldown for merchant attempts (subtask 4)
    private DateTime _lastFailedMerchantAttempt = DateTime.MinValue;

    // Navigation state
    private Point _patrolCenter;
    private int _patrolTickCounter;
    private int _scriptTickCounter;

    // Blacklist: targets that pathfinding couldn't reach, with expiry tick
    private readonly Dictionary<ushort, int> _targetBlacklist = new();

    // Stuck detection: if we have the same target for too many ticks without moving, clear it
    private IAttackable? _lastTarget;
    private Point _lastTargetPos;
    private int _sameTargetTickCount;

    // Position-stuck detection: if position hasn't changed for too many ticks while in combat,
    // force a random patrol to break the local combat loop
    private Point _lastPosition;
    private int _ticksAtSamePosition;

    // Pickup cooldown: ticks remaining before pickup_nearby can trigger again.
    // Prevents AI from being stuck in a permanent pickup loop when items are
    // constantly dropping within scan range.
    private int _pickupCooldownTicksLeft;

    // E1f: script-driven route walking state
    private string? _scriptRouteId;
    private int _scriptRouteStep;

    // Runtime variables for script logic
    private readonly ScriptVariableSet _variables = new();

    // Quest dialog state: set when walk_to_quest_npc opens NPC dialog,
    // cleared when accept/submit completes. Allows fallthrough to quest
    // actions even if the bot moves away between ticks.
    private bool _questDialogReady;

    // Execution statistics
    private int _totalTicks;
    private int _nodesExecuted;
    private int _actionsFailed;

    // Tick timing (microseconds)
    private long _lastTickDurationUs;

    // Branch hit counts for IF/ELIF/ELSE tracking
    private readonly Dictionary<string, int> _branchHitCounts = new();

    /// <summary>
    /// Result of WalkToQuestNpcWithFallthroughAsync — distinguishes walking-in-progress
    /// from legitimate fallthrough (AlreadyDone) from genuine failures (NoTarget).
    /// </summary>
    private enum WalkToNpcResult
    {
        Walking,     // actively walking toward NPC → InProgress
        Fallthrough, // at NPC or already close enough → AlreadyDone (PC advances)
        Failed,      // NPC not found, path blocked, etc. → NoTarget (PC retries same node)
    }

    /// <summary>
    /// Gets the human-readable Chinese name of the current action/behavior
    /// (e.g. "战斗中", "巡逻中", "索敌中"). Updated every tick.
    /// </summary>
    public string CurrentBehavior { get; private set; } = "初始化中";

    /// <summary>
    /// Gets the name/id of the currently loaded script (e.g. "basic_hunting_loop").
    /// Updated every tick from the active <see cref="BehaviorScript"/>.
    /// </summary>
    public string? ScriptName { get; private set; }

    /// <summary>
    /// Gets the current position within the script: the current paragraph label
    /// (paragraph mode) or the last executed node name (linear mode).
    /// </summary>
    public string? ScriptPosition { get; private set; }

    /// <summary>
    /// Gets the file path of the currently loaded script, if loaded from a file.
    /// </summary>
    public string? ScriptFilePath { get; private set; }

    /// <summary>
    /// Gets the script file content split into lines (pretty-printed JSON).
    /// Used for line-number lookup during debugging and monitoring.
    /// Empty if the script was not loaded from a file.
    /// </summary>
    public string[] ScriptLines { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Gets the 1-based line number of the currently executing node name
    /// within <see cref="ScriptLines"/>, or 0 if not found / not applicable.
    /// </summary>
    public int CurrentLineNumber { get; private set; }

    // Potion cooldown tracking
    private DateTime _lastHpPotionTime = DateTime.MinValue;
    private DateTime _lastMpPotionTime = DateTime.MinValue;

    // Hysteresis state for potion thresholds
    private bool _hpHysteresisActive;
    private bool _mpHysteresisActive;

    // Paragraph mode state
    private readonly Dictionary<string, int> _paragraphTable = new(); // label → index mapping
    private int _currentParagraphIndex;
    private string? _gotoPending; // pending goto target label

    // v2.0: Hotspot system
    private List<Point> _hotspotPoints = new();
    private int _currentHotspotIndex = -1;
    private int _pendingHotspotIndex = -1; // 中断恢复: 行走被打断后重试同一热点
    private int _noMonsterTickCount; // consecutive ticks without finding a monster
    private int _leaveSafezoneTick; // 上次离开安全区的 tick 号，防抖
    private bool _forceLeaveSafezone; // 强制离开安全区标志

    // Anti-stacking: kill efficiency tracking window
    private int _killCountInWindow;
    private int _ticksInWindow;

    // v2.0: Death recovery
    private Point _deathPosition;
    private DateTime _deathStartTime = DateTime.MinValue; // death timeout timer (UtcNow)
    private bool _justRevived; // true for one tick after HP goes from 0 to >0

    // Force-through death tracking: when all terrain-filtered algorithms fail,
    // TryWalkToAsync retries on the original grid. If the AI dies 3+ times
    // while walking these dangerous paths, force-through is disabled and the
    // target area is marked as DangerZone in the shadow map.
    private int _forceThruDeathCount;
    private bool _forceThruActive;

    // General death strategy: tracks consecutive deaths since last kill.
    // Used to escalate strategies — change hotspot → buy potions → change map.
    // Reset to 0 on any kill (progress = AI is still effective).
    // Game design: strong monsters patrol hotspots intentionally; AI must adapt.
    private int _consecutiveDeaths;

    // AR-20: Program Counter (PC) architecture — fetch-execute-advance pipeline
    private int _programCounter;
    private int _ticksAtCurrentPc;
    private ScriptTaskState _executionState = ScriptTaskState.Idle;

    // 三层熔断保护：连续无进展时空转减速
    private int _idleTickCount;           // 连续无进展 tick 数
    private int _hardRecoveryRemaining;   // 硬熔断后剩余冷却 tick

    // Execution tracking for TickResult reporting
    private string? _currentNodeName;
    private string? _currentNodeAction;
    private IActionResult? _lastActionResult;

    // v2.0: Pickup filter for dropped money threshold
    private static readonly HashSet<(short Number, byte Group)> LowValueItems = new()
    {
        // Group 14 (potions): all potion numbers
        { (1, 14) }, { (2, 14) }, { (3, 14) }, { (4, 14) }, { (5, 14) }, { (6, 14) },
        { (7, 14) }, // Antidote
        // Group 15 (ammo): arrows, bolts
        { (1, 15) }, { (2, 15) },
        // Low-value scrolls (Group 15 or similar consumable scrolls)
        { (0, 15) },
        // Town portal scroll (Group 12, Number varies — just include by group 12 for safety)
        // Group varies by server, but Group 0 items below dropLevel 2 are trash
    };

    // Item identifiers for potions
    private static readonly (byte Group, short Number, int HealAmount)[] HpPotions =
    {
        (14, 3, 750),   // Large Healing (Number=3)
        (14, 2, 450),   // Medium Healing (Number=2)
        (14, 1, 200),   // Small Healing (Number=1)
    };

    private static readonly (byte Group, short Number, int ManaAmount)[] MpPotions =
    {
        (14, 6, 300),   // Large Mana (Number=6)
        (14, 5, 150),   // Medium Mana (Number=5)
        (14, 4, 70),    // Small Mana (Number=4)
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptExecutor"/> class.
    /// </summary>
    /// <param name="player">The AI player to control.</param>
    /// <param name="context">The shared behavior context.</param>
    /// <param name="script">The deserialized behavior script.</param>
    /// <param name="scriptFilePath">Optional file path of the script. When provided, the file is read and
    /// pretty-printed to populate <see cref="ScriptLines"/> for line-number lookups.</param>
    public ScriptExecutor(AiPlayer player, BehaviorContext context, BehaviorScript script, string? scriptFilePath = null)
    {
        this._player = player;
        this._context = context;
        this._script = script;
        this._logger = player.Logger;

        // 初始化角色标签，在所有 [ScriptExec] 日志中带上角色标识
        this._charTag = "[" + (player.SelectedCharacter?.Name ?? "?") + "] ";

        // Store the file path
        if (scriptFilePath is not null)
        {
            try
            {
                // Use DSL source if available, otherwise compile from paragraphs
                this.ScriptLines = CompileScriptLines(script);
            }
            catch (Exception ex)
            {
                this._logger.LogWarning(ex, "[ScriptExec]" + this._charTag + " Failed to format readable lines from {Path}", scriptFilePath);
                this.ScriptLines = Array.Empty<string>();
            }
        }

        // Detect paragraph mode vs linear mode
        if (script.Paragraphs is { Count: > 0 } paragraphs)
        {
            for (var i = 0; i < paragraphs.Count; i++)
            {
                this._paragraphTable[paragraphs[i].Label] = i;
            }

            this._currentParagraphIndex = 0;
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " Paragraph mode activated with {Count} paragraphs", paragraphs.Count);

            // Paragraph mode: resolve node parameters against global defaults
            foreach (var paragraph in paragraphs)
            {
                foreach (var node in paragraph.Nodes)
                {
                    ResolveNodeParameters(node, script.Parameters);
                }
            }
        }
        else
        {
            // Linear mode: resolve parameters for priority chain (including branches)
            foreach (var node in script.PriorityChain)
            {
                ResolveNodeParameters(node, script.Parameters);
            }
        }

        // Initialize patrol center to current position
        this._patrolCenter = context.GameAdapter.GetPlayerPosition();
        this._lastPosition = context.GameAdapter.GetPlayerPosition();
        this._nativeExec = new NativeExecutionService(this._player, this._logger);
        this._npcService = new NpcInteractionService(player, player.Logger);
    }

    /// <summary>
    /// Gets the loaded script.
    /// </summary>
    public BehaviorScript Script => this._script;

    /// <summary>
    /// Gets the current tick counter for this executor.
    /// </summary>
    public int TickCounter => this._scriptTickCounter;

    /// <summary>
    /// Gets the current script parameters.
    /// Returns the merged global parameters from the active script.
    /// Returns null if no script is loaded.
    /// </summary>
    public ScriptParameters? CurrentParameters => this._script?.Parameters;

    /// <summary>上一次 TickAsync 执行耗时 (微秒)。</summary>
    public long LastTickDurationUs => this._lastTickDurationUs;

    /// <summary>
    /// Gets or sets a value indicating whether hot-reload is enabled.
    /// When true, checks for pending script reload at the start of each TickAsync().
    /// Default: true.
    /// </summary>
    public bool EnableHotReload
    {
        get => this._enableHotReload;
        set => this._enableHotReload = value;
    }

    /// <summary>
    /// Atomically replaces the active script with a new one.
    /// The actual switch happens at the start of the next TickAsync() call
    /// to avoid mid-iteration state corruption.
    /// </summary>
    /// <param name="newScript">The new script to activate.</param>
    public void ReloadScript(BehaviorScript newScript)
    {
        this._pendingReload = newScript;
    }

    /// <summary>
    /// Returns a formatted string with execution statistics for monitoring and debugging.
    /// </summary>
    public string GetStats()
    {
        var successRate = this._totalTicks > 0
            ? (this._nodesExecuted * 100.0 / this._totalTicks).ToString("F1")
            : "N/A";
        var stats = $"Script={this._script.Id} v{this._script.Version} | TotalTicks={this._totalTicks} | Executed={this._nodesExecuted} | Failed={this._actionsFailed} | SuccessRate={successRate}%";

        if (this._branchHitCounts.Count > 0)
        {
            var branchInfo = string.Join(" | ", this._branchHitCounts.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={kvp.Value}"));
            stats += $" | Branches: [{branchInfo}]";
        }

        return stats;
    }

    /// <summary>
    /// Loads a script from a JSON file path.
    /// </summary>
    public static BehaviorScript LoadFromFile(string path)
    {
        var json = System.IO.File.ReadAllText(path);
        return JsonSerializer.Deserialize<BehaviorScript>(json)
               ?? throw new InvalidOperationException($"Failed to deserialize script from {path}");
    }

    /// <summary>
    /// Executes one tick of the behavior script (fetch-execute-advance pipeline).
    /// <para>Phase 1 — Hard interrupts (death/HP/MP) checked before PC fetch (CPU-level).</para>
    /// <para>Phase 2 — Instruction buffer: external commands in FIFO order (max 5).</para>
    /// <para>Phase 3 — PC fetch: evaluates condition at current PC, executes action, advances PC.</para>
    /// <para>Phase 4 — Watchdog: PC stuck for 50+ ticks → Stuck state.</para>
    /// <para>PC wraps to 0 at paragraph end (paragraph loop). Goto @label jumps to target paragraph.</para>
    /// </summary>
    /// <returns>A <see cref="TickResult"/> describing the full execution state of this tick.</returns>
    public async ValueTask<TickResult> TickAsync()
    {
        this._scriptTickCounter++;
        this._totalTicks++;

        // Reset execution state for fresh tick (including Completed after stop).
        // prev tick's stop set Completed → heartbeat calls Advance() and starts new script.
        // This tick must start fresh so C# code doesn't lock into Completed.
        if (this._executionState != ScriptTaskState.Suspended
            && this._executionState != ScriptTaskState.Stuck)
        {
            this._executionState = ScriptTaskState.Idle;
        }

        var sw = Stopwatch.StartNew();

        // v2.0: Reset just_revived flag at start of tick (it was set by wait_respawn
        // last tick and consumed by just_revived condition this tick — or stale)
        this._justRevived = false;

        // Decrement pickup cooldown (no lower bound — it's checked with > 0)
        if (this._pickupCooldownTicksLeft > 0)
        {
            this._pickupCooldownTicksLeft--;
        }

        // LeaveSafezone 防抖：已经在安全区外时降低优先级
        this._forceLeaveSafezone = false;
        if (this._context.WorldState.IsAtSafezone)
        {
            this._leaveSafezoneTick = this._scriptTickCounter;
        }
        else if (this._scriptTickCounter - this._leaveSafezoneTick > 10)
        {
            // 连续 10 tick 不在安全区 → 标记已离开
            this._forceLeaveSafezone = true;
        }

        // Anti-stacking: efficiency window tracking
        this._ticksInWindow++;
        if (this._ticksInWindow >= 250) // ~100 seconds at 400ms/tick
        {
            // Periodically reset kill counter to evaluate hunt efficiency
            this._killCountInWindow = 0;
            this._ticksInWindow = 0;
        }

        // Kill detection: if last tick's target just died, count it
        if (this._lastTarget is Monster killedMonster && !killedMonster.IsAlive)
        {
            this._killCountInWindow++;
            this._consecutiveDeaths = 0; // progress made — reset death strategy
            this.ResetIdleTimer(); // 击杀 = 明确进展，重置熔断
        }

        try
        {
        // Check for pending script reload (hot-reload support)
        // We do this at the START of each tick to avoid switching mid-iteration
        // through the priority chain or paragraph nodes.
        if (this._enableHotReload && this._pendingReload is not null)
        {
            var newScript = this._pendingReload;
            this._pendingReload = null;
            this._logger.LogInformation(
                "[ScriptExec]" + this._charTag + " Hot-reload: switching to '{ScriptId}' v{Version} (was '{OldId}' v{OldVersion})",
                newScript.Id, newScript.Version, this._script.Id, this._script.Version);
            this._script = newScript;

            // === Rebuild paragraph table on hot-reload ===
            this._paragraphTable.Clear();
            this._gotoPending = null;

            if (newScript.Paragraphs is { Count: > 0 } paragraphs)
            {
                for (var i = 0; i < paragraphs.Count; i++)
                {
                    this._paragraphTable[paragraphs[i].Label] = i;
                }

                this._currentParagraphIndex = 0;
                this._logger.LogDebug("[ScriptExec]" + this._charTag + " Hot-reload: paragraph mode with {Count} paragraphs", paragraphs.Count);

                // Re-resolve node parameters against new script defaults
                foreach (var paragraph in paragraphs)
                {
                    foreach (var node in paragraph.Nodes)
                    {
                        ResolveNodeParameters(node, newScript.Parameters);
                    }
                }
            }
            else
            {
                this._currentParagraphIndex = 0;
                // Linear mode: re-resolve node parameters against new defaults
                foreach (var node in newScript.PriorityChain)
                {
                    ResolveNodeParameters(node, newScript.Parameters);
                }
            }

            // Reset v2.0 runtime state so stale positions/ticks don't carry over
            this.ResetHotspotState();
            this.ResetDeathState();

            // Reset execution state: hot-reloaded script shouldn't inherit Completed from prior script
            this._executionState = ScriptTaskState.Idle;

            // Clear branch hit counters for fresh diagnostics
            newScript.BranchHitCounts.Clear();
        }

        // Stuck detection: if same target persists for too long without approaching, clear it
        var currentTarget = this._context.CurrentTarget;
        if (currentTarget is not null && currentTarget == this._lastTarget)
        {
            this._sameTargetTickCount++;
            // Use configured stuck detection threshold from ScriptParameters
            if (this._sameTargetTickCount > this._script.Parameters.StuckDetectionTicks)
            {
                this._logger.LogTrace("[ScriptExec]" + this._charTag + " Target {Id} stuck for {N} ticks — clearing", currentTarget, this._sameTargetTickCount);
                if (currentTarget is Monster m)
                {
                    this._targetBlacklist[m.Id] = this._scriptTickCounter;
                }

                this._context.CurrentTarget = null;
                this._sameTargetTickCount = 0;
                this._lastTarget = null;
            }
        }
        else
        {
            this._lastTarget = currentTarget;
            this._sameTargetTickCount = currentTarget is not null ? 1 : 0;
        }

        // Position-based stuck detection: track position independently of CurrentTarget
        // (target-stuck detection above may clear CurrentTarget, so we must track position
        // unconditionally to avoid counter being reset by null CurrentTarget).
        var currentPos = this._context.GameAdapter.GetPlayerPosition();
        var positionStuckThreshold = this._script.Parameters.PositionStuckThreshold;
        if (currentPos == this._lastPosition)
        {
            this._ticksAtSamePosition++;
            if (this._ticksAtSamePosition > positionStuckThreshold
                && this._context.CurrentTarget is not null)
            {
                this._logger.LogInformation(
                    "[ScriptExec]" + this._charTag + " Position stuck at ({X},{Y}) for {N} ticks with target — forcing patrol",
                    currentPos.X, currentPos.Y, this._ticksAtSamePosition);
                this._context.CurrentTarget = null;
                await RandomPatrolAsync(this._script.Parameters).ConfigureAwait(false);
                this._ticksAtSamePosition = 0;
                this._lastPosition = this._context.GameAdapter.GetPlayerPosition();
                return CreateTickResult(ScriptTaskState.Running, pcAdvanced: false);
            }
        }
        else
        {
            this._lastPosition = currentPos;
            this._ticksAtSamePosition = 0;
        }

        // 三层熔断保护 (Anti-Idle)
        // 每次 tick 递增空闲计数器；有进展时 ResetIdleTimer() 清零。
        this._idleTickCount++;

        // 硬熔断：冷却中 → 跳过本 tick
        if (this._hardRecoveryRemaining > 0)
        {
            this._hardRecoveryRemaining--;
            if (this._hardRecoveryRemaining == 0)
            {
                this._logger.LogInformation("[AntiIdle] Hard recovery done — resuming execution");
                this._idleTickCount = 0;
            }
            return CreateTickResult(ScriptTaskState.Suspended, pcAdvanced: false);
        }

        // 硬限触发：连续 idle 超过 hard limit → 强制冷却
        if (this._idleTickCount > this._script.Parameters.IdleHardLimitTicks)
        {
            var recover = this._script.Parameters.IdleRecoveryTicks;
            this._hardRecoveryRemaining = recover;
            this._logger.LogWarning(
                "[AntiIdle] HARD limit ({Limit}) reached after {Idle} ticks — cooling for {Recovery} ticks",
                this._script.Parameters.IdleHardLimitTicks, this._idleTickCount, recover);
            // 复位一些状态防止冷启动后立即再次熔断
            this._context.CurrentTarget = null;
            this._noMonsterTickCount = 0;
            this._currentHotspotIndex = -1;
            return CreateTickResult(ScriptTaskState.Suspended, pcAdvanced: false);
        }

        // 软限触发：连续 idle 超过 soft limit → 日志警告 + 强制巡逻复位
        if (this._idleTickCount > this._script.Parameters.IdleSoftLimitTicks
            && this._idleTickCount % 30 == 0) // 每 30 tick 只警告一次，不刷屏
        {
            this._logger.LogWarning(
                "[AntiIdle] SOFT limit — idle for {Idle} ticks, forcing patrol reset",
                this._idleTickCount);
            // 强制复位当前目标触发巡逻
            this._context.CurrentTarget = null;
        }

        // Phase 0: Warp route — exclusively drive route until complete
        if (this._context.ActiveWarpRoute is not null)
        {
            if (!this._context.WarpInProgress)
            {
                // Execute next step (walk to gate, enter gate, use warp menu)
                await this.ExecuteNextWarpStepAsync().ConfigureAwait(false);
            }

            // Always skip PC fetch while route active (walking or waiting for map change)
            await this.CheckWarpProgressAsync().ConfigureAwait(false);
            goto BuildResult;
        }

        // Phase 1: Hard interrupts — checked BEFORE PC fetch (CPU-level priority)
        if (this._context.GameAdapter.GetCurrentHp() <= 0)
        {
            this._executionState = ScriptTaskState.Suspended;
            this._currentNodeName = "interrupt:is_dead";
            this._currentNodeAction = "wait_respawn";
            await ExecuteActionInternalAsync("wait_respawn", "interrupt:is_dead", this._script.Parameters).ConfigureAwait(false);
            goto BuildResult;
        }

        if (EvaluateHpWithHysteresis(this._script.Parameters))
        {
            this._currentNodeName = "interrupt:hp_below_threshold";
            this._currentNodeAction = "use_hp_potion";
            await UseHpPotionAsync(this._script.Parameters).ConfigureAwait(false);
            goto BuildResult;
        }

        if (EvaluateMpWithHysteresis(this._script.Parameters))
        {
            this._currentNodeName = "interrupt:mp_below_threshold";
            this._currentNodeAction = "use_mp_potion";
            await UseMpPotionAsync(this._script.Parameters).ConfigureAwait(false);
            goto BuildResult;
        }

        // Phase 2: PC fetch-execute-advance — one node per tick
        {
            var nodes = GetCurrentNodes();
            if (nodes.Count > 0)
            {
                // Wrap PC at paragraph end → PC=0 (paragraph loop, AR-20 §3)
                if (this._programCounter >= nodes.Count)
                {
                    WrapParagraph();
                }

                var node = nodes[this._programCounter];
                var nodeParams = ResolveParametersForNode(node);

                // Update external observation state (AR-20 §6)
                this._currentNodeName = node.Name;
                this._currentNodeAction = node.Action;
                this.ScriptName = $"{this._script.Id} v{this._script.Version}";
                this.ScriptPosition = GetCurrentParagraphLabel() is { } lbl
                    ? $"@{lbl}/{node.Name}"
                    : $"linear/{node.Name}";

                // Track line number in readable script
                this.CurrentLineNumber = 0;
                if (this.ScriptLines.Length > 0 && !string.IsNullOrEmpty(node.Name))
                {
                    // Search by Chinese or English node name in trailing comment
                    var searchCn = MuScriptCompiler.TranslateNodeName(node.Name);
                    for (var i = 0; i < this.ScriptLines.Length; i++)
                    {
                        if (this.ScriptLines[i].Contains($"# {searchCn}", StringComparison.OrdinalIgnoreCase)
                            || this.ScriptLines[i].Contains($"# {node.Name}", StringComparison.OrdinalIgnoreCase))
                        {
                            this.CurrentLineNumber = i + 1;
                            break;
                        }
                    }
                }

                // Handle goto @label (control flow — not a game action)
                if (node.Action.StartsWith("goto @", StringComparison.OrdinalIgnoreCase))
                {
                    if (EvaluateCondition(node.Condition, nodeParams))
                    {
                        RecordBranchHit(node.Name, "if");
                        var label = node.Action.AsSpan(6).Trim().ToString();
                        this._logger.LogInformation("[ScriptExec]" + this._charTag + " Goto @{Label} from PC={Pc} node={Node}",
                            label, this._programCounter, node.Name);
                        ProcessGotoJump(label);
                    }
                    else
                    {
                        // Condition false — skip to next node
                        this._logger.LogTrace("[ScriptExec]" + this._charTag + " Goto skip: PC={Pc} node={Node} cond=false",
                            this._programCounter, node.Name);
                        this._programCounter++;
                    }
                }
                else
                {
                    // Normal action: execute via existing pipeline
                    this._logger.LogTrace("[ScriptExec]" + this._charTag + " tick={Tick} PC={Pc} node={Node} action={Action}",
                        this._scriptTickCounter, this._programCounter, node.Name, node.Action);

                    this.CurrentBehavior = node.Name;
                    var execResult = await TryExecuteBranchAsync(node, nodeParams).ConfigureAwait(false);
                    this._lastActionResult = execResult;
                    if (execResult.Status == NodeStatusCode.Completed || execResult.Status == NodeStatusCode.InProgress)
                    {
                        this._nodesExecuted++;
                    }
                    else
                    {
                        this._actionsFailed++;
                    }

                    // Advance PC (fetch-next, AR-20 §2)
                    // Only advance when the action is NOT InProgress — multi-tick
                    // actions (e.g. walk_to_quest_npc) stay on the same node until
                    // they complete. This prevents the PC from racing past walking
                    // and hitting fallback before the NPC dialog opens.
                    if (execResult.Status == NodeStatusCode.InProgress)
                    {
                        // Multi-tick action still in progress — retry same node next tick.
                        // Reset watchdog: continuous multi-tick progress isn't a stall.
                        this._ticksAtCurrentPc = 0;
                    }
                    else
                    {
                        this._programCounter++;
                    }
                }
            }
        }

        // Phase 4: Watchdog — detect PC stall (AR-20 §7)
        {
            this._ticksAtCurrentPc++;
            if (this._ticksAtCurrentPc > 50)
            {
                this._executionState = ScriptTaskState.Stuck;
                this._logger.LogWarning(
                    "[ScriptExec]" + this._charTag + " Watchdog: PC={Pc} stuck for {N} ticks — state=Stuck",
                    this._programCounter, this._ticksAtCurrentPc);
            }
        }

    BuildResult:
        if (this._executionState == ScriptTaskState.Idle)
        {
            this._executionState = ScriptTaskState.Running;
        }
        }
        finally
        {
            this._lastTickDurationUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
        }

        return CreateTickResult(this._executionState);
    }

    /// <summary>
    /// Executes one tick in DAG + paragraph mode: runs ALL nodes in the current paragraph
    /// (no short-circuit — DAG semantic). Each node is evaluated independently.
    /// After all nodes are processed, the pending goto (if any) is applied.
    /// </summary>
    /// <summary>
    /// <summary>
    /// Executes one tick in paragraph mode: runs the current paragraph's priority chain.
    /// If a node has a <c>goto @label</c> action, the goto is queued and applied
    /// at the end of this paragraph's chain execution.
    /// If no goto is set, the current paragraph repeats (loops) next tick.
    /// </summary>
    private async ValueTask<bool> ExecuteParagraphTickAsync()
    {
        var paragraphs = this._script.Paragraphs!;
        var currentParagraph = paragraphs[this._currentParagraphIndex];

        // Diagnostic: log current paragraph every 25 ticks
        if (this._scriptTickCounter % 25 == 0)
        {
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " D: paragraph=@{Label} nodes=[{Nodes}] pos=({X},{Y}) map={Map}",
                currentParagraph.Label,
                string.Join(",", currentParagraph.Nodes.Select(n => n.Name)),
                this._context.GameAdapter.GetPlayerPosition().X,
                this._context.GameAdapter.GetPlayerPosition().Y,
                this._context.GameAdapter.GetCurrentMap()?.Definition.Number);
        }

        foreach (var node in currentParagraph.Nodes)
        {
            // Resolve parameters: global defaults → per-node overrides
            var parameters = this._script.Parameters;
            if (node.Parameters is not null)
            {
                parameters = MergeParameters(this._script.Parameters, node.Parameters);
            }

            // Check for goto action on main node
            if (node.Action.StartsWith("goto @", StringComparison.OrdinalIgnoreCase))
            {
                // Evaluate the condition first
                var condResult = EvaluateCondition(node.Condition, parameters);
                if (condResult)
                {
                    RecordBranchHit(node.Name, "if");
                    var label = node.Action.AsSpan(6).Trim().ToString();
                    this._logger.LogInformation("[ScriptExec]" + this._charTag + " goto @{Label} from @{CurrentLabel} node={Node} cond={Cond}=true",
                        label, currentParagraph.Label, node.Name, node.Condition);
                    this._gotoPending = label;
                    this._nodesExecuted++;
                    goto ProcessGoto;
                }
                else
                {
                    this._logger.LogInformation("[ScriptExec]" + this._charTag + " goto @{Label} from @{CurrentLabel} node={Node} cond={Cond}=false",
                        node.Action.AsSpan(6).Trim().ToString(), currentParagraph.Label, node.Name, node.Condition);
                }

                // Try elif branches for goto
                if (node.ElifNodes is { Count: > 0 })
                {
                    var matched = false;
                    for (var i = 0; i < node.ElifNodes.Count; i++)
                    {
                        var elif = node.ElifNodes[i];
                        var elifParams = elif.Parameters is not null
                            ? MergeParameters(this._script.Parameters, elif.Parameters)
                            : this._script.Parameters;
                        if (EvaluateCondition(elif.Condition, elifParams) && elif.Action.StartsWith("goto @", StringComparison.OrdinalIgnoreCase))
                        {
                            RecordBranchHit(node.Name, $"elif.{i}");
                            var label = elif.Action.AsSpan(6).Trim().ToString();
                            this._gotoPending = label;
                            this._logger.LogTrace("[ScriptExec]" + this._charTag + " Paragraph goto: @{Label}", label);
                            this._nodesExecuted++;
                            matched = true;
                            goto ProcessGoto;
                        }
                    }

                    if (matched)
                    {
                        goto ProcessGoto;
                    }
                }

                // Try else branch for goto
                if (node.ElseNode?.Action.StartsWith("goto @", StringComparison.OrdinalIgnoreCase) == true)
                {
                    RecordBranchHit(node.Name, "else");
                    var label = node.ElseNode.Action.AsSpan(6).Trim().ToString();
                    this._gotoPending = label;
                    this._logger.LogTrace("[ScriptExec]" + this._charTag + " Paragraph goto: @{Label}", label);
                    this._nodesExecuted++;
                    goto ProcessGoto;
                }

                continue;
            }

            // Use merged parameters (with node-level overrides). Paragraph nodes aren't
            // pre-resolved via ResolveNodeParameters, so _nodeParams lookup in
            // TryExecuteBranchAsync returns null and falls back to globalParams.
            var pResult = await TryExecuteBranchAsync(node, parameters).ConfigureAwait(false);
            if (pResult.Status != NodeStatusCode.Skipped)
            {
                this._nodesExecuted++;
                goto ProcessGoto;
            }

            this._actionsFailed++;
        }

    ProcessGoto:
        // At end of paragraph chain: process pending goto if set
        if (this._gotoPending is not null)
        {
            if (this._paragraphTable.TryGetValue(this._gotoPending, out var targetIndex))
            {
                this._currentParagraphIndex = targetIndex;
                this._logger.LogInformation("[ScriptExec]" + this._charTag + " >>> Paragraph switch: @{Label} (index {Index}) gotoPending={Goto}",
                    this._gotoPending, targetIndex, this._gotoPending);
            }
            else
            {
                this._logger.LogWarning("[ScriptExec]" + this._charTag + " Goto target '@{Label}' not found in paragraph table",
                    this._gotoPending);
            }

            this._gotoPending = null;
        }

        return true;
    }

    /// <summary>
    /// Tries to execute a node's IF/ELIF/ELSE branches.
    /// Returns a <see cref="NodeExecutionResult"/> with full status (AR-21).
    /// </summary>
    private async ValueTask<NodeExecutionResult> TryExecuteBranchAsync(PriorityNode node, ScriptParameters globalParams)
    {
        var parameters = this._nodeParams.GetValueOrDefault(node.Name)
                         ?? globalParams;

        // 1) Evaluate the main IF condition
        if (EvaluateCondition(node.Condition, parameters))
        {
            RecordBranchHit(node.Name, "if");
            var result = await ExecuteActionWithFallthroughAsync(node.Action, node.Name, parameters).ConfigureAwait(false);
            return result with { ConditionMet = true };
        }

        // 2) If main condition false, try ELIF branches in order
        if (node.ElifNodes is { Count: > 0 } elifNodes)
        {
            for (var i = 0; i < elifNodes.Count; i++)
            {
                var elif = elifNodes[i];
                var elifParams = this._nodeParams.GetValueOrDefault(elif.Name)
                                 ?? MergeParameters(globalParams, elif.Parameters);

                if (EvaluateCondition(elif.Condition, elifParams))
                {
                    RecordBranchHit(node.Name, $"elif.{i}");
                    var result = await ExecuteActionWithFallthroughAsync(elif.Action, elif.Name, elifParams).ConfigureAwait(false);
                    return result with { ConditionMet = true };
                }
            }
        }

        // 3) If no IF or ELIF matched, try ELSE branch
        if (node.ElseNode is not null)
        {
            var elseParams = this._nodeParams.GetValueOrDefault(node.ElseNode.Name)
                             ?? MergeParameters(globalParams, node.ElseNode.Parameters);
            RecordBranchHit(node.Name, "else");
            var result = await ExecuteActionWithFallthroughAsync(node.ElseNode.Action, node.ElseNode.Name, elseParams).ConfigureAwait(false);
            return result with { ConditionMet = true };
        }

        // 4) No branches matched and no else — fall through (original behavior)
        return new NodeExecutionResult(
            NodeStatusCode.Skipped, node.Name, node.Action, false, false, true, false, null, null,
            "no branch matched");
    }

    /// <summary>
    /// Recursively resolves node parameters for a node and its elif/else children.
    /// </summary>
    private void ResolveNodeParameters(PriorityNode node, ScriptParameters globalParams)
    {
        this._nodeParams[node.Name] = MergeParameters(globalParams, node.Parameters);

        if (node.ElifNodes is { Count: > 0 } elifNodes)
        {
            foreach (var elif in elifNodes)
            {
                ResolveNodeParameters(elif, globalParams);
            }
        }

        if (node.ElseNode is not null)
        {
            ResolveNodeParameters(node.ElseNode, globalParams);
        }
    }

    /// <summary>
    /// Records a branch hit for execution statistics.
    /// Key format: "nodeName.if", "nodeName.elif.0", "nodeName.elif.1", "nodeName.else"
    /// Also syncs to the script's BranchHitCounts for JSON serialization.
    /// </summary>
    private void RecordBranchHit(string nodeName, string branchSuffix)
    {
        var key = $"{nodeName}.{branchSuffix}";
        this._branchHitCounts.TryGetValue(key, out var current);
        this._branchHitCounts[key] = current + 1;
        this._script.BranchHitCounts[key] = current + 1;
    }

    /// <summary>
    /// Executes an action and returns a <see cref="NodeExecutionResult"/> with full status.
    /// Rich result type (AR-21): callers use Status for deterministic branching decisions.
    /// </summary>
    private async ValueTask<NodeExecutionResult> ExecuteActionWithFallthroughAsync(string action, string nodeName, ScriptParameters p)
    {
        // Track script name from the active script (id + version)
        this.ScriptName = $"{this._script.Id} v{this._script.Version}";

        this._logger.LogTrace("[ScriptExec]" + this._charTag + " tick={Tick} node={Node} action={Action}",
            this._scriptTickCounter, nodeName, action);

        // Extract the action name without parameters for the switch dispatch.
        // Actions can have the form "actionName param1 param2" — split on first space.
        var actionName = action;
        var spaceIdx = action.IndexOf(' ');
        if (spaceIdx > 0)
        {
            actionName = action[..spaceIdx];
        }

        switch (actionName)
        {
            case "find_nearest_monster":
            case "find_target":
            {
                var found = FindNearestMonster(p);
                return new NodeExecutionResult(
                    found ? NodeStatusCode.Completed : NodeStatusCode.NoTarget,
                    nodeName, action, true, false, true, false, null, null,
                    found ? "monster found" : "no monster in range");
            }
            case "has_target_in_range":
            case "has_target_not_in_range":
                // Condition checks, not real actions — fall through (no effect)
                return new NodeExecutionResult(
                    NodeStatusCode.Skipped, nodeName, action, true, false, true, false, null, null,
                    "condition check only");
            case "pickup_nearby":
            {
                this.CurrentBehavior = "拾取中";
                if (this._pickupCooldownTicksLeft > 0)
                {
                    return new NodeExecutionResult(
                        NodeStatusCode.Skipped, nodeName, action, true, false, true, false, null, null,
                        "pickup on cooldown");
                }

                var picked = await PickupNearbyItemsAsync(p).ConfigureAwait(false);
                if (picked)
                {
                    this._pickupCooldownTicksLeft = p.PickupCooldownTicks;
                    return new NodeExecutionResult(
                        NodeStatusCode.Completed, nodeName, action, true, false, true, false, null, null,
                        "items picked up");
                }

                return new NodeExecutionResult(
                    NodeStatusCode.NoTarget, nodeName, action, true, false, true, false, null, null,
                    "no items to pick up");
            }
            case "return_and_sell":
            {
                var result = await ReturnAndSellAsync(p).ConfigureAwait(false);
                return new NodeExecutionResult(
                    result ? NodeStatusCode.Completed : NodeStatusCode.Skipped,
                    nodeName, action, true, false, true, false, null, null,
                    result ? "merchant reached" : "no merchant reachable");
            }
            case "walk_to_quest_npc":
            {
                var result = await WalkToQuestNpcWithFallthroughAsync(p).ConfigureAwait(false);
                var status = result switch
                {
                    WalkToNpcResult.Walking => NodeStatusCode.InProgress,
                    WalkToNpcResult.Fallthrough => NodeStatusCode.AlreadyDone,
                    WalkToNpcResult.Failed => NodeStatusCode.NoTarget,
                    _ => NodeStatusCode.Skipped,
                };
                var reason = result switch
                {
                    WalkToNpcResult.Walking => "walking to quest npc",
                    WalkToNpcResult.Fallthrough => "already at npc — fallthrough",
                    WalkToNpcResult.Failed => "cannot reach quest npc — retry",
                    _ => "unknown",
                };
                // 更新行为状态供看板/AIODS 读取
                this.CurrentBehavior = result switch
                {
                    WalkToNpcResult.Walking => "走向任务NPC",
                    WalkToNpcResult.Fallthrough => "任务NPC对话就绪",
                    WalkToNpcResult.Failed => "无法到达任务NPC",
                    _ => "任务NPC交互",
                };
                return new NodeExecutionResult(
                    status, nodeName, action, true,
                    result == WalkToNpcResult.Fallthrough,  // PcAdvanced: advance only on fallthrough
                    result == WalkToNpcResult.Walking,       // HasMoreNodes: more nodes while walking
                    false, null, null, reason);
            }
            case "warp_to_map":
                await ExecuteActionInternalAsync(action, nodeName, p).ConfigureAwait(false);
                return new NodeExecutionResult(
                    NodeStatusCode.Completed, nodeName, action, true, false, true, false, null, null,
                    "warp executed");
            case "stop":
                this._executionState = ScriptTaskState.Completed;
                return new NodeExecutionResult(
                    NodeStatusCode.Completed, nodeName, action, true, true, false, false, null,
                    ScriptTaskState.Completed, "script completed via stop");
            default:
                await ExecuteActionInternalAsync(action, nodeName, p).ConfigureAwait(false);
                return new NodeExecutionResult(
                    NodeStatusCode.Completed, nodeName, action, true, false, true, false, null, null,
                    "action executed");
        }
    }

    /// <summary>
    /// Internal action dispatcher — all actions that always "count" go here.
    /// </summary>
    private async ValueTask ExecuteActionInternalAsync(string action, string nodeName, ScriptParameters p)
    {
        // Extract base action name (without parameters)
        var actionName = action;
        var spaceIdx = action.IndexOf(' ');
        if (spaceIdx > 0)
        {
            actionName = action[..spaceIdx];
        }

        // "stop" action: mark script as completed and return immediately
        if (actionName == "stop")
        {
            this._executionState = ScriptTaskState.Completed;
            return;
        }

        // Update current behavior display name
        this.CurrentBehavior = actionName switch
        {
            "attack_target" => "战斗中",
            "approach_target" => "追击中",
            "find_nearest_monster" => "索敌中",
            "random_patrol" => "巡逻中",
            "relocate_to_hotspot" => "换点中",
            "return_to_death_spot" => "返回死亡点",
            "return_and_sell" => "回城补给",
            "wait_respawn" => "等待重生",
            "pickup_nearby" => "拾取中",
            "use_hp_potion" or "use_mp_potion" => "喝药中",
            "use_buff" => "加Buff",
            "leave_safezone" => "脱离安全区",
            "walk_to_target" => "走向目标",
            "walk_route" => "沿路线行走",
            "use_skill" or "use_heal_skill" => "施放技能",
            "travel_to_map" or "warp_to_map" => "传送中",
            "walk_to_quest_npc" => "走向任务NPC",
            "start_quest" => "接任务中",
            "accept_quest" => "接任务中",
            "submit_quest" or "complete_quest" => "交任务中",
            "close_npc_dialog" => "关闭NPC对话",
            _ => nodeName, // fallback to raw node name
        };

        switch (actionName)
        {
            case "use_hp_potion":
                await UseHpPotionAsync(p).ConfigureAwait(false);
                break;
            case "use_mp_potion":
                await UseMpPotionAsync(p).ConfigureAwait(false);
                break;
            case "sit_regen":
                await SitAndRegenAsync(p).ConfigureAwait(false);
                break;
            case "wait_respawn":
                // v2.0: death recovery — auto-revive after 5s timeout
                var hp = this._context.GameAdapter.GetCurrentHp();
                if (hp <= 0)
                {
                    // First tick of death — record death position and start timer
                    if (this._deathStartTime == DateTime.MinValue)
                    {
                        this._deathPosition = this._context.GameAdapter.GetPlayerPosition();
                        this._context.CurrentTarget = null;
                        this._justRevived = false;
                        this._deathStartTime = DateTime.UtcNow;

                        // General death tracking: count consecutive deaths since last kill
                        this._consecutiveDeaths++;

                        // Force-through death tracking
                        if (this._forceThruActive)
                        {
                            this._forceThruDeathCount++;
                            this._forceThruActive = false;
                            this._logger!.LogWarning(
                                "[ForceThru] Died during force-through ({Count}/3) — will give up after 3 deaths",
                                this._forceThruDeathCount);
                        }
                    }

                    // Death > 5 seconds — auto-revive via warp to safezone
                    if ((DateTime.UtcNow - this._deathStartTime).TotalSeconds >= 5)
                    {
                        this._logger.LogWarning(
                            "[ScriptExec]" + this._charTag + " 💀 Death exceeded 5s — auto-reviving...");
                        await this._player.WarpToSafezoneAsync().ConfigureAwait(false);
                        this._logger.LogInformation(
                            "[ScriptExec]" + this._charTag + " ✅ WarpToSafezoneAsync complete");

                        // Restore HP/MP/AG/SD to full (same pattern as HeartbeatService.RespawnPlayerAsync)
                        foreach (var regen in Stats.IntervalRegenerationAttributes)
                        {
                            this._player.Attributes![regen.CurrentAttribute] =
                                this._player.Attributes[regen.MaximumAttribute];
                        }
                        this._player.IsAlive = true;

                        // If CurrentMap is null after warp, confirm map change
                        if (this._player.CurrentMap is null)
                        {
                            await this._player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
                            this._logger.LogInformation(
                                "[ScriptExec]" + this._charTag + " ✅ ClientReadyAfterMapChangeAsync complete (auto-revive)");
                        }

                        this._justRevived = true;
                        this._deathStartTime = DateTime.MinValue;
                        this._deathPosition = default;
                        this._logger.LogWarning(
                            "[ScriptExec]" + this._charTag + " 💀 Auto-revive complete — HP/MP restored");
                    }
                }
                else
                {
                    // HP > 0 — alive
                    if (this._deathStartTime != DateTime.MinValue)
                    {
                        // Was dead, now alive — game engine revived us
                        this._justRevived = true;
                        this._deathStartTime = DateTime.MinValue;
                        this._logger.LogInformation(
                            "[ScriptExec]" + this._charTag + " Just revived — consecutive deaths: {Count}" +
                            (_consecutiveDeaths >= 5 ? " ⚠️ ESCALATING — considering map change" :
                             _consecutiveDeaths >= 3 ? " ⚠️ Will cycle hotspot" :
                             _consecutiveDeaths >= 1 ? " — normal retry" : ""),
                            this._consecutiveDeaths);
                    }
                    else if (this._deathPosition.X != 0 || this._deathPosition.Y != 0)
                    {
                        // HP recovered without death tracking (reset death position)
                        this._deathPosition = default;
                    }
                }
                break;
            case "attack_target":
                await AttackTargetAsync(p).ConfigureAwait(false);
                break;
            case "walk_to_target":
                await WalkToTargetAsync(p).ConfigureAwait(false);
                break;
            case "random_patrol":
                await RandomPatrolAsync(p).ConfigureAwait(false);
                break;
            case "approach_target":
                await ApproachTargetAsync(p).ConfigureAwait(false);
                break;
            case "pickup_nearby":
                await PickupNearbyItemsAsync(p).ConfigureAwait(false);
                break;
            case "variable_op":
                ExecuteVariableOp(p);
                break;
            case "use_buff":
                await UseBuffAsync(p).ConfigureAwait(false);
                break;
            case "use_heal_skill":
                await UseHealSkillAsync(p).ConfigureAwait(false);
                break;
            case "use_skill":
                await UseSkillAsync(p).ConfigureAwait(false);
                break;
            case "drop_item":
                await DropItemAsync(p).ConfigureAwait(false);
                break;
            case "walk_route":
                await ScriptWalkRouteAsync(p).ConfigureAwait(false);
                break;
            case "leave_safezone":
                await ScriptLeaveSafezoneAsync(p).ConfigureAwait(false);
                break;
            case "discover_trail":
                await ScriptDiscoverTrailAsync(p).ConfigureAwait(false);
                break;
            case "start_quest":
                // 检查该任务是否已在系统中激活 → 跳过（防止 ClearAsync 删除 KillCount）
                if (p.QuestGroup is not null && p.QuestNumber is not null)
                {
                    var activeQuests = this._context.GameAdapter.GetActiveQuests();
                    if (activeQuests.Any(q => q.Group == p.QuestGroup.Value && q.Number == p.QuestNumber.Value))
                    {
                        this._logger.LogInformation("[ScriptExec]" + this._charTag + " start_quest: G{Group}/N{Number} already active, skipping",
                            p.QuestGroup.Value, p.QuestNumber.Value);
                        break;
                    }
                }
                await StartQuestAsync(p).ConfigureAwait(false);
                break;
            case "accept_quest":
                // 检查该任务是否已在系统中激活 → 跳过（防止 repeatable 任务被重复接受导致 KillCount 清零）
                if (p.QuestGroup is not null && p.QuestNumber is not null)
                {
                    var activeQuests = this._context.GameAdapter.GetActiveQuests();
                    if (activeQuests.Any(q => q.Group == p.QuestGroup.Value && q.Number == p.QuestNumber.Value))
                    {
                        this._logger.LogInformation("[ScriptExec]" + this._charTag + " accept_quest: G{Group}/N{Number} already active, skipping accept",
                            p.QuestGroup.Value, p.QuestNumber.Value);
                        break;
                    }
                }

                await AcceptQuestAsync(p).ConfigureAwait(false);
                break;
            case "submit_quest":
            case "complete_quest":
                if (action == "submit_quest")
                {
                    await SubmitQuestAsync(p).ConfigureAwait(false);
                }
                else
                {
                    await CompleteQuestAsync(p).ConfigureAwait(false);
                }
                break;
            case "follow_leader":
                await FollowLeaderAsync(p).ConfigureAwait(false);
                break;
            case "travel_to_map":
                await TravelToMapAsync(p).ConfigureAwait(false);
                break;
            case "warp_to_map":
                // 已在目标地图 → 跳过，让段落继续执行后续节点
                if (p.TargetMapNumber is not null)
                {
                    var currentMap = this._context.GameAdapter.GetCurrentMap();
                    if (currentMap?.Definition.Number == p.TargetMapNumber.Value)
                    {
                        this._logger.LogDebug("[ScriptExec]" + this._charTag + " warp_to_map: already on map {Map}", p.TargetMapNumber);
                        break;
                    }
                }

                await WarpToMapAsync(p).ConfigureAwait(false);
                break;
            // v2.0 actions
            case "relocate_to_hotspot":
                this._lastActionResult = await RelocateToHotspotAsync(p).ConfigureAwait(false);
                break;
            case "warp_to_hunt_map":
                await WarpToHuntMapAsync(p).ConfigureAwait(false);
                break;
            case "return_to_death_spot":
                await ReturnToDeathSpotAsync(p).ConfigureAwait(false);
                break;
            case "close_npc_dialog":
                this._logger.LogDebug("[ScriptExec]" + this._charTag + " close_npc_dialog");
                await this._closeNpcDialog.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
                break;
            default:
                this._logger.LogWarning("[ScriptExec]" + this._charTag + " Unknown action: {Action}", action);
                break;
        }
    }

    // ===== Condition Evaluation =====

    private bool EvaluateCondition(string condition, ScriptParameters p)
    {
        // "temp" nodes execute exactly once per DAG paragraph cycle.
        // When a temp node is active, the PC does not auto-advance past it if
        // the underlying action is handled by AIStateMachine (which runs async).
        // After the state machine completes, the node action is skipped (Completed status).
        // In linear (PC) mode, temp acts like "always" — the action runs and PC advances.
        return condition switch
        {
            "temp" => true,
            "always" => true,
            "hp_below_threshold" => EvaluateHpWithHysteresis(p),
            "mp_below_threshold" => EvaluateMpWithHysteresis(p),
            "is_dead" => this._context.GameAdapter.GetCurrentHp() <= 0,
            "inventory_full" => IsInventoryFull(),
            "equip_durable_low" => IsEquipDurableLow(p.DurabilityThreshold),
            "has_target_in_range" => HasTargetInRange(p.AttackRange),
            "has_target_not_in_range" => HasTargetNotInRange(p.AttackRange),
            "no_target" => this._context.CurrentTarget is null,
            "in_safe_zone" => this._context.WorldState.IsAtSafezone && !this._forceLeaveSafezone,
            "variable" => EvaluateVariableCondition(p),
            "potion_count_below_threshold" => CountPotions() < p.PotionThreshold,
            "is_in_party" => this._player.Party is not null
                              && this._player.Party.PartyList.Count > 0,
            "is_on_target_map" => this._context.TargetMapNumber is null
                                  || (this._context.GameAdapter.GetCurrentMap()?.Definition.Number
                                      == this._context.TargetMapNumber),
            // v2.0 conditions
            "no_monsters_recently" => this._noMonsterTickCount > p.NoMonsterTicksLimit,
            "just_revived" => this._justRevived,
            // quest conditions
            "has_active_quest" => EvaluateHasActiveQuest(p),
            "quest_conditions_met" => EvaluateQuestConditionsMet(p),
            "no_active_quest" => EvaluateNoActiveQuest(p),
            "can_accept_quest" => EvaluateCanAcceptQuest(p),
            "not_at_hotspot" => !IsNearAnyHotspot(p.PatrolRadius),
            "not_on_hunt_map" => EvaluateNotOnHuntMap(p),
            "has_target" => this._context.CurrentTarget is not null,
            "quest_completable" => EvaluateQuestCompletable(p),
            _ => false,
        };
    }

    private bool EvaluateCanAcceptQuest(ScriptParameters p)
    {
        var targetGroup = p.QuestGroup;
        var targetNumber = p.QuestNumber;
        if (targetGroup is null || targetNumber is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " can_accept_quest: Skipped — no questGroup/questNumber parameters set");
            return false;
        }

        // 自动接取任务（QuestNpcNumber 为 null/0）：无需 NPC 对话，跳过 OpenedNpc 检查
        if (p.QuestNpcNumber is null || p.QuestNpcNumber.Value == 0)
        {
            // GetAvailableQuests() requires OpenedNpc != null, which is false for auto-accept.
            // Instead, directly check if the quest exists in the game configuration.
            var questDef = FindQuestDefinition(targetGroup.Value, targetNumber.Value);
            var autoHasTarget = questDef is not null;
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " can_accept_quest: Auto-accept quest (no NPC), target=G{Group}/N{Number}, found={Found}, questDef={Def}",
                targetGroup.Value, targetNumber.Value, autoHasTarget, questDef?.Name);
            return autoHasTarget;
        }

        if (this._player.OpenedNpc is null)
        {
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " can_accept_quest: No opened NPC — returning false");
            return false;
        }

        var available = this._context.GameAdapter.GetAvailableQuests();
        var hasTarget = available.Any(q => q.Group == targetGroup.Value && q.Number == targetNumber.Value);
        var pos = this._player.Position;

        this._logger.LogInformation("[ScriptExec]" + this._charTag + " can_accept_quest: OpenedNpc={NpcId}, target=G{Group}/N{Number}, found={Found}, available={Count}, pos=({X},{Y})",
            this._player.OpenedNpc?.Definition?.Number, targetGroup.Value, targetNumber.Value, hasTarget, available.Count, pos.X, pos.Y);
        return hasTarget;
    }

    /// <summary>Searches all game configuration NPCs for a quest definition matching group/number.</summary>
    private QuestDefinition? FindQuestDefinition(short group, short number)
    {
        var config = this._player.GameContext?.Configuration;
        if (config is null) return null;

        foreach (var monster in config.Monsters)
        {
            if (monster.Quests is null) continue;
            var quest = monster.Quests.FirstOrDefault(q =>
                q.Group == group && q.Number == number
                && (q.QualifiedCharacter is null || Equals(q.QualifiedCharacter, this._player.SelectedCharacter?.CharacterClass)));
            if (quest is not null)
                return quest;
        }
        return null;
    }

    /// <summary>
    /// 检查任务是否可提交（用于 submit_quest 段落的 check_ready 条件）。
    /// 与 quest_conditions_met 的区别：本函数不检查 NPC 对话框是否打开，
    /// 只检查系统中是否有该任务的活跃记录且杀怪条件已满足。
    /// 这是脚本中 "can I call CompleteQuestAsync now?" 的判断。
    /// </summary>
    private bool EvaluateQuestCompletable(ScriptParameters p)
    {
        var targetGroup = p.QuestGroup;
        var targetNumber = p.QuestNumber;
        if (targetGroup is null || targetNumber is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " quest_completable: No questGroup/questNumber parameters set");
            return false;
        }

        var activeQuests = this._context.GameAdapter.GetActiveQuests();
        var myQuest = activeQuests.FirstOrDefault(q =>
            q.Group == targetGroup.Value && q.Number == targetNumber.Value);
        if (myQuest is null)
        {
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " quest_completable: G{Group}/N{Number} not active — returning false",
                targetGroup.Value, targetNumber.Value);
            return false;
        }

        // 杀怪要求全部满足？
        if (myQuest.RequiredKills is { Count: > 0 })
        {
            if (myQuest.RequiredKills.All(k => k.Current >= k.Required))
            {
                this._logger.LogInformation("[ScriptExec]" + this._charTag + " quest_completable: MET for G{Group}/N{Number}!",
                    myQuest.Group, myQuest.Number);
                return true;
            }
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " quest_completable: NOT MET for G{Group}/N{Number} — kills incomplete",
                myQuest.Group, myQuest.Number);
            return false;
        }

        // 无杀怪需求 → 可提交
        this._logger.LogInformation("[ScriptExec]" + this._charTag + " quest_completable: MET for G{Group}/N{Number} — no kill requirements",
            myQuest.Group, myQuest.Number);
        return true;
    }

    private bool IsNearAnyHotspot(float patrolRadius)
    {
        if (this._hotspotPoints.Count == 0)
        {
            this._logger.LogDebug("[IsNearAnyHotspot] no hotspot points loaded — returning false (not_at_hotspot=true)");
            return false;
        }

        var pos = this._context.GameAdapter.GetPlayerPosition();
        foreach (var h in this._hotspotPoints)
        {
            var dist = pos.EuclideanDistanceTo(h);
            this._logger.LogDebug("[IsNearAnyHotspot] check hotspot ({HX},{HY}) distance={Dist:F1} radius={Radius} result={Result}",
                h.X, h.Y, dist, patrolRadius, dist <= patrolRadius);
            if (dist <= patrolRadius)
            {
                return true;
            }
        }

        this._logger.LogDebug("[IsNearAnyHotspot] no hotspot in range — returning false (not_at_hotspot=true)");
        return false;
    }

    private bool EvaluateQuestConditionsMet(ScriptParameters p)
    {
        var targetGroup = p.QuestGroup;
        var targetNumber = p.QuestNumber;
        if (targetGroup is null || targetNumber is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " quest_conditions_met: Skipped — no questGroup/questNumber parameters set");
            return false;
        }

        var activeQuests = this._context.GameAdapter.GetActiveQuests();
        var myQuest = activeQuests.FirstOrDefault(q =>
            q.Group == targetGroup.Value && q.Number == targetNumber.Value);
        if (myQuest is null)
        {
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " quest_conditions_met: G{Group}/N{Number} not found among {Count} active quests — returning false",
                targetGroup.Value, targetNumber.Value, activeQuests.Count);
            return false;
        }

        // 杀怪检查
        if (myQuest.RequiredKills is { Count: > 0 })
        {
            foreach (var kill in myQuest.RequiredKills)
            {
                this._logger.LogInformation("[ScriptExec]" + this._charTag + " quest_conditions_met: G{Group}/N{Number}, monster={Monster}, kill={Current}/{Required}",
                    myQuest.Group, myQuest.Number, kill.MonsterName, kill.Current, kill.Required);
            }

            if (myQuest.RequiredKills.All(k => k.Current >= k.Required))
            {
                this._logger.LogInformation("[ScriptExec]" + this._charTag + " quest_conditions_met: MET for G{Group}/N{Number}!", myQuest.Group, myQuest.Number);
                return true;
            }

            this._logger.LogInformation("[ScriptExec]" + this._charTag + " quest_conditions_met: NOT MET for G{Group}/N{Number} — kills incomplete",
                myQuest.Group, myQuest.Number);
            return false;
        }

        // 无杀怪需求 → 提交条件满足
        this._logger.LogInformation("[ScriptExec]" + this._charTag + " quest_conditions_met: MET for G{Group}/N{Number} — no kill requirements",
            myQuest.Group, myQuest.Number);
        return true;
    }

    private bool EvaluateNoActiveQuest(ScriptParameters p)
    {
        var targetGroup = p.QuestGroup;
        var targetNumber = p.QuestNumber;
        if (targetGroup is null || targetNumber is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " no_active_quest: No questGroup/questNumber parameters — treating as no active quest for this script (returning true)");
            return true;
        }

        var activeQuests = this._context.GameAdapter.GetActiveQuests();
        if (activeQuests.Any(q => q.Group == targetGroup.Value && q.Number == targetNumber.Value))
        {
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " no_active_quest: G{Group}/N{Number} is active via GetActiveQuests — returning false",
                targetGroup.Value, targetNumber.Value);
            return false;
        }

        // Fallback: GetActiveQuests didn't find it, but check QuestStates directly
        // to handle EF Core proxy/navigation loading issues after DB load.
        var questStates = this._player.SelectedCharacter?.QuestStates;
        if (questStates is { Count: > 0 })
        {
            var hasActiveForTarget = questStates.Any(qs =>
                qs.Group == targetGroup.Value
                && qs.ActiveQuest is not null
                && qs.ActiveQuest.Number == targetNumber.Value);
            if (hasActiveForTarget)
            {
                this._logger.LogInformation("[ScriptExec]" + this._charTag + " no_active_quest: FALLBACK — GetActiveQuests filtered, but QuestStates has ActiveQuest for G{Group}/N{Number}, returning false",
                    targetGroup.Value, targetNumber.Value);
                return false;
            }
        }

        this._logger.LogInformation("[ScriptExec]" + this._charTag + " no_active_quest: G{Group}/N{Number} not active — returning true",
            targetGroup.Value, targetNumber.Value);
        return true;
    }

    private bool EvaluateHasActiveQuest(ScriptParameters p)
    {
        var targetGroup = p.QuestGroup;
        var targetNumber = p.QuestNumber;
        if (targetGroup is null || targetNumber is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " has_active_quest: Skipped — no questGroup/questNumber parameters set");
            return false;
        }

        var activeQuests = this._context.GameAdapter.GetActiveQuests();
        var result = activeQuests.Any(q => q.Group == targetGroup.Value && q.Number == targetNumber.Value);

        var pos = this._player.Position;
        var questStates = this._player.SelectedCharacter?.QuestStates;
        var qsCount = questStates?.Count ?? -1;
        var details = questStates is null ? "null" : string.Join(", ", questStates.Select(qs => $"G{qs.Group} AQ={(qs.ActiveQuest is null ? "null" : qs.ActiveQuest.Number.ToString())}"));

        // Fallback: check QuestStates directly when GetActiveQuests returns 0
        // but the character clearly has quest states with ActiveQuest set.
        // This handles EF Core proxy detachment issues after DB load.
        var hasFromQuestStates = false;
        if (!result && questStates is { Count: > 0 })
        {
            hasFromQuestStates = questStates.Any(qs =>
                qs.Group == targetGroup.Value
                && qs.ActiveQuest is not null
                && qs.ActiveQuest.Number == targetNumber.Value);
            if (hasFromQuestStates)
            {
                this._logger.LogInformation("[ScriptExec]" + this._charTag + " has_active_quest: FALLBACK — GetActiveQuests filtered, but QuestStates has ActiveQuest for G{Group}/N{Number}",
                    targetGroup.Value, targetNumber.Value);
            }
        }

        result = result || hasFromQuestStates;
        this._logger.LogInformation("[ScriptExec]" + this._charTag + " has_active_quest: G{Group}/N{Number} — count={Count}, qsCount={QsCount}, details=[{Details}], result={Result}, pos=({X},{Y})",
            targetGroup.Value, targetNumber.Value, activeQuests.Count, qsCount, details, result, pos.X, pos.Y);
        return result;
    }

    private bool EvaluateHpWithHysteresis(ScriptParameters p)
    {
        var ratio = GetHpRatio();
        var margin = p.PotionHysteresisMargin;
        var stopThreshold = p.HpThreshold * (1 + margin);

        if (this._hpHysteresisActive)
        {
            if (ratio > stopThreshold)
                this._hpHysteresisActive = false; // 退出滞回区
            return false;
        }

        return ratio < p.HpThreshold;
    }

    private bool EvaluateMpWithHysteresis(ScriptParameters p)
    {
        var ratio = GetMpRatio();
        var margin = p.PotionHysteresisMargin;
        var stopThreshold = p.MpThreshold * (1 + margin);

        if (this._mpHysteresisActive)
        {
            if (ratio > stopThreshold)
                this._mpHysteresisActive = false;
            return false;
        }

        return ratio < p.MpThreshold;
    }

    // ===== Action Implementations =====

    private async ValueTask UseHpPotionAsync(ScriptParameters p)
    {
        if ((DateTime.UtcNow - this._lastHpPotionTime).TotalMilliseconds < p.PotionCooldownMs)
        {
            return;
        }

        var inventory = this._player.Inventory;
        if (inventory is null)
        {
            return;
        }

        var deficit = this._context.GameAdapter.GetMaxHp()
                       - this._context.GameAdapter.GetCurrentHp();
        if (deficit <= 0)
        {
            return;
        }

        // Deficit-matched potion selection
        foreach (var (group, number, heal) in HpPotions)
        {
            if (deficit >= heal)
            {
                var potion = inventory.Items
                    .FirstOrDefault(i => i.Definition?.Group == group && i.Definition.Number == number);
                if (potion is not null)
                {
                    await this._context.GameAdapter.ConsumeItemAsync(potion.ItemSlot)
                        .ConfigureAwait(false);
                    this._lastHpPotionTime = DateTime.UtcNow;
                    this._logger.LogInformation("[ScriptExec]" + this._charTag + " Used HP potion: deficit={Deficit}, item={Item}",
                        deficit, potion.Definition?.Name);
                    this._hpHysteresisActive = true;
                    return;
                }
            }
        }

        // Fallback: use any available potion (smallest first)
        for (int i = HpPotions.Length - 1; i >= 0; i--)
        {
            var (group, number, _) = HpPotions[i];
            var potion = inventory.Items
                .FirstOrDefault(i => i.Definition?.Group == group && i.Definition.Number == number);
            if (potion is not null)
            {
                await this._context.GameAdapter.ConsumeItemAsync(potion.ItemSlot)
                    .ConfigureAwait(false);
                this._lastHpPotionTime = DateTime.UtcNow;
                this._hpHysteresisActive = true;
                return;
            }
        }
    }

    private async ValueTask UseMpPotionAsync(ScriptParameters p)
    {
        if ((DateTime.UtcNow - this._lastMpPotionTime).TotalMilliseconds < p.PotionCooldownMs)
        {
            return;
        }

        var inventory = this._player.Inventory;
        if (inventory is null)
        {
            return;
        }

        var deficit = this._context.GameAdapter.GetMaxMp()
                       - this._context.GameAdapter.GetCurrentMp();
        if (deficit <= 0)
        {
            return;
        }

        foreach (var (group, number, mana) in MpPotions)
        {
            if (deficit >= mana)
            {
                var potion = inventory.Items
                    .FirstOrDefault(i => i.Definition?.Group == group && i.Definition.Number == number);
                if (potion is not null)
                {
                    await this._context.GameAdapter.ConsumeItemAsync(potion.ItemSlot)
                        .ConfigureAwait(false);
                    this._lastMpPotionTime = DateTime.UtcNow;
                    this._mpHysteresisActive = true;
                    return;
                }
            }
        }

        // Fallback
        for (int i = MpPotions.Length - 1; i >= 0; i--)
        {
            var (group, number, _) = MpPotions[i];
            var potion = inventory.Items
                .FirstOrDefault(i => i.Definition?.Group == group && i.Definition.Number == number);
            if (potion is not null)
            {
                await this._context.GameAdapter.ConsumeItemAsync(potion.ItemSlot)
                    .ConfigureAwait(false);
                this._lastMpPotionTime = DateTime.UtcNow;
                this._mpHysteresisActive = true;
                return;
            }
        }
    }

    private bool FindNearestMonster(ScriptParameters p)
    {
        var attackables = this._context.WorldState.AttackablesInRange;
        if (attackables is null || attackables.Count == 0)
        {
            this._context.CurrentTarget = null;
            return false;
        }

        var myPos = this._context.WorldState.PlayerPosition;
        var myLevel = this._context.GameAdapter.GetPlayerLevel();

        // Purge expired blacklist entries (use configured expiry threshold)
        var expiryTicks = p.BlacklistExpiryTicks;
        var expired = this._targetBlacklist
            .Where(kvp => this._scriptTickCounter - kvp.Value > expiryTicks)
            .Select(kvp => kvp.Key).ToList();
        foreach (var id in expired)
        {
            this._targetBlacklist.Remove(id);
        }

        // Collect all valid targets with distances
        var candidates = new List<(Monster Monster, float Distance)>(attackables.Count);
        foreach (var a in attackables)
        {
            if (a is not Monster m || !m.IsAlive)
            {
                continue;
            }

            // Skip recently unreachable targets
            if (this._targetBlacklist.ContainsKey(m.Id))
            {
                continue;
            }

            // NPC 黑名单过滤
            if (p.NpcBlacklist is { Count: > 0 } && p.NpcBlacklist.Contains(m.Id))
            {
                continue;
            }

            var monsterLevel = (int)(m.Attributes?[Stats.Level] ?? 0);
            var levelDiff = monsterLevel - myLevel;
            if (levelDiff > p.MaxLevelDiff)
            {
                continue;
            }

            var dist = (float)myPos.EuclideanDistanceTo(a.Position);
            candidates.Add((m, dist));
        }

        if (candidates.Count == 0)
        {
            this._context.CurrentTarget = null;
            this._noMonsterTickCount++;
            return false;
        }

        // 任务目标优先: 当有活跃任务需要击杀特定怪物时，优先选择任务目标
        HashSet<short>? questTargetNumbers = null;
        try
        {
            var activeQuests = this._context.GameAdapter.GetActiveQuests();
            foreach (var q in activeQuests)
            {
                if (q.RequiredKills is { Count: > 0 })
                {
                    foreach (var k in q.RequiredKills)
                    {
                        if (k.Current < k.Required && k.MonsterNumber > 0)
                        {
                            this._logger.LogInformation("[FindNearestMonster] QuestTarget: monster #{Monster} ({Name}), kill={Current}/{Required}",
                                k.MonsterNumber, k.MonsterName, k.Current, k.Required);
                            (questTargetNumbers ??= new HashSet<short>()).Add(k.MonsterNumber);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this._logger.LogDebug("[FindNearestMonster] QuestTarget query failed: {Ex}", ex.Message);
        }

        if (questTargetNumbers is { Count: > 0 })
        {
            var questCandidates = candidates.Where(c => questTargetNumbers.Contains((short)c.Monster.Definition.Number)).ToList();
            if (questCandidates.Count > 0)
            {
                this._logger.LogDebug("[FindNearestMonster] Filtered to {Count} quest-target monsters from {Total} candidates",
                    questCandidates.Count, candidates.Count);
                candidates = questCandidates;
            }
            else
            {
                // 有任务需求但周围找不到任务目标怪物
                // 降级: 攻击最近的任意怪物（非任务怪也行），避免站着被白打
                this._logger.LogDebug("[FindNearestMonster] No quest target monsters #{Monsters} in range — falling back to non-quest targets ({Total} available)",
                    string.Join(",", questTargetNumbers), candidates.Count);
                // 不返回 NoTarget — 用任意怪物维持战斗状态
                // 如果没有任何怪物，才返回 NoTarget
                if (candidates.Count == 0)
                {
                    this._logger.LogDebug("[FindNearestMonster] No monsters in range at all — returning NoTarget");
                    this._context.CurrentTarget = null;
                    this._noMonsterTickCount++;
                    return false;
                }
                // 有非任务怪就用它们
                this._logger.LogDebug("[FindNearestMonster] Falling back to {Count} non-quest monsters", candidates.Count);
            }
        }

        // 防堆叠: 从最近 N 个候选中选择
        // 先按距离排序，取最近的 TargetRandomizationCount 个
        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var randomCount = Math.Max(1, p.TargetRandomizationCount);
        var topN = candidates.Take(randomCount).ToList();

        IAttackable? best;
        if (topN.Count == 1)
        {
            best = topN[0].Monster;
        }
        else if (this._player.Party is { PartyList.Count: > 0 } party)
        {
            // 组队模式: 选靠近队友平均位置的怪物
            // 计算队友平均位置（排除自己）
            float avgX = 0, avgY = 0;
            var partyMemberCount = 0;
            foreach (var member in party.PartyList)
            {
                if (member == this._player) continue;
                avgX += member.Position.X;
                avgY += member.Position.Y;
                partyMemberCount++;
            }

            if (partyMemberCount > 0)
            {
                avgX /= partyMemberCount;
                avgY /= partyMemberCount;
                var partyCenter = new Point((byte)avgX, (byte)avgY);

                // 加权评分: 60% 距离权重 + 40% 距队伍中心权重
                var bestScore = float.MaxValue;
                best = null;
                foreach (var (monster, dist) in topN)
                {
                    var distToParty = (float)partyCenter.EuclideanDistanceTo(monster.Position);
                    var score = dist * 0.6f + distToParty * 0.4f;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = monster;
                    }
                }
            }
            else
            {
                // 单人队（仅自己）— 随机选
                best = topN[Random.Shared.Next(topN.Count)].Monster;
            }
        }
        else
        {
            // 非组队模式: 从最近 N 个中随机选（防止所有 AI 锁定同一目标）
            best = topN[Random.Shared.Next(topN.Count)].Monster;
        }

        this._context.CurrentTarget = best;

        if (best is not null)
        {
            this.ResetIdleTimer(); // 找到目标 = 有进展
            this._logger.LogTrace("[ScriptExec]" + this._charTag + " Found target: {Id} at ({X},{Y}) dist={Dist:F1}",
                best, best.Position.X, best.Position.Y,
                (float)myPos.EuclideanDistanceTo(best.Position));
            return true;
        }

        // v2.0: no monster found this tick — increment counter for relocate_to_hotspot
        this._noMonsterTickCount++;
        return false;
    }

    /// <remarks>
    /// AttackByAsync internally handles dead targets, no guards needed here.
    /// </remarks>
    private async ValueTask AttackTargetAsync(ScriptParameters p)
    {
        var target = this._context.CurrentTarget;
        if (target is null) return;

        // Clear target if already dead so AI doesn't spin on a corpse.
        if (target is Monster m && !m.IsAlive)
        {
            this._context.CurrentTarget = null;
            return;
        }

        // Use NativeExecutionService for all attacks — this triggers proper damage pipeline
        // and quest kill count tracking, unlike the old GameAdapter/PacketInjector path.
        if (p.SkillPriority is { Count: > 0 })
        {
            var skillList = this._player.SkillList;
            if (skillList is not null)
            {
                foreach (var skillNumber in p.SkillPriority)
                {
                    var skillEntry = skillList.Skills
                        .FirstOrDefault(s => s.Skill?.Number == skillNumber);
                    if (skillEntry is null) continue;

                    await this._nativeExec.SkillAttackAsync(target, skillEntry).ConfigureAwait(false);
                    return;
                }
            }
        }

        // Melee fallback
        await this._nativeExec.MeleeAttackAsync(target).ConfigureAwait(false);
    }

    private async ValueTask WalkToTargetAsync(ScriptParameters p)
    {
        var target = this._context.CurrentTarget;
        if (target is null)
        {
            return;
        }

        // 目标已死亡 — 清除 target，容许巡逻
        if (target is Monster m && !m.IsAlive)
        {
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " WalkToTargetAsync: target {Id} is dead, clearing", target);
            this._context.CurrentTarget = null;
            return;
        }

        var map = this._context.WorldState.CurrentMap;
        if (map is null)
        {
            return;
        }

        if (await TryWalkToAsync(target.Position, map).ConfigureAwait(false))
        {
            return;
        }

        // Pathfinding failed — target is unreachable. Blacklist it so
        // find_target doesn't immediately re-select it, then clear.
        if (target is Monster monster2)
        {
            this._targetBlacklist[monster2.Id] = this._scriptTickCounter;
        }

        this._logger.LogTrace("[ScriptExec]" + this._charTag + " Walk to target ({X},{Y}) failed — blacklisting and clearing target", target.Position.X, target.Position.Y);
        this._context.CurrentTarget = null;
    }

    private async ValueTask ApproachTargetAsync(ScriptParameters p)
    {
        var target = this._context.CurrentTarget;
        if (target is null)
        {
            return;
        }

        // 目标已死亡 — 清除 target，容许巡逻
        if (target is Monster m && !m.IsAlive)
        {
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " ApproachTargetAsync: target {Id} is dead, clearing", target);
            this._context.CurrentTarget = null;
            return;
        }

        var dist = this._context.WorldState.PlayerPosition.EuclideanDistanceTo(target.Position);
        if (dist <= p.AttackRange)
        {
            // Already in range — attack instead
            await AttackTargetAsync(p).ConfigureAwait(false);
            return;
        }

        var map = this._context.WorldState.CurrentMap;
        if (map is null)
        {
            return;
        }

        // Walk toward target, but stop 1 tile before attack range
        this._logger.LogDebug("[ScriptExec]" + this._charTag + " ApproachTarget: TryWalkToAsync to target at ({X},{Y})", target.Position.X, target.Position.Y);
        var walkResult = await TryWalkToAsync(target.Position, map).ConfigureAwait(false);
        if (walkResult)
        {
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " ApproachTarget: TryWalkToAsync succeeded to ({X},{Y})", target.Position.X, target.Position.Y);
            this.ResetIdleTimer(); // 走路成功 = 有进展
            return;
        }

        this._logger.LogDebug("[ScriptExec]" + this._charTag + " ApproachTarget: TryWalkToAsync failed to ({X},{Y})", target.Position.X, target.Position.Y);

        // Pathfinding failed — target is unreachable. Blacklist it so
        // find_target doesn't immediately re-select it, then clear.
        if (target is Monster monster3)
        {
            this._targetBlacklist[monster3.Id] = this._scriptTickCounter;
        }

        this._logger.LogTrace("[ScriptExec]" + this._charTag + " Approach to target ({X},{Y}) failed — blacklisting and clearing target", target.Position.X, target.Position.Y);
        this._context.CurrentTarget = null;
    }

    private async ValueTask RandomPatrolAsync(ScriptParameters p)
    {
        var map = this._context.WorldState.CurrentMap;
        if (map is null)
        {
            return;
        }

        // 已在行走中时，不做随机巡逻（避免覆盖 relocate_to_hotspot 的寻路）
        if (this._player.IsWalking)
        {
            return;
        }

        // 如果在热点附近，不做巡逻（等待怪物刷出）
        if (IsNearAnyHotspot(p.PatrolRadius))
        {
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " At hotspot — skipping patrol to wait for monster spawns");
            return;
        }

        // Periodically shift patrol center
        this._patrolTickCounter++;
        if (this._patrolTickCounter > p.PatrolCenterResetTicks)
        {
            this._patrolTickCounter = 0;
            this._patrolCenter = this._context.GameAdapter.GetPlayerPosition();
        }

        this._logger.LogDebug("[ScriptExec]" + this._charTag + " RandomPatrolAsync: starting patrol (center={CX},{CY}, radius={R})", this._patrolCenter.X, this._patrolCenter.Y, p.PatrolRadius);

        const int maxAttempts = 10;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var offsetX = Random.Shared.Next(-p.PatrolRadius, p.PatrolRadius + 1);
            var offsetY = Random.Shared.Next(-p.PatrolRadius, p.PatrolRadius + 1);

            var tx = Math.Clamp(this._patrolCenter.X + offsetX, 0, 255);
            var ty = Math.Clamp(this._patrolCenter.Y + offsetY, 0, 255);

            if (!map.Terrain.WalkMap[tx, ty])
            {
                continue;
            }

            var target = new Point((byte)tx, (byte)ty);
            if (await TryWalkToAsync(target, map).ConfigureAwait(false))
            {
                this._logger.LogDebug("[ScriptExec]" + this._charTag + " RandomPatrolAsync: walked to ({TX},{TY})", target.X, target.Y);
                return;
            }
        }

        this._logger.LogDebug("[ScriptExec]" + this._charTag + " RandomPatrolAsync: all {N} attempts failed, using short-range fallback", maxAttempts);

        // Short-range fallback (bypass A*, walk directly to nearby walkable tile)
        await ShortRangeFallbackAsync(map).ConfigureAwait(false);
    }

    private async ValueTask<bool> TryWalkToAsync(Point target, GameMap map)
    {
        // Diagnostic: check AIgrid values at start and end
        var terrain = map.Terrain.AIgrid;
        var pos = this._context.GameAdapter.GetPlayerPosition();
        var sv = terrain[pos.X, pos.Y];
        var ev = terrain[target.X, target.Y];
        var originalGrid = map.Terrain.AIgrid; // unmodified original for force-through fallback

        // Reset force-through flag — only set to true if Phase 2 (original grid) is used
        this._forceThruActive = false;

        this._logger.LogDebug("[ScriptExec]" + this._charTag + " TryWalkToAsync: pos=({PX},{PY}) target=({TX},{TY}), grid[start]={SV}, grid[end]={EV}",
            pos.X, pos.Y, target.X, target.Y, sv, ev);

        var selector = this._player.AlgorithmSelector;
        if (selector is null || selector.Count == 0)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " TryWalkToAsync: No algorithm selector available!");
            return false;
        }

        try
        {
            // Phase 1: Try each registered algorithm in registration order (fast→robust)
            // until one finds a valid path. Falls back through A* → WeightedA* → Diffusion →
            // AntColony → Genetic → RandomWalk before attempting incremental step.
            var algorithms = selector.GetAll();
            IList<PathResultNode>? path = null;
            IPathFindingAlgorithm? usedAlgorithm = null;

            // Shadow Map Layer: merge monster density, failure records, etc. into AIgrid
            // before A* pathfinding so the path naturally avoids dangerous areas.
            byte[,] searchGrid;
            searchGrid = map.Terrain.AIgrid;

            // Monster Danger Filter: mark tiles near monsters that exceed the AI's
            // capability as impassable (127), so A* routes around them.
            // As the AI levels up, fewer monsters exceed the threshold → areas "unlock".
            {
                var myLevel = this._context.GameAdapter.GetPlayerLevel();
                var levelThreshold = this._script?.Parameters?.MaxLevelDiff ?? 15;
                var attackables = this._context.WorldState.AttackablesInRange;
                if (attackables is { Count: > 0 })
                {
                    foreach (var a in attackables)
                    {
                        if (a is not Monster m || !m.IsAlive)
                        {
                            continue;
                        }

                        var monsterLevel = (int)(m.Attributes?[Stats.Level] ?? 0);
                        if (monsterLevel <= myLevel + levelThreshold)
                        {
                            continue;
                        }

                        // Block 3×3 block around dangerous monster
                        var mx = m.Position.X;
                        var my = m.Position.Y;
                        for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var tx = mx + dx;
                            var ty = my + dy;
                            if (tx >= 0 && tx < 256 && ty >= 0 && ty < 256)
                            {
                                searchGrid[tx, ty] = 127;
                            }
                        }

                        this._logger?.LogDebug(
                            "[MonsterDanger] #{Monster} Lv{Level} at ({X},{Y}) exceeds AI Lv{MyLevel}+{Thresh} — blocked 3×3",
                            m.Definition.Number, monsterLevel, m.Position.X, m.Position.Y,
                            myLevel, levelThreshold);
                    }
                }
            }

            foreach (var algo in algorithms)
            {
                path = algo.FindPath(pos, target, searchGrid, true);
                if (path is { Count: > 0 })
                {
                    usedAlgorithm = algo;
                    break;
                }

                this._logger!.LogDebug("[ScriptExec]" + this._charTag + " TryWalkToAsync: {Algo} FAILED from ({PX},{PY}) to ({TX},{TY})",
                    algo.Name, pos.X, pos.Y, target.X, target.Y);
            }

            if (path is null || path.Count == 0)
            {
                // Phase 2: Force-through — retry on original unfiltered grid
                // But if we've died 3+ times force-throughing, mark target as DangerZone and skip.
                if (this._forceThruDeathCount >= 3)
                {
                    this._logger!.LogWarning(
                        "[ForceThru] Skipping force-through after {Count} deaths — marking ({TX},{TY}) as DangerZone",
                        this._forceThruDeathCount, target.X, target.Y);
                }
                else
                {
                    this._forceThruActive = true;
                    this._logger!.LogInformation(
                        "[MonsterDanger] All algorithms blocked by terrain filters, retrying on original grid from ({PX},{PY}) to ({TX},{TY})...",
                        pos.X, pos.Y, target.X, target.Y);

                    foreach (var algo in algorithms)
                    {
                        path = algo.FindPath(pos, target, originalGrid, true);
                        if (path is { Count: > 0 })
                        {
                            usedAlgorithm = algo;
                            this._logger!.LogInformation(
                                "[MonsterDanger] Force-through OK — {Algo} found path on original grid ({Count} steps)",
                                algo.Name, path.Count);
                            break;
                        }
                    }
                }
            }

            if (path is null || path.Count == 0)
            {
                this._logger!.LogInformation(
                    "[ScriptExec]" + this._charTag + " TryWalkToAsync: ALL {Count} algorithms + force-through failed from ({PX},{PY}) to ({TX},{TY}), trying incremental step...",
                    algorithms.Count, pos.X, pos.Y, target.X, target.Y);

                // Phase 3: Greedy incremental walk toward target
                const int maxFallbackSteps = 16;
                for (var step = 0; step < maxFallbackSteps; step++)
                {
                    if (!await TryWalkOneStepTowardAsync(target, map).ConfigureAwait(false))
                    {
                        return false;
                    }
                }

                return true;
            }

            this._logger!.LogInformation(
                "[ScriptExec]" + this._charTag + " TryWalkToAsync: {Algo} OK — {Count} steps from ({PX},{PY}) to ({TX},{TY})",
                usedAlgorithm!.Name, path.Count, pos.X, pos.Y, target.X, target.Y);

            // Walk the found path (max 16 steps per call to bound execution time)
            const int maxSteps = 16;
            var stepsCount = Math.Min(path.Count, maxSteps);
            var steps = new WalkingStep[stepsCount];
            for (int i = 0; i < stepsCount; i++)
            {
                var node = path[i];
                var prevPos = i == 0 ? pos : steps[i - 1].To;
                steps[i] = new WalkingStep(prevPos, node.Point, prevPos.GetDirectionTo(node.Point));
            }

            var targetNode = path[Math.Min(path.Count - 1, maxSteps - 1)];
            await this._context.GameAdapter.WalkDirectAsync(new Point(targetNode.X, targetNode.Y), steps).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "[ScriptExec]" + this._charTag + " TryWalkToAsync: pathfinding threw from ({PX},{PY}) to ({TX},{TY})", pos.X, pos.Y, target.X, target.Y);
            return false;
        }
    }

    /// <summary>
    /// Greedy single-step walk toward <paramref name="target"/>.
    /// Tries all 8 neighbors sorted by Euclidean distance to target,
    /// picks the first walkable one. This avoids A* for long paths
    /// that may exceed the scoped network or search limit.
    /// </summary>
    private async ValueTask<bool> TryWalkOneStepTowardAsync(Point target, GameMap map)
    {
        var pos = this._context.GameAdapter.GetPlayerPosition();
        if (pos == target)
        {
            return false;
        }

        // Try all 8 directions, preferring the one closest to target
        var bestStep = default(WalkingStep);
        var bestDist = double.MaxValue;
        var found = false;

        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                var nx = Math.Clamp(pos.X + dx, 0, 255);
                var ny = Math.Clamp(pos.Y + dy, 0, 255);
                if (!map.Terrain.WalkMap[nx, ny])
                {
                    continue;
                }

                var dist = Math.Sqrt(((nx - target.X) * (nx - target.X)) + ((ny - target.Y) * (ny - target.Y)));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    var next = new Point((byte)nx, (byte)ny);
                    bestStep = new WalkingStep(pos, next, pos.GetDirectionTo(next));
                    found = true;
                }
            }
        }

        if (!found)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " TryWalkOneStepTowardAsync: no walkable neighbor toward ({TX},{TY}) from ({PX},{PY})",
                target.X, target.Y, pos.X, pos.Y);
            return false;
        }

        var targetPoint = bestStep.To;
        await this._context.GameAdapter.WalkDirectAsync(targetPoint, new[] { bestStep }).ConfigureAwait(false);

        this._logger.LogDebug("[ScriptExec]" + this._charTag + " TryWalkOneStepTowardAsync: step ({PX},{PY})→({NX},{NY}) toward ({TX},{TY})",
            pos.X, pos.Y, targetPoint.X, targetPoint.Y, target.X, target.Y);
        return true;
    }

    private async ValueTask ShortRangeFallbackAsync(GameMap map)
    {
        var pos = this._context.GameAdapter.GetPlayerPosition();
        const int maxAttempts = 20;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var dx = Random.Shared.Next(-5, 6);
            var dy = Random.Shared.Next(-5, 6);
            if (dx == 0 && dy == 0)
            {
                continue;
            }

            var tx = Math.Clamp(pos.X + dx, 0, 255);
            var ty = Math.Clamp(pos.Y + dy, 0, 255);

            if (!map.Terrain.WalkMap[tx, ty])
            {
                continue;
            }

            var target = new Point((byte)tx, (byte)ty);
            var direction = pos.GetDirectionTo(target);
            var step = new WalkingStep(pos, target, direction);

            await this._context.GameAdapter.WalkDirectAsync(target, new[] { step }).ConfigureAwait(false);
            return;
        }
    }

    private async ValueTask<bool> ReturnAndSellAsync(ScriptParameters p)
    {
        this._logger.LogInformation("[ScriptExec]" + this._charTag + " >>> ReturnAndSellAsync ENTERED at tick={Tick}, pos=({X},{Y})",
            this._scriptTickCounter, this._context.GameAdapter.GetPlayerPosition().X, this._context.GameAdapter.GetPlayerPosition().Y);

        var npcService = this._npcService;
        if (npcService is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " NpcInteractionService not available — cannot return and sell.");
            return false;
        }

        var map = this._context.WorldState.CurrentMap;
        if (map is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " No current map — cannot return and sell.");
            return false;
        }

        // Step 0: check interaction cooldown
        var ready = npcService.IsReadyForInteraction;
        this._logger.LogInformation("[ScriptExec]" + this._charTag + " IsReadyForInteraction={Ready}", ready);
        if (!ready)
        {
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " NPC interaction on cooldown — waiting.");
            return false;
        }

        var playerPos = this._context.GameAdapter.GetPlayerPosition();

        // Step 1: find nearest merchant NPC
        // Cooldown check: if last failed attempt was within returnCooldownSec, skip
        var returnCooldownSec = p.ReturnCooldownSec;
        if ((DateTime.UtcNow - this._lastFailedMerchantAttempt).TotalSeconds < returnCooldownSec)
        {
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " ReturnAndSell on cooldown ({Sec}s) — skipping.", returnCooldownSec);
            return false;
        }

        var merchant = npcService.FindNearestMerchant(map, playerPos);
        this._logger.LogInformation("[ScriptExec]" + this._charTag + " FindNearestMerchant returned {Merchant}", merchant?.Definition?.Designation ?? "null");
        if (merchant is null)
        {
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " No merchant NPC found on map — cannot sell. Falling through to drop_item.");
            this._lastFailedMerchantAttempt = DateTime.UtcNow;
            return false;
        }

        // Step 2: find walkable tile near merchant
        const int dialogRange = 5;
        var walkTarget = FindNearestWalkableTile(merchant.Position, map, playerPos, maxRadius: dialogRange);
        var merchantDist = walkTarget.EuclideanDistanceTo(merchant.Position);

        // Verify walkTarget is within dialog range of the merchant NPC itself
        if (merchantDist > dialogRange)
        {
            this._logger.LogWarning(
                "[ScriptExec]" + this._charTag + " Nearest walkable tile ({Wx},{Wy}) is {Dist:F0} tiles from merchant ({Mx},{My}) — too far for dialog (max {Range}). Setting cooldown.",
                walkTarget.X, walkTarget.Y, merchantDist, merchant.Position.X, merchant.Position.Y, dialogRange);
            this._lastFailedMerchantAttempt = DateTime.UtcNow;
            return false;
        }

        var dist = playerPos.EuclideanDistanceTo(walkTarget);
        if (dist > dialogRange)
        {
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " Walking to merchant {Name} at ({X},{Y}), dist={Dist:F1}",
                merchant.Definition?.Designation, walkTarget.X, walkTarget.Y, dist);

            SuppressAutoNavigationForThisTick();

            if (!await TryWalkToAsync(walkTarget, map).ConfigureAwait(false))
            {
                this._logger.LogWarning("[ScriptExec]" + this._charTag + " Could not pathfind to merchant walk target at ({X},{Y})", walkTarget.X, walkTarget.Y);
                this._lastFailedMerchantAttempt = DateTime.UtcNow;
                return false;
            }

            // Need to wait for next tick to actually arrive
            return true;
        }

        // Step 3: we're in range — open dialog
        this._logger.LogDebug("[ScriptExec]" + this._charTag + " Opening NPC dialog with merchant {Name}", merchant.Definition?.Designation);

        SuppressAutoNavigationForThisTick();

        if (!await npcService.TryOpenDialogAsync(this._player, merchant).ConfigureAwait(false))
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " Failed to open NPC dialog with merchant.");
            return false;
        }

        // Step 4: sell items (inventory slot >= 12, i.e. non-equipped)
        var sold = await npcService.SellItemsAsync(this._player).ConfigureAwait(false);
        this._logger.LogInformation("[ScriptExec]" + this._charTag + " Sold {Sold} items to merchant.", sold);

        // Step 5: repair all equipment
        try
        {
            await npcService.RepairAllEquipmentAsync(this._player).ConfigureAwait(false);
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " Equipment repaired.");
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "[ScriptExec]" + this._charTag + " Equipment repair failed.");
        }

        // Step 6: buy potions
        var bought = await npcService.BuyPotionsAsync(this._player).ConfigureAwait(false);
        if (bought > 0)
        {
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " Bought {Bought} potions.", bought);
        }

        // Step 7: close dialog
        await npcService.CloseDialogAsync(this._player).ConfigureAwait(false);
        this._logger.LogInformation("[ScriptExec]" + this._charTag + " Return-and-sell complete. Sold={Sold}, RepairDone, Bought={Bought}", sold, bought);

        // Clear target so patrol resumes
        this._context.CurrentTarget = null;

        // Set cooldown to prevent immediate re-triggering
        this._lastFailedMerchantAttempt = DateTime.UtcNow;

        // Walk back to patrol center to avoid getting stuck at merchant
        var returnTarget = FindNearestWalkableTile(this._patrolCenter, map, this._context.GameAdapter.GetPlayerPosition(), maxRadius: 10);
        if (returnTarget != this._context.GameAdapter.GetPlayerPosition())
        {
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " ReturnAndSell: walking back to patrol area ({X},{Y})", returnTarget.X, returnTarget.Y);
            SuppressAutoNavigationForThisTick();
            if (await TryWalkToAsync(returnTarget, map).ConfigureAwait(false))
            {
                return true; // walking, need next tick to arrive
            }
        }

        return true;
    }

    /// <summary>
    /// Finds the nearest walkable tile near <paramref name="center"/> that is reachable
    /// from <paramref name="playerPos"/> via BFS on the walkable grid.
    /// First computes a BFS reachability map from <paramref name="playerPos"/> (search
    /// radius equal to <paramref name="maxRadius"/>), then spiral-searches around
    /// <paramref name="center"/> for a tile that is both walkable and reachable.
    /// Falls back to spiral-only (no reachability) if BFS is not desired, then
    /// finally falls back to <paramref name="center"/>.
    /// </summary>
    private Point FindNearestWalkableTile(Point center, GameMap map, Point playerPos, int maxRadius = 15)
    {
        // 1. BFS from player position to compute reachable tiles
        var reachable = new bool[256, 256];
        var queue = new Queue<Point>(maxRadius * maxRadius * 2);
        var visited = new bool[256, 256];
        var startX = Math.Clamp((int)playerPos.X, 0, 255);
        var startY = Math.Clamp((int)playerPos.Y, 0, 255);

        if (map.Terrain.WalkMap[startX, startY])
        {
            queue.Enqueue(new Point((byte)startX, (byte)startY));
            visited[startX, startY] = true;
            reachable[startX, startY] = true;
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    var nx = Math.Clamp(current.X + dx, 0, 255);
                    var ny = Math.Clamp(current.Y + dy, 0, 255);
                    if (visited[nx, ny])
                    {
                        continue;
                    }

                    visited[nx, ny] = true;
                    if (!map.Terrain.WalkMap[nx, ny])
                    {
                        continue;
                    }

                    reachable[nx, ny] = true;
                    var dist = Math.Abs(nx - startX) + Math.Abs(ny - startY);
                    if (dist < maxRadius)
                    {
                        queue.Enqueue(new Point((byte)nx, (byte)ny));
                    }
                }
            }
        }

        // 2. Spiral search around center for a walkable + reachable tile
        for (var r = 0; r <= maxRadius; r++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                for (var dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) == r || Math.Abs(dy) == r)
                    {
                        var tx = Math.Clamp(center.X + dx, 0, 255);
                        var ty = Math.Clamp(center.Y + dy, 0, 255);
                        if (map.Terrain.WalkMap[tx, ty] && reachable[tx, ty])
                        {
                            return new Point((byte)tx, (byte)ty);
                        }
                    }
                }
            }
        }

        // 3. Fallback: spiral without reachability (center tile itself may be walkable
        //    but unreachable if player is far away; still better than returning center
        //    which is definitely unwalkable)
        for (var r = 0; r <= maxRadius; r++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                for (var dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) == r || Math.Abs(dy) == r)
                    {
                        var tx = Math.Clamp(center.X + dx, 0, 255);
                        var ty = Math.Clamp(center.Y + dy, 0, 255);
                        if (map.Terrain.WalkMap[tx, ty])
                        {
                            this._logger.LogDebug(
                                "[ScriptExec]" + this._charTag + " FindNearestWalkableTile: fallback to walkable-only tile ({Tx},{Ty}) near ({Cx},{Cy}) — not reachable from player ({Px},{Py}).",
                                tx, ty, center.X, center.Y, playerPos.X, playerPos.Y);
                            return new Point((byte)tx, (byte)ty);
                        }
                    }
                }
            }
        }

        this._logger.LogWarning(
            "[ScriptExec]" + this._charTag + " FindNearestWalkableTile: no walkable tile found within radius {Radius} around ({Cx},{Cy}). Walkable tiles in area: {Count}",
            maxRadius,
            center.X,
            center.Y,
            CountWalkableTilesAround(center, map, maxRadius));

        return center;
    }

    /// <summary>
    /// Counts walkable tiles in a square of size <paramref name="radius"/> around <paramref name="center"/>.
    /// Used for diagnostic logging when no walkable tile is found.
    /// </summary>
    private static int CountWalkableTilesAround(Point center, GameMap map, int radius)
    {
        var count = 0;
        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                var tx = Math.Clamp(center.X + dx, 0, 255);
                var ty = Math.Clamp(center.Y + dy, 0, 255);
                if (map.Terrain.WalkMap[tx, ty])
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Temporarily sets SuppressAutoNavigation so the script tick's navigation doesn't
    /// override the walk we just queued. Must be called as a side effect before returning
    /// from the action so the caller (TickAsync) does not also run patrol/move logic.
    /// Since ScriptExecutor.TickAsync() returns immediately after this action completes,
    /// the effect carries to the main tick loop.
    /// </summary>
    private void SuppressAutoNavigationForThisTick()
    {
        this._context.SuppressAutoNavigation = true;
    }

    /// <summary>
    /// Drops the lowest-value non-equipped item from inventory to free a slot.
    /// Called when inventory is full and return_and_sell is not possible.
    /// </summary>
    private async ValueTask DropItemAsync(ScriptParameters p)
    {
        this._logger.LogInformation("[ScriptExec]" + this._charTag + " drop_item: stub — inventory manager removed; item drop handled by stubs.");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// 沿预设路线行走一步（walk_route 脚本动作）。
    /// 参数: routeId (可选), jitter (可选, 默认 3)。
    /// 无 routeId 时随机选一条公共路线。
    /// 到达终点后重置，下次 tick 重新选路。
    /// </summary>
    private async ValueTask ScriptWalkRouteAsync(ScriptParameters p)
    {
        // AiMap-based route walking is no longer available — routes can't be resolved
        // without the AiMap service. Log a warning and silently return.
        this._logger.LogWarning(
            "[ScriptExec]" + this._charTag + " walk_route: AiMap not available, route walking skipped (route='{RouteId}')",
            p.RouteId ?? "auto");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// 离开安全区（leave_safezone 脚本动作）。
    /// 用 GameMap.Terrain 找最近的 non-safezone 可走瓦片，走过去。
    /// 清除当前目标。
    /// </summary>
    private async ValueTask ScriptLeaveSafezoneAsync(ScriptParameters p)
    {
        var map = this._context.WorldState.CurrentMap;
        if (map is null)
        {
            return;
        }

        var pos = this._context.GameAdapter.GetPlayerPosition();

        // 从当前位置向外螺旋搜索最近的 non-safezone 可走瓦片
        const int maxRadius = 30;
        const int mapSize = 256;
        for (var radius = 1; radius <= maxRadius; radius++)
        {
            // 扫描当前半径的矩形边框
            for (var dx = -radius; dx <= radius; dx++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                {
                    // 只检查边框上的点（四边），避免重复扫描内部
                    if (Math.Abs(dx) != radius && Math.Abs(dy) != radius)
                    {
                        continue;
                    }

                    var tx = pos.X + dx;
                    var ty = pos.Y + dy;

                    if (tx < 0 || tx >= mapSize || ty < 0 || ty >= mapSize)
                    {
                        continue;
                    }

                    if (!map.Terrain.WalkMap[tx, ty])
                    {
                        continue;
                    }

                    if (map.Terrain.SafezoneMap[tx, ty])
                    {
                        continue; // 仍在安全区内
                    }

                    // 找到最近的 non-safezone 可走瓦片，走过去
                    var target = new Point((byte)tx, (byte)ty);
                    if (await TryWalkToAsync(target, map).ConfigureAwait(false))
                    {
                        this._logger.LogDebug("[ScriptExec]" + this._charTag + " LeaveSafezone: walking to ({TX},{TY})", tx, ty);
                        this._context.CurrentTarget = null;
                        return;
                    }
                }
            }
        }

        // 半径内没找到合适目标 → 降级策略：直接朝外走一步
        this._logger.LogWarning("[ScriptExec]" + this._charTag + " LeaveSafezone: TryWalkToAsync failed for all candidates within radius {Radius}, trying direct walk fallback", maxRadius);
        await FallbackLeaveSafezoneAsync(map, pos).ConfigureAwait(false);
    }

    /// <summary>
    /// 离开安全区降级策略 — 直接朝远离安全区的方向走一步。
    /// 当 TryWalkToAsync 因 Monster Danger Filter 等阻塞所有路径时生效。
    /// </summary>
    private async ValueTask FallbackLeaveSafezoneAsync(GameMap map, Point pos)
    {
        // 朝地图中心的反方向走（远离安全区中心的大致方向）
        const int mapCenterX = 128;
        const int mapCenterY = 128;
        var awayX = pos.X + (pos.X - mapCenterX);
        var awayY = pos.Y + (pos.Y - mapCenterY);

        // 避免超出地图边界
        awayX = Math.Clamp(awayX, 0, 255);
        awayY = Math.Clamp(awayY, 0, 255);

        // 检查目标是否可走且非安全区
        if (map.Terrain.WalkMap[awayX, awayY] && !map.Terrain.SafezoneMap[awayX, awayY])
        {
            // 使用 GameAdapter.WalkToAsync（不走 TryWalkToAsync，跳过 Monster Danger Filter）
            var result = await this._context.GameAdapter.WalkToAsync(new Point((byte)awayX, (byte)awayY), map).ConfigureAwait(false);
            if (result.Status == WalkStatusCode.Success)
            {
                this._logger.LogInformation("[ScriptExec]" + this._charTag + " FallbackLeaveSafezone: direct walk to ({X},{Y}) away from map center", awayX, awayY);
                this._context.CurrentTarget = null;
                return;
            }
        }

        // 备用：走一步（TryWalkOneStepTowardAsync 用 WalkMap 判断，最简单可靠）
        this._logger.LogWarning("[ScriptExec]" + this._charTag + " FallbackLeaveSafezone: direct walk failed, one-step fallback");
        if (await TryWalkOneStepTowardAsync(new Point((byte)awayX, (byte)awayY), map).ConfigureAwait(false))
        {
            this._context.CurrentTarget = null;
        }
    }

    /// <summary>
    /// 沿信息素痕迹行走一步（discover_trail 脚本动作）。
    /// 无痕迹时无操作（由 TickCoreAsync 兜底 fallthrough）。
    /// </summary>
    private async ValueTask ScriptDiscoverTrailAsync(ScriptParameters p)
    {
        // AiMap-based trail discovery is no longer available — silently skip.
        this._logger.LogWarning("[ScriptExec]" + this._charTag + " discover_trail: AiMap not available, trail discovery skipped");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async ValueTask<bool> PickupNearbyItemsAsync(ScriptParameters p)
    {
        // Pickup handled by stubs — always return false here
        await Task.CompletedTask.ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Filter predicate for "rare_and_above" pickup mode.
    /// Returns true for jewels, ancient items, socket items, items with options,
    /// items upgraded to +4+, and money >= 10k.
    /// Excludes potions (Group 14), ammo (Group 15), and low-value consumables.
    /// </summary>
    private static bool ShouldPickupRareAndAbove(ILocateable drop)
    {
        switch (drop)
        {
            case DroppedMoney moneyDrop:
                return moneyDrop.Amount >= 10000;
            case DroppedItem itemDrop:
                var item = itemDrop.Item;
                if (item.Definition is null)
                {
                    return false;
                }

                // Exclude potions and ammo
                if (item.Definition.Group == 14 || item.Definition.Group == 15)
                {
                    // Exception: jewels share Group 14 with potions — check by Number
                    // Jewel of Bless = (14,13), Soul = (14,14), Life = (14,16)
                    // Chaos = (14,12), Creation = (14,41), Harmony = (14,42)
                    if (item.Definition.Number == 12 || item.Definition.Number == 13
                        || item.Definition.Number == 14 || item.Definition.Number == 16
                        || item.Definition.Number == 41 || item.Definition.Number == 42)
                    {
                        return true; // jewels
                    }

                    return false; // potions / ammo excluded
                }

                // ★5 — jewels (also caught above by group check, but handle non-group-14 jewels)
                if (item.Definition.Number == 12 || item.Definition.Number == 13
                    || item.Definition.Number == 14 || item.Definition.Number == 16
                    || item.Definition.Number == 41 || item.Definition.Number == 42)
                {
                    return true;
                }

                // ★5 — ancient items
                if (item.ItemSetGroups.Count > 0)
                {
                    return true;
                }

                // ★4 — items with options (proxy for excellent/socket)
                if (item.ItemOptions.Count > 0)
                {
                    return true;
                }

                // ★4 — socket items
                if (item.SocketCount > 0)
                {
                    return true;
                }

                // ★3 — items upgraded to +4 or higher
                if (item.Level >= 4)
                {
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Applies auto-buff skills — no-op since SkillService was removed.
    /// </summary>
    private async ValueTask UseBuffAsync(ScriptParameters p)
    {
        // SkillService-based auto-buff is no longer available — skip.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Applies healing/regeneration skills — no-op since SkillService was removed.
    /// </summary>
    private async ValueTask UseHealSkillAsync(ScriptParameters p)
    {
        // SkillService-based auto-heal is no longer available — skip.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async ValueTask SitAndRegenAsync(ScriptParameters p)
    {
        var hpRatio = GetHpRatio();
        var mpRatio = GetMpRatio();
        if (hpRatio >= 0.9f && mpRatio >= 0.9f)
        {
            this._context.CurrentTarget = null;
        }
        // 如果 HP/MP 不足，等待恢复（什么都不做）
    }

    /// <summary>
    /// Executes a script-specified skill by SkillNumber against the current target.
    /// Unlike attack_target which auto-selects the best skill, this uses the
    /// explicit skill number from script parameters for fixed-skill scenarios.
    /// </summary>
    private async ValueTask UseSkillAsync(ScriptParameters p)
    {
        var target = this._context.CurrentTarget;
        if (target is null || p.SkillNumber is null)
        {
            return;
        }

        var skillList = this._player.SkillList;
        if (skillList is null)
        {
            return;
        }

        var skillEntry = skillList.Skills.FirstOrDefault(s => s.Skill?.Number == p.SkillNumber.Value);
        if (skillEntry is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " use_skill: skill #{SkillNumber} not found in skill list", p.SkillNumber.Value);
            return;
        }

        await this._context.GameAdapter.HitWithSkillAsync(target, skillEntry).ConfigureAwait(false);
    }

    // ===== Helpers =====

    private float GetHpRatio()
    {
        // Include shield in effective HP: shield absorbs 90% of damage,
        // so CurrentHealth alone drops too slowly to trigger potion use.
        var hp = this._context.GameAdapter.GetCurrentHp();
        var maxHp = this._context.GameAdapter.GetMaxHp();
        var shield = this._player.Attributes?[Stats.CurrentShield] ?? 0;
        var maxShield = this._player.Attributes?[Stats.MaximumShield] ?? 0;
        var effective = hp + shield;
        var effectiveMax = maxHp + maxShield;
        return effectiveMax > 0 ? (float)effective / effectiveMax : 1f;
    }

    private float GetMpRatio()
    {
        var mp = this._context.GameAdapter.GetCurrentMp();
        var maxMp = this._context.GameAdapter.GetMaxMp();
        return maxMp > 0 ? (float)mp / maxMp : 1f;
    }

    private bool HasTargetInRange(float attackRange)
    {
        var target = this._context.CurrentTarget;
        if (target is null)
        {
            return false;
        }

        var dist = this._context.WorldState.PlayerPosition.EuclideanDistanceTo(target.Position);
        return dist <= attackRange;
    }

    private bool HasTargetNotInRange(float attackRange)
    {
        var target = this._context.CurrentTarget;
        if (target is null)
        {
            return false;
        }

        var dist = this._context.WorldState.PlayerPosition.EuclideanDistanceTo(target.Position);
        return dist > attackRange;
    }

    private bool IsInventoryFull()
    {
        var inventory = this._player.Inventory;
        if (inventory is null)
        {
            return false;
        }

        return !inventory.FreeSlots.Any();
    }

    private bool IsEquipDurableLow(float threshold)
    {
        var inventory = this._player.Inventory;
        if (inventory is null) return false;
        // 检查装备栏（slot 0-11）是否有耐久低于阈值的物品
        foreach (var item in inventory.Items)
        {
            if (item.ItemSlot <= 11) // 装备槽
            {
                var durability = item.Durability;
                var maxDura = item.Definition?.Durability ?? 1;
                if (maxDura > 0 && (float)durability / maxDura < threshold)
                    return true;
            }
        }
        return false;
    }

    internal static ScriptParameters MergeParameters(ScriptParameters global, ScriptParameters? local)
    {
        if (local is null)
        {
            return global;
        }

        return new ScriptParameters
        {
            HpThreshold = local.HpThreshold,
            MpThreshold = local.MpThreshold,
            MaxLevelDiff = local.MaxLevelDiff != 0 ? local.MaxLevelDiff : global.MaxLevelDiff,
            PatrolRadius = local.PatrolRadius != 0 ? local.PatrolRadius : global.PatrolRadius,
            AttackRange = local.AttackRange > 0 ? local.AttackRange : global.AttackRange,
            PickupFilter = local.PickupFilter ?? global.PickupFilter,
            ReturnWhenInventoryFull = local.ReturnWhenInventoryFull,
            SearchRange = local.SearchRange != 0 ? local.SearchRange : global.SearchRange,
            SkillNumber = local.SkillNumber ?? global.SkillNumber,
            BlacklistExpiryTicks = local.BlacklistExpiryTicks != 50 ? local.BlacklistExpiryTicks : global.BlacklistExpiryTicks,
            PatrolCenterResetTicks = local.PatrolCenterResetTicks != 50 ? local.PatrolCenterResetTicks : global.PatrolCenterResetTicks,
            PotionThreshold = local.PotionThreshold,
            BuyPotionCount = local.BuyPotionCount,
            QuestGroup = local.QuestGroup ?? global.QuestGroup,
            QuestNumber = local.QuestNumber ?? global.QuestNumber,
            NpcBlacklist = local.NpcBlacklist ?? global.NpcBlacklist,
            RouteId = local.RouteId ?? global.RouteId,
            Jitter = local.Jitter != 3 ? local.Jitter : global.Jitter,
            DurabilityThreshold = local.DurabilityThreshold,
            PotionHysteresisMargin = local.PotionHysteresisMargin,
            VariableExpression = local.VariableExpression ?? global.VariableExpression,
            VariableOp = local.VariableOp ?? global.VariableOp,
            PositionStuckThreshold = local.PositionStuckThreshold != 50 ? local.PositionStuckThreshold : global.PositionStuckThreshold,
            PickupCooldownTicks = local.PickupCooldownTicks != 10 ? local.PickupCooldownTicks : global.PickupCooldownTicks,
            TargetRandomizationCount = local.TargetRandomizationCount != 3 ? local.TargetRandomizationCount : global.TargetRandomizationCount,
            HotspotOccupiedRadius = local.HotspotOccupiedRadius != 8 ? local.HotspotOccupiedRadius : global.HotspotOccupiedRadius,
            QuestNpcNumber = local.QuestNpcNumber ?? global.QuestNpcNumber,
            TargetMapNumber = local.TargetMapNumber ?? global.TargetMapNumber,
            NoMonsterTicksLimit = local.NoMonsterTicksLimit != 30 ? local.NoMonsterTicksLimit : global.NoMonsterTicksLimit,
            Hotspots = local.Hotspots ?? global.Hotspots,
            SkillPriority = local.SkillPriority ?? global.SkillPriority,
            ReturnCooldownSec = local.ReturnCooldownSec != 60 ? local.ReturnCooldownSec : global.ReturnCooldownSec,
            PotionCooldownMs = local.PotionCooldownMs != 2000 ? local.PotionCooldownMs : global.PotionCooldownMs,
        };
    }

    // ===== Variable System =====

    private bool EvaluateVariableCondition(ScriptParameters p)
    {
        var expr = p.VariableExpression;
        if (string.IsNullOrWhiteSpace(expr))
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " variable condition with null/empty VariableExpression");
            return false;
        }

        // Expected format: "varName == 3" or "attackCount > 5"
        // Split on whitespace to get [varName, operator, value]
        var parts = expr.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " Invalid variable expression: {Expr}", expr);
            return false;
        }

        var varName = parts[0];
        var op = parts[1];
        if (!int.TryParse(parts[2], out var value))
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " Invalid variable comparison value: {Value} in expression: {Expr}", parts[2], expr);
            return false;
        }

        return op switch
        {
            "==" => this._variables.Equal(varName, value),
            ">" => this._variables.Large(varName, value),
            "<" => this._variables.Small(varName, value),
            ">=" => this._variables.LargeOrEqual(varName, value),
            "<=" => this._variables.SmallOrEqual(varName, value),
            _ => false,
        };
    }

    private bool ExecuteVariableOp(ScriptParameters p)
    {
        var opDesc = p.VariableOp;
        if (string.IsNullOrWhiteSpace(opDesc))
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " variable_op with null/empty VariableOp");
            return false;
        }

        // Expected format: "set attackCount 0" or "inc attackCount" or "dec attackCount 2"
        var parts = opDesc.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " Invalid variable operation: {Op}", opDesc);
            return false;
        }

        var cmd = parts[0];
        var varName = parts[1];

        switch (cmd)
        {
            case "set":
                if (parts.Length < 3 || !int.TryParse(parts[2], out var setValue))
                {
                    this._logger.LogWarning("[ScriptExec]" + this._charTag + " Invalid set value: {Op}", opDesc);
                    return false;
                }

                this._variables.Set(varName, setValue);
                this._logger.LogTrace("[ScriptExec]" + this._charTag + " Variable set: {Name} = {Value}", varName, setValue);
                return true;

            case "inc":
                var incDelta = parts.Length >= 3 && int.TryParse(parts[2], out var parsedInc) ? parsedInc : 1;
                this._variables.Inc(varName, incDelta);
                this._logger.LogTrace("[ScriptExec]" + this._charTag + " Variable inc: {Name} += {Delta} (now {Val})", varName, incDelta, this._variables.Get(varName));
                return true;

            case "dec":
                var decDelta = parts.Length >= 3 && int.TryParse(parts[2], out var parsedDec) ? parsedDec : 1;
                this._variables.Dec(varName, decDelta);
                this._logger.LogTrace("[ScriptExec]" + this._charTag + " Variable dec: {Name} -= {Delta} (now {Val})", varName, decDelta, this._variables.Get(varName));
                return true;

            default:
                this._logger.LogWarning("[ScriptExec]" + this._charTag + " Unknown variable operation: {Cmd} in {Op}", cmd, opDesc);
                return false;
        }
    }

    private int CountPotions()
    {
        var inventory = this._player.Inventory;
        if (inventory?.Items is null)
            return 0;
        var count = 0;
        foreach (var item in inventory.Items)
        {
            if (item.Definition?.Group == 14 && item.Definition.Number >= 1 && item.Definition.Number <= 6)
                count++;
        }

        // Also scan extensions (quick slots / personal store area) — potions bought from merchants
        // may be placed into an extension slot (slot >= FirstExtensionItemSlotIndex = 76) rather
        // than the main inventory area. Without this check, the AI loops buying potions that it
        // already has but can't see.
        if (inventory.Extensions is not null)
        {
            foreach (var extension in inventory.Extensions)
            {
                foreach (var item in extension.Items)
                {
                    if (item.Definition?.Group == 14 && item.Definition.Number >= 1 && item.Definition.Number <= 6)
                        count++;
                }
            }
        }

        return count;
    }

    private async ValueTask StartQuestAsync(ScriptParameters p)
    {
        if (p.QuestGroup is null || p.QuestNumber is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " start_quest requires questGroup and questNumber parameters");
            return;
        }
        var group = p.QuestGroup.Value;
        var number = p.QuestNumber.Value;
        try
        {
            var questAction = new MUnique.OpenMU.GameLogic.PlayerActions.Quests.QuestStartAction();
            await questAction.StartQuestAsync(this._player, group, number).ConfigureAwait(false);

            // Check if ActiveQuest was actually set by QuestStartAction.
            // It silently fails when GetQuest() returns null (OpenedNpc-dependent).
            var questStates = this._player.SelectedCharacter?.QuestStates;
            var activeQuestSet = questStates?.Any(qs => qs.Group == group && qs.ActiveQuest is not null) ?? false;

            if (activeQuestSet)
            {
                this._logger.LogDebug("[ScriptExec]" + this._charTag + " Started quest group={Group} number={Number}", group, number);
                return;
            }

            // Fallback: QuestStartAction.StartQuestAsync failed (likely G0/QuestGiver=null)
            // because GetQuest() depends on OpenedNpc. Find QuestDefinition from global config.
            var questDef = this.FindQuestDefinition(group, number);
            if (questDef is null)
            {
                this._logger.LogWarning("[ScriptExec]" + this._charTag + " start_quest fallback: QuestDefinition G{Group}/N{Number} not found in config", group, number);
                return;
            }

            this._logger.LogInformation("[ScriptExec]" + this._charTag + " Auto-activating quest G{Group}/N{Number} via config fallback (QuestDefinition found={Found})", group, number, true);

            // Check prerequisites: level range
            if (questDef.MinimumCharacterLevel > this._player.Level ||
                (questDef.MaximumCharacterLevel > 0 && questDef.MaximumCharacterLevel < this._player.Level))
            {
                this._logger.LogWarning("[ScriptExec]" + this._charTag + " start_quest fallback: level check failed for G{Group}/N{Number} (min={Min},max={Max})",
                    group, number, questDef.MinimumCharacterLevel, questDef.MaximumCharacterLevel);
                return;
            }

            // Get or create CharacterQuestState for this group
            var questState = this._player.SelectedCharacter!.QuestStates.FirstOrDefault(q => q.Group == group);
            if (questState is null)
            {
                questState = this._player.PersistenceContext.CreateNew<CharacterQuestState>();
                questState.Group = group;
                this._player.SelectedCharacter.QuestStates.Add(questState);
            }

            // Check repeatability
            if (Equals(questState.LastFinishedQuest, questDef) && !questDef.Repeatable)
            {
                this._logger.LogWarning("[ScriptExec]" + this._charTag + " start_quest fallback: quest G{Group}/N{Number} is not repeatable", group, number);
                return;
            }

            // Check RequiredStartMoney
            if (questDef.RequiredStartMoney > 0)
            {
                if (!this._player.TryRemoveMoney(questDef.RequiredStartMoney))
                {
                    this._logger.LogWarning("[ScriptExec]" + this._charTag + " start_quest fallback: insufficient money for G{Group}/N{Number} (need={Need})",
                        group, number, questDef.RequiredStartMoney);
                    return;
                }
            }

            // Clear existing state (matching QuestStartAction line 64-65 pattern)
            await questState.ClearAsync(this._player.PersistenceContext).ConfigureAwait(false);
            questState.ActiveQuest = questDef;

            this._logger.LogInformation("[ScriptExec]" + this._charTag + " Quest G{Group}/N{Number} activated via config fallback", group, number);
        }
        catch (Exception ex)
        {
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " Failed to start quest group={Group} number={Number}: {Msg}", group, number, ex.Message);
        }
    }

    private async ValueTask CompleteQuestAsync(ScriptParameters p)
    {
        if (p.QuestGroup is null || p.QuestNumber is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " complete_quest requires questGroup and questNumber parameters");
            return;
        }
        try
        {
            var questAction = new MUnique.OpenMU.GameLogic.PlayerActions.Quests.QuestCompletionAction();
            await questAction.CompleteQuestAsync(this._player, p.QuestGroup.Value, p.QuestNumber.Value).ConfigureAwait(false);
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " Completed quest group={Group} number={Number}", p.QuestGroup, p.QuestNumber);
            await this._closeNpcDialog.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " Failed to complete quest group={Group} number={Number}: {Msg}", p.QuestGroup, p.QuestNumber, ex.Message);
        }
        finally
        {
            this._questDialogReady = false;
        }
    }

    private async ValueTask AcceptQuestAsync(ScriptParameters p)
    {
        if (p.QuestGroup is null || p.QuestNumber is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " accept_quest requires questGroup and questNumber parameters");
            return;
        }

        try
        {
            var result = await this._context.GameAdapter.StartQuestAsync(p.QuestGroup.Value, p.QuestNumber.Value).ConfigureAwait(false);
            if (result.Status == QuestStatusCode.Accepted)
            {
                this._logger.LogInformation("[ScriptExec]" + this._charTag + " Accepted quest group={Group} number={Number} status={Status}",
                    p.QuestGroup, p.QuestNumber, result.Status);
                this.ResetIdleTimer(); // 接任务成功 = 明确进展
                // Log quest state right after accept
                var qsAfter = this._player.SelectedCharacter?.QuestStates;
                var afterCount = qsAfter?.Count ?? -1;
                var afterDetails = qsAfter is null ? "null" : string.Join(", ", qsAfter.Select(qs => $"G{qs.Group} AQ={(qs.ActiveQuest is null ? "null" : qs.ActiveQuest.Number.ToString())}"));
                this._logger.LogInformation("[ScriptExec]" + this._charTag + " After accept: qsCount={Count}, details=[{Details}]", afterCount, afterDetails);
                // Close NPC dialog so player returns to EnteredWorld and can resume hunting
                await this._closeNpcDialog.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
                // Log quest state after dialog close too
                var qsAfterClose = this._player.SelectedCharacter?.QuestStates;
                var afterCloseCount = qsAfterClose?.Count ?? -1;
                var afterCloseDetails = qsAfterClose is null ? "null" : string.Join(", ", qsAfterClose.Select(qs => $"G{qs.Group} AQ={(qs.ActiveQuest is null ? "null" : qs.ActiveQuest.Number.ToString())}"));
                this._logger.LogInformation("[ScriptExec]" + this._charTag + " After dialog close: qsCount={Count}, details=[{Details}]", afterCloseCount, afterCloseDetails);
            }
            else
            {
                this._logger.LogWarning("[ScriptExec]" + this._charTag + " Failed to accept quest group={Group} number={Number}: {Reason} (status={Status})",
                    p.QuestGroup, p.QuestNumber, result.Reason ?? "未知", result.Status);
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "[ScriptExec]" + this._charTag + " Error accepting quest group={Group} number={Number}", p.QuestGroup, p.QuestNumber);
        }
        finally
        {
            this._questDialogReady = false;
        }
    }

    private async ValueTask SubmitQuestAsync(ScriptParameters p)
    {
        if (p.QuestGroup is null || p.QuestNumber is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " submit_quest requires questGroup and questNumber parameters");
            return;
        }

        try
        {
            var result = await this._context.GameAdapter.CompleteQuestAsync(p.QuestGroup.Value, p.QuestNumber.Value).ConfigureAwait(false);
            if (result.Status == QuestStatusCode.Accepted)
            {
                this._logger.LogInformation("[ScriptExec]" + this._charTag + " Submitted quest group={Group} number={Number} status={Status}",
                    p.QuestGroup, p.QuestNumber, result.Status);
                this.ResetIdleTimer(); // 交任务成功 = 明确进展
                // Close NPC dialog so player returns to EnteredWorld and can resume next cycle
                await this._closeNpcDialog.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
            }
            else
            {
                this._logger.LogWarning("[ScriptExec]" + this._charTag + " Failed to submit quest group={Group} number={Number}: {Reason} (status={Status})",
                    p.QuestGroup, p.QuestNumber, result.Reason ?? "未知", result.Status);
            }
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "[ScriptExec]" + this._charTag + " Error submitting quest group={Group} number={Number}", p.QuestGroup, p.QuestNumber);
        }
        finally
        {
            this._questDialogReady = false;
        }
    }

    /// <summary>
    /// Walks toward the quest NPC and/or opens its dialog.
    /// Returns true if a meaningful action was taken (walking started or dialog just opened).
    /// Returns false if already at the NPC with dialog open — to allow paragraph fallthrough
    /// to subsequent nodes (check_accepted, try_accept, etc.).
    /// </summary>
    private async ValueTask<WalkToNpcResult> WalkToQuestNpcWithFallthroughAsync(ScriptParameters p)
    {
        this._logger.LogInformation("[ScriptExec]" + this._charTag + " walk_to_quest_npc ENTERED: QuestNpcNumber={Npc}, QuestGroup={Grp}, QuestNumber={Num}",
            p.QuestNpcNumber, p.QuestGroup, p.QuestNumber);

        if (p.QuestNpcNumber is null || p.QuestNpcNumber.Value == 0)
        {
            // null/0 QuestNpcNumber 意味着这是一个自动接受任务（如 G0 主线），没有 NPC 可对话
            // 直接 Fallthrough 放行，脚本继续执行 accept/submit（start_quest/complete_quest
            // 内部会处理无 NPC 对话的自动接取/提交）
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " walk_to_quest_npc: QuestNpcNumber is null/0 — auto-accept quest, skipping NPC walk.");
            return WalkToNpcResult.Fallthrough;
        }

        // 对话已就绪 → 段落继续执行 accept/submit
        if (this._questDialogReady)
        {
            this._logger.LogTrace("[ScriptExec]" + this._charTag + " Quest dialog ready — allowing fallthrough.");
            return WalkToNpcResult.Fallthrough;
        }

        // 直接用游戏实体的 CurrentMap — 它是真实地图，始终由游戏服务器保持同步。
        // 不依赖 BehaviorContext.WorldState（决策模式下未被 AiPlayerLogic 刷新）。
        var map = this._player.CurrentMap;
        if (map is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " walk_to_quest_npc: player.CurrentMap is null");
            return WalkToNpcResult.Failed;
        }

        var npcService = this._npcService;
        if (npcService is null)
        {
            return WalkToNpcResult.Failed;
        }

        var npcNumber = p.QuestNpcNumber.Value;

        // === 第一步：从 MonsterSpawns 获取 NPC 在当前地图的刷出坐标 ===
        var spawnPos = npcService.GetQuestNpcSpawn(map, npcNumber);
        this._logger.LogInformation("[ScriptExec]" + this._charTag + " DIAG: GetQuestNpcSpawn(#" + npcNumber + ")=({X},{Y})", spawnPos?.X, spawnPos?.Y);

        if (spawnPos is null)
        {
            // NPC 不在当前地图 → 跨地图查找并传送
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " Quest NPC #{NpcNumber} not spawned on current map — searching all maps...",
                npcNumber);
            var targetMapNumber = npcService.FindNpcMapNumber(this._player, npcNumber);
            if (targetMapNumber.HasValue && targetMapNumber.Value != map.Definition.Number)
            {
                this._logger.LogInformation("[ScriptExec]" + this._charTag + " NPC #{NpcNumber} found on map {MapNum} — warping...",
                    npcNumber, targetMapNumber.Value);
                var npcParams = new ScriptParameters { TargetMapNumber = targetMapNumber.Value };
                await WarpToMapAsync(npcParams).ConfigureAwait(false);
                return WalkToNpcResult.Walking;
            }
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " Quest NPC #{NpcNumber} not found on any map — NPC may be hidden/gate-spawned or not in MonsterSpawns. Falling through.", npcNumber);
            // NPC 找不到不一定致命: 有些任务 NPC 由事件/门触发而不是 MonsterSpawns 配置,
            // 或者 QuestGiver 不可达但任务本身无需 NPC 对话(自动接取的副本任务等)
            // Fallthrough 让脚本继续走 accept/submit, 服务器会处理实际任务逻辑
            return WalkToNpcResult.Fallthrough;
        }

        // === 第二步：检查是否已在 NPC 附近 ===
        var playerPos = this._context.GameAdapter.GetPlayerPosition();
        var distance = playerPos.EuclideanDistanceTo(spawnPos.Value);
        const int interactionRange = 3;

        if (distance <= interactionRange)
        {
            // 已有 NPC 实例且对话已打开 → 放行
            if (this._player.OpenedNpc?.Definition?.Number == npcNumber)
            {
                this._logger.LogTrace("[ScriptExec]" + this._charTag + " Already at NPC #{NpcNumber} with open dialog — allowing fallthrough.",
                    npcNumber);
                return WalkToNpcResult.Fallthrough;
            }

            // 尝试获取 NPC 实例 (GetNpcsInRange 在此范围可以可靠获取已生成的 NPC)
            var questNpc = npcService.FindQuestNpc(map, playerPos, npcNumber);
            if (questNpc is not null)
            {
                // 尝试打开对话
                if (this._player.OpenedNpc is null)
                {
                    var opened = await npcService.TryOpenDialogAsync(this._player, questNpc).ConfigureAwait(false);
                    this._logger.LogInformation("[ScriptExec]" + this._charTag + " Opened NPC #{NpcNumber} dialog: {Result}",
                        npcNumber, opened);
                    if (opened)
                    {
                        this._questDialogReady = true;
                        return WalkToNpcResult.Walking;
                    }

                    // Dialog may not stay open (e.g. LeavesDialogOpen=false). Retry next tick.
                    this._logger.LogTrace("[ScriptExec]" + this._charTag + " TryOpenDialogAsync for NPC #{NpcNumber} returned false — retrying next tick.",
                        npcNumber);
                    return WalkToNpcResult.Walking;
                }
                return WalkToNpcResult.Fallthrough;
            }
            else
            {
                // 坐标已到但 NPC 实例还没生成（刚切地图 / 加载延迟）→ 等一 tick
                this._logger.LogTrace("[ScriptExec]" + this._charTag + " At NPC #{NpcNumber} spawn ({X},{Y}) but instance not yet available — waiting.",
                    npcNumber, spawnPos.Value.X, spawnPos.Value.Y);
                return WalkToNpcResult.Walking;
            }
        }

        // === 第三步：走到 NPC 刷出坐标 ===
        this._logger.LogInformation("[ScriptExec]" + this._charTag + " Walking to quest NPC #{NpcNumber} spawn at ({X},{Y}), distance={Dist:F1}.",
            npcNumber, spawnPos.Value.X, spawnPos.Value.Y, distance);

        var walkOk = await TryWalkToAsync(spawnPos.Value, map).ConfigureAwait(false);
        if (!walkOk)
        {
            // 第一层 fallback: 找 NPC 附近的可走格子
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " Direct path to NPC #{NpcNumber} spawn ({X},{Y}) failed — trying nearby walkable tile.",
                npcNumber, spawnPos.Value.X, spawnPos.Value.Y);
            var fallbackTarget = FindNearestWalkableTile(spawnPos.Value, map, playerPos, maxRadius: 10);
            if (playerPos.EuclideanDistanceTo(fallbackTarget) <= interactionRange)
            {
                return WalkToNpcResult.Fallthrough;
            }
            SuppressAutoNavigationForThisTick();
            if (!await TryWalkToAsync(fallbackTarget, map).ConfigureAwait(false))
            {
                // 第二层 fallback: Monster Danger Filter 可能阻塞了所有路径,
                // 使用 GameAdapter.WalkToAsync（不走危险过滤, 用原始 AStar）
                this._logger.LogInformation("[ScriptExec]" + this._charTag +
                    " TryWalkToAsync fallback also failed — trying direct AStar walk to nearest walkable tile around player.",
                    npcNumber);
                var playerWalkable = FindNearestWalkableTile(playerPos, map, playerPos, maxRadius: 10);
                if (playerPos.EuclideanDistanceTo(playerWalkable) > 1)
                {
                    var result = await this._context.GameAdapter.WalkToAsync(playerWalkable, map).ConfigureAwait(false);
                    if (result.Status == WalkStatusCode.Success)
                    {
                        return WalkToNpcResult.Walking;
                    }
                }

                // 第三层 fallback: 所有路径都失败, 但 NPC 可能已经在地图上存在
                // 不用精确寻路, 用 GetNpcsInRange 找 NPC 实例直接走到
                var anyNpc = npcService.FindQuestNpc(map, playerPos, npcNumber);
                if (anyNpc is not null)
                {
                    this._logger.LogInformation("[ScriptExec]" + this._charTag +
                        " Found NPC #{NpcNumber} instance on map — walking directly to it.", npcNumber);
                    var npcWalkTarget = FindNearestWalkableTile(anyNpc.Position, map, playerPos, maxRadius: 10);
                    if (playerPos.EuclideanDistanceTo(npcWalkTarget) <= interactionRange)
                    {
                        return WalkToNpcResult.Fallthrough;
                    }
                    var directResult = await this._context.GameAdapter.WalkToAsync(npcWalkTarget, map).ConfigureAwait(false);
                    if (directResult.Status == WalkStatusCode.Success)
                    {
                        return WalkToNpcResult.Walking;
                    }
                }

                this._logger.LogWarning("[ScriptExec]" + this._charTag + " All paths to NPC #{NpcNumber} failed.", npcNumber);
                return WalkToNpcResult.Failed;
            }
        }

        return WalkToNpcResult.Walking;
    }

    /// <summary>
    /// Follows the party leader (PartyMaster). Gets the leader's position
    /// and walks toward it. If the leader is on a different map or
    /// not reachable, logs a warning and falls through.
    /// </summary>
    private async ValueTask FollowLeaderAsync(ScriptParameters p)
    {
        var party = this._player.Party;
        if (party is null || party.PartyList.Count == 0)
        {
            return;
        }

        var leader = party.PartyMaster;
        if (leader is null)
        {
            return;
        }

        // Check if leader is on the same map
        if (leader.CurrentMap != this._context.GameAdapter.GetCurrentMap() || this._context.WorldState.CurrentMap is null)
        {
            this._logger.LogTrace("[ScriptExec]" + this._charTag + " follow_leader: leader {Name} is on a different map or current map unknown",
                leader.Name);
            return;
        }

        var leaderPos = leader.Position;
        var myPos = this._context.GameAdapter.GetPlayerPosition();
        const float followDistance = 3f;
        var dist = myPos.EuclideanDistanceTo(leaderPos);

        if (dist <= followDistance)
        {
            // Already close to leader
            return;
        }

        // Walk toward leader (stop at followDistance range)
        this._logger.LogDebug("[ScriptExec]" + this._charTag + " follow_leader: following {Name} from ({X},{Y}) to ({LX},{LY}), dist={Dist:F1}",
            leader.Name, myPos.X, myPos.Y, leaderPos.X, leaderPos.Y, dist);

        SuppressAutoNavigationForThisTick();

        var map = this._context.WorldState.CurrentMap;
        if (!await TryWalkToAsync(leaderPos, map).ConfigureAwait(false))
        {
            this._logger.LogTrace("[ScriptExec]" + this._charTag + " follow_leader: pathfinding to leader {Name} at ({X},{Y}) failed",
                leader.Name, leaderPos.X, leaderPos.Y);
        }
    }

    /// <summary>
    /// Travels to the target map (<see cref="BehaviorContext.TargetMapNumber"/>)
    /// by finding an EnterGate on the current map that leads to the target.
    /// Walks to the gate and uses <see cref="WarpGateAction.EnterGateAsync"/>
    /// to warp through. Must be on the same map as the gate for warp to work.
    /// </summary>
    private async ValueTask TravelToMapAsync(ScriptParameters p)
    {
        var targetMapNumber = p.TargetMapNumber ?? this._context.TargetMapNumber;
        if (targetMapNumber is null)
        {
            this._logger.LogTrace("[ScriptExec]" + this._charTag + " travel_to_map: no target map set");
            return;
        }

        var currentMap = this._context.GameAdapter.GetCurrentMap();
        if (currentMap?.Definition is null)
        {
            return;
        }

        // Check if we're already on the target map
        if (currentMap.Definition.Number == targetMapNumber.Value)
        {
            this._context.TargetMapNumber = null; // arrived, clear target
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " travel_to_map: arrived at target map {Map}",
                currentMap.Definition.Name);
            return;
        }

        // Find an EnterGate on this map that leads to the target map
        var enterGates = currentMap.Definition.EnterGates;
        if (enterGates is null || enterGates.Count == 0)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " travel_to_map: no enter gates on current map");
            return;
        }

        EnterGate? targetGate = null;
        foreach (var gate in enterGates)
        {
            if (gate.TargetGate?.Map?.Number == targetMapNumber.Value)
            {
                targetGate = gate;
                break;
            }
        }

        if (targetGate is null)
        {
            this._logger.LogWarning(
                "[ScriptExec]" + this._charTag + " travel_to_map: no gate found from current map to target map {Target}",
                targetMapNumber.Value);
            return;
        }

        // Check if we're already at the gate position (within info range)
        var playerPos = this._context.GameAdapter.GetPlayerPosition();
        var inaccuracy = this._player.GameContext.Configuration.InfoRange;
        var gateCenterX = (targetGate.X1 + targetGate.X2) / 2;
        var gateCenterY = (targetGate.Y1 + targetGate.Y2) / 2;
        var atGate = Math.Abs(playerPos.X - gateCenterX) <= inaccuracy
                     && Math.Abs(playerPos.Y - gateCenterY) <= inaccuracy;

        if (!atGate)
        {
            // Walk to the center of the gate first
            this._logger.LogDebug(
                "[ScriptExec]" + this._charTag + " travel_to_map: walking to gate at ({X},{Y}) on map {Map}",
                gateCenterX, gateCenterY, currentMap.Definition.Name);

            SuppressAutoNavigationForThisTick();

            var walkTarget = new Point((byte)gateCenterX, (byte)gateCenterY);
            if (!await TryWalkToAsync(walkTarget, currentMap).ConfigureAwait(false))
            {
                this._logger.LogTrace(
                    "[ScriptExec]" + this._charTag + " travel_to_map: could not walk to gate at ({X},{Y}) on map {Map}",
                    gateCenterX, gateCenterY, currentMap.Definition.Name);
            }

            return;
        }

        // At the gate — enter it
        this._logger.LogDebug(
            "[ScriptExec]" + this._charTag + " travel_to_map: entering gate to map {Target} (gate #{Num})",
            targetMapNumber.Value, targetGate.Number);

        try
        {
            var warpAction = new MUnique.OpenMU.GameLogic.PlayerActions.WarpGateAction();
            await warpAction.EnterGateAsync(this._player, targetGate).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex,
                "[ScriptExec]" + this._charTag + " travel_to_map: EnterGateAsync failed for gate #{Num} on map {Map}",
                targetGate.Number, currentMap.Definition.Name);
        }
    }

    /// <summary>
    /// Direct warp to a target map via GameAdapter.WarpToMapAsync.
    /// Reads targetMapNumber from node parameters.
    /// </summary>
    private async ValueTask WarpToMapAsync(ScriptParameters p)
    {
        if (p.TargetMapNumber is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " warp_to_map: no targetMapNumber parameter");
            return;
        }

        var target = p.TargetMapNumber.Value;
        var currentMap = this._context.GameAdapter.GetCurrentMap();
        if (currentMap?.Definition.Number == target)
        {
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " warp_to_map: already on target map {Map}", target);
            return;
        }

        this._logger.LogInformation("[ScriptExec]" + this._charTag + " warp_to_map: warping to map {Map}", target);
        var result = await this._context.GameAdapter.WarpToMapAsync(target).ConfigureAwait(false);
        if (result.Status == WarpStatusCode.Success || result.Status == WarpStatusCode.AlreadyOnTarget)
        {
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " warp_to_map: arrived at map {Map} (status={Status})", target, result.Status);
        }
        else
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " warp_to_map: failed to warp to map {Map}: {Reason} (status={Status})",
                target, result.Reason ?? "未知", result.Status);
        }
    }

    // ===== v2.0 Bot Feature Implementations =====

    /// <summary>
    /// Relocates to the next hotspot in the configured list.
    /// Resets patrol center to the hotspot position and walks there.
    /// Resets no-monster counter on arrival.
    /// Requires ScriptParameters.Hotspots to be configured.
    /// </summary>
    /// <returns>A <see cref="RelocateResult"/> with status code and detailed context (AR-21).</returns>
    private async ValueTask<RelocateResult> RelocateToHotspotAsync(ScriptParameters p)
    {
        var playerPos = this._context.GameAdapter.GetPlayerPosition();

        // Build hotspot list from parameters if not yet cached
        if (this._hotspotPoints.Count == 0 && p.Hotspots is { Count: > 0 })
        {
            foreach (var h in p.Hotspots)
            {
                this._hotspotPoints.Add(new Point(h.X, h.Y));
            }

            if (this._hotspotPoints.Count > 0)
            {
                this._logger.LogInformation(
                    "[ScriptExec]" + this._charTag + " Hotspots loaded: {Count} points, starting at index 0",
                    this._hotspotPoints.Count);
            }
        }

        if (this._hotspotPoints.Count == 0)
        {
            this._logger.LogTrace("[ScriptExec]" + this._charTag + " relocate_to_hotspot: no hotspots configured");
            return new RelocateResult(RelocateStatusCode.NoHotspots, -1, null, playerPos, 0, "no hotspots configured");
        }

        // === 跨地图 warp: 当前热点在另一地图时 warp 过去 ===
        if (p.Hotspots is { Count: > 0 })
        {
            // 获取当前要前往的热点索引（与下方逻辑保持一致）
            var targetIdx = this._pendingHotspotIndex >= 0 ? this._pendingHotspotIndex
                : (this._currentHotspotIndex + 1) % this._hotspotPoints.Count;
            if (targetIdx < p.Hotspots.Count)
            {
                var hd = p.Hotspots[targetIdx];
                if (hd.MapNumber != ushort.MaxValue)
                {
                    var currentMap = this._context.GameAdapter.GetCurrentMap();
                    if (currentMap is not null && currentMap.Definition.Number != hd.MapNumber)
                    {
                        this._logger.LogInformation(
                            "[ScriptExec]" + this._charTag + " Hotspot #{Idx} ({X},{Y}) on map #{Map}, current is #{CurMap} — warping...",
                            targetIdx, hd.X, hd.Y, hd.MapNumber, currentMap.Definition.Number);
                        // 使用完整命名空间前缀（WarpResult 是 record，没有 using）
                        var warpResult = await this._context.GameAdapter.WarpToMapAsync(hd.MapNumber).ConfigureAwait(false);
                        if (warpResult.Status == MUnique.OpenMU.AIPlayer.WarpStatusCode.Success || warpResult.Status == MUnique.OpenMU.AIPlayer.WarpStatusCode.AlreadyOnTarget)
                        {
                            // 重置状态以便在新地图重新开始
                            this._currentHotspotIndex = targetIdx;
                            this._pendingHotspotIndex = -1;
                            this._context.CurrentTarget = null;
                            this._noMonsterTickCount = 0;
                            this._patrolCenter = this._context.GameAdapter.GetPlayerPosition();
                            return new RelocateResult(RelocateStatusCode.Moving, this._currentHotspotIndex,
                                this._hotspotPoints.Count > 0 ? this._hotspotPoints[this._currentHotspotIndex] : null,
                                playerPos, 0, $"warped to map #{hd.MapNumber}");
                        }
                        else
                        {
                            this._logger.LogWarning("[ScriptExec]" + this._charTag + " Warp to map #{Map} failed: {Reason}", hd.MapNumber, warpResult.Reason);
                        }
                    }
                }
            }
        }

        // 已在行走中 — 不中断当前寻路，不推进索引
        if (this._player.IsWalking)
        {
            return new RelocateResult(RelocateStatusCode.Moving, this._currentHotspotIndex,
                this._currentHotspotIndex >= 0 && this._currentHotspotIndex < this._hotspotPoints.Count
                    ? this._hotspotPoints[this._currentHotspotIndex] : null,
                playerPos, 0, "already walking to hotspot");
        }

        var map = this._context.WorldState.CurrentMap;
        if (map is null)
        {
            return new RelocateResult(RelocateStatusCode.PathBlocked, this._currentHotspotIndex, null, playerPos, 0, "no map context");
        }

        // Death strategy escalation: adapt hotspot selection based on consecutive deaths.
        // Game design: strong monsters patrol hotspots intentionally to drive player progression.
        if (this._consecutiveDeaths >= 5)
        {
            this._logger.LogWarning(
                "[DeathStrategy] {Count} consecutive deaths — all hotspots too dangerous, considering map change",
                this._consecutiveDeaths);

            // Mark ALL hotspots as DangerZone for 15 minutes to force strategy change
            // AiMap not available — danger zone marking skipped.

            // Reset counter so we don't spam this every tick
            this._consecutiveDeaths = 0;
            this._forceThruDeathCount = 0;
        }
        else if (this._consecutiveDeaths >= 3)
        {
            this._logger.LogWarning(
                "[DeathStrategy] {Count} consecutive deaths — will cycle hotspot more aggressively",
                this._consecutiveDeaths);
        }

        // 防堆叠: 热点占用检测 — 尝试选择非占用的热点
        var otherPlayers = this._context.WorldState.OtherPlayersInRange;
        var occupiedRadius = p.HotspotOccupiedRadius;

        // 中断恢复: 上次行走被打断(战斗干扰)时重试同一热点
        // IMPORTANT: 如果 AlreadyThere 已经推进了索引，_pendingHotspotIndex 已被设为 -1
        if (this._pendingHotspotIndex >= 0)
        {
            this._currentHotspotIndex = this._pendingHotspotIndex;
            this._pendingHotspotIndex = -1;
        }
        else
        {
            // 正常: 推进到下一个热点(循环)
            this._currentHotspotIndex = (this._currentHotspotIndex + 1) % this._hotspotPoints.Count;
        }
        // 重置 pending 标记，防止被 advance 路径（PC1）推进后又因回退覆盖
        this._pendingHotspotIndex = -1;

        var checkedCount = 0;
        for (var attempt = 0; attempt < this._hotspotPoints.Count; attempt++)
        {
            checkedCount++;
            var candidate = this._hotspotPoints[this._currentHotspotIndex];

            // 检查该热点是否被其他非队友玩家占用
            var isOccupied = false;
            if (otherPlayers.Count > 0)
            {
                foreach (var other in otherPlayers)
                {
                    var otherDist = candidate.EuclideanDistanceTo(other.Position);
                    if (otherDist <= occupiedRadius)
                    {
                        isOccupied = true;
                        this._logger.LogDebug(
                            "[ScriptExec]" + this._charTag + " Hotspot #{Idx} ({X},{Y}) occupied by {Name} at dist={Dist:F1} — skipping",
                            this._currentHotspotIndex, candidate.X, candidate.Y,
                            other.Name, otherDist);
                        break;
                    }
                }
            }

            if (!isOccupied)
            {
                // 找到空闲热点，尝试前往
                var targetPoint = candidate;

                // 如果有矩形范围（非零），在矩形内随机选落脚点
                if (p.Hotspots is not null && this._currentHotspotIndex < p.Hotspots.Count)
                {
                    var hd = p.Hotspots[this._currentHotspotIndex];
                    if (hd.X1 != 0 || hd.X2 != 0 || hd.Y1 != 0 || hd.Y2 != 0)
                    {
                        var rx = Random.Shared.Next(hd.X1, hd.X2 + 1);
                        var ry = Random.Shared.Next(hd.Y1, hd.Y2 + 1);
                        var randomPt = new Point((byte)rx, (byte)ry);
                        // 如果随机点可走则用它，否则 fallback 到热点中心
                        if (map.Terrain.WalkMap[rx, ry])
                            targetPoint = randomPt;
                    }
                }

                // Check if already at or near the hotspot
                if (playerPos.EuclideanDistanceTo(targetPoint) <= 3f)
                {
                    // 即使到达热点也立即推进到下一个，这样 patrol 离开后触发 not_at_hotspot 就是新热点
                    // 避免死循环：巡逻→走远→被拉回暖点#0→巡逻→走远→又被拉回暖点#0
                    var nextIdx = (this._currentHotspotIndex + 1) % this._hotspotPoints.Count;
                    this._logger.LogInformation(
                        "[ScriptExec]" + this._charTag + " Already at hotspot #{Idx} ({X},{Y}) — immediately advancing to #{NextIdx}",
                        this._currentHotspotIndex, targetPoint.X, targetPoint.Y, nextIdx);
                    this._currentHotspotIndex = nextIdx;  // 立即推进
                    this._pendingHotspotIndex = -1;  // 清除中断恢复标记，避免下次被拉回旧热点
                    this._patrolCenter = targetPoint;
                    // _noMonsterTickCount 不重置：到热点了但没怪，保留递增计数让巡逻逻辑正常工作
                    return new RelocateResult(RelocateStatusCode.AlreadyThere, this._currentHotspotIndex,
                        targetPoint, playerPos, checkedCount, "already at hotspot, advanced to next");
                }

                this._logger.LogInformation(
                    "[ScriptExec]" + this._charTag + " Relocating to hotspot #{Idx} ({X},{Y}) \"{Name}\"",
                    this._currentHotspotIndex, targetPoint.X, targetPoint.Y,
                    p.Hotspots is not null && this._currentHotspotIndex < p.Hotspots.Count
                        ? p.Hotspots[this._currentHotspotIndex].Name ?? ""
                        : "");

                SuppressAutoNavigationForThisTick();

                if (await TryWalkToAsync(targetPoint, map).ConfigureAwait(false))
                {
                    this._pendingHotspotIndex = this._currentHotspotIndex; // 记录以便中断后重试
                    this._patrolCenter = targetPoint;
                    // 只在首次加载热点（_currentHotspotIndex == 0）时重置 noMonsterTickCount
                    // 热点间循环时保留计数器，让 no_monsters_recently 条件正常工作
                    if (this._currentHotspotIndex == 0 && this._hotspotPoints.Count > 1)
                        this._noMonsterTickCount = 0;
                    this._context.CurrentTarget = null;
                    this._forceThruDeathCount = 0; // 成功到达热点，重置死亡计数
                    this._forceThruActive = false;

                    return new RelocateResult(RelocateStatusCode.Moving, this._currentHotspotIndex,
                        targetPoint, this._context.GameAdapter.GetPlayerPosition(), checkedCount, "walking to hotspot");
                }

                return new RelocateResult(RelocateStatusCode.PathBlocked, this._currentHotspotIndex,
                    targetPoint, playerPos, checkedCount, "path not found to hotspot");
            }

            // 热点被占用 — 推进到下一个候选
            this._currentHotspotIndex = (this._currentHotspotIndex + 1) % this._hotspotPoints.Count;
        }

        // 所有热点均被占用，保持当前位置
        this._logger.LogInformation(
            "[ScriptExec]" + this._charTag + " All {Count} hotspots occupied by other players — staying at current position",
            this._hotspotPoints.Count);
        return new RelocateResult(RelocateStatusCode.AllOccupied, this._currentHotspotIndex,
            this._hotspotPoints.Count > 0 ? this._hotspotPoints[this._currentHotspotIndex % this._hotspotPoints.Count] : null,
            this._context.GameAdapter.GetPlayerPosition(), checkedCount, "all hotspots occupied");
    }

    /// <summary>
    /// Returns to the recorded death position after revival.
    /// Walks to _deathPosition and resets it after arrival.
    /// </summary>
    private async ValueTask ReturnToDeathSpotAsync(ScriptParameters p)
    {
        if (this._deathPosition.X == 0 && this._deathPosition.Y == 0)
        {
            this._logger.LogTrace("[ScriptExec]" + this._charTag + " return_to_death_spot: no death position recorded");
            return;
        }

        var map = this._context.WorldState.CurrentMap;
        if (map is null)
        {
            return;
        }

        var dist = this._context.GameAdapter.GetPlayerPosition().EuclideanDistanceTo(this._deathPosition);
        if (dist <= 5f)
        {
            this._logger.LogInformation(
                "[ScriptExec]" + this._charTag + " Returned to death spot ({X},{Y}) — resuming hunt",
                this._deathPosition.X, this._deathPosition.Y);
            this._patrolCenter = this._deathPosition;
            this._deathPosition = default;
            return;
        }

        this._logger.LogInformation(
            "[ScriptExec]" + this._charTag + " Returning to death spot ({X},{Y}) from ({PX},{PY}), dist={Dist:F1}",
            this._deathPosition.X, this._deathPosition.Y,
            this._context.GameAdapter.GetPlayerPosition().X, this._context.GameAdapter.GetPlayerPosition().Y, dist);

        SuppressAutoNavigationForThisTick();

        if (await TryWalkToAsync(this._deathPosition, map).ConfigureAwait(false))
        {
            this._patrolCenter = this._deathPosition;
        }
    }

    /// <summary>
    /// Warps to the hunt map using the routing engine (WarpPlanner).
    /// Uses gate walking first, falls back to warp menu with gold if needed.
    /// </summary>
    private async ValueTask WarpToHuntMapAsync(ScriptParameters p)
    {
        if (p.Hotspots is not { Count: > 0 } || p.Hotspots[0].MapNumber == ushort.MaxValue)
        {
            return;
        }

        var targetMap = p.Hotspots[0].MapNumber;
        var currentMap = this._context.GameAdapter.GetCurrentMap();
        if (currentMap is not null && currentMap.Definition.Number == targetMap)
        {
            this._logger.LogDebug("[ScriptExec]" + this._charTag + " warp_to_hunt_map: already on map #{Map}", targetMap);
            return;
        }

        // Use WarpPlanner routing engine
        var planner = this.GetWarpPlanner();
        var fromMap = currentMap?.Definition.Number ?? 0;
        var level = this._context.GameAdapter.GetPlayerLevel();
        var money = this._player.Money;

        var route = planner.ComputeRoute((short)fromMap, (short)targetMap, level, money);
        if (route is null)
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " warp_to_hunt_map: no route from map {From} to {To}", fromMap, targetMap);
            return;
        }

        if (route.IsFeasible)
        {
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " warp_to_hunt_map: found {StepCount}-hop route (gold={Gold}, maxLevel={Level})",
                route.Steps.Count, route.TotalGoldCost, route.HighestLevelRequirement);
            this._context.ActiveWarpRoute = route;
            this._context.ActiveWarpStepIndex = 0;
            this._context.WarpInProgress = false;
            await this.ExecuteNextWarpStepAsync().ConfigureAwait(false);
        }
        else
        {
            // Route exists but blocked by level/gold
            var blocker = route.Steps.FirstOrDefault(s => s.LevelRequirement > level);
            if (blocker is not null)
            {
                this._logger.LogWarning("[ScriptExec]" + this._charTag + " warp_to_hunt_map: blocked — level {Needed} required at step {From}->{To}",
                    blocker.LevelRequirement, blocker.FromMap, blocker.ToMap);
            }
            else
            {
                this._logger.LogWarning("[ScriptExec]" + this._charTag + " warp_to_hunt_map: blocked — need {Gold} gold, have {Have}",
                    route.TotalGoldCost, money);
            }
        }
    }

    private bool EvaluateNotOnHuntMap(ScriptParameters p)
    {
        // Condition: true if the first hotspot is on a different map than current
        if (p.Hotspots is not { Count: > 0 })
        {
            return false;
        }

        var hotspotMap = p.Hotspots[0].MapNumber;
        if (hotspotMap == ushort.MaxValue)
        {
            return false;
        }

        var currentMap = this._context.GameAdapter.GetCurrentMap();
        if (currentMap is null)
        {
            return true; // no map context → need warp
        }

        return currentMap.Definition.Number != hotspotMap;
    }

    /// <summary>
    /// Resets hotspot state (when new script loaded).
    /// </summary>
    private void ResetHotspotState()
    {
        this._hotspotPoints = new List<Point>();
        this._currentHotspotIndex = -1;
        this._pendingHotspotIndex = -1;
        this._noMonsterTickCount = 0;
    }

    /// <summary>
    /// Resets the death recovery state (used when a new script is loaded or player respawns normally).
    /// </summary>
    private void ResetDeathState()
    {
        this._deathPosition = default;
        this._justRevived = false;
        this.ResetIdleTimer(); // 复活 = 状态复位，清零熔断
    }

    /// <summary>
    /// 重置空闲熔断计数器。在有进展的节点执行成功后调用：
    /// - 成功击杀怪物
    /// - 成功接/交任务
    /// - 成功走到新位置
    /// - 成功找到新目标
    /// </summary>
    private void ResetIdleTimer()
    {
        if (this._idleTickCount > 0)
        {
            this._logger.LogDebug("[AntiIdle] Reset idle timer (was {Count} ticks)", this._idleTickCount);
        }
        this._idleTickCount = 0;
    }

    // ===== PC Architecture Helpers (AR-20) =====

    /// <summary>
    /// Returns the current node list (paragraph nodes or priority chain).
    /// </summary>
    private IReadOnlyList<PriorityNode> GetCurrentNodes()
    {
        if (this._paragraphTable.Count > 0 && this._script.Paragraphs is { Count: > 0 })
        {
            var paragraphs = this._script.Paragraphs;
            if (this._currentParagraphIndex < paragraphs.Count)
            {
                return paragraphs[this._currentParagraphIndex].Nodes;
            }
        }

        return this._script.PriorityChain;
    }

    /// <summary>
    /// Gets the current paragraph label, or null if in linear mode.
    /// </summary>
    private string? GetCurrentParagraphLabel()
    {
        if (this._paragraphTable.Count > 0 && this._script.Paragraphs is { Count: > 0 })
        {
            var paragraphs = this._script.Paragraphs;
            if (this._currentParagraphIndex < paragraphs.Count)
            {
                return paragraphs[this._currentParagraphIndex].Label;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the merged parameters for a given node.
    /// </summary>
    private ScriptParameters ResolveParametersForNode(PriorityNode node)
    {
        return this._nodeParams.GetValueOrDefault(node.Name) ?? this._script.Parameters;
    }

    /// <summary>
    /// Wraps the paragraph PC back to 0 and records path outcome.
    /// Called when PC reaches end of current paragraph's node array.
    /// </summary>
    private void WrapParagraph()
    {
        this._programCounter = 0;
        this._ticksAtCurrentPc = 0;
        this._logger.LogTrace("[ScriptExec]" + this._charTag + " Paragraph wrap: PC=0 label={Label}", GetCurrentParagraphLabel());
    }

    /// <summary>
    /// Processes a goto jump to a target paragraph label.
    /// Sets the paragraph index, resets PC and watchdog counter.
    /// </summary>
    private void ProcessGotoJump(string? targetLabel)
    {
        if (targetLabel is not null && this._paragraphTable.TryGetValue(targetLabel, out var targetIndex))
        {
            this._currentParagraphIndex = targetIndex;
            this._programCounter = 0;
            this._ticksAtCurrentPc = 0;
            this._gotoPending = null;
            this._logger.LogInformation("[ScriptExec]" + this._charTag + " Goto @{Label} → paragraph index {Idx}", targetLabel, targetIndex);
        }
        else
        {
            this._logger.LogWarning("[ScriptExec]" + this._charTag + " Goto target '@{Label}' not found", targetLabel ?? "null");
        }
    }

    /// <summary>
    /// Creates a TickResult from current execution state.
    /// </summary>
    private TickResult CreateTickResult(ScriptTaskState state, bool? conditionMet = null, bool pcAdvanced = true, NodeExecutionResult? nodeResult = null)
    {
        var nodes = GetCurrentNodes();
        return new TickResult(
            state,
            this.ScriptName,
            GetCurrentParagraphLabel(),
            this._programCounter,
            nodes.Count,
            this._currentNodeName,
            this._currentNodeAction,
            conditionMet ?? false,
            pcAdvanced,
            this._gotoPending,
            nodeResult ?? this._lastActionResult as NodeExecutionResult,
            this._ticksAtCurrentPc);
    }

    /// <summary>
    /// Executes a hard interrupt action (death/HP/MP — checked before PC fetch).
    /// Hard interrupts are CPU-level — they don't advance the PC.
    /// </summary>
    private async ValueTask ExecuteHardInterruptAsync(string action)
    {
        this._currentNodeName = $"interrupt:{action}";
        this._currentNodeAction = action;
        await ExecuteActionInternalAsync(action, this._currentNodeName, this._script.Parameters).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an external command from the instruction buffer.
    /// External commands are injected by AI decision layer or other systems
    /// and processed between PC instruction executions (FIFO order, max 5).
    /// </summary>
    private async ValueTask ExecuteExternalCommandAsync(string command)
    {
        this._logger.LogInformation("[ScriptExec]" + this._charTag + " External command: {Cmd}", command);
        this._currentNodeName = "external";
        this._currentNodeAction = command;
        switch (command)
        {
            case "stop":
                this._executionState = ScriptTaskState.Completed;
                break;
            case "reload":
                break;
            default:
                this._logger.LogWarning("[ScriptExec]" + this._charTag + " Unknown external command: {Cmd}", command);
                break;
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// 获取脚本的可读行。优先使用 dslSource，否则编译 paragraphs。
    /// </summary>
    private static string[] CompileScriptLines(BehaviorScript script)
    {
        if (!string.IsNullOrEmpty(script.DslSource))
        {
            return script.DslSource.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        var dsl = MuScriptCompiler.CompileToDsl(script);
        return dsl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Gets the shared WarpPlanner instance (lazy-init).
    /// </summary>
    private WarpPlanner? _warpPlanner;

    private WarpPlanner GetWarpPlanner()
    {
        if (_warpPlanner is null)
        {
            var config = this._player.GameContext?.Configuration;
            if (config is null)
            {
                throw new InvalidOperationException("GameConfiguration not available for WarpPlanner");
            }

            _warpPlanner = new WarpPlanner(config, this._logger);
        }

        return _warpPlanner;
    }

    /// <summary>
    /// Executes the next step of an active multi-hop warp route.
    /// Supports both gate walking (free) and warp menu (paid) methods.
    /// </summary>
    private async ValueTask ExecuteNextWarpStepAsync()
    {
        var route = this._context.ActiveWarpRoute;
        if (route is null || this._context.ActiveWarpStepIndex >= route.Steps.Count)
        {
            this._logger.LogWarning("[WarpRoute] No active route or step index out of range");
            return;
        }

        var step = route.Steps[this._context.ActiveWarpStepIndex];
        this._logger.LogInformation("[WarpRoute] Step {Idx}/{Total}: map {From}->{To} method={Method}",
            this._context.ActiveWarpStepIndex + 1, route.Steps.Count,
            step.FromMap, step.ToMap, step.Method);

        if (step.Method == WarpEdgeType.Gate)
        {
            this._logger.LogInformation("[WarpRoute] Executing gate step: pos=({PX},{PY}) on map #{Map}, gate=({GX},{GY})",
                this._context.GameAdapter.GetPlayerPosition().X,
                this._context.GameAdapter.GetPlayerPosition().Y,
                this._context.GameAdapter.GetCurrentMap()?.Definition.Number,
                step.GateCenter.X, step.GateCenter.Y);
            await this.ExecuteGateStepAsync(step).ConfigureAwait(false);
        }
        else
        {
            await this.ExecuteWarpMenuStepAsync(step).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Executes a gate walk step: walk to gate center, then enter via WarpGateAction.
    /// </summary>
    private async ValueTask ExecuteGateStepAsync(WarpStep step)
    {
        var currentMap = this._context.GameAdapter.GetCurrentMap();
        if (currentMap is null) return;

        var playerPos = this._context.GameAdapter.GetPlayerPosition();
        var inaccuracy = this._player.GameContext!.Configuration.InfoRange;

        var atGate = Math.Abs(playerPos.X - step.GateCenter.X) <= inaccuracy
                  && Math.Abs(playerPos.Y - step.GateCenter.Y) <= inaccuracy;

        if (!atGate)
        {
            // Walk to gate center — retry next tick
            SuppressAutoNavigationForThisTick();

            // Check if AI is walking (already in motion from previous tick's TryWalkToAsync)
            if (this._player.IsWalking)
            {
                return;
            }

            await TryWalkToAsync(step.GateCenter, currentMap).ConfigureAwait(false);
            return;
        }

        // At gate — enter it
        if (step.EnterGate is null)
        {
            this._logger.LogWarning("[WarpRoute] Gate step has no EnterGate reference");
            return;
        }

        this._logger.LogInformation("[WarpRoute] At gate #{Gate} — entering...", step.EnterGate.Number);
        try
        {
            var warpAction = new MUnique.OpenMU.GameLogic.PlayerActions.WarpGateAction();
            await warpAction.EnterGateAsync(this._player, step.EnterGate).ConfigureAwait(false);

            // WarpToAsync sets CurrentMap=null internally — wait for map change
            this._context.WarpInProgress = true;
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "[WarpRoute] Gate enter failed for #{Gate}", step.EnterGate.Number);
        }
    }

    /// <summary>
    /// Executes a warp menu step: use WarpAction to teleport (pays gold).
    /// </summary>
    private async ValueTask ExecuteWarpMenuStepAsync(WarpStep step)
    {
        if (step.WarpInfo is null)
        {
            this._logger.LogWarning("[WarpRoute] Warp menu step has no WarpInfo reference");
            return;
        }

        if (this._player.Money < step.GoldCost)
        {
            this._logger.LogWarning("[WarpRoute] Insufficient gold: have {Have}, need {Need}", this._player.Money, step.GoldCost);
            return;
        }

        this._logger.LogInformation("[WarpRoute] Using warp menu #{Warp} \"{Name}\" (cost={Gold})",
            step.WarpInfo.Index, step.WarpInfo.Name, step.GoldCost);
        try
        {
            var warpAction = new MUnique.OpenMU.GameLogic.PlayerActions.WarpAction();
            await warpAction.WarpToAsync(this._player, step.WarpInfo).ConfigureAwait(false);
            this._context.WarpInProgress = true;
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "[WarpRoute] Warp menu failed for #{Warp}", step.WarpInfo.Index);
        }
    }

    /// <summary>
    /// Checks warp progress in TickAsync: detect map change, advance to next step or complete.
    /// </summary>
    private async ValueTask CheckWarpProgressAsync()
    {
        if (!this._context.WarpInProgress)
        {
            return;
        }

        var route = this._context.ActiveWarpRoute;
        if (route is null || this._context.ActiveWarpStepIndex >= route.Steps.Count)
        {
            this._context.WarpInProgress = false;
            this._context.ActiveWarpRoute = null;
            return;
        }

        var currentMap = this._context.GameAdapter.GetCurrentMap();
        var currentStep = route.Steps[this._context.ActiveWarpStepIndex];

        // If CurrentMap is null, warp is in progress — check if we need to ClientReadyAfterMapChangeAsync
        if (currentMap is null)
        {
            this._logger.LogDebug("[WarpRoute] Waiting for map change — CurrentMap is null, calling ClientReadyAfterMapChangeAsync");

            // For AI players without real client, we call ClientReadyAfterMapChangeAsync
            // when CurrentMap is null after a warp. This completes the map change protocol.
            if (this._player is AiPlayer)
            {
                await this._player.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
                currentMap = this._context.GameAdapter.GetCurrentMap();
            }
        }

        if (currentMap is not null && currentMap.Definition.Number == currentStep.ToMap)
        {
            // Arrived at destination of this step
            this._context.ActiveWarpStepIndex++;
            this._context.WarpInProgress = false;

            if (this._context.ActiveWarpStepIndex >= route.Steps.Count)
            {
                // Route complete!
                this._logger.LogInformation("[WarpRoute] ✅ Arrived at destination map #{Map}", currentStep.ToMap);
                this._context.ActiveWarpRoute = null;
                this._context.ActiveWarpStepIndex = 0;
                this._noMonsterTickCount = 0;
                this._patrolCenter = this._context.GameAdapter.GetPlayerPosition();
                return;
            }

            // Execute next step
            await this.ExecuteNextWarpStepAsync().ConfigureAwait(false);
        }
        // else still waiting for map change
    }

    /// <summary>
    /// Checks whether a target map is reachable given current level/money.
    /// </summary>
    private WarpBlockReason CheckWarpBlocked(short targetMap)
    {
        var currentMap = this._context.GameAdapter.GetCurrentMap();
        var fromMap = currentMap?.Definition.Number ?? 0;
        var level = this._context.GameAdapter.GetPlayerLevel();
        var money = this._player.Money;

        var planner = this.GetWarpPlanner();
        var route = planner.ComputeRoute((short)fromMap, (short)targetMap, level, money);

        if (route is null)
        {
            return new WarpBlockReason { IsBlocked = true, Reason = "No gate route exists" };
        }

        if (route.IsFeasible)
        {
            return new WarpBlockReason { IsBlocked = false };
        }

        var levelBlocker = route.Steps.FirstOrDefault(s => s.LevelRequirement > level);
        if (levelBlocker is not null)
        {
            return new WarpBlockReason
            {
                IsBlocked = true,
                Reason = $"Level {levelBlocker.LevelRequirement} required",
                LowestMissingLevel = levelBlocker.LevelRequirement,
            };
        }

        if (route.TotalGoldCost > money)
        {
            return new WarpBlockReason
            {
                IsBlocked = true,
                Reason = $"Need {route.TotalGoldCost} gold, have {money}",
                GoldShortfall = route.TotalGoldCost - money,
            };
        }

        return new WarpBlockReason { IsBlocked = false };
    }
}