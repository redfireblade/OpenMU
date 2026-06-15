// <copyright file="AiPlayerLogic.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using System.Diagnostics;
using System.Linq;
using System.Threading;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.AIPlayer.Scripting;

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

        // Create shared context
        this._context = new BehaviorContext(player)
        {
            GameAdapter = new GameAdapter(player),
        };

        // Load behavior execution engine: ScriptExecutor (1D) or Heartbeat (Decision)
        if (script is not null)
        {
            // 1D Script-driven mode: priority-chain executor replaces DAG modules
            this._scriptExecutor = new Scripting.ScriptExecutor(player, this._context, script, player.ScriptPath);
            player.Logger.LogInformation("[AiPlayerLogic] Script-driven mode: {ScriptId} v{Version}",
                script.Id, script.Version);
        }
        else if (script is null && player.SelectedCharacter is not null)
        {
            // Decision mode: Heartbeat + MissionBoard drives behavior autonomously
            var nativeExec = new NativeExecutionService(player, player.Logger);
            this._missionBoard = new Decision.MissionBoardService(player, this._adapter, player.Logger);
            this._heartbeat = new Decision.HeartbeatService(player, this._context, this._missionBoard, this._adapter, player.Logger);
            player.Logger.LogInformation("[AiPlayerLogic] Decision mode: Heartbeat-driven (no script)");
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

    /// <summary>
    /// Gets the MissionBoardService, or null if not in Decision mode.
    /// </summary>
    public Decision.MissionBoardService? GetMissionBoard() => this._missionBoard;

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

        if (this._adapter.IsPlayerWalking())
        {
            this.RecordEndOfTickSnapshot();
            return;
        }

        // ScriptExecutor mode: allow NpcDialogOpened so quest accept/submit can interact with NPC dialog
        // Decision mode (Heartbeat): allow all states, HeartbeatService handles state filtering internally
        if (this._player.PlayerState.CurrentState != GameLogic.PlayerState.EnteredWorld
            && !(this._scriptExecutor is not null && this._player.PlayerState.CurrentState == GameLogic.PlayerState.NpcDialogOpened)
            && this._heartbeat is null)
        {
            this.RecordEndOfTickSnapshot();
            return;
        }

        var map = this._adapter.GetCurrentMap();
        if (map is null)
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
                map.Definition.Number,
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

        this._context.WorldState = new WorldState
        {
            CurrentMap = map,
            PlayerPosition = this._adapter.GetPlayerPosition(),
            AttackablesInRange = map.GetAttackablesInRange(this._adapter.GetPlayerPosition(), SearchRange),
            DropsInRange = map.GetDropsInRange(this._adapter.GetPlayerPosition(), 10)
                .OfType<DroppedItem>()
                .Take(50)
                .ToList(),
            IsAtSafezone = false,
            OtherPlayersInRange = map.GetAttackablesInRange(this._adapter.GetPlayerPosition(), 100)
                .OfType<Player>()
                .Where(p => p != this._player && !IsInSameParty(p))
                .Take(20)
                .ToList(),
        };
        worldSw.Stop();
        timing.WorldRefreshUs = worldSw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;

        // 2. Execute behavior engine: ScriptExecutor (1D) or Heartbeat (Decision)
        if (this._scriptExecutor is not null)
        {
            // 1D Script-driven mode
            var scriptSw = Stopwatch.StartNew();
            var tickResult = await this._scriptExecutor.TickAsync().ConfigureAwait(false);
            scriptSw.Stop();
            timing.ScriptExecutionUs = scriptSw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
            this._context.RecordDecision("script", scriptSw.Elapsed);
            if (tickResult.State == MUnique.OpenMU.AIPlayer.ScriptTaskState.Stuck)
                this._player.Logger.LogWarning("[AiPlayer] Script watchdog: PC={Pc} stuck", tickResult.PC);
        }
        else if (this._heartbeat is not null)
        {
            // Decision mode: Heartbeat-driven (no script)
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

        // Stat allocation (every 5 seconds)
        if (this._lastStatTick.AddSeconds(5) < now)
        {
            this._lastStatTick = now;
            await this.AllocateStatsWithGatingAsync().ConfigureAwait(false);
        }

        // Record end-of-tick snapshot for debug state API.
        // Early-return paths (walking, not-entered-world, no-map) each
        // call this individually.  The main execution path reaches here.
        this.RecordEndOfTickSnapshot();
    }

    private async ValueTask AllocateStatsWithGatingAsync()
    {
        var character = this._player.SelectedCharacter;
        if (character is null || character.LevelUpPoints <= 0)
        {
            return;
        }

        var classNumber = character.CharacterClass?.Number ?? 0;
        var baseClass = StatAllocationStrategy.GetBaseClass(classNumber);

        if (!StatAllocationStrategy.ClassBuilds.TryGetValue(baseClass, out var phases))
        {
            return;
        }

        var level = this._adapter.GetPlayerLevel();

        // Find the ideal phase for the current level
        (int MinLevel, int MaxLevel, float Str, float Agi, float Vit, float Ene) phase = default;
        var phaseIndex = -1;
        for (var i = 0; i < phases.Length; i++)
        {
            var p = phases[i];
            if (level >= p.MinLevel && level <= p.MaxLevel)
            {
                phase = p;
                phaseIndex = i;
            }
        }

        if (phaseIndex < 0)
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

        var strPoints = (ushort)(toAllocate * phase.Str);
        var agiPoints = (ushort)(toAllocate * phase.Agi);
        var vitPoints = (ushort)(toAllocate * phase.Vit);
        var enePoints = (ushort)(toAllocate * phase.Ene);

        // Distribute remainder to the highest-weight stat
        var allocated = strPoints + agiPoints + vitPoints + enePoints;
        if (allocated < toAllocate)
        {
            var remaining = (ushort)(toAllocate - allocated);
            var maxWeight = Math.Max(phase.Str, Math.Max(phase.Agi, Math.Max(phase.Vit, phase.Ene)));
            if (maxWeight == phase.Str) { strPoints += remaining; }
            else if (maxWeight == phase.Agi) { agiPoints += remaining; }
            else if (maxWeight == phase.Vit) { vitPoints += remaining; }
            else { enePoints += remaining; }
        }

        var increaseStats = new MUnique.OpenMU.GameLogic.PlayerActions.Character.IncreaseStatsAction();
        if (strPoints > 0) { await increaseStats.IncreaseStatsAsync(this._player, MUnique.OpenMU.GameLogic.Attributes.Stats.BaseStrength, strPoints).ConfigureAwait(false); }

        if (agiPoints > 0) { await increaseStats.IncreaseStatsAsync(this._player, MUnique.OpenMU.GameLogic.Attributes.Stats.BaseAgility, agiPoints).ConfigureAwait(false); }

        if (vitPoints > 0) { await increaseStats.IncreaseStatsAsync(this._player, MUnique.OpenMU.GameLogic.Attributes.Stats.BaseVitality, vitPoints).ConfigureAwait(false); }

        if (enePoints > 0) { await increaseStats.IncreaseStatsAsync(this._player, MUnique.OpenMU.GameLogic.Attributes.Stats.BaseEnergy, enePoints).ConfigureAwait(false); }
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
    /// Gets a recommended hunting map based on the player's experience.
    /// Simplified to return the current map (hotspot-based recommendations
    /// have been removed in the cleanup).
    /// </summary>
    /// <param name="topN">Ignored in simplified version.</param>
    /// <returns>The current map number with a default score of 0.</returns>
    public (ushort MapNumber, double Score) GetRecommendedMap(int topN = 3)
    {
        var currentMapNum = (ushort)(this._adapter.GetCurrentMap()?.Definition.Number ?? 0);
        return (currentMapNum, 0.0);
    }

}
