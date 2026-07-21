// <copyright file="AiEntity.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.Views.World;
using MUnique.OpenMU.Pathfinding;
using Nito.AsyncEx;

/// <summary>
/// AI 角色实体 — 继承 Player 以在客户端显示为黄点(玩家标记)。
/// Player 自带 IAttackable/IAttacker/IBucketMapObserver/ISupportWalk/Walker。
/// </summary>
public sealed class AiEntity : Player
{
    private readonly AsyncLock _moveLock = new();
    private readonly FullGridNetwork _network;

    /// <summary>唯一标识。</summary>
    public Guid AiId { get; } = Guid.NewGuid();

    /// <summary>角色名称。</summary>
    public new string Name => this.SelectedCharacter?.Name ?? "AI";

    /// <summary>职业编号。</summary>
    public byte ClassNumber { get; }

    /// <summary>等级(覆盖Player.Level)。</summary>
    public new int Level { get; set; } = 1;

    /// <summary>感知层。</summary>
    public AiPerception Perception { get; }

    /// <summary>主循环。</summary>
    public AiLoop Loop { get; }

    /// <summary>出生坐标。</summary>
    public Point InitialPosition { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AiEntity"/> class.
    /// </summary>
    public AiEntity(IGameContext gameContext, string name, byte classNumber, int level)
        : base(gameContext)
    {
        this.ClassNumber = classNumber;
        this.Level = level;
        this.NameOverride = name;
        this._network = new FullGridNetwork(allowDiagonals: true);
        this.Perception = new AiPerception(this);
        this.Loop = new AiLoop(this);
    }

    /// <summary>构造函数时设置的名字（Player.Name 来自 SelectedCharacter）。</summary>
    private string NameOverride { get; set; } = "AI";

    // ===== 行走 =====

    /// <summary>A* 寻路走到目标。</summary>
    public async ValueTask<bool> WalkTargetAsync(Point target)
    {
        if (this.IsWalking) await this.StopWalkingAsync();

        var map = this.CurrentMap;
        if (map is null) return false;

        var pf = new PathFinder(this._network);
        pf.ResetPathFinder();
        var path = pf.FindPath(this.Position, target, map.Terrain.AIgrid, this.CanWalkOnSafezone);
        if (path is null || path.Count == 0) return false;

        var walkMap = map.Terrain.WalkMap;
        if (!path.All(p => walkMap[p.X, p.Y])) return false;

        var batch = path.Take(16).ToList();
        var steps = new WalkingStep[batch.Count];
        var prev = this.Position;
        for (int i = 0; i < batch.Count; i++)
        {
            var cur = batch[i].Point;
            steps[i] = new WalkingStep(prev, cur, prev.GetDirectionTo(cur));
            prev = cur;
        }

        var finalTarget = steps[^1].To;
        await this.WalkToAsync(finalTarget, steps);
        return true;
    }

    // ===== 攻击 =====

    /// <summary>攻击目标。</summary>
    public async ValueTask AttackTargetAsync(IAttackable target)
    {
        if (this.IsAtSafezone() || target.IsAtSafezone()) return;
        var hitInfo = await target.AttackByAsync(this, null, false);
        if (hitInfo?.HealthDamage < 50 && target is Monster m && m.IsAlive)
            await target.ApplyBleedingDamageAsync(this, 50);
    }

    // ===== 初始化 =====

    /// <summary>初始化：设置属性、加入地图。</summary>
    public async ValueTask InitializeAsync(GameMap map)
    {
        // 设置出生位置
        if (this.InitialPosition != default)
            this.Position = this.InitialPosition;

        await map.AddAsync(this);
    }

    // ===== Player 需要的最小 ViewPlugIn =====

    /// <inheritdoc/>
    protected override MUnique.OpenMU.PlugIns.ICustomPlugInContainer<MUnique.OpenMU.GameLogic.Views.IViewPlugIn> CreateViewPlugInContainer()
        => new AiPlayerViewPlugInContainer();

    /// <inheritdoc/>
    public override string ToString() => $"[{this.NameOverride}] Lv{this.Level} at {this.Position}";
}

/// <summary>
/// AI 角色的最小 ViewPlugIn 容器 — 返回 null 给所有插件调用。
/// </summary>
internal sealed class AiPlayerViewPlugInContainer : MUnique.OpenMU.PlugIns.ICustomPlugInContainer<MUnique.OpenMU.GameLogic.Views.IViewPlugIn>
{
    public T? GetPlugIn<T>() where T : class, MUnique.OpenMU.GameLogic.Views.IViewPlugIn
        => default;
}
