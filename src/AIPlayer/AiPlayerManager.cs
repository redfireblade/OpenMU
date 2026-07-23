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
using System.IO;
using System.Text.Json;
using MUnique.OpenMU.AIPlayer.Scripting;
using OAPS.Evolution;

/// <summary>
/// Manages the lifecycle of all AI player entities.
/// Implements <see cref="IAiService"/> and is registered as a singleton in DI.
/// </summary>
public sealed class AiPlayerManager : IAiService, IAiDebugService, IEventBroadcaster, IDisposable
{
    /// <inheritdoc/>
    public GameConfiguration? GameConfiguration => this._gameContext?.Configuration;

    private readonly ConcurrentDictionary<Guid, AiPlayer> _activePlayers = new();
    private IGameContext? _gameContext;
    private readonly ILogger<AiPlayerManager> _logger;
    private int _nameCounter;

    /// <summary>
    /// 群体级事件活动广播器 — 检测 MiniGameDefinition 状态变更并分发到各 AI。
    /// 不依赖任何单个 AI 角色的生命周期。
    /// </summary>
    private EventWatcherService? _eventWatcher;

    /// <summary>OAPS 旁观者系统 — 包装 PBO 采集行为事件。</summary>
    private OapsObserver? _oapsObserver;

    /// <summary>AI 角色管理器（Phase 0 新增）。</summary>
    private AiHost? _aiHost;

    /// <summary>AI 角色定时器 — 驱动 AI Tick。</summary>
    private Timer? _aiTimer;

    /// <summary>OAPS 知识桥接器 — 学习闭环协调。</summary>
    private OapsKnowledgeBridge? _knowledgeBridge;

    /// <summary>全局规则引擎 — AI 角色决策系统的共享规则库。</summary>
    /// 旁观者系统学习的经验规则注入到此引擎，所有 AI 角色共享。
    /// 各 AI 角色的 HeartbeatService 从此引擎读取规则驱动行为。
    /// 这是"观察者学习→决策系统执行"解耦架构的核心桥梁。</summary>
    private Decision.RuleEngine? _sharedRuleEngine;

    /// <summary>学习同步定时器 — 每 60 秒调用一次 SyncAll。</summary>
    private Timer? _syncTimer;

    /// <summary>群体进化引擎。</summary>
    private SwarmEvolution? _swarmEvolution;

    /// <summary>进化定时器 — 每 5 分钟。</summary>
    private Timer? _evolutionTimer;

    /// <summary>Fugu v4.0 共享记忆层（全局单例，跨所有 AI 角色）。</summary>
    private Knowledge.SharedMemoryLayer? _sharedMemory;

    /// <summary>Fugu v4.0 CMA-ES 群体进化服务（每 5 分钟自动进化）。</summary>
    private OAPS.Evolution.FuguEvolutionService? _fuguEvolution;

    /// <summary>Fugu v4.0 群体编排器（全局事件→子任务分配）。</summary>
    private OAPS.Evolution.SwarmOrchestrator? _swarmOrchestrator;

    /// <summary>Fugu v4.0 群体 Rewards 聚合器。</summary>
    private OAPS.Evolution.SwarmRewardAggregator? _rewardAggregator;

    /// <summary>Fugu v4.0 SFT 训练器 — 离线训练 SoftRouter 权重。</summary>
    private OAPS.Evolution.FuguSftTrainer? _fuguSftTrainer;

    /// <summary>SFT 训练定时器 — 每 10 分钟。</summary>
    private Timer? _sftTrainingTimer;

    /// <summary>Fugu v4.0 冷启动引导器 — 从领域知识生成初始 SFT 权重。</summary>
    private OAPS.Evolution.FuguColdStartBootstrapper? _fuguBootstrapper;

    /// <summary>Fugu v4.0 五列看板 — 监视所有 AI 任务状态(TODO/IN_PROG/REVIEW/DONE/BLOCKED)。</summary>
    private Decision.FuguKanbanBoard? _kanbanBoard;

