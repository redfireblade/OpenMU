// <copyright file="AiEntity.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views.World;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.PlugIns;
using Nito.AsyncEx;

/// <summary>
/// AI 角色实体 — 服务端自主运行，不依赖客户端连接。
/// 继承 AttackableNpcBase 以复用战斗和 AOI 系统。
/// </summary>
public sealed class AiEntity : AttackableNpcBase, IBucketMapObserver, ISupportWalk
{
    private readonly Walker _walker;
    private readonly AsyncLock _moveLock = new();

    /// <summary>
    /// Gets the AI 唯一标识。
    /// </summary>
    public Guid AiId { get; } = Guid.NewGuid();

    /// <summary>
    /// Gets the 角色名称。
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the 职业编号 0=DW, 4=DK, 8=Elf。
    /// </summary>
    public byte ClassNumber { get; }

    /// <summary>
    /// Gets or sets the 等级。
    /// </summary>
    public int Level { get; set; } = 1;

    /// <summary>
    /// Gets the 感知层。
    /// </summary>
    public AiPerception Perception { get; }

    /// <summary>
    /// Gets the 主循环。
    /// </summary>
    public AiLoop Loop { get; }

    /// <summary>
    /// Gets the 行走引擎。
    /// </summary>
    public Walker Walker => this._walker;

    /// <summary>
    /// Gets the 移动锁。
    /// </summary>
    public AsyncLock MoveLock => this._moveLock;

    /// <inheritdoc/>
    public bool IsWalking => this._walker.CurrentTarget != default;

    /// <inheritdoc/>
    bool ISupportWalk.IsWalking => this.IsWalking;

    /// <inheritdoc/>
    public bool CanWalkOnSafezone => false;

    /// <inheritdoc/>
    public TimeSpan StepDelay => TimeSpan.FromMilliseconds(400);

    /// <inheritdoc/>
    public Point WalkTarget => this._walker.CurrentTarget;

    /// <inheritdoc/>
    public int InfoRange => 20;

    /// <inheritdoc/>
    public IList<Bucket<ILocateable>> ObservingBuckets { get; } = new List<Bucket<ILocateable>>();

    /// <inheritdoc/>
    public event EventHandler<BucketItemEventArgs<ILocateable>>? ItemAddedToBucket;

    /// <inheritdoc/>
    public event EventHandler<BucketItemEventArgs<ILocateable>>? ItemRemovedFromBucket;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiEntity"/> class.
    /// </summary>
    public AiEntity(
        MonsterSpawnArea spawnInfo,
        MonsterDefinition definition,
        GameMap map,
        IEventStateProvider? eventStateProvider,
        IDropGenerator dropGenerator,
        PlugInManager plugInManager,
        string name,
        byte classNumber,
        int level)
        : base(spawnInfo, definition, map, eventStateProvider, dropGenerator, plugInManager)
    {
        this.Name = name;
        this.ClassNumber = classNumber;
        this.Level = level;
        this._walker = new Walker(this, map);
        this.Perception = new AiPerception(this);
        this.Loop = new AiLoop(this);
    }

