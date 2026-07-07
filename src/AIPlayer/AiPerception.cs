// <copyright file="AiPerception.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;

/// <summary>
/// AI 角色的感知层 — 通过 IBucketMapObserver 事件收集周围环境信息。
/// 维护可见怪物/NPC/掉落物列表。
/// </summary>
public sealed class AiPerception
{
    private readonly AiEntity _entity;
    private readonly List<Monster> _visibleMonsters = new();
    private readonly List<NonPlayerCharacter> _visibleNpcs = new();
    private readonly List<ILocateable> _visibleDrops = new();

    internal AiPerception(AiEntity entity)
    {
        this._entity = entity;
    }

    /// <summary>获取当前可见的怪物列表。</summary>
    public IReadOnlyList<Monster> VisibleMonsters => this._visibleMonsters.AsReadOnly();

    /// <summary>获取当前可见的 NPC 列表。</summary>
    public IReadOnlyList<NonPlayerCharacter> VisibleNpcs => this._visibleNpcs.AsReadOnly();

    /// <summary>获取当前可见的掉落物列表。</summary>
    public IReadOnlyList<ILocateable> VisibleDrops => this._visibleDrops.AsReadOnly();

    /// <summary>获取最近的可攻击怪物。</summary>
    public Monster? NearestMonster =>
        this._visibleMonsters
            .Where(m => m.IsAlive)
            .MinBy(m => m.GetDistanceTo(this._entity));

    /// <summary>获取视野内怪物数量。</summary>
    public int MonsterCount => this._visibleMonsters.Count(m => m.IsAlive);

    internal void AddMonster(Monster monster)
    {
        if (!this._visibleMonsters.Contains(monster))
            this._visibleMonsters.Add(monster);
    }

    internal void AddNpc(NonPlayerCharacter npc)
    {
        if (!this._visibleNpcs.Contains(npc))
            this._visibleNpcs.Add(npc);
    }

    internal void AddDrop(ILocateable drop)
    {
        if (!this._visibleDrops.Contains(drop))
            this._visibleDrops.Add(drop);
    }

    internal void Remove(ILocateable obj)
    {
        if (obj is Monster m) this._visibleMonsters.Remove(m);
        else if (obj is NonPlayerCharacter n) this._visibleNpcs.Remove(n);
        else this._visibleDrops.Remove(obj);
    }

    /// <summary>按 NPC Number 找特定 NPC。</summary>
    public NonPlayerCharacter? FindNpc(short number)
    {
        return this._visibleNpcs.FirstOrDefault(n => n.Definition?.Number == number);
    }

    /// <summary>清空所有列表。</summary>
    internal void Clear()
    {
        this._visibleMonsters.Clear();
        this._visibleNpcs.Clear();
        this._visibleDrops.Clear();
    }
}
