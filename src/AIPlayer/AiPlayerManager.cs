// <copyright file="AiPlayerManager.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.AIPlayer.Scripting;
using MUnique.OpenMU.AIPlayer.Decision;

/// <summary>
/// Manages the lifecycle of all AI player entities.
/// Implements <see cref="IAiService"/> and is registered as a singleton in DI.
/// </summary>
public sealed class AiPlayerManager : IAiService, IAiDebugService, IEventBroadcaster, IDisposable
{
    private readonly ConcurrentDictionary<Guid, AiPlayer> _activePlayers = new();
    private IGameContext? _gameContext;
    private readonly ILogger<AiPlayerManager> _logger;
    private int _nameCounter;

    /// <summary>
    /// 群体级事件活动广播器 — 检测 MiniGameDefinition 状态变更并分发到各 AI。
    /// 不依赖任何单个 AI 角色的生命周期。
    /// </summary>
    private EventWatcherService? _eventWatcher;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiPlayerManager"/> class.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    /// <param name="logger">The logger.</param>
    public AiPlayerManager(IGameContext gameContext, ILogger<AiPlayerManager> logger)
    {
        this._gameContext = gameContext;
        this._logger = logger;

        // Load the knowledge base from the embedded resource at startup.
        // Safe to call multiple times; subsequent calls are no-ops.
        KnowledgeLoader.Load();

        // 启动群体级事件活动广播器（依赖已就绪的 IGameContext）
        this._eventWatcher = new EventWatcherService(gameContext, this, logger);
    }

    /// <summary>
    /// Initializes a new instance with delayed game context resolution.
    /// Used when GameServerContainer hasn't fully started yet.
    /// EventWatcherService 会在 Context 首次可用时自动创建。
    /// </summary>
    public AiPlayerManager(IGameServerContextResolver contextResolver, ILogger<AiPlayerManager> logger)
    {
        var gameContext = contextResolver.ResolveContext();
        if (gameContext is null)
        {
            this._delayedContextResolver = contextResolver;
            this._logger = logger;
        }
        else
        {
            this._gameContext = gameContext;
            this._logger = logger;
        }
        KnowledgeLoader.Load();
    }

