// <copyright file="AiPlayer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.PlugIns;
using MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// An AI-controlled player entity that exists in the game world
/// without a real client connection. Follows the same pattern as
/// <see cref="MUnique.OpenMU.GameLogic.Offline.OfflinePlayer"/>.
/// </summary>
public sealed class AiPlayer : Player
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AiPlayer"/> class.
    /// </summary>
    /// <param name="gameContext">The game context.</param>
    public AiPlayer(IGameContext gameContext)
        : base(gameContext)
    {
    }

    /// <summary>
    /// Gets the unique AI player identifier.
    /// </summary>
    public Guid AiPlayerId { get; internal set; }

    /// <summary>
    /// Gets the algorithm selector for pathfinding.
    /// </summary>
    public AlgorithmSelector AlgorithmSelector { get; } = new();

    /// <summary>
    /// Gets the timestamp when this AI player was started.
    /// </summary>
    public DateTime StartTimestamp { get; internal set; }

    /// <summary>
    /// Gets the AI behavior logic, if started.
    /// </summary>
    public AiPlayerLogic? Logic { get; private set; }

    /// <summary>
    /// Gets or sets the build direction for stat allocation and equipment scoring.
    /// Set during player creation by <see cref="AiPlayerManager.CreateAiPlayerAsync"/>.
    /// </summary>
    public BuildDirection? BuildDirection { get; set; }

    /// <summary>
    /// Gets the character memory (persistent AI knowledge) for this player.
    /// Loaded from JSON on initialization, saved on <see cref="StopAsync"/>.
    /// </summary>
    public CharacterMemory? CharacterMemory { get; internal set; }

    /// <summary>
    /// Gets the experience memory (AI learning system) for this player.
    /// Records kills, drops, deaths, gold per map/area for experience-based learning.
    /// Saved alongside <see cref="CharacterMemory"/>.
    /// </summary>
    public ExperienceMemory? ExperienceMemory { get; internal set; }

    /// <summary>
    /// Gets the experience learner (per-tick learning engine) for this player.
    /// Collects observations each tick and writes to <see cref="ExperienceMemory"/>.
    /// </summary>
    public ExperienceLearner? ExperienceLearner { get; internal set; }

    /// <summary>Gets the Fugu v4.0 shared memory layer for this AI character.</summary>
    public Knowledge.SharedMemoryLayer? SharedMemory { get; internal set; }

    /// <summary>Gets or sets the behavior event store for AI event collection.</summary>
    public BehaviorEventStore? BehaviorEventStore { get; internal set; }

    /// <summary>Gets or sets the bootstrapped SFT weights path (non-random initialization).</summary>
    public string? SftWeightsPath { get; internal set; }

    /// <summary>
    /// Gets or sets a value indicating whether the AI logic should run in step mode.
    /// When <c>true</c>, the periodic timer loop is disabled and <see cref="AiPlayerLogic.TickOnceAsync"/>
    /// must be called manually. Intended for the test client.
    /// </summary>
    internal bool StepModeForTesting { get; set; }

    /// <summary>
    /// Sets a target map for the AI to travel to.
    /// The AI will navigate through gates until it reaches the specified map.
    /// </summary>
    /// <param name="mapNumber">The target map number.</param>
    public void SetTargetMap(ushort mapNumber)
    {
        if (this.Logic is { } logic)
        {
            logic.TargetMapNumber = mapNumber;
        }
    }

    /// <summary>
    /// 从集体经验记忆合并数据到当前角色的经验记忆。
    /// 在初始化完成后调用，使 AI 角色继承玩家社区积累的狩猎/任务知识。
    /// </summary>
    /// <param name="collective">集体经验记忆（来自 AiPlayerManager）。</param>
    public void MergeCollectiveExperience(ExperienceMemory? collective)
    {
        if (collective is null || collective.MapStats.Count == 0) return;

        if (this.ExperienceMemory is null)
        {
            this.ExperienceMemory = new ExperienceMemory { CharacterName = this.SelectedCharacter?.Name ?? "unknown" };
        }

        this.ExperienceMemory.MergeFrom(collective);
        this.Logger.LogInformation(
            "[CollectiveMerge] 已合并集体经验: {Maps}张地图数据到 {Char}",
            collective.MapStats.Count,
            this.SelectedCharacter?.Name);
    }

    /// <summary>
    /// Gets or sets the path to a behavior script JSON file.
    /// When set before <see cref="InitializeAsync"/>, the AI uses the 1D
    /// ScriptExecutor (priority chain) instead of the default DAG executor.
    /// </summary>
    public string? ScriptPath { get; set; }

    /// <summary>
    /// Gets the event-driven AI state machine for async ScriptItem execution.
    /// Initialized by <see cref="AiPlayerLogic"/> on startup.
    /// When non-null and running, the Tick loop delegates to the state machine
    /// instead of executing the normal behavior engine.
    /// </summary>
    public object? StateMachine { get; internal set; }

    /// <summary>
    /// Initializes the AI player from the given account and character.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="character">The character.</param>
    /// <param name="needsAttach">If true, entities are detached and need to be attached to the persistence context.</param>
    /// <returns><c>true</c> if successfully initialized.</returns>
    public async ValueTask<bool> InitializeAsync(Account account, Character character, bool needsAttach = false)
    {
        try
        {
            this.StartTimestamp = DateTime.UtcNow;
            this.Account = account;

            if (needsAttach)
            {
                this.PersistenceContext.Attach(account);
            }

            await this.AdvanceToCharacterSelectionStateAsync().ConfigureAwait(false);
            await this.SetupCharacterAsync(character, needsAttach).ConfigureAwait(false);
            await this.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);

            if (this.CurrentMap is { } map)
            {
                this.Logger.LogDebug("AI player entering map {MapName}", map.Definition.Name);
            }

            this.AlgorithmSelector.RegisterAlgorithms();

            // Load persistent character memory from JSON
            var memoryPath = GetMemoryFilePath(character.Name);
            this.CharacterMemory = await CharacterMemory.LoadAsync(memoryPath).ConfigureAwait(false)
                                  ?? CharacterMemory.CreateNew(character.Name, character.CharacterClass?.Number ?? 0, this.Level);

            // Load experience memory (AI learning system)
            var expMemoryPath = GetExpMemoryFilePath(character.Name);
            this.ExperienceMemory = await ExperienceMemory.LoadAsync(expMemoryPath).ConfigureAwait(false)
                                  ?? new ExperienceMemory { CharacterName = character.Name };

            // Load behavior script if configured (1D mode)
            Scripting.BehaviorScript? script = null;
            if (this.ScriptPath is not null)
            {
                script = Scripting.ScriptExecutor.LoadFromFile(this.ScriptPath);
            }

            this.Logic = new AiPlayerLogic(this, this.AlgorithmSelector, this.StepModeForTesting, script);

            // Initialize Fugu v4.0 routing if shared memory is available
            if (this.SharedMemory is not null)
            {
                this.Logic.InitializeFuguComponents(this.SharedMemory);
            }

            this.Logger.LogDebug(
                "AI player started for character {CharacterName} on map {Map} at {Position}.",
                character.Name,
                character.CurrentMap?.Name,
                this.Position);

            return true;
        }
        catch (Exception ex)
        {
            this.Logger.LogError(ex, "Failed to initialize AI player for {player}.", this);
            return false;
        }
    }

    /// <summary>
    /// Stops the AI player and removes it from the world.
    /// </summary>
    public async ValueTask StopAsync()
    {
        if (this.Logic is { } logic)
        {
            logic.Dispose();
            this.Logic = null;
        }

        try
        {
            await this.SaveProgressAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.Logger.LogError(ex, "Failed to save progress of AI player {AiPlayerId}.", this.AiPlayerId);
        }

        // Persist CharacterMemory to JSON
        if (this.CharacterMemory is { } memory
            && this.SelectedCharacter?.Name is { } name
            && name.Length > 0)
        {
            var path = GetMemoryFilePath(name);
            try
            {
                await memory.SaveAsync(path).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.Logger.LogWarning(ex, "[CharacterMemory] Failed to save memory for {Name}.", name);
            }
        }

        // Persist ExperienceMemory to JSON
        if (this.ExperienceMemory is { } expMem
            && this.SelectedCharacter?.Name is { } expName
            && expName.Length > 0)
        {
            var expPath = GetExpMemoryFilePath(expName);
            try
            {
                await expMem.SaveAsync(expPath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.Logger.LogWarning(ex, "[ExperienceMemory] Failed to save experience for {Name}.", expName);
            }
        }

        await this.DisconnectAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Override respawn to auto-confirm map change after death.
    /// Base RespawnAtAsync calls WarpToAsync which sets CurrentMap=null
    /// and waits for client F3 12 packet — AI has no client, so we
    /// call ClientReadyAfterMapChangeAsync to complete the transition.
    /// </summary>
    public override async ValueTask RespawnAtAsync(ExitGate gate)
    {
        await base.RespawnAtAsync(gate).ConfigureAwait(false);

        // After base respawn, CurrentMap is null (WarpToAsync sets it null).
        // Auto-confirm map change so the AI can continue execution.
        if (this.CurrentMap is null)
        {
            await this.ClientReadyAfterMapChangeAsync().ConfigureAwait(false);
            this.Logger.LogDebug(
                "[AiPlayer] RespawnAtAsync: auto-confirmed map change, now on map {Map} at ({X},{Y})",
                this.CurrentMap?.Definition?.Number, this.Position.X, this.Position.Y);
        }
    }

    /// <inheritdoc />
    protected override ICustomPlugInContainer<IViewPlugIn> CreateViewPlugInContainer()
        => new AiViewPlugInContainer(this);

    private static string GetMemoryFilePath(string characterName)
    {
        var dir = GetDataDirectory();
        return Path.Combine(dir, $"{characterName}.json");
    }

    private static string GetExpMemoryFilePath(string characterName)
    {
        var dir = GetDataDirectory();
        return Path.Combine(dir, $"exp_{characterName}.json");
    }

    private static string GetDataDirectory()
    {
        return Path.Combine(Directory.GetCurrentDirectory(), "aiplayer_data");
    }

    [Obsolete("Use GetDataDirectory() instead.")]
    private static string GetContextDirectory() => GetDataDirectory();

    private async ValueTask AdvanceToCharacterSelectionStateAsync()
    {
        await this.PlayerState.TryAdvanceToAsync(GameLogic.PlayerState.LoginScreen).ConfigureAwait(false);
        await this.PlayerState.TryAdvanceToAsync(GameLogic.PlayerState.Authenticated).ConfigureAwait(false);
        await this.PlayerState.TryAdvanceToAsync(GameLogic.PlayerState.CharacterSelection).ConfigureAwait(false);
    }

    private async ValueTask SetupCharacterAsync(Character character, bool needsAttach)
    {
        await this.GameContext.AddPlayerAsync(this).ConfigureAwait(false);
        await this.SetSelectedCharacterAsync(character).ConfigureAwait(false);

        if (needsAttach && this.SelectedCharacter is { } selectedCharacter)
        {
            this.PersistenceContext.Attach(selectedCharacter);
        }
    }
}