    /// <inheritdoc/>
    public override async ValueTask ApplyPoisonDamageAsync(IAttacker attacker, uint damage)
    {
        if (this.MagicEffectList.ActiveEffects.OfType<PoisonMagicEffect>().FirstOrDefault() is { } poison)
        {
            await poison.UpdateAsync(damage).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public override async ValueTask ApplyBleedingDamageAsync(IAttacker attacker, uint damage)
    {
        this.Health -= (int)damage;
        if (this.Health <= 0)
        {
            this.Health = 0;
            this.IsAlive = false;
            await this.CurrentMap!.RemoveAsync(this).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public override async ValueTask ReflectDamageAsync(IAttacker attacker, uint damage)
    {
        if (attacker is IAttackable attackable)
        {
            await attackable.AttackByAsync(this, null, false).ConfigureAwait(false);
        }
    }

    // ===== 行走 =====

    /// <summary>
    /// 使用 A* 寻路走到目标坐标。
    /// </summary>
    public async ValueTask<bool> WalkToAsync(Point target)
    {
        if (!this.IsAlive) return false;
        if (this.IsWalking) await this._walker.StopAsync();

        var map = this.CurrentMap;
        if (map is null) return false;

        using var pathFinder = new PathFinder(map.Terrain.AIgrid);
        var path = pathFinder.FindPath(this.Position, target);
        if (path is null || path.Count == 0) return false;

        var walkMap = map.Terrain.WalkMap;
        if (!path.All(p => walkMap[p.X, p.Y])) return false;

        var batch = path.Take(16).ToList();
        var steps = this.BuildSteps(batch);
        var finalTarget = steps[^1].To;

        await this._walker.InitializeWalkToAsync(finalTarget, steps);
        await map.MoveAsync(this, finalTarget, this._moveLock, MoveType.Walk);
        this._walker.StartWalkAsync(CancellationToken.None);
        return true;
    }

    /// <summary>停止行走。</summary>
    public async ValueTask StopWalkingAsync() => await this._walker.StopAsync();

    private WalkingStep[] BuildSteps(List<Point> nodes)
    {
        var steps = new WalkingStep[nodes.Count];
        var prev = this.Position;
        for (int i = 0; i < nodes.Count; i++)
        {
            var dir = prev.GetDirectionTo(nodes[i]);
            steps[i] = new WalkingStep(prev, nodes[i], dir);
            prev = nodes[i];
        }
        return steps;
    }

    // ===== AOI 感知 =====

    /// <inheritdoc/>
    public async ValueTask LocateableAddedAsync(ILocateable item)
    {
        if (item is Monster { IsAlive: true } monster
            && !monster.IsAtSafezone()
            && monster.Definition?.NpcWindow == DataModel.Configuration.NpcWindow.Undefined
            && monster.Definition?.ObjectKind == NpcObjectKind.Monster)
        {
            this.Perception.AddMonster(monster);
        }
        if (item is NonPlayerCharacter npc && npc.Definition?.Number is >= 200 and <= 300)
        {
            this.Perception.AddNpc(npc);
        }
        if (item is DroppedItem || item is DroppedMoney)
        {
            this.Perception.AddDrop(item);
        }
        await ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask LocateableRemovedAsync(ILocateable item)
    {
        this.Perception.Remove(item);
        await ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask NewLocateablesInScopeAsync(IEnumerable<ILocateable> newObjects)
    {
        foreach (var obj in newObjects) await this.LocateableAddedAsync(obj);
    }

    /// <inheritdoc/>
    public async ValueTask LocateablesOutOfScopeAsync(IEnumerable<ILocateable> oldObjects)
    {
        foreach (var obj in oldObjects) await this.LocateableRemovedAsync(obj);
    }

    /// <inheritdoc/>
    public bool CanWalkOn(Point target)
    {
        return this.CurrentMap?.Terrain.AIgrid[target.X, target.Y] == 1;
    }

    /// <inheritdoc/>
    public ValueTask<int> GetDirectionsAsync(Memory<Direction> directions) => this._walker.GetDirectionsAsync(directions);

    /// <inheritdoc/>
    public ValueTask<int> GetStepsAsync(Memory<WalkingStep> steps) => this._walker.GetStepsAsync(steps);

    /// <summary>能否行动。</summary>
    public bool CanAct() => this.IsAlive && !this.IsTeleporting;

    /// <summary>初始化实体：加入地图。</summary>
    public async ValueTask InitializeAsync()
    {
        if (this.CurrentMap is not null)
        {
            await this.CurrentMap.AddAsync(this);
            this.IsAlive = true;
            this.Health = (int)(this.Attributes?[Stats.MaximumHealth] ?? 1);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"[{this.Name}] Lv{this.Level} at {this.Position}";
}
