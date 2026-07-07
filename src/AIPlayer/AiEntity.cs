// <copyright file="AiEntity.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

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
/// 继承 AttackableNpcBase 以复用战斗、血量、死亡、AOI 系统。
/// 实现 IBucketMapObserver 以感知周围环境。
/// </summary>
public sealed class AiEntity : AttackableNpcBase, IAttacker, IBucketMapObserver, ISupportWalk
{
    private readonly Walker _walker;
    private readonly AsyncLock _moveLock = new();
    private readonly FullGridNetwork _network;

    /// <summary>唯一标识。</summary>
    public Guid AiId { get; } = Guid.NewGuid();

    /// <summary>角色名称。</summary>
    public string Name { get; }

    /// <summary>职业编号 0=DW 4=DK 8=Elf。</summary>
    public byte ClassNumber { get; }

    /// <summary>等级。</summary>
    public int Level { get; set; } = 1;

    /// <summary>经验值。</summary>
    public long Experience { get; set; }

    /// <inheritdoc/>
    public ComboStateMachine? ComboState => null;

    /// <summary>感知层。</summary>
    public AiPerception Perception { get; }

    /// <summary>主循环。</summary>
    public AiLoop Loop { get; }

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
        this._network = new FullGridNetwork(allowDiagonals: true);
        this._walker = new Walker(this, () => this.StepDelay);
        this.Perception = new AiPerception(this);
        this.Loop = new AiLoop(this);
    }

    /// <inheritdoc/>
    public override async ValueTask ApplyPoisonDamageAsync(IAttacker attacker, uint damage) { await ValueTask.CompletedTask; }

    /// <inheritdoc/>
    public override async ValueTask ApplyBleedingDamageAsync(IAttacker attacker, uint damage)
    {
        this.Health -= (int)damage;
        if (this.Health <= 0)
        {
            this.Health = 0;
            await this.OnDeathAsync(attacker).ConfigureAwait(false);
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

        var path = this.FindPath(target, map);
        if (path is null || path.Count == 0) return false;

        var walkMap = map.Terrain.WalkMap;
        if (!path.All(p => walkMap[p.X, p.Y])) return false;

        var batch = path.Take(16).ToList();
        var steps = this.BuildSteps(batch);
        var finalTarget = steps[^1].To;

        await this._walker.InitializeWalkToAsync(finalTarget, steps);
        await map.MoveAsync(this, finalTarget, this._moveLock, MoveType.Walk);
        await this._walker.StartWalkAsync(Guid.NewGuid());
        return true;
    }

    public async ValueTask StopWalkingAsync() => await this._walker.StopAsync();

    private IList<PathResultNode>? FindPath(Point target, GameMap map)
    {
        var pf = new PathFinder(this._network);
        pf.ResetPathFinder();
        return pf.FindPath(this.Position, target, map.Terrain.AIgrid, this.CanWalkOnSafezone);
    }

    private WalkingStep[] BuildSteps(IList<PathResultNode> nodes)
    {
        var steps = new WalkingStep[nodes.Count];
        var prev = this.Position;
        for (int i = 0; i < nodes.Count; i++)
        {
            var current = nodes[i].Point;
            var dir = prev.GetDirectionTo(current);
            steps[i] = new WalkingStep(prev, current, dir);
            prev = current;
        }
        return steps;
    }

    // ===== AOI 感知 =====

    public async ValueTask LocateableAddedAsync(ILocateable item)
    {
        if (item is Monster m && IsValidMonster(m))
            this.Perception.AddMonster(m);
        else if (item is DroppedItem || item is DroppedMoney)
            this.Perception.AddDrop(item);
        await ValueTask.CompletedTask;
    }

    /// <summary>
    /// 综合判断目标是否为有效的野外怪物（不是守卫/NPC/安全区内目标）。
    /// </summary>
    private static bool IsValidMonster(Monster m)
    {
        if (!m.IsAlive) return false;
        if (m.IsAtSafezone()) return false;

        var def = m.Definition;
        if (def is null) return false;

        // 1. 必须是怪物类型
        if (def.ObjectKind != NpcObjectKind.Monster) return false;

        // 2. 不能有 NPC 功能窗口（Guard=15, Merchant=3, Vault=11 等）
        if (def.NpcWindow != DataModel.Configuration.NpcWindow.Undefined) return false;

        // 3. 守卫编号黑名单
        var guardIds = new HashSet<short> { 247, 249, 251, 253, 254, 255, 240 };
        if (guardIds.Contains(def.Number)) return false;

        // 4. 守卫名称关键字过滤 (Designation 是 LocalizedString 结构体)
        try
        {
            string name = def.Designation;
            if (!string.IsNullOrWhiteSpace(name))
            {
                var upper = name.ToUpperInvariant();
                if (upper.Contains("GUARD") || upper.Contains("SOLDIER")
                    || upper.Contains("BOWGIRL") || upper.Contains("SIEGEWARFARE")
                    || upper.Contains("SENATUS"))
                    return false;
            }
        }
        catch { /* 名称读取失败时放行 */ }

        return true;
    }

    public async ValueTask LocateableRemovedAsync(ILocateable item)
    {
        this.Perception.Remove(item);
        await ValueTask.CompletedTask;
    }

    public async ValueTask NewLocateablesInScopeAsync(IEnumerable<ILocateable> objects)
    {
        foreach (var obj in objects) await this.LocateableAddedAsync(obj);
    }

    public async ValueTask LocateablesOutOfScopeAsync(IEnumerable<ILocateable> objects)
    {
        foreach (var obj in objects) await this.LocateableRemovedAsync(obj);
    }

    public bool CanWalkOn(Point target) => this.CurrentMap?.Terrain.AIgrid[target.X, target.Y] == 1;

    public ValueTask<int> GetDirectionsAsync(Memory<Direction> directions) => this._walker.GetDirectionsAsync(directions);
    public ValueTask<int> GetStepsAsync(Memory<WalkingStep> steps) => this._walker.GetStepsAsync(steps);

    public bool CanAct() => this.IsAlive && !this.IsTeleporting;

    /// <summary>初始化：加入地图。</summary>
    public async ValueTask InitializeAsync()
    {
        if (this.CurrentMap is not null)
        {
            await this.CurrentMap.AddAsync(this);
        }
    }

    public override string ToString() => $"[{this.Name}] Lv{this.Level} at {this.Position}";
}