    private readonly IGameServerContextResolver? _delayedContextResolver;
    private IGameContext Context
    {
        get
        {
            if (this._gameContext is null && this._delayedContextResolver is not null)
            {
                var ctx = this._delayedContextResolver.ResolveContext();
                if (ctx is not null)
                {
                    this._gameContext = ctx;

                    // 延迟初始化的场景：Context 刚刚可用，启动事件广播器
                    if (this._eventWatcher is null)
                    {
                        this._eventWatcher = new EventWatcherService(ctx, this, this._logger);
                    }
                }
            }

            return this._gameContext ?? throw new InvalidOperationException("IGameContext not available yet.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<AiPlayerCreateResult> CreateAiPlayerAsync(AiPlayerCreateConfig config)
    {
        var gameContext = this.Context;
        if (gameContext is null)
        {
            return new AiPlayerCreateResult(false, null, "No game server context available. Server may still be starting.");
        }

        // Check if already active
        foreach (var kvp in this._activePlayers)
        {
            if (kvp.Value.SelectedCharacter?.Name == config.CharacterName)
            {
                return new AiPlayerCreateResult(false, null, $"Character '{config.CharacterName}' is already active as an AI player.");
            }
        }

        var playerId = Guid.NewGuid();
        var aiPlayer = new AiPlayer(gameContext)
        {
            AiPlayerId = playerId,
            ScriptPath = config.ScriptPath,
        };

        if (!this._activePlayers.TryAdd(playerId, aiPlayer))
        {
            await aiPlayer.DisposeAsync().ConfigureAwait(false);
            return new AiPlayerCreateResult(false, null, "Duplicate player ID. This should not happen.");
        }

        try
        {
            // Try loading existing character from database first,
            // so level/stats/equipment persist across sessions.
            Account? account = null;
            Character? character = null;
            var loadedFromDb = false;

            using (var loadContext = gameContext.PersistenceContextProvider.CreateNewPlayerContext(gameContext.Configuration))
            {
                account = await loadContext.GetAccountByCharacterNameAsync(config.CharacterName).ConfigureAwait(false);
                if (account is not null)
                {
                    character = account.Characters.FirstOrDefault(c => c.Name == config.CharacterName);
                    if (character is not null)
                    {
                        loadedFromDb = true;
                        this._logger.LogInformation("Loaded existing character '{Name}' from database.", config.CharacterName);
                    }
                }
            }

            if (!loadedFromDb)
            {
                // Double-check: character name may already exist in DB even though
                // GetAccountByCharacterNameAsync returned null (e.g. caching issues).
                // If so, try loading again with a fresh context.
                using (var checkContext = gameContext.PersistenceContextProvider.CreateNewPlayerContext(gameContext.Configuration))
                {
                    var existing = await checkContext.GetAccountByCharacterNameAsync(config.CharacterName).ConfigureAwait(false);
                    if (existing is not null)
                    {
                        // The character actually exists — try loading instead.
                        await aiPlayer.DisposeAsync().ConfigureAwait(false);
                        this._activePlayers.TryRemove(playerId, out _);
                        return await this.LoadAiPlayerAsync(config.CharacterName).ConfigureAwait(false);
                    }
                }

                (account, character) = this.CreatePlaceholderData(aiPlayer.PersistenceContext, gameContext, config);
                if (account is null || character is null)
                {
                    this._activePlayers.TryRemove(playerId, out _);
                    await aiPlayer.DisposeAsync().ConfigureAwait(false);
                    return new AiPlayerCreateResult(false, null, "Failed to create placeholder account/character.");
                }
            }

            if (!await aiPlayer.InitializeAsync(account!, character!, needsAttach: loadedFromDb).ConfigureAwait(false))
            {
                this._activePlayers.TryRemove(playerId, out _);
                return new AiPlayerCreateResult(false, null, "AI player initialization failed.");
            }

            // For new-placeholder accounts, immediately persist so EF Core's ChangeTracker
            // transitions from Added → Unchanged. This prevents the PeriodicSaveProgressPlugIn
            // from attempting a duplicate INSERT later when acceptChanges=false due to
            // _changeListener presence.
            if (!loadedFromDb)
            {
                try
                {
                    await aiPlayer.SaveProgressAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this._logger.LogWarning(ex, "Initial save failed for new AI player {Name}; it may still work with DB-loaded state.", config.CharacterName);
                }
            }

            this._logger.LogInformation(
                "AI player {PlayerId} {Mode}: {CharacterName} on map {MapId} at ({X}, {Y}).",
                playerId,
                loadedFromDb ? "loaded" : "created",
                config.CharacterName,
                config.MapId,
                aiPlayer.Position.X,
                aiPlayer.Position.Y);

            return new AiPlayerCreateResult(true, playerId, null);
        }
        catch (Exception ex)
        {
            this._activePlayers.TryRemove(playerId, out _);
            this._logger.LogError(ex, "Failed to create AI player.");
            return new AiPlayerCreateResult(false, null, $"Creation failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask<AiPlayerCreateResult> LoadAiPlayerAsync(string characterName)
    {
        var gameContext = this.Context;
        if (gameContext is null)
        {
            return new AiPlayerCreateResult(false, null, "No game server context available. Server may still be starting.");
        }

        // Load the account and character from the database
        Account? account;
        Character? character;
        using (var loadContext = gameContext.PersistenceContextProvider.CreateNewPlayerContext(gameContext.Configuration))
        {
            account = await loadContext.GetAccountByCharacterNameAsync(characterName).ConfigureAwait(false);
            if (account is null)
            {
                return new AiPlayerCreateResult(false, null, $"Character '{characterName}' not found in database.");
            }

            character = account.Characters.FirstOrDefault(c => c.Name == characterName);
            if (character is null)
            {
                return new AiPlayerCreateResult(false, null, $"Character '{characterName}' not found in account.");
            }
        }

        // Check if already active
        foreach (var kvp in this._activePlayers)
        {
            if (kvp.Value.SelectedCharacter?.Name == characterName)
            {
                return new AiPlayerCreateResult(false, null, $"Character '{characterName}' is already active as an AI player.");
            }
        }

        var playerId = Guid.NewGuid();
        var aiPlayer = new AiPlayer(gameContext)
        {
            AiPlayerId = playerId,
        };

        if (!this._activePlayers.TryAdd(playerId, aiPlayer))
        {
            await aiPlayer.DisposeAsync().ConfigureAwait(false);
            return new AiPlayerCreateResult(false, null, "Duplicate player ID. This should not happen.");
        }

        try
        {
            if (!await aiPlayer.InitializeAsync(account, character, needsAttach: true).ConfigureAwait(false))
            {
                this._activePlayers.TryRemove(playerId, out _);
                await aiPlayer.DisposeAsync().ConfigureAwait(false);
                return new AiPlayerCreateResult(false, null, "AI player initialization failed.");
            }

            this._logger.LogInformation(
                "AI player {PlayerId} loaded: {CharacterName} on map {Map} at ({X}, {Y}).",
                playerId,
                characterName,
                aiPlayer.CurrentMap?.Definition.Number,
                aiPlayer.Position.X,
                aiPlayer.Position.Y);

            return new AiPlayerCreateResult(true, playerId, null);
        }
        catch (Exception ex)
        {
            this._activePlayers.TryRemove(playerId, out _);
            await aiPlayer.DisposeAsync().ConfigureAwait(false);
            this._logger.LogError(ex, "Failed to load AI player.");
            return new AiPlayerCreateResult(false, null, $"Load failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> StopAiPlayerAsync(Guid playerId)
    {
        if (this._activePlayers.TryRemove(playerId, out var aiPlayer))
        {
            await aiPlayer.StopAsync().ConfigureAwait(false);
            await aiPlayer.DisposeAsync().ConfigureAwait(false);
            this._logger.LogInformation("AI player {PlayerId} stopped.", playerId);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public ValueTask<bool> SetAiPlayerTargetMapAsync(Guid playerId, ushort mapNumber)
    {
        if (this._activePlayers.TryGetValue(playerId, out var aiPlayer))
        {
            aiPlayer.SetTargetMap(mapNumber);
            this._logger.LogInformation("AI player {PlayerId} target map set to {MapNumber}.", playerId, mapNumber);
            return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(false);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyCollection<AiPlayerState>> GetAiPlayersAsync()
    {
        var snapshot = this._activePlayers.Values
            .Select(p => new AiPlayerState(
                p.AiPlayerId,
                p.SelectedCharacter?.Name ?? "Unknown",
                (ushort)(p.CurrentMap?.Definition.Number ?? 0),
                (uint)(p.Attributes?[Stats.CurrentHealth] ?? 0),
                (uint)(p.Attributes?[Stats.MaximumHealth] ?? 0),
                p.Level,
                p.StartTimestamp))
            .ToList()
            .AsReadOnly();

        return ValueTask.FromResult<IReadOnlyCollection<AiPlayerState>>(snapshot);
    }

    /// <inheritdoc />
    public ValueTask<AiPlayerState?> GetAiPlayerStateAsync(Guid playerId)
    {
        if (this._activePlayers.TryGetValue(playerId, out var p))
        {
            return ValueTask.FromResult<AiPlayerState?>(new AiPlayerState(
                p.AiPlayerId,
                p.SelectedCharacter?.Name ?? "Unknown",
                (ushort)(p.CurrentMap?.Definition.Number ?? 0),
                (uint)(p.Attributes?[Stats.CurrentHealth] ?? 0),
                (uint)(p.Attributes?[Stats.MaximumHealth] ?? 0),
                p.Level,
                p.StartTimestamp));
        }

        return ValueTask.FromResult<AiPlayerState?>(null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var kvp in this._activePlayers)
        {
            kvp.Value.StopAsync().AsTask().GetAwaiter().GetResult();
            kvp.Value.Dispose();
        }

        this._activePlayers.Clear();

        // 停止群体级事件广播器
        this._eventWatcher?.Dispose();
    }

    /// <summary>
    /// Returns a snapshot of all active AI players.
    /// Used by <see cref="Scripting.ScriptReloadBridge"/> to find players using a given script.
    /// </summary>
    public IEnumerable<AiPlayer> GetActivePlayers()
    {
        return this._activePlayers.Values.ToList();
    }

    // ==================== IEventBroadcaster Implementation ====================

    /// <inheritdoc />
    void IEventBroadcaster.OnEventOpen(MiniGameType type, int gameLevel, string name, int entranceFee)
    {
        var config = this._gameContext?.Configuration;
        if (config is null)
        {
            return;
        }

        // 查找 MiniGameDefinition 用于等级过滤
        var mgDef = config.MiniGameDefinitions
            .FirstOrDefault(d => d.Type == type && d.GameLevel == gameLevel);
        if (mgDef is null)
        {
            this._logger.LogWarning("[EventBroadcaster] 未找到 MiniGameDefinition: {Type} Lv.{Level}", type, gameLevel);
            return;
        }

        // 遍历所有活跃 AI，按等级过滤分发
        foreach (var kvp in this._activePlayers)
        {
            var aiPlayer = kvp.Value;
            var level = aiPlayer.Level;

            // 等级过滤
            if (level < mgDef.MinimumCharacterLevel)
            {
                continue;
            }

            if (mgDef.MaximumCharacterLevel > 0 && level > mgDef.MaximumCharacterLevel)
            {
                continue;
            }

            // 通过 HeartbeatService 分发事件
            var heartbeat = aiPlayer.Logic?.GetHeartbeat();
            if (heartbeat is IEventBroadcaster broadcaster)
            {
                broadcaster.OnEventOpen(type, gameLevel, name, entranceFee);
            }
        }
    }

    /// <inheritdoc />
    void IEventBroadcaster.OnEventReminder(MiniGameType type, int gameLevel, string name, int minutesLeft)
    {
        var config = this._gameContext?.Configuration;
        if (config is null)
        {
            return;
        }

        var mgDef = config.MiniGameDefinitions
            .FirstOrDefault(d => d.Type == type && d.GameLevel == gameLevel);
        if (mgDef is null)
        {
            return;
        }

        foreach (var kvp in this._activePlayers)
        {
            var aiPlayer = kvp.Value;
            var level = aiPlayer.Level;

            if (level < mgDef.MinimumCharacterLevel)
            {
                continue;
            }

            if (mgDef.MaximumCharacterLevel > 0 && level > mgDef.MaximumCharacterLevel)
            {
                continue;
            }

            var heartbeat = aiPlayer.Logic?.GetHeartbeat();
            if (heartbeat is IEventBroadcaster broadcaster2)
            {
                broadcaster2.OnEventReminder(type, gameLevel, name, minutesLeft);
            }
        }
    }

    /// <inheritdoc />
    void IEventBroadcaster.OnEventClosed(MiniGameType type, int gameLevel, string name)
    {
        var config = this._gameContext?.Configuration;
        if (config is null)
        {
            return;
        }

        var mgDef = config.MiniGameDefinitions
            .FirstOrDefault(d => d.Type == type && d.GameLevel == gameLevel);
        if (mgDef is null)
        {
            return;
        }

        foreach (var kvp in this._activePlayers)
        {
            var aiPlayer = kvp.Value;
            var level = aiPlayer.Level;

            if (level < mgDef.MinimumCharacterLevel)
            {
                continue;
            }

            if (mgDef.MaximumCharacterLevel > 0 && level > mgDef.MaximumCharacterLevel)
            {
                continue;
            }

            var heartbeat = aiPlayer.Logic?.GetHeartbeat();
            if (heartbeat is IEventBroadcaster broadcaster3)
            {
                broadcaster3.OnEventClosed(type, gameLevel, name);
            }
        }
    }

    // ==================== IAiDebugService Implementation ====================

    /// <inheritdoc />
    public AiPlayerDebugData? GetDebugData(Guid playerId)
    {
        if (!this._activePlayers.TryGetValue(playerId, out var p))
        {
            return null;
        }

        var snap = p.Logic?.Context.GetLatestSnapshot();
        return BuildDebugData(p, snap);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AiPlayerDebugData> GetAllDebugData()
    {
        var now = DateTime.UtcNow;
        return this._activePlayers.Values
            .Select(p => BuildDebugData(p, p.Logic?.Context.GetLatestSnapshot()))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async ValueTask<TickSnapshot?> StepOnceAsync(Guid playerId)
    {
        if (!this._activePlayers.TryGetValue(playerId, out var p))
        {
            return null;
        }

        if (p.Logic is null)
        {
            return null;
        }

        await p.Logic.TickOnceAsync().ConfigureAwait(false);
        return p.Logic.Context.GetLatestSnapshot();
    }

    /// <inheritdoc />
    public ValueTask SetHpAsync(Guid playerId, uint hp)
    {
        if (this._activePlayers.TryGetValue(playerId, out var p)
            && p.SelectedCharacter?.Attributes.FirstOrDefault(a => a.Definition == Stats.CurrentHealth) is { } attr)
        {
            attr.Value = hp;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SetMpAsync(Guid playerId, uint mp)
    {
        if (this._activePlayers.TryGetValue(playerId, out var p)
            && p.SelectedCharacter?.Attributes.FirstOrDefault(a => a.Definition == Stats.CurrentMana) is { } attr)
        {
            attr.Value = mp;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SetMoneyAsync(Guid playerId, uint money)
    {
        if (this._activePlayers.TryGetValue(playerId, out var p))
        {
            p.Money = (int)money;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SetLevelAsync(Guid playerId, int level)
    {
        if (!this._activePlayers.TryGetValue(playerId, out var p))
        {
            return ValueTask.CompletedTask;
        }

        if (p.SelectedCharacter is { } c)
        {
            var levelAttr = c.Attributes.FirstOrDefault(a => a.Definition == Stats.Level);
            if (levelAttr is not null)
            {
                var oldLevel = (int)levelAttr.Value;
                levelAttr.Value = level;
                if (level > oldLevel)
                {
                    c.LevelUpPoints += 5 * (level - oldLevel);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SetPositionAsync(Guid playerId, byte x, byte y)
    {
        if (this._activePlayers.TryGetValue(playerId, out var p)
            && p.SelectedCharacter is { } c)
        {
            c.PositionX = x;
            c.PositionY = y;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SetStatAttributeAsync(Guid playerId, Guid attributeDefinitionId, float value)
    {
        if (this._activePlayers.TryGetValue(playerId, out var p)
            && p.SelectedCharacter?.Attributes.FirstOrDefault(a => a.Definition?.Id == attributeDefinitionId) is { } attr)
        {
            attr.Value = value;
        }

        return ValueTask.CompletedTask;
    }

    private static AiPlayerDebugData BuildDebugData(AiPlayer p, TickSnapshot? snap)
    {
        var pos = snap?.Position ?? p.Position;
        dynamic? sm = p.StateMachine;
        var heartbeat = sm?.LastHeartbeat;

        // Build quest progress string
        string? questProgress = null;
        try
        {
            var adapter = p.Logic?.Context.GameAdapter;
            if (adapter is not null)
            {
                var activeQuests = adapter.GetActiveQuests();
                if (activeQuests.Count > 0)
                {
                    questProgress = string.Join(" | ",
                        activeQuests.Select(q =>
                        {
                            var kills = q.RequiredKills is { Count: > 0 }
                                ? string.Join(",", q.RequiredKills.Select(k => $"{k.Current}/{k.Required}"))
                                : "";
                            return $"G{q.Group}N{q.Number}" + (kills.Length > 0 ? $" [{kills}]" : "");
                        }));
                }
            }
        }
        catch
        {
            // ignore
        }

        // Determine execution mode
        // ScriptExecutor-driven → Mode1D; otherwise → Heartbeat/Decision (best-effort)
        var runMode = ExecutionMode.Mode1D;
        var executor = p.Logic?.GetScriptExecutor();
        if (executor is null)
        {
            runMode = ExecutionMode.Mode1D; // Decision/Heartbeat mode — map to Mode1D for debug
        }

        // Compute benchmark metrics from recent tick history
        double avgTickUs = 0;
        double p95TickUs = 0;
        double ticksPerSecond = 0;
        var tickHistory = p.Logic?.Context.TickHistory;
        if (tickHistory is { Count: > 0 })
        {
            var recentTicks = tickHistory
                .Skip(Math.Max(0, tickHistory.Count - 100))
                .Select(s => s.Timing.TotalUs)
                .OrderBy(v => v)
                .ToList();
            if (recentTicks.Count > 0)
            {
                var p95Index = (int)Math.Ceiling(0.95 * recentTicks.Count) - 1;
                p95TickUs = recentTicks[Math.Max(0, p95Index)];
                avgTickUs = recentTicks.Average();
                if (avgTickUs > 0)
                {
                    ticksPerSecond = 1_000_000.0 / avgTickUs;
                }
            }
        }

        // Build Dashboard todos JSON
        string? dashboardTodos = null;
        string? dashboardActiveModule = null;
        string? dashboardState = null;
        int dashboardEventQueueLength = 0;
        try
        {
            var missionBoard = p.Logic?.GetMissionBoard();
            if (missionBoard is not null)
            {
                var missions = missionBoard.BoardState.Missions;
                if (missions.Count > 0)
                {
                    var todoItems = missions.Select((t, idx) => new
                    {
                        idx = idx + 1,
                        title = t.Title,
                        priority = t.Priority,
                        type = t.Type.ToString(),
                        status = t.Status.ToString(),
                        module = t.Module,
                        group = t.QuestDef?.Group,
                        number = t.QuestDef?.Number,
                    });
                    dashboardTodos = System.Text.Json.JsonSerializer.Serialize(todoItems, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
                }

                var hb = p.Logic?.GetHeartbeat();
                if (hb is not null)
                {
                    dashboardActiveModule = hb.ActiveModule?.ModuleId;
                    dashboardState = hb.State;
                    dashboardEventQueueLength = hb.EventBus?.Count ?? 0;
                }
            }
        }
        catch
        {
            // ignore
        }

        return new AiPlayerDebugData(
            p.AiPlayerId,
            p.SelectedCharacter?.Name ?? "Unknown",
            (ushort)(p.CurrentMap?.Definition.Number ?? 0),
            snap?.Hp ?? (uint)(p.Attributes?[Stats.CurrentHealth] ?? 0),
            snap?.MaxHp ?? (uint)(p.Attributes?[Stats.MaximumHealth] ?? 0),
            snap?.Mp ?? (uint)(p.Attributes?[Stats.CurrentMana] ?? 0),
            snap?.MaxMp ?? (uint)(p.Attributes?[Stats.MaximumMana] ?? 0),
            snap?.Level ?? p.Level,
            p.StartTimestamp,
            (byte)pos.X,
            (byte)pos.Y,
            snap?.TickNumber ?? 0,
            snap?.SurvivalLevel ?? SurvivalLevel.Normal,
            snap?.HasTarget ?? false,
            snap?.EmergencyRetreat ?? false,
            snap?.Decisions,
            p.Logic?.CurrentBehavior,
            p.Logic?.ScriptName,
            p.Logic?.ScriptPosition,
            p.Logic?.ScriptLines,
            p.Logic?.ScriptLineNumber ?? 0,
            sm?.IsRunning ?? false,
            heartbeat?.CurrentItemLabel ?? sm?.CurrentItem?.Label,
            heartbeat?.CurrentItemStatus?.ToString() ?? sm?.CurrentItem?.Status?.ToString(),
            sm?.PendingItemCount ?? 0,
            sm?.CompletedItemCount ?? 0,
            sm?.FailedItemCount ?? 0,
            sm?.EventQueueCount ?? 0,
            sm?.TotalEventsProcessed ?? 0,
            heartbeat?.IsStuckDetected ?? false,
            questProgress,
            runMode,
            snap?.Timing.TotalUs ?? 0,
            snap?.Timing.WorldRefreshUs ?? 0,
            snap?.Timing.ScriptExecutionUs ?? 0,
            0,  // ModuleExecutionUs — removed in cleanup
            0,  // MindEngineUs — removed in cleanup
            avgTickUs,
            p95TickUs,
            ticksPerSecond,
            dashboardTodos,
            dashboardActiveModule,
            dashboardState,
            dashboardEventQueueLength);
    }

    private (Account? Account, Character? Character) CreatePlaceholderData(IPlayerContext persistenceContext, IGameContext gameContext, AiPlayerCreateConfig config)
    {
        var account = persistenceContext.CreateNew<Account>();
        if (account is null)
        {
            return (null, null);
        }

        var counter = Interlocked.Increment(ref this._nameCounter);
        account.LoginName = $"_{counter:X}{Guid.NewGuid():N}"[..10];
        account.State = AccountState.Normal;
        account.Vault = persistenceContext.CreateNew<ItemStorage>();
        account.Vault?.Items.Add(persistenceContext.CreateNew<Item>());

        var character = persistenceContext.CreateNew<Character>();
        if (character is null)
        {
            return (null, null);
        }

        character.Name = $"_{counter:X}{Guid.NewGuid():N}"[..10];
        character.CharacterClass = gameContext.Configuration.CharacterClasses
            .FirstOrDefault(c => c.Number == config.CharacterClassNumber)
            ?? gameContext.Configuration.CharacterClasses.FirstOrDefault();

        // Create inventory with potions so bots can heal
        var inventory = persistenceContext.CreateNew<ItemStorage>();
        character.Inventory = inventory;
        var potionDefs = gameContext.Configuration.Items
            .Where(i => i.Group == 14 && i.Number is >= 1 and <= 6)
            .ToDictionary(i => i.Number);
        byte slot = 0;
        foreach (var num in new byte[] { 1, 2, 3, 4, 5, 6 })
        {
            if (potionDefs.TryGetValue(num, out var def))
            {
                var potion = persistenceContext.CreateNew<Item>();
                potion.Definition = def;
                potion.ItemSlot = slot++;
                potion.Durability = 3; // stack of 3
                inventory.Items.Add(potion);
            }
        }

        // Add version-filtered non-master skills so the AI can use skills for combat
        foreach (var skill in gameContext.Configuration.Skills.Where(s => s.MasterDefinition is null))
        {
            if (!ContentVersionCatalog.IsSkillAvailable((ushort)skill.Number))
            {
                continue;
            }

            var skillEntry = persistenceContext.CreateNew<SkillEntry>();
            skillEntry.Skill = skill;
            skillEntry.Level = 0;
            character.LearnedSkills.Add(skillEntry);
        }

        if (character.CharacterClass?.StatAttributes is { } statDefs)
        {
            var attrs = statDefs
                .Select(a => persistenceContext.CreateNew<StatAttribute>(a.Attribute, a.BaseValue))
                .ToList();
            attrs.ForEach(character.Attributes.Add);
        }

        character.Experience = 0;
        character.MasterLevelUpPoints = 0;

        var pointsPerLevelUp = character.CharacterClass?.StatAttributes
            .FirstOrDefault(a => a.Attribute == Stats.PointsPerLevelUp)
            ?.BaseValue ?? 5;
        character.LevelUpPoints = (int)pointsPerLevelUp;

        var mapDefinition = gameContext.Configuration.Maps.FirstOrDefault(m => m.Number == config.MapId
                                                                              && ContentVersionCatalog.IsMapAvailable((ushort)m.Number))
                            ?? gameContext.Configuration.Maps.FirstOrDefault(m => m.Number == 0
                                                                              && ContentVersionCatalog.IsMapAvailable(0));
        character.CurrentMap = mapDefinition;

        if (mapDefinition is not null)
        {
            character.PositionX = 130;
            character.PositionY = 130;
        }

        // Initialize empty QuestStates so quest module can create new quest states.
        // Without this, GetActiveQuests returns empty and the quest script loops forever.
        if (character.QuestStates is { Count: 0 })
        {
            // QuestStates is initialized as empty collection by the Persistence context.
            // If it is null, we initialize it. This passes through to Persistence layer.
            try { _ = character.QuestStates?.Count; } catch { /* ignore */ }
        }

        account.PasswordHash = string.Empty;

        return (account, character);
    }
}
