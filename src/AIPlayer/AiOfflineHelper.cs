// <copyright file="AiOfflineHelper.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.GameLogic.Offline;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Wraps the game engine's built-in OfflinePlayer (内挂) handlers for use by AiPlayer.
/// Delegates combat, healing, pickup, buff, repair to the battle-tested GameLogic implementations.
/// This replaces the script-level combat/pickup/patrol entirely with the engine's native auto-bot.
/// </summary>
public sealed class AiOfflineHelper : IDisposable
{
    private readonly AiPlayer _player;
    private readonly CombatHandler _combatHandler;
    private readonly BuffHandler _buffHandler;
    private readonly ItemPickupHandler _itemPickupHandler;
    private readonly MovementHandler _movementHandler;
    private readonly HealingHandler _healingHandler;
    private readonly ILogger _logger;

    private int _tickCounter;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiOfflineHelper"/> class.
    /// </summary>
    public AiOfflineHelper(AiPlayer player, IMuHelperSettings? config, Point origin, ILogger logger)
    {
        this._player = player;
        this._logger = logger;

        this._buffHandler = new BuffHandler(player, config);
        this._healingHandler = new HealingHandler(player, config);
        this._itemPickupHandler = new ItemPickupHandler(player, config);
        this._movementHandler = new MovementHandler(player, config, origin);
        this._combatHandler = new CombatHandler(player, config, this._movementHandler, this._buffHandler, origin);

        logger.LogInformation("[AiOffline] Initialized: origin=({X},{Y})", origin.X, origin.Y);
    }

    /// <summary>
    /// Executes one tick of the auto-bot loop.
    /// Order matches the original CMuHelper::Work() and OfflinePlayerMuHelper.TickAsync().
    /// </summary>
    public async ValueTask TickAsync()
    {
        if (this._disposed) return;
        if (this._player.PlayerState.CurrentState != PlayerState.EnteredWorld)
        {
            if (this._tickCounter % 50 == 0)
                this._logger.LogDebug("[AiOffline] Skipping tick: state={State}", this._player.PlayerState.CurrentState);
            return;
        }

        this._tickCounter++;

        if (this._tickCounter % 10 == 0)
            this._logger.LogDebug("[AiOffline] Tick #{T}: pos=({X},{Y}) hp={HP}/{MaxHP} walk={Walk}",
                this._tickCounter, this._player.Position.X, this._player.Position.Y,
                this._player.Attributes?[Stats.CurrentHealth] ?? 0,
                this._player.Attributes?[Stats.MaximumHealth] ?? 0,
                this._player.IsWalking);

        // 1. Apply buffs
        await this._buffHandler.PerformBuffsAsync().ConfigureAwait(false);

        // 3. Auto-heal (skill + potion)
        await this._healingHandler.PerformHealthRecoveryAsync().ConfigureAwait(false);

        // 4. Drain life recovery
        await this._combatHandler.PerformDrainLifeRecoveryAsync().ConfigureAwait(false);

        // 5. Auto-loot items/zen
        await this._itemPickupHandler.PickupItemsAsync().ConfigureAwait(false);

        // 6. If walking, don't interrupt movement
        if (this._player.IsWalking) return;

        // 7. Regroup to origin if too far
        await this._movementHandler.RegroupAsync().ConfigureAwait(false);

        // 8. Attack nearest valid target
        await this._combatHandler.PerformAttackAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        this._disposed = true;
    }
}