    /// <summary>Fugu v4.0 Worker 可用性追踪 — 死亡/断线检测 + 动态聚合器选择。</summary>
    private OAPS.Evolution.WorkerAvailabilityService? _workerAvailability;

    /// <summary>行为事件存储 — 持久化玩家离散行为。</summary>
    private BehaviorEventStore? _behaviorEventStore;

    /// <summary>玩家行为观察器 — 分析真实玩家行为模式。</summary>
    private PlayerBehaviorObserver? _pbo;

    /// <summary>群体影子地图 — 跨角色知识融合。</summary>
    private GlobalShadowMap? _globalShadowMap;

    /// <summary>集体经验记忆 — 全服打怪/金币/死亡统计。</summary>
    private ExperienceMemory? _collectiveExpMem;

    /// <summary>OAPS 懒初始化锁。</summary>
    private readonly object _oapsLock = new();

    /// <summary>PBO 观察循环取消令牌。</summary>
    private CancellationTokenSource? _pboCts;

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

        // 创建 AI 角色系统（Phase 0）
        this.InitializeAiSystem();
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

        // 创建 AI 角色系统（Phase 0）— 延迟初始化，等待 Context 就绪
        this.InitializeAiSystem();
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

                    // OAPS 学习系统自动初始化
                    this.EnsureOapsInitialized();

                    // Phase 0: AI 角色系统初始化（延迟初始化场景）
                    // InitializeAiSystem 通过延时 Task 自动触发，此处不再重复调用
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

            // Initialize Fugu v4.0 + AiCollector before AI logic starts
            aiPlayer.SharedMemory = _sharedMemory;
            aiPlayer.FuguKanban = _kanbanBoard;

            // F11: Access Control List — agent isolation per Fugu design
            aiPlayer.Acl = new Knowledge.AccessControlList(config.CharacterName ?? playerId.ToString("N"));

            aiPlayer.BehaviorEventStore = _behaviorEventStore;
            aiPlayer.SftWeightsPath = Path.Combine(AppContext.BaseDirectory, "scripts", "learned", "fugu_sft_weights_bootstrap.bin");

            // 注意：不自动加载学习脚本到 ScriptPath。
            // 学习脚本是旁观者系统(OAPS)的输出记录，AI角色始终由决策系统(HeartbeatService+RuleEngine)驱动。
            // 旁观者系统通过 RuleEngine.AddLearnedRules() 注入经验规则，而非替换 AI 决策模式。
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

            // F7: Register with CMA-ES evolution service
            if (_fuguEvolution is not null && aiPlayer.Logic?.FuguOrchestrator is not null)
            {
                _fuguEvolution.RegisterOrchestrator(aiPlayer.Logic.FuguOrchestrator);
            }

            // F10: Register worker with SwarmOrchestrator → Kanban task assignment
            var workerId = config.CharacterName ?? playerId.ToString("N")[..8];
            _swarmOrchestrator?.RegisterWorker(workerId, _kanbanBoard!);

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
            // Initialize Fugu v4.0 + AiCollector before AI logic starts
            aiPlayer.SharedMemory = _sharedMemory;
            aiPlayer.FuguKanban = _kanbanBoard;

            // F11: Access Control List — agent isolation per Fugu design
            aiPlayer.Acl = new Knowledge.AccessControlList(characterName ?? playerId.ToString("N"));

            aiPlayer.BehaviorEventStore = _behaviorEventStore;
            aiPlayer.SftWeightsPath = Path.Combine(AppContext.BaseDirectory, "scripts", "learned", "fugu_sft_weights_bootstrap.bin");

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

            // F7: Register with CMA-ES
            if (_fuguEvolution is not null && aiPlayer.Logic?.FuguOrchestrator is not null)
            {
                _fuguEvolution.RegisterOrchestrator(aiPlayer.Logic.FuguOrchestrator);
            }

            // F10: SwarmOrchestrator worker registration
            _swarmOrchestrator?.RegisterWorker(characterName ?? "unknown", _kanbanBoard!);

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

        // 停止 PBO 观察循环
        try { _pboCts?.Cancel(); } catch { }
        _pboCts?.Dispose();

