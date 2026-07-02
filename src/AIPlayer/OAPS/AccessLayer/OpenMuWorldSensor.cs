// <copyright file="OpenMuWorldSensor.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.AccessLayer;

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;
using Nito.AsyncEx;
using OAPS.World;

// AiPlayer lives in MUnique.OpenMU.AIPlayer; we alias the type to avoid
// pulling in the whole namespace which contains its own WorldState class.
using AiPlayer = MUnique.OpenMU.AIPlayer.AiPlayer;

/// <summary>
/// OpenMU server-integrated implementation of <see cref="IWorldSensor"/>.
/// Wraps an <see cref="AiPlayer"/> instance and its current <see cref="GameMap"/>
/// to produce <see cref="WorldState"/> snapshots consumed by the OAPS cognitive loop.
/// All reads go through the server's public GameLogic API — no packet injection or CV.
/// </summary>
public sealed class OpenMuWorldSensor : IWorldSensor, IDisposable
{
    private readonly AiPlayer _player;
    private readonly int _viewRange;
    private readonly ILogger<OpenMuWorldSensor> _logger;
    private readonly Dictionary<uint, int> _lastMonsterHp = new();

    private int _lastLevel;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenMuWorldSensor"/> class.
    /// </summary>
    /// <param name="player">The AI player whose world state is to be captured.</param>
    /// <param name="viewRange">Maximum range in tiles for detecting entities. Default 20, matching the standard AI search radius.</param>
    /// <param name="logger">Optional logger. When omitted, a <see cref="NullLogger{T}"/> is used.</param>
    public OpenMuWorldSensor(AiPlayer player, int viewRange = 20, ILogger<OpenMuWorldSensor>? logger = null)
    {
        this._player = player;
        this._viewRange = viewRange;
        this._logger = logger ?? NullLogger<OpenMuWorldSensor>.Instance;
        this._lastLevel = player.Level;

        // Wire up player lifecycle events fired by GameLogic → OAPS GameEvent.
        player.Died += this.OnPlayerDied;
        player.PlayerPickedUpItem += this.OnPlayerPickedUpItem;
    }

    /// <inheritdoc />
    public event Action<GameEvent>? OnGameEvent;

    /// <inheritdoc />
    public AccessMode GetCurrentMode() => AccessMode.OfficialApi;

    /// <inheritdoc />
    public async ValueTask<WorldState> CaptureAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var player = this._player;
        var pos = player.Position;

        // ── Self snapshot ──────────────────────────────────────────────
        var self = new PlayerSnapshot(
            Id: player.Id,
            X: (byte)pos.X,
            Y: (byte)pos.Y,
            HP: (int)(player.Attributes?[Stats.CurrentHealth] ?? 0),
            MaxHP: (int)(player.Attributes?[Stats.MaximumHealth] ?? 1),
            MP: (int)(player.Attributes?[Stats.CurrentMana] ?? 0),
            MaxMP: (int)(player.Attributes?[Stats.MaximumMana] ?? 1),
            Level: player.Level,
            Zen: player.SelectedCharacter?.Inventory?.Money ?? 0);

        // ── Level-up detection (polled) ────────────────────────────────
        if (player.Level > this._lastLevel)
        {
            this._lastLevel = player.Level;
            this.PublishEvent(new GameEvent
            {
                Type = GameEventType.PlayerLevelUp,
                EntityId = player.Id,
                Value = player.Level,
                Timestamp = DateTime.UtcNow,
            });
        }

        // ── Monster & item snapshots ───────────────────────────────────
        IReadOnlyDictionary<uint, MonsterSnapshot> monsters;
        IReadOnlyDictionary<uint, ItemSnapshot> items;

        var map = player.CurrentMap;
        if (map is not null)
        {
            monsters = this.CaptureMonsters(map, pos);
            items = CaptureItems(map, pos);
        }
        else
        {
            monsters = ImmutableDictionary<uint, MonsterSnapshot>.Empty;
            items = ImmutableDictionary<uint, ItemSnapshot>.Empty;
        }

        return new WorldState
        {
            Self = self,
            Monsters = monsters,
            Items = items,
            AccessMode = AccessMode.OfficialApi,
            LastUpdate = DateTime.UtcNow,
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        this._player.Died -= this.OnPlayerDied;
        this._player.PlayerPickedUpItem -= this.OnPlayerPickedUpItem;
    }

    /// <summary>
    /// Scans the current map for alive monsters in view range and builds
    /// a snapshot dictionary. Also detects kills by comparing HP deltas
    /// with the previous capture.
    /// </summary>
    private Dictionary<uint, MonsterSnapshot> CaptureMonsters(GameMap map, Point pos)
    {
        var attackables = map.GetAttackablesInRange(pos, this._viewRange);
        var snapshot = new Dictionary<uint, MonsterSnapshot>();
        var currentHp = new Dictionary<uint, int>(capacity: this._lastMonsterHp.Count);

        foreach (var attackable in attackables)
        {
            if (attackable is not Monster monster || !monster.IsAlive)
            {
                continue;
            }

            var id = (uint)monster.Id;
            var hp = (int)(monster.Attributes?[Stats.CurrentHealth] ?? 0);
            var maxHp = (int)(monster.Attributes?[Stats.MaximumHealth] ?? 1);
            var typeId = (ushort)(monster.Definition?.Number ?? 0);

            currentHp[id] = hp;

            snapshot[id] = new MonsterSnapshot(
                Id: id,
                TypeId: typeId,
                X: (byte)monster.Position.X,
                Y: (byte)monster.Position.Y,
                HP: hp,
                MaxHP: maxHp,
                Level: 0);

            // HP transition >0 → ≤0 → monster killed
            if (this._lastMonsterHp.TryGetValue(id, out var prevHp) && prevHp > 0 && hp <= 0)
            {
                this.PublishEvent(new GameEvent
                {
                    Type = GameEventType.MonsterKilled,
                    EntityId = id,
                    X = (byte)monster.Position.X,
                    Y = (byte)monster.Position.Y,
                    Value = typeId,
                    Timestamp = DateTime.UtcNow,
                });
            }
        }

        // Swap tracking dictionary
        this._lastMonsterHp.Clear();
        foreach (var kvp in currentHp)
        {
            this._lastMonsterHp[kvp.Key] = kvp.Value;
        }

        return snapshot;
    }

