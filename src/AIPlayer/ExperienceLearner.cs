// <copyright file="ExperienceLearner.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// AI 角色经验学习器 — 每个 AI 角色一个实例。
/// 每 tick 收集战斗/拾取/死亡/行走数据，写入 <see cref="ExperienceMemory"/>
/// 和 <see cref="CharacterMemory"/>，用于长期经验积累。
///
/// 学习内容：
/// 1. 哪里打怪经验多（按地图/怪物统计）
/// 2. 哪里掉好东西（按坐标记录掉落）
/// 3. 哪里捡钱多（按区域统计金币）
/// 4. 哪里容易死（按地图/怪物统计死亡）
/// 5. 什么等级适合去哪里（记录访问等级）
/// </summary>
public sealed class ExperienceLearner
{
    private readonly AiPlayer _player;
    private readonly ExperienceMemory _expMem;
    private readonly CharacterMemory _charMem;
    private readonly ILogger _logger;

    // 学习周期：每 5 tick 采集一次（约 2 秒间隔，减少性能开销）
    private const int SampleInterval = 5;
    private int _tickCounter;

    // 当前 tick 的累计数据
    private long _expGainedThisTick;
    private short _lastKilledMonsterId;
    private int _killedMonsterCount;
    private int _deathCount;

    // 当前所在的热点位置（用于 HotspotStats 更新）
    private int _currentMapNum;
    private Point _currentPos;

    // 标记是否需要全量保存
    private bool _dirty;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExperienceLearner"/> class.
    /// </summary>
    public ExperienceLearner(AiPlayer player, ExperienceMemory expMem, CharacterMemory charMem, ILogger logger)
    {
        this._player = player;
        this._expMem = expMem;
        this._charMem = charMem;
        this._logger = logger;
    }

    /// <summary>
    /// 每 tick 调用 — 收集学习数据。
    /// </summary>
    public void Tick()
    {
        this._tickCounter++;

        // 只在采样周期执行
        if (this._tickCounter % SampleInterval != 0) return;

        var map = this._player.CurrentMap;
        if (map is null) return;

        this._currentMapNum = map.Definition.Number;
        this._currentPos = this._player.Position;

        // 1. 学习地图访问
        this.LearnMapVisit();

        // 2. 学习当前位置的热点活动
        this.LearnHotspotActivity();

        // 3. 处理累计的战斗数据
        this.FlushCombatData();
    }

    /// <summary>
    /// 外部通知 — 一个怪物被击杀（由 GameAdapter.HitAsync/HitWithSkillAsync 调用）。
    /// </summary>
    public void NotifyMonsterKilled(short monsterNumber, short monsterLevel, long expGained)
    {
        this._expGainedThisTick += expGained;
        this._lastKilledMonsterId = monsterNumber;
        this._killedMonsterCount++;
        this._dirty = true;
    }

    /// <summary>
    /// 外部通知 — 物品被拾取。
    /// </summary>
    public void NotifyItemPickup(short monsterNumber, int itemGroup, int itemNumber, byte x, byte y, bool isExcellent, bool isJewel)
    {
        var map = this._player.CurrentMap;
        var mapNum = map?.Definition.Number ?? 0;

        this._expMem.RecordDrop(mapNum, monsterNumber, itemGroup, itemNumber, x, y, isExcellent, isJewel);
        this._dirty = true;

        // 也记录到 HotspotStats
        var hotspot = this._charMem.GetOrCreateHotspot((ushort)mapNum, x, y);
        hotspot.TotalDrops++;
        if (isExcellent) hotspot.ExcellentDrops++;
        if (isJewel) hotspot.JewelDrops++;
    }

    /// <summary>
    /// 外部通知 — 角色死亡。
    /// </summary>
    public void NotifyDeath(short? killerMonsterNumber)
    {
        var map = this._player.CurrentMap;
        var mapNum = map?.Definition.Number ?? 0;

        this._expMem.RecordDeath(mapNum, killerMonsterNumber, this._player.Level);
        this._deathCount++;
        this._dirty = true;
    }

    /// <summary>
    /// 外部通知 — 拾取金币。
    /// </summary>
    public void NotifyGoldPickup(int goldAmount, byte x, byte y)
    {
        var map = this._player.CurrentMap;
        var mapNum = map?.Definition.Number ?? 0;

        this._expMem.RecordGoldPickup(mapNum, goldAmount, x, y);
        this._dirty = true;
    }

    /// <summary>
    /// 检查是否有未保存的更改。
    /// </summary>
    public bool IsDirty => this._dirty;

    private void LearnMapVisit()
    {
        var map = this._player.CurrentMap;
        if (map is null) return;

        var mapNum = map.Definition.Number;
        this._expMem.RecordMapVisit(mapNum, this._player.Level);
    }

    private void LearnHotspotActivity()
    {
        var map = this._player.CurrentMap;
        if (map is null) return;

        // 每采样周期 + 1 秒活跃时间
        var secondsIncrement = (double)SampleInterval * 0.4; // 每个 tick 400ms

        var hotspot = this._charMem.GetOrCreateHotspot(
            (ushort)map.Definition.Number,
            (byte)this._player.Position.X,
            (byte)this._player.Position.Y);

        hotspot.TotalActiveSeconds += secondsIncrement;
        hotspot.LastVisitedAt = DateTime.UtcNow;
        hotspot.SessionsCount++;

        // 如果当前位置有击杀，记录到 hotspot
        if (this._killedMonsterCount > 0)
        {
            hotspot.TotalKills += this._killedMonsterCount;
            if (this._expGainedThisTick > 0)
            {
                hotspot.TotalExperience += this._expGainedThisTick;
            }
        }
    }

    private void FlushCombatData()
    {
        if (this._killedMonsterCount <= 0 && this._expGainedThisTick <= 0)
        {
            // 检查是否有未记录的击杀——通过检查 lastKilledMonsterId
            if (this._lastKilledMonsterId == 0) return;
        }

        var map = this._player.CurrentMap;
        if (map is null) return;

        var mapNum = map.Definition.Number;

        // 如果有击杀，记录到 ExperienceMemory
        if (this._lastKilledMonsterId > 0 && this._expGainedThisTick > 0)
        {
            // 获取怪物等级（从当前地图的 MonsterSpawns 中查找）
            short monsterLevel = 0;
            try
            {
                var monsterDef = map.Definition.MonsterSpawns?
                    .FirstOrDefault(s => s.MonsterDefinition?.Number == this._lastKilledMonsterId)
                    ?.MonsterDefinition;

                if (monsterDef?.Attributes is not null)
                {
                    var levelAttr = monsterDef.Attributes
                        .FirstOrDefault(a => a.AttributeDefinition == Stats.Level);
                    if (levelAttr is not null)
                    {
                        monsterLevel = (short)levelAttr.Value;
                    }
                }
            }
            catch
            {
                // ignore lookup errors
            }

            this._expMem.RecordKill(
                mapNum,
                this._lastKilledMonsterId,
                monsterLevel,
                this._expGainedThisTick,
                (byte)this._player.Position.X,
                (byte)this._player.Position.Y);
        }

        // 重置累计计数器
        this._expGainedThisTick = 0;
        this._lastKilledMonsterId = 0;
        this._killedMonsterCount = 0;
    }
}
