// <copyright file="WorldStateSyncEngine.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.WorldState.Sync;

using MUnique.OpenMU.WorldState.Core;
using MUnique.OpenMU.WorldState.Events;
using MUnique.OpenMU.WorldState.Snapshots;

/// <summary>
/// Listens to the <see cref="WorldEventBus"/> and applies events
/// to the <see cref="Runtime.WorldState"/> to keep the digital mirror in sync.
/// </summary>
public class WorldStateSyncEngine
{
    private readonly Runtime.WorldState _worldState;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorldStateSyncEngine"/> class.
    /// </summary>
    /// <param name="worldState">The world state to update.</param>
    public WorldStateSyncEngine(Runtime.WorldState worldState)
    {
        this._worldState = worldState;
        WorldEventBus.Subscribe(this.HandleEvent);
    }

    private void HandleEvent(IWorldEvent worldEvent)
    {
        switch (worldEvent)
        {
            case MonsterSpawnedEvent e:
                this.HandleMonsterSpawn(e);
                break;
            case MonsterMovedEvent e:
                this.HandleMonsterMoved(e);
                break;
            case MonsterDiedEvent e:
                this.HandleMonsterDied(e);
                break;
            case MonsterHitEvent e:
                this.HandleMonsterHit(e);
                break;
            case ItemDroppedEvent e:
                this.HandleItemDropped(e);
                break;
            case ItemPickedUpEvent e:
                this.HandleItemPickedUp(e);
                break;
            case PlayerMovedEvent e:
                this.HandlePlayerMoved(e);
                break;
            case PlayerMapChangedEvent e:
                this.HandlePlayerMapChanged(e);
                break;
            case PlayerHitEvent e:
                this.HandlePlayerHit(e);
                break;
        }
    }

    private void HandleMonsterSpawn(MonsterSpawnedEvent e)
    {
        this._worldState.Monsters[e.MonsterId] = new MonsterSnapshot
        {
            Id = e.MonsterId,
            MonsterType = e.MonsterType,
            MapId = e.MapId,
            X = e.X,
            Y = e.Y,
            IsAlive = true,
            LastSeen = DateTime.UtcNow,
        };

        this._worldState.Timestamp = DateTime.UtcNow;
    }

    private void HandleMonsterMoved(MonsterMovedEvent e)
    {
        if (this._worldState.Monsters.TryGetValue(e.MonsterId, out var snapshot))
        {
            snapshot.X = e.X;
            snapshot.Y = e.Y;
            snapshot.LastSeen = DateTime.UtcNow;
            this._worldState.Timestamp = DateTime.UtcNow;
        }
    }

    private void HandleMonsterDied(MonsterDiedEvent e)
    {
        if (this._worldState.Monsters.TryGetValue(e.MonsterId, out var snapshot))
        {
            snapshot.IsAlive = false;
            snapshot.HP = 0;
            snapshot.LastSeen = DateTime.UtcNow;
            this._worldState.Timestamp = DateTime.UtcNow;
        }
    }

    private void HandleMonsterHit(MonsterHitEvent e)
    {
        if (this._worldState.Monsters.TryGetValue(e.MonsterId, out var snapshot))
        {
            snapshot.HP = Math.Max(0, snapshot.HP - e.HealthDamage);
            snapshot.TargetId = e.AttackerId;
            snapshot.IsAggressive = true;
            snapshot.LastSeen = DateTime.UtcNow;
            this._worldState.Timestamp = DateTime.UtcNow;
        }
    }

    private void HandleItemDropped(ItemDroppedEvent e)
    {
        this._worldState.Items[e.DropId] = new ItemSnapshot
        {
            Id = e.DropId,
            ItemType = e.ItemType,
            MapId = e.MapId,
            X = e.X,
            Y = e.Y,
            IsPicked = false,
            DropTime = DateTime.UtcNow,
        };

        this._worldState.Timestamp = DateTime.UtcNow;
    }

    private void HandleItemPickedUp(ItemPickedUpEvent e)
    {
        if (this._worldState.Items.TryGetValue(e.DropId, out var snapshot))
        {
            snapshot.IsPicked = true;
            this._worldState.Timestamp = DateTime.UtcNow;
        }
    }

    private void HandlePlayerMoved(PlayerMovedEvent e)
    {
        if (this._worldState.Players.TryGetValue(e.PlayerId, out var snapshot))
        {
            snapshot.X = e.X;
            snapshot.Y = e.Y;
            snapshot.MapId = e.MapId;
            this._worldState.Timestamp = DateTime.UtcNow;
        }
    }

    private void HandlePlayerMapChanged(PlayerMapChangedEvent e)
    {
        if (this._worldState.Players.TryGetValue(e.PlayerId, out var snapshot))
        {
            snapshot.MapId = e.MapId;
            snapshot.X = e.X;
            snapshot.Y = e.Y;
            this._worldState.Timestamp = DateTime.UtcNow;
        }
    }

    private void HandlePlayerHit(PlayerHitEvent e)
    {
        if (this._worldState.Players.TryGetValue(e.PlayerId, out var snapshot))
        {
            snapshot.HP = Math.Max(0, snapshot.HP - e.HealthDamage);
            this._worldState.Timestamp = DateTime.UtcNow;
        }
    }
}
