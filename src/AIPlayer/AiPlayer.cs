// <copyright file="AiPlayer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AIPlayer.World;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.PlugIns;

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
    /// Gets the world map for cross-map navigation (populated at startup).
    /// </summary>
    public WorldMap? WorldMap { get; internal set; }

    /// <summary>
    /// Gets the algorithm selector for pathfinding.
    /// </summary>
    public AlgorithmSelector AlgorithmSelector { get; } = new();

    /// <summary>
    /// Gets or sets the personality profile identifier (reserved for Phase 2+).
    /// </summary>
    public string? PersonalityProfile { get; internal set; }

    /// <summary>
    /// Gets or sets the personality-driven behavioral preferences.
    /// Shapes PK tendency, greed, risk tolerance, and hunting style.
    /// </summary>
    public PersonalityProfile Personality { get; set; } = MUnique.OpenMU.AIPlayer.PersonalityProfile.Balanced();

    /// <summary>
    /// Gets the timestamp when this AI player was started.
    /// </summary>
    public DateTime StartTimestamp { get; internal set; }

    /// <summary>
    /// Gets the AI behavior logic, if started.
    /// </summary>
    public AiPlayerLogic? Logic { get; private set; }

    /// <summary>
    /// Gets or sets a value indicating whether the AI logic should run in step mode.
    /// When <c>true</c>, the periodic timer loop is disabled and <see cref="AiPlayerLogic.TickOnceAsync"/>
    /// must be called manually. Intended for the test client.
    /// </summary>
    internal bool StepModeForTesting { get; set; }

    /// <summary>
    /// Gets the persistent character growth record for this AI player.
    /// Tracks max level achieved, rebirth count, unlocked knowledge, and other
    /// long-term progression data that survives across sessions and rebirths.
    /// </summary>
    public CharacterMemory? CharacterMemory { get; internal set; }

    /// <summary>
    /// Gets the execution context for this AI player session.
    /// Preserves the current goal and task queue across disconnects.
    /// Saved on stop, restored on start.
    /// </summary>
    public ExecutionContext? ExecutionContext { get; internal set; }

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
    public AIStateMachine.AIStateMachine? StateMachine { get; internal set; }

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

            // Load behavior script if configured (1D mode)
            Scripting.BehaviorScript? script = null;
            if (this.ScriptPath is not null)
            {
                script = Scripting.ScriptExecutor.LoadFromFile(this.ScriptPath);
            }

            this.Logic = new AiPlayerLogic(this, this.AlgorithmSelector, this.StepModeForTesting, script);

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

        await this.DisconnectAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override ICustomPlugInContainer<IViewPlugIn> CreateViewPlugInContainer()
        => new AiViewPlugInContainer(this);

    private static string GetMemoryFilePath(string characterName)
    {
        var dir = GetDataDirectory();
        return Path.Combine(dir, $"{characterName}.json");
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