        // Phase 0: AI 角色系统清理
        this._aiTimer?.Dispose();
        this._aiHost?.Dispose();
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
        byte slot = DataModel.InventoryConstants.EquippableSlotsCount; // 12 = first inv slot after equipment
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
            // Try SpawnGate first, then any ExitGate, then any gate
            var gate = mapDefinition.ExitGates?.Where(g => g.IsSpawnGate).SelectRandom()
                       ?? mapDefinition.ExitGates?.SelectRandom();
            if (gate is not null)
            {
                byte spawnX = (byte)Random.Shared.Next(gate.X1, gate.X2 + 1);
                byte spawnY = (byte)Random.Shared.Next(gate.Y1, gate.Y2 + 1);

                // 地形校验：确保出生坐标可行走
                var terrainData = gate.Map?.TerrainData ?? mapDefinition.TerrainData;
                if (terrainData is { Length: >= 65539 })
                {
                    bool IsWalkable(int px, int py)
                    {
                        int idx = 3 + px * 256 + py;
                        return idx < terrainData.Length && terrainData[idx] != 0xFF && (terrainData[idx] & 0x54) == 0;
                    }

                    bool found = IsWalkable(spawnX, spawnY);
                    for (int attempt = 0; attempt < 10 && !found; attempt++)
                    {
                        spawnX = (byte)Random.Shared.Next(gate.X1, gate.X2 + 1);
                        spawnY = (byte)Random.Shared.Next(gate.Y1, gate.Y2 + 1);
                        found = IsWalkable(spawnX, spawnY);
                    }

                    if (!found)
                    {
                        for (int radius = 1; radius <= 3 && !found; radius++)
                        {
                            for (int dx = -radius; dx <= radius && !found; dx++)
                            for (int dy = -radius; dy <= radius && !found; dy++)
                            {
                                int tx = spawnX + dx, ty = spawnY + dy;
                                if (tx >= gate.X1 && tx <= gate.X2
                                    && ty >= gate.Y1 && ty <= gate.Y2
                                    && IsWalkable(tx, ty))
                                { spawnX = (byte)tx; spawnY = (byte)ty; found = true; }
                            }
                        }
                    }

                    if (!found)
                    {
                        for (int radius = 4; radius <= 30 && !found; radius++)
                        {
                            for (int dx = -radius; dx <= radius && !found; dx++)
                            for (int dy = -radius; dy <= radius && !found; dy++)
                            {
                                int tx = spawnX + dx, ty = spawnY + dy;
                                if (tx >= 0 && tx < 256 && ty >= 0 && ty < 256 && IsWalkable(tx, ty))
                                { spawnX = (byte)tx; spawnY = (byte)ty; found = true; }
                            }
                        }
                    }
                }

                character.PositionX = spawnX;
                character.PositionY = spawnY;
            }
            else
            {
                character.PositionX = 130;
                character.PositionY = 130;
            }
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

    // ==================== OAPS Bridge Members ====================

