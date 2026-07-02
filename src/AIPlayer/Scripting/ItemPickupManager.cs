// <copyright file="ItemPickupManager.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Scripting;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.AIPlayer.Decision;

/// <summary>
/// Script mode item pickup manager — picks up dropped items/money nearby.
/// Uses WorldState.DropsInRange for detection and ValueAssessmentService for filtering.
/// Only picks up items worth picking up (jewels, excellent, ancient, socket, etc.)
/// and all money drops.
/// </summary>
public sealed class ItemPickupManager
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly BehaviorContext _context;
    private readonly ILogger _logger;
    private readonly ValueAssessmentService? _valueAssessment;
    private readonly string _charTag;

    private const float MaxPickupDistance = 30f;
    private const int MinFreeSlotsForPickup = 2;

    public ItemPickupManager(AiPlayer player, IGameAdapter adapter, BehaviorContext context, ILogger logger, ValueAssessmentService? valueAssessment = null)
    {
        this._player = player;
        this._adapter = adapter;
        this._context = context;
        this._logger = logger;
        this._valueAssessment = valueAssessment;
        this._charTag = "[" + (player.SelectedCharacter?.Name ?? "?") + "] ";
    }

    /// <summary>Try to pick up nearby items. Returns true if an item was picked up or walking toward one.</summary>
    public async Task<bool> TryPickupNearbyItemsAsync()
    {
        var dropLocateables = this._context.WorldState.DropsInRange;
        if (dropLocateables is null || dropLocateables.Count == 0)
        {
            return false;
        }

        var inv = this._player.Inventory;
        if (inv is null) return false;

        var freeSlots = CountFreeInventorySlots(inv);

        // Money: always pick up (doesn't use inventory slots)
        var moneyDrops = dropLocateables.OfType<DroppedMoney>().ToList();
        if (moneyDrops.Count > 0)
        {
            var nearestMoney = moneyDrops
                .OrderBy(m => this._player.Position.EuclideanDistanceTo(m.Position))
                .First();
            var moneyDist = this._player.Position.EuclideanDistanceTo(nearestMoney.Position);

            if (moneyDist <= 2.0f)
            {
                await this._adapter.PickupItemAsync(nearestMoney.Id).ConfigureAwait(false);
                this._logger.LogInformation("[ScriptPickup]{Tag} 拾取金钱: {Amount}", this._charTag, nearestMoney.Amount);
                return true;
            }

            if (moneyDist <= MaxPickupDistance)
            {
                var map = this._adapter.GetCurrentMap();
                if (map is not null)
                {
                    await this._adapter.WalkToAsync(
                        new Point((byte)nearestMoney.Position.X, (byte)nearestMoney.Position.Y), map)
                        .ConfigureAwait(false);
                    return true;
                }
            }
        }

        // Items: check free slots
        if (freeSlots < MinFreeSlotsForPickup)
        {
            return false;
        }

        // Filter valuable items
        var itemDrops = dropLocateables.OfType<DroppedItem>().ToList();
        var valuableDrops = itemDrops
            .Where(d => this.IsWorthPickingUp(d))
            .OrderBy(d => this._player.Position.EuclideanDistanceTo(d.Position))
            .ToList();

        if (valuableDrops.Count == 0)
        {
            return false;
        }

        var target = valuableDrops[0];
        var dist = this._player.Position.EuclideanDistanceTo(target.Position);

        if (dist <= 2.0f)
        {
            this._logger.LogInformation("[ScriptPickup]{Tag} 拾取: {Item}",
                this._charTag, target.Item?.Definition?.Name ?? $"?");
            await this._adapter.PickupItemAsync(target.Id).ConfigureAwait(false);
            return true;
        }

        if (dist <= MaxPickupDistance)
        {
            var map = this._adapter.GetCurrentMap();
            if (map is not null)
            {
                await this._adapter.WalkToAsync(
                    new Point((byte)target.Position.X, (byte)target.Position.Y), map)
                    .ConfigureAwait(false);
                return true;
            }
        }

        return false;
    }

    private bool IsWorthPickingUp(DroppedItem drop)
    {
        var item = drop.Item;
        if (item is null || item.Definition is null) return false;

        // Use ValueAssessmentService if available (respects ShouldPickup threshold)
        if (this._valueAssessment is not null)
        {
            return this._valueAssessment.ShouldPickup(item);
        }

        // Fallback: jewels, excellent items, ancient items, socket items
        if (item.Definition.Group == 12) return true; // Jewel group
        if (item.ItemOptions is not null && item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Excellent))
            return true;
        if (item.ItemSetGroups.Count > 0) return true; // Ancient
        if (item.SocketCount > 0) return true;

        // Potions and apples (Group 14, Numbers 0-6) — only pick up below Lv50.
        // 50+玩家靠自然回血和买药，背包格子留给卓越和宝石。
        if (item.Definition.Group == 14 && item.Definition.Number <= 6)
        {
            return this._player.Level < 50;
        }

        // Arrows / bolts (Group 4, Number 15) — needed by Elf class
        if (item.Definition.Group == 4 && item.Definition.Number == 15) return true;

        return false;
    }

    private static int CountFreeInventorySlots(IInventoryStorage inv)
    {
        const int equipSlots = 12;
        const int baseRows = 8;
        const int rowSize = 8;
        var totalInventorySlots = equipSlots + (baseRows * rowSize);
        var occupiedCount = inv.Items.Count(i => i.ItemSlot >= equipSlots && i.ItemSlot < totalInventorySlots);
        return Math.Max(0, (baseRows * rowSize) - occupiedCount);
    }
}