    /// <summary>
    /// Scans the current map for dropped items and money in view range
    /// and builds an <see cref="ItemSnapshot"/> dictionary.
    /// </summary>
    private static Dictionary<uint, ItemSnapshot> CaptureItems(GameMap map, Point pos)
    {
        var drops = map.GetDropsInRange(pos, 20);
        var snapshot = new Dictionary<uint, ItemSnapshot>(drops.Count);

        foreach (var drop in drops)
        {
            switch (drop)
            {
                case DroppedItem droppedItem:
                {
                    var item = droppedItem.Item;
                    var itemId = (uint)droppedItem.Id;
                    snapshot[itemId] = new ItemSnapshot(
                        Id: itemId,
                        TypeId: (ushort)(item.Definition?.Number ?? 0),
                        X: (byte)droppedItem.Position.X,
                        Y: (byte)droppedItem.Position.Y,
                        Level: item.Level,
                        Grade: ClassifyItemGrade(item));
                    break;
                }

                case DroppedMoney money:
                {
                    var moneyId = (uint)money.Id;
                    snapshot[moneyId] = new ItemSnapshot(
                        Id: moneyId,
                        TypeId: 0xFFFF,
                        X: (byte)money.Position.X,
                        Y: (byte)money.Position.Y,
                        Level: 0,
                        Grade: ItemGrade.E);
                    break;
                }
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Handles the player's <see cref="Player.Died"/> event and publishes a
    /// <see cref="GameEventType.PlayerDeath"/> event.
    /// </summary>
    private void OnPlayerDied(object? sender, DeathInformation deathInfo)
    {
        this.PublishEvent(new GameEvent
        {
            Type = GameEventType.PlayerDeath,
            EntityId = this._player.Id,
            X = (byte)this._player.Position.X,
            Y = (byte)this._player.Position.Y,
            Value = deathInfo.KillerId,
            Timestamp = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Handles the player's <see cref="Player.PlayerPickedUpItem"/> event and publishes an
    /// <see cref="GameEventType.ItemPickedUp"/> event.
    /// Nito.AsyncEx <see cref="AsyncEventHandler{TEventArgs}"/> passes only the event args.
    /// </summary>
    private ValueTask OnPlayerPickedUpItem((Player Picker, ILocateable DroppedItem) args)
    {
        var (_, item) = args;
        this.PublishEvent(new GameEvent
        {
            Type = GameEventType.ItemPickedUp,
            EntityId = item.Id,
            X = (byte)item.Position.X,
            Y = (byte)item.Position.Y,
            Timestamp = DateTime.UtcNow,
        });

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Safely publishes a <see cref="GameEvent"/> to subscribers. Exceptions from
    /// individual handlers are caught and logged so they never propagate upstream.
    /// </summary>
    /// <param name="evt">The event to publish.</param>
    private void PublishEvent(GameEvent evt)
    {
        try
        {
            this.OnGameEvent?.Invoke(evt);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "Failed to publish game event {Type}.", evt.Type);
        }
    }

    /// <summary>
    /// Classifies a dropped <see cref="Item"/> into an <see cref="ItemGrade"/>
    /// based on its group, options, and level.
    /// Aligned with the OAPS KnowledgeBase value-assessment heuristic.
    /// </summary>
    private static ItemGrade ClassifyItemGrade(Item item)
    {
        var def = item.Definition;
        if (def is null)
        {
            return ItemGrade.Junk;
        }

        // ── Gems (Group 12) ───────────────────────────────────────────
        if (def.Group == 12)
        {
            return def.Number switch
            {
                15 => ItemGrade.S,  // Jewel of Bless
                16 => ItemGrade.S,  // Jewel of Soul
                22 => ItemGrade.A,  // Jewel of Chaos
                31 => ItemGrade.S,  // Jewel of Creation
                42 => ItemGrade.S,  // Jewel of Guardian
                _ => ItemGrade.B,
            };
        }

        // ── Excellent option → A grade ─────────────────────────────────
        if (item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Excellent))
        {
            return ItemGrade.A;
        }

        // ── Ancient set → A grade ──────────────────────────────────────
        if (item.ItemSetGroups.Any(s => s.AncientSetDiscriminator != 0))
        {
            return ItemGrade.A;
        }

        // ── Potions (Group 14) ─────────────────────────────────────────
        if (def.Group == 14)
        {
            return def.Number switch
            {
                3 => ItemGrade.D,   // Large Healing
                2 => ItemGrade.E,   // Medium Healing
                1 => ItemGrade.E,   // Small Healing
                _ => ItemGrade.E,
            };
        }

        // ── Weapons / Armor (Groups 0–6) ──────────────────────────────
        if (def.Group <= 6)
        {
            if (item.Level >= 8) return ItemGrade.C;
            if (item.Level >= 4) return ItemGrade.D;
            return ItemGrade.E;
        }

        // ── Wings (Group 7) ───────────────────────────────────────────
        if (def.Group == 7) return ItemGrade.B;

        // ── Scrolls (Group 9) ─────────────────────────────────────────
        if (def.Group == 9) return ItemGrade.D;

        return ItemGrade.E;
    }
}