    /// <summary>
    /// Ensures OAPS components are initialized (lazy, once).
    /// </summary>
    private void EnsureOapsInitialized()
    {
        if (_pbo is not null) return;
        lock (_oapsLock)
        {
            if (_pbo is not null) return;
            try
            {
                var ctx = Context;
                var logger = _logger;
                var dataDir = Path.Combine(AppContext.BaseDirectory, "scripts", "learned");
                Directory.CreateDirectory(dataDir);

                _collectiveExpMem = new ExperienceMemory
                {
                    CharacterName = "collective",
                    LastUpdated = DateTime.UtcNow,
                };

                _behaviorEventStore = new BehaviorEventStore(dataDir, logger);
                _pbo = new PlayerBehaviorObserver(ctx, _collectiveExpMem, logger);
                _pbo.SetEventStore(_behaviorEventStore);

                _oapsObserver = new OapsObserver(_pbo, _behaviorEventStore, logger);
                _globalShadowMap = new GlobalShadowMap(dataDir, logger);

                // 创建全局共享规则引擎 — 所有 AI 角色的决策系统从这里读取规则
                // 旁观者系统学习的经验规则也注入到此引擎（解耦架构：观察者不直接控制AI）
                // 注意：ScriptLibrary 需要 AiPlayer 实例才能在运行时解析脚本ID，
                // 在 AiPlayerManager 层面用 null 创建共享 RuleEngine，仅用于接收学习规则。
                // 各 AI 角色自己的 HeartbeatService 仍创建独立的 RuleEngine 用于实际决策。
                _sharedRuleEngine = new Decision.RuleEngine(logger);

                _knowledgeBridge = new OapsKnowledgeBridge(_behaviorEventStore, _sharedRuleEngine, logger);

                // Start PBO tick loop (~4s) — scan real players, collect behavior data
                _pboCts = new CancellationTokenSource();
                var ct = _pboCts.Token;
                _ = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            _oapsObserver?.Observe();
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "[OAPS] PBO Observe error");
                        }

                        await Task.Delay(4000, ct).ConfigureAwait(false);
                    }
                }, ct);

                // Start sync timer (60s) — SyncLearning for each observed player
                _syncTimer = new Timer(_ =>
                {
                    try { _knowledgeBridge?.SyncAll(); }
                    catch (Exception ex) { logger.LogWarning(ex, "[OAPS] SyncAll error"); }

                    // 学习规则写入 rules_experience.json（规则库文件）
                    // AI 角色通过 RuleWatcherService 监听文件变化自主加载
                    // 两个系统完全解耦，仅通过文件库通信
                }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

                // 学习脚本文件监控 — 当 OapsKnowledgeBridge 写出新的 learned_{Name}.json 时，
                // 自动热加载到对应的 AI 玩家
                try
                {
                    var learnedDir = Path.Combine(AppContext.BaseDirectory, "scripts", "learned");
                    Directory.CreateDirectory(learnedDir);
                    var watcher = new FileSystemWatcher(learnedDir, "learned_*.json")
                    {
                        EnableRaisingEvents = true,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    };
                    void ReloadScriptForPlayer(string fullPath, string fileName)
                    {
                        try
                        {
                            // learned_fashi01.json → "fashi01"
                            var nameOnly = Path.GetFileNameWithoutExtension(fileName);
                            var charName = nameOnly?.StartsWith("learned_") == true ? nameOnly["learned_".Length..] : null;
                            if (charName is null || charName.Length == 0) return;

                            // Wait for file write to complete
                            for (int retry = 0; retry < 5; retry++)
                            {
                                try { using var fs = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read); break; }
                                catch { Thread.Sleep(200); }
                            }

                            // 找到对应的 AI 玩家并热加载脚本
                            foreach (var kvp in _activePlayers)
                            {
                                if (kvp.Value.SelectedCharacter?.Name == charName && !kvp.Value.IsDisposed)
                                {
                                    var script = Scripting.ScriptExecutor.LoadFromFile(fullPath);
                                    if (script is not null)
                                    {
                                        kvp.Value.Logic?.ReloadScript(script);
                                        logger.LogInformation("[OAPS] Hot-reloaded script for {Name}", charName);
                                    }
                                    break;
                                }
                            }
                        }
                        catch (Exception ex) { logger.LogWarning(ex, "[OAPS] Script watcher error"); }
                    }

                    watcher.Changed += (_, e) => { if (e.Name is not null) ReloadScriptForPlayer(e.FullPath, e.Name); };
                    watcher.Created += (_, e) => { if (e.Name is not null) ReloadScriptForPlayer(e.FullPath, e.Name); };
                    logger.LogInformation("[OAPS] Script watcher active on {Dir}", learnedDir);
                }
                catch (Exception ex) { logger.LogWarning(ex, "[OAPS] Failed to start script watcher"); }

                logger.LogInformation("[OAPS] OAPS learning system initialized");

                // ═══ Fugu v4.0 Component Initialization ═══
                try
                {
                    _sharedMemory = new Knowledge.SharedMemoryLayer(
                        _logger as Microsoft.Extensions.Logging.ILogger<Knowledge.SharedMemoryLayer>
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Knowledge.SharedMemoryLayer>.Instance);

                    _fuguEvolution = new OAPS.Evolution.FuguEvolutionService(
                        _logger as Microsoft.Extensions.Logging.ILogger<OAPS.Evolution.FuguEvolutionService>
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OAPS.Evolution.FuguEvolutionService>.Instance);

                    _swarmOrchestrator = new OAPS.Evolution.SwarmOrchestrator(
                        _sharedMemory,
                        _logger as Microsoft.Extensions.Logging.ILogger<OAPS.Evolution.SwarmOrchestrator>
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OAPS.Evolution.SwarmOrchestrator>.Instance);

                    _rewardAggregator = new OAPS.Evolution.SwarmRewardAggregator(
                        _sharedMemory,
                        _logger as Microsoft.Extensions.Logging.ILogger<OAPS.Evolution.SwarmRewardAggregator>
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OAPS.Evolution.SwarmRewardAggregator>.Instance);

                    _fuguEvolution.Start();
                    _swarmOrchestrator.Start();
                    _rewardAggregator.Start();

                    // ═══ Fugu v4.0 五列看板 — REVIEW列体现 reward 反馈机制 ═══
                    _kanbanBoard = new Decision.FuguKanbanBoard(
                        _logger as Microsoft.Extensions.Logging.ILogger<Decision.FuguKanbanBoard>
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Decision.FuguKanbanBoard>.Instance);
                    _rewardAggregator.SetKanban(_kanbanBoard);

                    logger.LogInformation("[OAPS-v4] Fugu 组件已启动: SharedMemory + FuguEvolution + SwarmOrchestrator + RewardAggregator + KanbanBoard");

                    // Initialize SFT trainer
                    var softBucketStore = new OAPS.Evolution.SoftBucketStore(
                        _logger as Microsoft.Extensions.Logging.ILogger
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                    _fuguSftTrainer = new OAPS.Evolution.FuguSftTrainer(
                        _behaviorEventStore!, softBucketStore,
                        _logger as Microsoft.Extensions.Logging.ILogger<OAPS.Evolution.FuguSftTrainer>
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OAPS.Evolution.FuguSftTrainer>.Instance);

                    // SFT training timer: every 10 minutes
                    _sftTrainingTimer = new Timer(_ =>
                    {
                        try
                        {
                            if (_fuguSftTrainer.TrainIfNeeded())
                            {
                                // Push trained weights to all running orchestrators
                                foreach (var (_, aiPlayer) in _activePlayers)
                                {
                                    if (aiPlayer.Logic?.FuguOrchestrator is { } orch)
                                    {
                                        _fuguSftTrainer.LoadIntoRouter(orch.Router);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[FuguSFT] Periodic training error");
                        }
                    }, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

                    // Bootstrapper: generate non-random initial SFT weights
                    _fuguBootstrapper = new OAPS.Evolution.FuguColdStartBootstrapper(
                        _logger as Microsoft.Extensions.Logging.ILogger<OAPS.Evolution.FuguColdStartBootstrapper>
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OAPS.Evolution.FuguColdStartBootstrapper>.Instance);
                    var bootWeights = _fuguBootstrapper.BootstrapWeights();

                    // Worker availability tracker
                    _workerAvailability = new OAPS.Evolution.WorkerAvailabilityService(
                        _logger as Microsoft.Extensions.Logging.ILogger<OAPS.Evolution.WorkerAvailabilityService>
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OAPS.Evolution.WorkerAvailabilityService>.Instance);

                    logger.LogInformation("[Fugu] v4.0 components initialized: SharedMemory, Evolution, Orchestrator, Rewards, SFT, Bootstrap, Workers");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Fugu] Failed to initialize v4.0 components (non-critical)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[OAPS] Failed to initialize OAPS components");
            }
        }
    }

    /// <summary>Gets the Fugu SharedMemoryLayer (null if not initialized).</summary>
    public Knowledge.SharedMemoryLayer? SharedMemory => _sharedMemory;

    /// <summary>Gets the FuguEvolutionService (null if not initialized).</summary>
    public OAPS.Evolution.FuguEvolutionService? FuguEvolution => _fuguEvolution;

    /// <summary>Gets the SwarmOrchestrator (null if not initialized).</summary>
    public OAPS.Evolution.SwarmOrchestrator? SwarmOrchestrator => _swarmOrchestrator;

    /// <summary>Gets the SwarmRewardAggregator (null if not initialized).</summary>
    public OAPS.Evolution.SwarmRewardAggregator? RewardAggregator => _rewardAggregator;

    /// <summary>Gets all behavior records from PBO (observed player patterns).</summary>
    public IReadOnlyDictionary<string, PlayerBehaviorObserver.BehaviorRecord> AllBehaviorRecords
    {
        get
        {
            EnsureOapsInitialized();
            return _pbo?.AllBehaviorRecords ?? new Dictionary<string, PlayerBehaviorObserver.BehaviorRecord>(0);
        }
    }

    /// <summary>Gets the collective experience memory.</summary>
    public ExperienceMemory? CollectiveExpMem
    {
        get
        {
            EnsureOapsInitialized();
            return _collectiveExpMem;
        }
    }

    /// <summary>Gets the global shadow map.</summary>
    public GlobalShadowMap? GlobalShadowMap
    {
        get
        {
            EnsureOapsInitialized();
            return _globalShadowMap;
        }
    }

    /// <summary>Gets NPC interaction records for the specified player.</summary>
    public IReadOnlyList<PlayerBehaviorObserver.NpcInteractionRecord> GetNpcInteractions(string playerName)
    {
        EnsureOapsInitialized();
        return _pbo?.GetNpcInteractions(playerName) ?? Array.Empty<PlayerBehaviorObserver.NpcInteractionRecord>();
    }

    /// <summary>Exports all behavior records as summaries.</summary>
    public List<PlayerBehaviorObserver.BehaviorRecordExport> ExportBehaviorRecords()
    {
        EnsureOapsInitialized();
        return _pbo?.ExportAllBehaviorRecords() ?? new List<PlayerBehaviorObserver.BehaviorRecordExport>(0);
    }

    /// <summary>Gets behavior events for the specified player.</summary>
    public IReadOnlyList<BehaviorEvent> GetBehaviorEvents(string playerName)
    {
        EnsureOapsInitialized();
        return _behaviorEventStore?.GetEvents(playerName) ?? Array.Empty<BehaviorEvent>();
    }

    /// <summary>Gets behavior stats summaries for all characters.</summary>
    public Dictionary<string, string> GetAllBehaviorStats()
    {
        EnsureOapsInitialized();
        var result = new Dictionary<string, string>();
        if (_behaviorEventStore is null) return result;
        foreach (var name in _behaviorEventStore.CharacterNames)
        {
            result[name] = _behaviorEventStore.GetStatsSummary(name);
        }

        return result;
    }

    /// <summary>Gets the max observed levels for all characters.</summary>
    public Dictionary<string, int> GetBehaviorEventLevels()
    {
        EnsureOapsInitialized();
        return _behaviorEventStore?.GetMaxLevels() ?? new Dictionary<string, int>(0);
    }

    /// <summary>Gets the behavior plan (leveling plan) for a character.</summary>
    public LevelingPlan GetBehaviorPlan(string playerName)
    {
        EnsureOapsInitialized();
        var events = _behaviorEventStore?.GetEvents(playerName) ?? Array.Empty<BehaviorEvent>();
        var characterClass = events.Count > 0 ? events.FirstOrDefault()?.CharacterClass ?? 0 : 0;
        return BehaviorPlanGenerator.GeneratePlan(playerName, characterClass, events, _logger);
    }

    /// <summary>Injects a behavior event (for testing via API).</summary>
    public void InjectBehaviorEvent(BehaviorEvent behaviorEvent)
    {
        EnsureOapsInitialized();
        _behaviorEventStore?.AddEvent(behaviorEvent);
    }

    /// <summary>Saves behavior events to disk.</summary>
    public void SaveBehaviorEvents()
    {
        EnsureOapsInitialized();
        _behaviorEventStore?.Save();
    }

    /// <summary>
    /// 初始化 AI 角色系统 — 创建 AiHost 和 9 个 AI 角色。
    /// Phase 0 新增。延迟等待 IGameContext 就绪后自动启动。
    /// </summary>
    private void InitializeAiSystem()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // 等待 IGameContext 可用
                IGameContext ctx;
                while (true)
                {
                    try
                    {
                        ctx = this.Context;
                        break;
                    }
                    catch
                    {
                        await Task.Delay(1000);
                    }
                }

                // 创建 AiHost
                var logger = this._logger as ILogger<AiHost> ?? new Microsoft.Extensions.Logging.Abstractions.NullLogger<AiHost>();
                this._aiHost = new AiHost(ctx, logger);

                // 启动 AI 定时器
                this._aiTimer = new Timer(this.AiTimerTick, null, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(200));

                // 延迟创建 9 个 AI 角色（等待地图就绪）
                await Task.Delay(8000);
                await this.CreateDefaultAiTeamAsync(ctx);

                this._logger.LogInformation("[AiSystem] AI 角色系统已启动（Phase 0）");
            }
            catch (Exception ex)
            {
                this._logger.LogWarning(ex, "[AiSystem] 初始化 AI 角色系统失败");
            }
        });
    }

    /// <summary>
    /// 创建 9 个 AI 角色（3 DW, 3 DK, 3 Elf），初始到洛伦西亚。
    /// </summary>
    private async ValueTask CreateDefaultAiTeamAsync(IGameContext gameContext)
    {
        var map = await gameContext.GetMapAsync(0);
        if (map is null)
        {
            this._logger.LogWarning("[AiSystem] 地图 0 (Lorencia) 未就绪，跳过 AI 创建");
            return;
        }

        var configs = new[]
        {
            ("AIDW00", (byte)0), ("AIDW01", (byte)0), ("AIDW02", (byte)0),
            ("AIDK00", (byte)4), ("AIDK01", (byte)4), ("AIDK02", (byte)4),
            ("AIElf00", (byte)8), ("AIElf01", (byte)8), ("AIElf02", (byte)8),
        };

        var positions = new[] {
            new MUnique.OpenMU.Pathfinding.Point(140, 120),
            new MUnique.OpenMU.Pathfinding.Point(145, 125),
            new MUnique.OpenMU.Pathfinding.Point(150, 118),
            new MUnique.OpenMU.Pathfinding.Point(138, 130),
            new MUnique.OpenMU.Pathfinding.Point(142, 135),
            new MUnique.OpenMU.Pathfinding.Point(148, 128),
            new MUnique.OpenMU.Pathfinding.Point(135, 122),
            new MUnique.OpenMU.Pathfinding.Point(152, 132),
            new MUnique.OpenMU.Pathfinding.Point(143, 115),
        };

        for (int i = 0; i < configs.Length && i < positions.Length; i++)
        {
            var (name, classNumber) = configs[i];
            try
            {
                var entity = await this._aiHost!.CreateAsync(
                    new AiCreateConfig(name, classNumber, 50, 0, positions[i]),
                    map);
                this._logger.LogDebug("[AiSystem] 创建 AI 角色 {Name}(class={Class}) at ({X},{Y})",
                    name, classNumber, positions[i].X, positions[i].Y);
            }
            catch (Exception ex)
            {
                this._logger.LogWarning(ex, "[AiSystem] 创建 AI 角色 {Name} 失败", name);
            }
        }

        this._logger.LogInformation("[AiSystem] AI 角色创建完成，共 {Count} 个", this._aiHost?.Count ?? 0);
    }

    /// <summary>
    /// AI 定时器 Tick — 分帧驱动 AI 决策。
    /// </summary>
    private void AiTimerTick(object? state)
    {
        if (this._aiHost is null) return;
        this._aiHost.OnGameTick();
    }
}
