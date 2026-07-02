// <copyright file="OpenMuActionExecutor.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.AccessLayer;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.AIPlayer;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// OpenMU server-integrated implementation of <see cref="IActionExecutor"/>.
/// Wraps <see cref="GameAdapter"/>, <see cref="NativeExecutionService"/> and
/// <see cref="AiPlayer"/> to translate OAPS action commands into real game
/// operations via the server's public GameLogic API.
/// </summary>
public sealed class OpenMuActionExecutor : IActionExecutor
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _gameAdapter;
    private readonly NativeExecutionService? _nativeExec;
    private readonly ILogger<OpenMuActionExecutor> _logger;

    /// <summary>
    /// HP potion definitions in descending quality order: (Group, Number).
    /// The executor tries higher-quality potions first.
    /// </summary>
    private static readonly (byte Group, short Number)[] HpPotions =
    [
        (14, 3), // Large Healing
        (14, 2), // Medium Healing
        (14, 1), // Small Healing
    ];

    /// <summary>
    /// MP potion definitions in descending quality order: (Group, Number).
    /// </summary>
    private static readonly (byte Group, short Number)[] MpPotions =
    [
        (14, 6), // Large Mana
        (14, 5), // Medium Mana
        (14, 4), // Small Mana
    ];

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenMuActionExecutor"/> class.
    /// </summary>
    /// <param name="player">The AI player to control.</param>
    /// <param name="gameAdapter">The game adapter for high-level operations (walk, pickup, consume).</param>
    /// <param name="nativeExecutionService">The native execution service for atomic combat actions (melee, skill). May be null when the cognitive loop runs in observation-only mode.</param>
    /// <param name="logger">Optional logger. When omitted, a <see cref="NullLogger{T}"/> is used.</param>
    public OpenMuActionExecutor(
        AiPlayer player,
        IGameAdapter gameAdapter,
        NativeExecutionService? nativeExecutionService,
        ILogger<OpenMuActionExecutor>? logger = null)
    {
        this._player = player;
        this._gameAdapter = gameAdapter;
        this._nativeExec = nativeExecutionService;
        this._logger = logger ?? NullLogger<OpenMuActionExecutor>.Instance;
    }

    /// <inheritdoc />
    public bool IsWalking => this._player.IsWalking;

    /// <inheritdoc />
    public async ValueTask<bool> MoveToAsync(byte x, byte y, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var map = this._player.CurrentMap;
        if (map is null)
        {
            return false;
        }

        try
        {
            var result = await this._gameAdapter.WalkToAsync(new Point(x, y), map).ConfigureAwait(false);
            return result.Status is WalkStatusCode.Success or WalkStatusCode.AlreadyAtTarget;
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "MoveToAsync: failed to walk to ({X}, {Y}).", x, y);
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> AttackAsync(uint targetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var target = this.FindAttackable(targetId);
        if (target is null || !target.IsAlive)
        {
            return false;
        }

        if (this._nativeExec is null)
        {
            this._logger.LogWarning("AttackAsync: NativeExecutionService not initialized.");
            return false;
        }

        try
        {
            var hitInfo = await this._nativeExec.MeleeAttackAsync(target).ConfigureAwait(false);
            return hitInfo is not null;
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "AttackAsync: failed on target #{TargetId}.", targetId);
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> SkillAttackAsync(uint targetId, ushort skillNumber, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var target = this.FindAttackable(targetId);
        if (target is null || !target.IsAlive)
        {
            return false;
        }

        var skillEntry = this._player.SkillList?.GetSkill(skillNumber);
        if (skillEntry is null)
        {
            this._logger.LogWarning("SkillAttackAsync: skill #{Skill} not found on player.", skillNumber);
            return false;
        }

        if (this._nativeExec is null)
        {
            this._logger.LogWarning("SkillAttackAsync: NativeExecutionService not initialized.");
            return false;
        }

        try
        {
            await this._nativeExec.SkillAttackAsync(target, skillEntry).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "SkillAttackAsync: failed on target #{TargetId}, skill #{Skill}.", targetId, skillNumber);
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> PickupAsync(uint itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            await this._gameAdapter.PickupItemAsync((ushort)itemId).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "PickupAsync: failed for item #{ItemId}.", itemId);
            return false;
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> InteractAsync(uint npcId, int dialogOption = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Simplified — reserved for future implementation.
        // Full version will walk to the NPC, verify it is a NonPlayerCharacter,
        // and open the interaction dialog. For now, returning success is the
        // safe default that avoids blocking the cognitive loop.
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc />
    public async ValueTask<bool> UseItemAsync(uint itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var inv = this._player.Inventory;
        if (inv is null)
        {
            return false;
        }

        var slot = (byte)itemId;
        if (inv.Items.All(i => i.ItemSlot != slot))
        {
            this._logger.LogWarning("UseItemAsync: no item found at inventory slot {Slot}.", slot);
            return false;
        }

        try
        {
            await this._gameAdapter.ConsumeItemAsync(slot).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "UseItemAsync: failed for slot {Slot}.", slot);
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> DrinkPotionAsync(PotionType type, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var inv = this._player.Inventory;
        if (inv is null)
        {
            return false;
        }

        var potionDefs = type == PotionType.Health ? HpPotions : MpPotions;

        try
        {
            foreach (var (group, number) in potionDefs)
            {
                var potion = inv.Items.FirstOrDefault(i =>
                    i.Definition?.Group == group && i.Definition?.Number == number && i.Durability > 0);

                if (potion is null)
                {
                    continue;
                }

                await this._gameAdapter.ConsumeItemAsync(potion.ItemSlot).ConfigureAwait(false);
                return true;
            }

            this._logger.LogInformation("DrinkPotionAsync: no {Type} potion found in inventory.", type);
            return false;
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "DrinkPotionAsync: failed for {Type}.", type);
            return false;
        }
    }

    /// <summary>
    /// Resolves an entity by its server ID and returns it as <see cref="IAttackable"/>.
    /// </summary>
    /// <param name="id">The server entity ID.</param>
    /// <returns>The attackable, or <c>null</c> if not found or not attackable.</returns>
    private IAttackable? FindAttackable(uint id)
    {
        var map = this._player.CurrentMap;
        if (map is null)
        {
            return null;
        }

        var obj = map.GetObject((ushort)id);
        return obj as IAttackable;
    }
}
