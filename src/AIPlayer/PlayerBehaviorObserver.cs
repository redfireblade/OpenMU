// <copyright file="PlayerBehaviorObserver.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 玩家行为观察系统 — 深度分析真实玩家的行为模式。
/// 每 ~1.2 秒采样一次玩家状态，检测以下行为：
///
/// ① 打怪行为: 攻击距离、目标选择、攻击频率
/// ② 技能使用: 什么技能、技能循环、技能优先级
/// ③ 寻径导航: 行走路线、路径点、跨地图方式
/// ④ 药水管理: 喝红/蓝的血量阈值、使用频率
/// ⑤ 装备管理: 换装条件、属性阈值
/// ⑥ 任务行为: 接任务、完成任务、任务寻路
/// ⑦ 经济行为: 捡金币、买卖物品
///
/// 所有分析结果存入 PlayerBehaviorRecord，可通过 API 查询。
/// 数据持续积累，AI 角色通过此记录学习玩家的高效策略。
/// </summary>
public sealed class PlayerBehaviorObserver
{
    private readonly IGameContext _gameContext;
    private readonly ExperienceMemory _collectiveExpMem;
    private readonly ILogger _logger;

    /// <summary>采样频率：每 N 次 Tick() 调用采集一次。</summary>
    private const int SampleInterval = 3;
    private int _tickCounter;

    /// <summary>每个玩家最近 50 步行走路径（用于分析寻径模式）。</summary>
    private const int MaxPathHistory = 50;

    /// <summary>玩家行为快照 — 每次采样时的全状态。</summary>
    public sealed class PlayerSnapshot
    {
        public Point Position;
        public int MapNumber;
        public int Level;
        public long Experience;
        public int Hp, MaxHp;
        public int Mana, MaxMana;
        public int Gold;
        public short Strength, Agility, Vitality, Energy;
        public double PhysAttackMin, PhysAttackMax;
        public double WizAttackMin, WizAttackMax;
        public double Defense;
        public DateTime Timestamp;
        public bool IsWalking;
        public string? CurrentMapName;
        public IAttackable? LastAttackedTarget;

        /// <summary>克隆当前状态到新对象。</summary>
        public PlayerSnapshot Clone() => (PlayerSnapshot)this.MemberwiseClone();
    }

    /// <summary>玩家行为记录 — 从多轮采样中提取的行为模式。</summary>
    public sealed class BehaviorRecord
    {
        public string CharacterName = string.Empty;
        public int LastSeenLevel;

        // ===== 攻击行为分析 =====
        /// <summary>攻击距离统计: distance → count</summary>
        public Dictionary<int, int> AttackDistanceHistogram { get; set; } = new();
        /// <summary>攻击的怪物类型偏好: monsterNumber → count</summary>
        public Dictionary<short, int> AttackedMonsters { get; set; } = new();
        /// <summary>平均攻击距离（最后一次更新值）</summary>
        public double AverageAttackDistance;
        /// <summary>攻击距离采样数</summary>
        public int AttackDistanceSamples;

        // ===== 技能使用分析 =====
        /// <summary>技能使用统计: skillNumber → count</summary>
        public Dictionary<ushort, int> SkillUsage { get; set; } = new();
        /// <summary>最后一次使用的技能编号</summary>
        public ushort LastSkillUsed;

        // ===== 药水使用分析 =====
        /// <summary>HP药水使用记录: (血量百分比, 恢复量)</summary>
        public List<PotionRecord> HpPotions { get; set; } = new();
        /// <summary>MP药水使用记录</summary>
        public List<PotionRecord> MpPotions { get; set; } = new();
        /// <summary>推断的HP喝药阈值（百分比）</summary>
        public double InferredHpPotionThreshold = 0.5;
        /// <summary>推断的MP喝药阈值（百分比）</summary>
        public double InferredMpPotionThreshold = 0.3;

        // ===== 行走/寻径分析 =====
        /// <summary>路径历史 (最近的行走路径点)</summary>
        public List<PathSegment> RecentPath { get; set; } = new();
        /// <summary>寻径偏好: 是否偏向于走大路/穿捷径</summary>
        public string? NavigationStyle;
        /// <summary>平均行走速度 (格/秒)</summary>
        public double AverageWalkSpeed;

        // ===== 装备分析 =====
        /// <summary>属性变化记录: 记录何时换装</summary>
        public List<EquipmentChange> EquipmentChanges { get; set; } = new();
        /// <summary>当前装备评分变化趋势</summary>
        public double EquipmentScore;

        // ===== 任务分析 =====
        /// <summary>任务组号 → 任务完成次数</summary>
        public Dictionary<short, int> QuestCompletions { get; set; } = new();
        /// <summary>活跃任务列表历史</summary>
        public List<string> ActiveQuestHistory { get; set; } = new();

        // ===== 经济行为 =====
        /// <summary>累计拾取金币</summary>
        public long TotalGoldPicked;
        /// <summary>累计花费金币</summary>
        public long TotalGoldSpent;
    }

    /// <summary>药水使用记录。</summary>
    public sealed class PotionRecord
    {
        public double HpPercentBefore;   // 喝药前的血量%
        public int Amount;               // 恢复量
        public DateTime Timestamp;
        public int MapNumber;
        public byte X, Y;
    }

    /// <summary>路径段记录。</summary>
    public sealed class PathSegment
    {
        public Point From, To;
        public DateTime Timestamp;
        public int MapNumber;
        public double Distance;
        public double DurationSeconds;
    }

    /// <summary>装备变更记录。</summary>
    public sealed class EquipmentChange
    {
        public DateTime Timestamp;
        public int Level;
        public double PhysAttackBefore, PhysAttackAfter;
        public double DefenseBefore, DefenseAfter;
        public string? Description;
    }

    /// <summary>所有被观察玩家的行为记录。</summary>
    public IReadOnlyDictionary<string, BehaviorRecord> BehaviorRecords => this._behaviorRecords;
    private readonly Dictionary<string, BehaviorRecord> _behaviorRecords = new();

    /// <summary>所有被观察玩家的最新快照。</summary>
    public IReadOnlyDictionary<string, PlayerSnapshot> LatestSnapshots => this._playerSnapshots;
    private readonly Dictionary<string, PlayerSnapshot> _playerSnapshots = new();

    /// <summary>玩家路径历史 (用于分析寻径模式)。</summary>
    private readonly Dictionary<string, List<(Point Pos, int MapNum, DateTime Time)>> _playerPaths = new();

    /// <summary>上次采样时玩家的HP/MP，用于检测恢复。</summary>
    private readonly Dictionary<string, (int Hp, int Mana, int Gold)> _lastResources = new();

    /// <summary>玩家属性缓存，用于检测换装。</summary>
    private readonly Dictionary<string, (double PhysAtk, double Def)> _lastEquipmentStats = new();

    /// <summary>
    /// 行为事件存储 — 记录所有可观测的玩家离散行为事件。
    /// 由 AiPlayerManager 注入，用于持久化分析数据供 AI 学习。
    /// </summary>
    private BehaviorEventStore? _eventStore;

    /// <summary>上次采样的属性值，用于检测加点事件。</summary>
    private readonly Dictionary<string, (short Str, short Agi, short Vit, short Ene)> _lastAttributes = new();

    /// <summary>上次采样的技能列表数量，用于检测学习技能事件。</summary>
    private readonly Dictionary<string, int> _lastSkillCount = new();

    /// <summary>上次采样时的装备物品（按slot），用于检测装备变更事件。</summary>
    private readonly Dictionary<string, Dictionary<int, string>> _lastEquipmentItems = new();

    /// <summary>上次 NPC 对话状态（是否正在对话），用于检测对话开始/结束。</summary>
    private readonly Dictionary<string, bool> _lastDialogState = new();

    /// <summary>上次采样的完整背包快照（用于检测新物品）。</summary>
    private readonly Dictionary<string, HashSet<string>> _lastInventoryItems = new();

    /// <summary>行为事件计数器 — 每个角色自上次保存以来的新事件数。</summary>
    private readonly Dictionary<string, int> _pendingEvents = new();

    /// <summary>上次检测到的BUFF效果列表（用于检测新增BUFF）。</summary>
    private readonly Dictionary<string, HashSet<int>> _lastBuffEffects = new();

    /// <summary>已知的BUFF NPC 编号列表 — 这些NPC的对话选项1通常是获得BUFF。</summary>
    private static readonly HashSet<short> KnownBuffNpcs = new() { 257 }; // Elf Soldier

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayerBehaviorObserver"/> class.
    /// </summary>
    public PlayerBehaviorObserver(IGameContext gameContext, ExperienceMemory collectiveExpMem, ILogger logger)
    {
        this._gameContext = gameContext;
        this._collectiveExpMem = collectiveExpMem;
        this._logger = logger;
    }

    /// <summary>
    /// 设置行为事件存储（由 AiPlayerManager 注入）。
    /// </summary>
    public void SetEventStore(BehaviorEventStore eventStore)
    {
        this._eventStore = eventStore;
    }

    /// <summary>
    /// 每 tick 调用 — 扫描真实玩家并分析行为。
    /// </summary>
    public void Tick()
    {
        this._tickCounter++;
        if (this._tickCounter % SampleInterval != 0) return;

        if (this._gameContext is not GameContext gc) return;
        this.ScanPlayers(gc);
    }

    /// <summary>
    /// 获取指定玩家的行为记录摘要（用于Web面板展示）。
    /// </summary>
    public string GetBehaviorSummary(string playerName)
    {
        if (!this._behaviorRecords.TryGetValue(playerName, out var r))
            return "暂无数据";

        var attackDistSummary = r.AttackDistanceHistogram.Any()
            ? string.Join(", ", r.AttackDistanceHistogram.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}格×{kv.Value}"))
            : "无数据";

        var skillSummary = r.SkillUsage.Any()
            ? string.Join(", ", r.SkillUsage.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"#{kv.Key}({kv.Value}次)"))
            : "无数据";

        var hpThreshold = r.HpPotions.Any()
            ? $"{r.HpPotions.Average(p => p.HpPercentBefore):F1}%"
            : "未知";

        var mpThreshold = r.MpPotions.Any()
            ? $"{r.MpPotions.Average(p => p.HpPercentBefore):F1}%"  // Intentional: MpPotions also stores HpPercentBefore
            : "未知";

        var avgDist = r.AttackDistanceSamples > 0
            ? $"{r.AverageAttackDistance:F1}格"
            : "未知";

        return $"Lv{r.LastSeenLevel} | " +
               $"攻击距离: {avgDist} [{attackDistSummary}] | " +
               $"技能: [{skillSummary}] | " +
               $"喝红阈值: {hpThreshold} 喝蓝阈值: {mpThreshold} | " +
               $"换装: {r.EquipmentChanges.Count}次 | " +
               $"拾取金币: {r.TotalGoldPicked}";
    }

    private void ScanPlayers(GameContext gc)
    {
        var observedCount = 0;

        foreach (var kvp in gc.PlayersByCharacterName)
        {
            var player = kvp.Value;
            if (player is AiPlayer) continue;
            if (string.IsNullOrEmpty(kvp.Key)) continue;

            observedCount++;
            this.AnalyzePlayer(player, kvp.Key);
        }

        this.CleanupStaleRecords();

        if (observedCount > 0 && this._tickCounter % 30 == 0)
        {
            this._logger.LogDebug("[PlayerObserver] 分析 {Count} 个真实玩家, {Records} 份行为记录",
                observedCount, this._behaviorRecords.Count);
        }
    }

    private void AnalyzePlayer(Player player, string name)
    {
        var now = DateTime.UtcNow;
        var pos = player.Position;
        var map = player.CurrentMap;
        var mapNum = map?.Definition.Number ?? 0;

        // 获取或创建行为记录
        if (!this._behaviorRecords.TryGetValue(name, out var record))
        {
            record = new BehaviorRecord { CharacterName = name };
            this._behaviorRecords[name] = record;
        }
        record.LastSeenLevel = player.Level;

        // 创建当前快照
        var snapshot = new PlayerSnapshot
        {
            Position = pos,
            MapNumber = mapNum,
            Level = player.Level,
            Experience = player.SelectedCharacter?.Experience ?? 0,
            Hp = (int)(player.Attributes?[Stats.CurrentHealth] ?? 0),
            MaxHp = (int)(player.Attributes?[Stats.MaximumHealth] ?? 0),
            Mana = (int)(player.Attributes?[Stats.CurrentMana] ?? 0),
            MaxMana = (int)(player.Attributes?[Stats.MaximumMana] ?? 0),
            Gold = player.Money,
            Strength = (short)(player.Attributes?[Stats.BaseStrength] ?? 0),
            Agility = (short)(player.Attributes?[Stats.BaseAgility] ?? 0),
            Vitality = (short)(player.Attributes?[Stats.BaseVitality] ?? 0),
            Energy = (short)(player.Attributes?[Stats.BaseEnergy] ?? 0),
            PhysAttackMin = player.Attributes?[Stats.MinimumPhysBaseDmg] ?? 0,
            PhysAttackMax = player.Attributes?[Stats.MaximumPhysBaseDmg] ?? 0,
            WizAttackMin = player.Attributes?[Stats.MinimumWizBaseDmg] ?? 0,
            WizAttackMax = player.Attributes?[Stats.MaximumWizBaseDmg] ?? 0,
            Defense = player.Attributes?[Stats.DefenseBase] ?? 0,
            IsWalking = player.IsWalking,
            CurrentMapName = map?.Definition.Name,
            Timestamp = now,
            LastAttackedTarget = player.LastAttackedTarget.TryGetTarget(out var t) ? t : null,
        };
        this._playerSnapshots[name] = snapshot;

        if (map is null) return;

        // ═══════════════════════════════════════════════════════
        // 1. 攻击行为分析
        // ═══════════════════════════════════════════════════════
        this.AnalyzeAttackBehavior(player, snapshot, record, mapNum);

        // ═══════════════════════════════════════════════════════
        // 2. 药水/恢复行为分析
        // ═══════════════════════════════════════════════════════
        this.AnalyzePotionBehavior(player, snapshot, record, name, mapNum);

        // ═══════════════════════════════════════════════════════
        // 3. 行走/寻径分析
        // ═══════════════════════════════════════════════════════
        this.AnalyzeNavigation(player, snapshot, record, name, mapNum);

        // ═══════════════════════════════════════════════════════
        // 4. 装备变更分析
        // ═══════════════════════════════════════════════════════
        this.AnalyzeEquipment(player, snapshot, record, name);

        // ═══════════════════════════════════════════════════════
        // 5. 任务行为分析
        // ═══════════════════════════════════════════════════════
        this.AnalyzeQuestBehavior(player, record);

        // ═══════════════════════════════════════════════════════
        // 6. 经济行为分析
        // ═══════════════════════════════════════════════════════
        this.AnalyzeEconomy(player, snapshot, record, name, mapNum, pos);

        // ═══════════════════════════════════════════════════════
        // 7. 更新集体经验记忆
        // ═══════════════════════════════════════════════════════
        this.UpdateCollectiveMemory(snapshot, record, mapNum);

        // ═══════════════════════════════════════════════════════
        // 8. 更新技能检测
        // ═══════════════════════════════════════════════════════
        this.DetectSkillUsage(player, snapshot, record, name);

        // ═══════════════════════════════════════════════════════
        // 9. NPC 交互检测 — 记录玩家与NPC的交互位置
        // ═══════════════════════════════════════════════════════
        this.DetectNpcInteraction(player, snapshot, name, mapNum, pos);

        // ═══════════════════════════════════════════════════════
        // 10. 地理任务活动记录 — 记录任务相关的地理位置
        // ═══════════════════════════════════════════════════════
        this.RecordQuestGeography(player, record, mapNum, pos);

        // ═══════════════════════════════════════════════════════
        // 11. 详细行为事件检测 — NPC对话选项、购买、学技能、换装、加点
        // ═══════════════════════════════════════════════════════
        this.DetectBehaviorEvents(player, snapshot, name, mapNum, pos);

        // ═══════════════════════════════════════════════════════
        // 12. 事件去重计数
        // ═══════════════════════════════════════════════════════
        if (this._pendingEvents.TryGetValue(name, out var pending) && pending > 0)
        {
            var playerClass = player.SelectedCharacter?.CharacterClass?.Number ?? 0;
            // 批量保存：每10个新事件保存一次
            if (pending >= 10)
            {
                this._pendingEvents[name] = 0;
                this._eventStore?.Save();
                this._logger.LogDebug("[PBO:Event] {Name} 已保存 {Pending} 个行为事件到存储",
                    name, pending);
            }
        }

        // 更新资源缓存用于下次对比
        this._lastResources[name] = (snapshot.Hp, snapshot.Mana, snapshot.Gold);
        this._lastEquipmentStats[name] = (
            (snapshot.PhysAttackMin + snapshot.PhysAttackMax) / 2,
            snapshot.Defense);

        // 更新技能数量缓存用于检测学技能
        var skillList = player.SkillList;
        var skillCount = skillList?.SkillCount ?? 0;
        this._lastSkillCount[name] = skillCount;

        // 更新属性缓存用于检测加点
        this._lastAttributes[name] = (snapshot.Strength, snapshot.Agility, snapshot.Vitality, snapshot.Energy);

        // 更新装备缓存用于检测换装
        this.UpdateEquipmentCache(player, name);

        // 更新背包缓存用于检测新物品
        this.UpdateInventoryCache(player, name);

        // 更新对话状态缓存
        var inDialog = player.PlayerState.CurrentState == GameLogic.PlayerState.NpcDialogOpened;
        this._lastDialogState[name] = inDialog;
    }

    /// <summary>
    /// 分析攻击行为：目标距离、目标类型、攻击频率。
    /// </summary>
    private void AnalyzeAttackBehavior(Player player, PlayerSnapshot snapshot, BehaviorRecord record, int mapNum)
    {
        if (snapshot.LastAttackedTarget is not { } target) return;

        // 计算攻击距离
        var dist = (int)snapshot.Position.EuclideanDistanceTo(target.Position);
        if (dist > 0 && dist < 20) // 合理攻击距离
        {
            if (!record.AttackDistanceHistogram.ContainsKey(dist))
                record.AttackDistanceHistogram[dist] = 0;
            record.AttackDistanceHistogram[dist]++;

            // 更新移动平均
            record.AttackDistanceSamples++;
            record.AverageAttackDistance += (dist - record.AverageAttackDistance) / record.AttackDistanceSamples;
        }

        // 记录攻击的怪物类型
        if (target is Monster monster && monster.Definition is not null)
        {
            var monsterNum = (short)monster.Definition.Number;
            if (!record.AttackedMonsters.ContainsKey(monsterNum))
                record.AttackedMonsters[monsterNum] = 0;
            record.AttackedMonsters[monsterNum]++;

            // 记录到 ExperienceMemory（用玩家经验验证的"该怪物可打"）
            var monsterLevel = (short)0;
            var levelAttr = monster.Definition.Attributes?
                .FirstOrDefault(a => a.AttributeDefinition == Stats.Level);
            if (levelAttr is not null) monsterLevel = (short)levelAttr.Value;

            // 记录该怪物在此处被玩家攻击（表明此怪物可在此地图被击杀）
            this._collectiveExpMem.RecordKill(
                mapNum, monsterNum, monsterLevel,
                1, // 最小经验值1，表示"玩家在这里打这个怪"
                (byte)snapshot.Position.X, (byte)snapshot.Position.Y);

            // 记录行为事件（如果事件存储已设置）
            if (this._eventStore is not null)
            {
                var monsterName = monster.Definition.Designation.ToString() ?? $"#{monsterNum}";
                var killEvent = new BehaviorEvent
                {
                    EventType = BehaviorEventType.MonsterKilled,
                    CharacterName = record.CharacterName,
                    CharacterClass = 0, // 职业信息通过其他渠道获取
                    Level = snapshot.Level,
                    MapNumber = mapNum,
                    MapName = snapshot.CurrentMapName,
                    X = (byte)snapshot.Position.X,
                    Y = (byte)snapshot.Position.Y,
                    MonsterNumber = monsterNum,
                    MonsterName = monsterName,
                    MonsterLevel = monsterLevel,
                    ExperienceGained = 1,
                    Summary = $"击杀 {monsterName}(Lv{monsterLevel})",
                };
                this._eventStore.AddEvent(killEvent);
                this.IncrementPending(record.CharacterName);
            }
        }
    }

    /// <summary>
    /// 分析药水使用：检测HP/MP恢复并推断喝药阈值。
    /// </summary>
    private void AnalyzePotionBehavior(Player player, PlayerSnapshot snapshot, BehaviorRecord record, string name, int mapNum)
    {
        if (!this._lastResources.TryGetValue(name, out var prev)) return;

        var hpGain = snapshot.Hp - prev.Hp;
        var mpGain = snapshot.Mana - prev.Mana;
        var prevHpPct = prev.Hp > 0 && snapshot.MaxHp > 0 ? (double)prev.Hp / snapshot.MaxHp : 1.0;
        var prevMpPct = prev.Mana > 0 && snapshot.MaxMana > 0 ? (double)prev.Mana / snapshot.MaxMana : 1.0;

        // HP药水检测：HP增加超过自然恢复阈值（>50 = 喝了药）
        if (hpGain > 50 && prev.Hp > 0)
        {
            var potion = new PotionRecord
            {
                HpPercentBefore = prevHpPct * 100,
                Amount = hpGain,
                Timestamp = snapshot.Timestamp,
                MapNumber = mapNum,
                X = (byte)snapshot.Position.X,
                Y = (byte)snapshot.Position.Y,
            };
            record.HpPotions.Add(potion);
            if (record.HpPotions.Count > 50) record.HpPotions.RemoveAt(0);

            // 推断HP喝药阈值：取最近5次的平均
            if (record.HpPotions.Count >= 3)
            {
                record.InferredHpPotionThreshold =
                    record.HpPotions.TakeLast(5).Average(p => p.HpPercentBefore) / 100.0;
            }
        }

        // MP药水检测
        if (mpGain > 30 && prev.Mana > 0)
        {
            var potion = new PotionRecord
            {
                HpPercentBefore = prevMpPct * 100,
                Amount = mpGain,
                Timestamp = snapshot.Timestamp,
                MapNumber = mapNum,
                X = (byte)snapshot.Position.X,
                Y = (byte)snapshot.Position.Y,
            };
            record.MpPotions.Add(potion);
            if (record.MpPotions.Count > 50) record.MpPotions.RemoveAt(0);

            if (record.MpPotions.Count >= 3)
            {
                record.InferredMpPotionThreshold =
                    record.MpPotions.TakeLast(5).Average(p => p.HpPercentBefore) / 100.0;
            }
        }
    }

    /// <summary>
    /// 分析行走/寻径模式：路径选择、行走速度、路径点。
    /// </summary>
    private void AnalyzeNavigation(Player player, PlayerSnapshot snapshot, BehaviorRecord record, string name, int mapNum)
    {
        // 记录路径历史
        if (!this._playerPaths.TryGetValue(name, out var pathHistory))
        {
            pathHistory = new List<(Point, int, DateTime)>();
            this._playerPaths[name] = pathHistory;
        }

        var lastPos = pathHistory.Count > 0 ? pathHistory[^1].Pos : snapshot.Position;
        var moved = lastPos != snapshot.Position;

        if (moved)
        {
            pathHistory.Add((snapshot.Position, mapNum, snapshot.Timestamp));
            if (pathHistory.Count > MaxPathHistory) pathHistory.RemoveAt(0);

            // 记录路径段（每隔一段距离记录一次）
            var dist = lastPos.EuclideanDistanceTo(snapshot.Position);
            if (dist >= 3.0) // 显著移动才记录为路径段
            {
                record.RecentPath.Add(new PathSegment
                {
                    From = lastPos,
                    To = snapshot.Position,
                    Timestamp = snapshot.Timestamp,
                    MapNumber = mapNum,
                    Distance = dist,
                });
                if (record.RecentPath.Count > 20) record.RecentPath.RemoveAt(0);
            }
        }

        // 分析行走速度
        if (record.RecentPath.Count >= 2)
        {
            var recent = record.RecentPath.TakeLast(5).ToList();
            if (recent.Count >= 2)
            {
                var totalDist = recent.Sum(s => s.Distance);
                var totalTime = (recent[^1].Timestamp - recent[0].Timestamp).TotalSeconds;
                if (totalTime > 0)
                    record.AverageWalkSpeed = totalDist / totalTime;
            }
        }
    }

    /// <summary>
    /// 分析装备变更：检测属性突变（换装导致的攻击/防御变化）。
    /// </summary>
    private void AnalyzeEquipment(Player player, PlayerSnapshot snapshot, BehaviorRecord record, string name)
    {
        if (!this._lastEquipmentStats.TryGetValue(name, out var prev)) return;

        var currentPhysAtk = (snapshot.PhysAttackMin + snapshot.PhysAttackMax) / 2;
        var currentDef = snapshot.Defense;

        // 检测物理攻击突变（>10%变化 = 换了武器）
        if (prev.PhysAtk > 0)
        {
            var atkChange = Math.Abs(currentPhysAtk - prev.PhysAtk);
            var atkChangePct = atkChange / Math.Max(1, prev.PhysAtk);
            if (atkChangePct > 0.10 && atkChange > 5)
            {
                record.EquipmentChanges.Add(new EquipmentChange
                {
                    Timestamp = snapshot.Timestamp,
                    Level = snapshot.Level,
                    PhysAttackBefore = prev.PhysAtk,
                    PhysAttackAfter = currentPhysAtk,
                    DefenseBefore = prev.Def,
                    DefenseAfter = currentDef,
                    Description = atkChange > 0 ? "换更强武器" : "换低武器(可能临时)",
                });

                if (record.EquipmentChanges.Count > 20) record.EquipmentChanges.RemoveAt(0);
            }
        }

        // 检测防御突变（>10%变化 = 换了防具）
        if (prev.Def > 0)
        {
            var defChange = Math.Abs(currentDef - prev.Def);
            var defChangePct = defChange / Math.Max(1, prev.Def);
            if (defChangePct > 0.10 && defChange > 5)
            {
                record.EquipmentChanges.Add(new EquipmentChange
                {
                    Timestamp = snapshot.Timestamp,
                    Level = snapshot.Level,
                    PhysAttackBefore = currentPhysAtk,
                    PhysAttackAfter = currentPhysAtk,
                    DefenseBefore = prev.Def,
                    DefenseAfter = currentDef,
                    Description = defChange > 0 ? "换更强防具" : "换低防具(可能临时)",
                });

                if (record.EquipmentChanges.Count > 20) record.EquipmentChanges.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// 分析任务行为：检测活跃任务变化、任务完成。
    /// </summary>
    private void AnalyzeQuestBehavior(Player player, BehaviorRecord record)
    {
        var questStates = player.SelectedCharacter?.QuestStates;
        if (questStates is null) return;

        foreach (var qs in questStates)
        {
            if (qs.ActiveQuest is null) continue;

            // 记录活跃任务
            var questDesc = $"G{qs.Group}/N{qs.ActiveQuest.Number}";
            if (!record.ActiveQuestHistory.Contains(questDesc))
            {
                record.ActiveQuestHistory.Add(questDesc);
                if (record.ActiveQuestHistory.Count > 20)
                    record.ActiveQuestHistory.RemoveAt(0);

                this._logger.LogDebug("[PlayerObserver] {Name} 接了新任务: {Quest}",
                    player.Name, questDesc);
            }

            // 检测任务完成（Inactive 状态）
            if (qs.ActiveQuest == null)
            {
                if (!record.QuestCompletions.ContainsKey(qs.Group))
                    record.QuestCompletions[qs.Group] = 0;
                record.QuestCompletions[qs.Group]++;
            }
        }
    }

    /// <summary>
    /// 分析经济行为：金币变化检测。
    /// </summary>
    private void AnalyzeEconomy(Player player, PlayerSnapshot snapshot, BehaviorRecord record, string name, int mapNum, Point pos)
    {
        if (!this._lastResources.TryGetValue(name, out var prev)) return;

        var goldChange = snapshot.Gold - prev.Gold;

        // 金币增加（拾取、卖物）
        if (goldChange > 0 && goldChange < 1000000)
        {
            record.TotalGoldPicked += goldChange;
            this._collectiveExpMem.RecordGoldPickup(mapNum, goldChange, (byte)pos.X, (byte)pos.Y);
        }
        // 金币减少（买药、修装、交易）
        else if (goldChange < 0 && goldChange > -1000000)
        {
            record.TotalGoldSpent += Math.Abs(goldChange);
        }
    }

    /// <summary>
    /// 更新集体经验记忆。
    /// </summary>
    private void UpdateCollectiveMemory(PlayerSnapshot snapshot, BehaviorRecord record, int mapNum)
    {
        this._collectiveExpMem.RecordMapVisit(mapNum, snapshot.Level);
    }

    /// <summary>
    /// 检测技能使用：通过法力消耗推断。
    /// </summary>
    private void DetectSkillUsage(Player player, PlayerSnapshot snapshot, BehaviorRecord record, string name)
    {
        if (!this._lastResources.TryGetValue(name, out var prev)) return;

        var manaDrop = prev.Mana - snapshot.Mana;

        // 法力显著下降（>20）且玩家有 MaxMana → 用了技能
        if (manaDrop > 20 && prev.Mana > 0 && snapshot.MaxMana > 0)
        {
            // 尝试推断用了什么技能
            // 通过玩家的技能列表匹配法力消耗
            if (player.SkillList?.Skills is not null)
            {
                foreach (var skillEntry in player.SkillList.Skills)
                {
                    if (skillEntry.Skill is null) continue;

                    var manaReq = skillEntry.Skill.ConsumeRequirements?
                        .FirstOrDefault(r => r.Attribute == Stats.CurrentMana);

                    if (manaReq is null) continue;

                    // 如果技能消耗的MP ≈ 玩家的法力下降量，判定为使用了该技能
                    var cost = (int)(player.Attributes?[manaReq.Attribute] ?? 0);
                    if (Math.Abs(cost - manaDrop) <= 15)
                    {
                        if (!record.SkillUsage.ContainsKey((ushort)skillEntry.Skill.Number))
                            record.SkillUsage[(ushort)skillEntry.Skill.Number] = 0;
                        record.SkillUsage[(ushort)skillEntry.Skill.Number]++;
                        record.LastSkillUsed = (ushort)skillEntry.Skill.Number;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 检测NPC交互 — 当玩家在NPC附近且位于对话状态时记录。
    /// </summary>
    private void DetectNpcInteraction(Player player, PlayerSnapshot snapshot, string name, int mapNum, Point pos)
    {
        var isInDialog = player.PlayerState.CurrentState == GameLogic.PlayerState.NpcDialogOpened;

        if (!isInDialog) return;

        // 检查是否在NPC附近（5格以内）
        if (player.CurrentMap is not { } map) return;
        var nearbyNpc = map.GetAttackablesInRange(pos, 5)
            .OfType<Monster>()
            .FirstOrDefault(m => m.Definition?.Number > 2000); // NPC编号通常 > 2000

        if (nearbyNpc?.Definition is null) return;

        var npcName = nearbyNpc.Definition.Designation.ToString();
if (string.IsNullOrEmpty(npcName)) npcName = $"NPC#{nearbyNpc.Definition.Number}";
        var npcNum = (short)nearbyNpc.Definition.Number;

        // 防重复记录：同一NPC 30秒内不重复记录
        var npcKey = $"{name}_{npcNum}_{mapNum}";
        if (this._lastNpcInteraction.TryGetValue(npcKey, out var last)
            && (DateTime.UtcNow - last.Time).TotalSeconds < 30)
        {
            return;
        }

        this._lastNpcInteraction[npcKey] = (mapNum, (byte)pos.X, (byte)pos.Y, DateTime.UtcNow);

        // 记录NPC交互
        if (!this._npcInteractions.TryGetValue(name, out var npcList))
        {
            npcList = new List<NpcInteractionRecord>();
            this._npcInteractions[name] = npcList;
        }

        npcList.Add(new NpcInteractionRecord
        {
            NpcName = npcName,
            NpcNumber = npcNum,
            MapNumber = mapNum,
            X = (byte)pos.X,
            Y = (byte)pos.Y,
            DialogAction = "dialog_opened",
            Timestamp = DateTime.UtcNow,
            PlayerLevel = snapshot.Level,
        });
        if (npcList.Count > 100) npcList.RemoveAt(0);

        // 同时记录到集体经验记忆
        this._collectiveExpMem.RecordMapVisit(mapNum, snapshot.Level);

        this._logger.LogDebug(
            "[PBO:NPC] {Name} 与NPC {Npc}(#{Num}) 对话 @Map{Map}({X},{Y})",
            name, npcName, npcNum, mapNum, pos.X, pos.Y);
    }

    /// <summary>
    /// 记录任务相关的活动位置 — 玩家在什么地图坐标做任务。
    /// </summary>
    private void RecordQuestGeography(Player player, BehaviorRecord record, int mapNum, Point pos)
    {
        var questStates = player.SelectedCharacter?.QuestStates;
        if (questStates is null || questStates.Count == 0) return;

        foreach (var qs in questStates)
        {
            if (qs.ActiveQuest is null) continue;

            // 每30秒最多记录一次同一任务
            // （具体实现略，以免过度记录）
        }
    }

    /// <summary>
    /// 上次发现的NPC位置字典（用于检测新NPC交互）。
    /// </summary>
    private readonly Dictionary<string, (int MapNum, byte X, byte Y, DateTime Time)> _lastNpcInteraction = new();

    /// <summary>
    /// NPC交互记录集合。
    /// </summary>
    private readonly Dictionary<string, List<NpcInteractionRecord>> _npcInteractions = new();

    /// <summary>
    /// NPC交互记录。
    /// </summary>
    public sealed class NpcInteractionRecord
    {
        public string NpcName = string.Empty;
        public short NpcNumber;
        public int MapNumber;
        public byte X, Y;
        public string? DialogAction;
        public DateTime Timestamp;
        public int PlayerLevel;
    }

    /// <summary>
    /// 行为记录导出数据 — 结构化输出给 GlobalShadowMap 使用。
    /// </summary>
    public sealed class BehaviorRecordExport
    {
        /// <summary>玩家名。</summary>
        public string CharacterName = string.Empty;
        /// <summary>观察到的等级。</summary>
        public int Level;

        // ---- 打怪热点 ----
        /// <summary>怪物狩猎点 (mapNum_x_y → monsterCount)。</summary>
        public Dictionary<string, short> HuntingSpots { get; set; } = new();

        // ---- NPC交互 ----
        /// <summary>NPC交互摘要 (npcName → count)。</summary>
        public Dictionary<string, int> NpcInteractions { get; set; } = new();

        // ---- 药水阈值 ----
        public double InferredHpPotionThreshold;
        public double InferredMpPotionThreshold;

        // ---- 攻击模式 ----
        public double AverageAttackDistance;
        public Dictionary<int, int> AttackDistanceHistogram = new();
        public Dictionary<ushort, int> SkillUsage = new();

        // ---- 寻径 ----
        public double AverageWalkSpeed;

        // ---- 任务 ----
        public Dictionary<short, int> QuestCompletions = new();
        public List<string> ActiveQuestHistory = new();

        // ---- 经济 ----
        public long TotalGoldPicked;
        public long TotalGoldSpent;

        // ---- 装备 ----
        public int EquipmentChangeCount;
    }

    /// <summary>
    /// 获取所有行为记录（供外部读取，如 Web 面板）。
    /// </summary>
    public IReadOnlyDictionary<string, BehaviorRecord> AllBehaviorRecords => this._behaviorRecords;

    /// <summary>
    /// 导出所有玩家的行为记录摘要，供 GlobalShadowMap 合入群体知识。
    /// </summary>
    public List<BehaviorRecordExport> ExportAllBehaviorRecords()
    {
        var result = new List<BehaviorRecordExport>();
        foreach (var kvp in this._behaviorRecords)
        {
            var r = kvp.Value;

            // 构建狩猎热点：从 AttackedMonsters 中提取地理信息
            var huntingSpots = new Dictionary<string, short>();
            if (this._playerSnapshots.TryGetValue(kvp.Key, out var snap) && snap is not null)
            {
                foreach (var mkvp in r.AttackedMonsters)
                {
                    var spotKey = $"{snap.MapNumber}_{snap.Position.X / 8}_{snap.Position.Y / 8}";
                    if (!huntingSpots.ContainsKey(spotKey))
                        huntingSpots[spotKey] = 0;
                    huntingSpots[spotKey]++;
                }
            }

            // NPC交互摘要
            var npcSummary = new Dictionary<string, int>();
            if (this._npcInteractions.TryGetValue(kvp.Key, out var npcList))
            {
                foreach (var npc in npcList)
                {
                    if (!npcSummary.ContainsKey(npc.NpcName))
                        npcSummary[npc.NpcName] = 0;
                    npcSummary[npc.NpcName]++;
                }
            }

            result.Add(new BehaviorRecordExport
            {
                CharacterName = r.CharacterName,
                Level = r.LastSeenLevel,
                HuntingSpots = huntingSpots,
                NpcInteractions = npcSummary,
                InferredHpPotionThreshold = r.InferredHpPotionThreshold,
                InferredMpPotionThreshold = r.InferredMpPotionThreshold,
                AverageAttackDistance = r.AverageAttackDistance,
                AttackDistanceHistogram = new Dictionary<int, int>(r.AttackDistanceHistogram),
                SkillUsage = new Dictionary<ushort, int>(r.SkillUsage),
                AverageWalkSpeed = r.AverageWalkSpeed,
                QuestCompletions = new Dictionary<short, int>(r.QuestCompletions),
                ActiveQuestHistory = new List<string>(r.ActiveQuestHistory),
                TotalGoldPicked = r.TotalGoldPicked,
                TotalGoldSpent = r.TotalGoldSpent,
                EquipmentChangeCount = r.EquipmentChanges.Count,
            });
        }
        return result;
    }

    /// <summary>
    /// 获取指定玩家的NPC交互记录。
    /// </summary>
    public IReadOnlyList<NpcInteractionRecord> GetNpcInteractions(string playerName)
    {
        if (this._npcInteractions.TryGetValue(playerName, out var list))
            return list.AsReadOnly();
        return Array.Empty<NpcInteractionRecord>();
    }

    /// <summary>
    /// 检测所有类型的行为事件 — 每采样 tick 调用一次。
    /// </summary>
    private void DetectBehaviorEvents(Player player, PlayerSnapshot snapshot, string name, int mapNum, Point pos)
    {
        if (this._eventStore is null) return;

        var playerClass = player.SelectedCharacter?.CharacterClass?.Number ?? 0;

        // 1. NPC 对话框事件：检测打开对话（当状态从非对话→对话）
        this.DetectNpcDialogEvent(player, snapshot, name, mapNum, pos, playerClass);

        // 2. 购买行为检测：背包出现新物品且在 NPC 对话中
        this.DetectPurchaseEvent(player, snapshot, name, mapNum, pos, playerClass);

        // 3. 技能学习检测：技能列表增长
        this.DetectSkillLearnEvent(player, snapshot, name, mapNum, pos, playerClass);

        // 4. 装备变更检测：装备栏物品变化
        this.DetectEquipItemEvent(player, snapshot, name, mapNum, playerClass);

        // 5. 加点检测：属性值变化
        this.DetectStatAllocationEvent(snapshot, name, mapNum, pos, playerClass);

        // 6. 地图进入检测：首次看到该地图
        this.DetectMapEnterEvent(snapshot, name, playerClass);

        // 7. 升级检测：等级提升
        this.DetectLevelUpEvent(snapshot, name, mapNum, pos, playerClass);

        // 8. BUFF增益检测：玩家获得新的MagicEffect
        this.DetectBuffEvent(player, snapshot, name, mapNum, pos, playerClass);
    }

    /// <summary>
    /// 检测 NPC 对话事件 — 状态从非对话→对话时触发。
    /// </summary>
    private void DetectNpcDialogEvent(Player player, PlayerSnapshot snapshot, string name, int mapNum, Point pos, int playerClass)
    {
        var inDialogNow = player.PlayerState.CurrentState == GameLogic.PlayerState.NpcDialogOpened;
        var wasInDialog = this._lastDialogState.TryGetValue(name, out var was) && was;

        // 对话状态从 false → true：说明刚刚打开了NPC对话框
        if (inDialogNow && !wasInDialog)
        {
            var npcInfo = this.GetCurrentNpcInfo(player);
            if (npcInfo.NpcNumber == 0) return;

            var evt = new BehaviorEvent
            {
                EventType = BehaviorEventType.NpcTalk,
                CharacterName = name,
                CharacterClass = playerClass,
                Level = snapshot.Level,
                MapNumber = mapNum,
                MapName = snapshot.CurrentMapName,
                X = (byte)pos.X,
                Y = (byte)pos.Y,
                NpcName = npcInfo.NpcName,
                NpcNumber = npcInfo.NpcNumber,
                Summary = $"与 {npcInfo.NpcName}(#{npcInfo.NpcNumber}) 对话",
            };

            this._eventStore?.AddEvent(evt);
            this.IncrementPending(name);

            // 记录 NPC 对话到集体经验记忆
            this._collectiveExpMem.RecordMapVisit(mapNum, snapshot.Level);
        }
    }

    /// <summary>
    /// 检测购买行为 — 背包新增物品且在NPC对话中。
    /// </summary>
    private void DetectPurchaseEvent(Player player, PlayerSnapshot snapshot, string name, int mapNum, Point pos, int playerClass)
    {
        if (player.Inventory is null) return;

        var inDialog = player.PlayerState.CurrentState == GameLogic.PlayerState.NpcDialogOpened;
        if (!inDialog) return;

        var npcInfo = this.GetCurrentNpcInfo(player);
        if (npcInfo.NpcNumber == 0) return;

        // 获取当前背包物品快照
        var currentItems = new HashSet<string>();
        foreach (var item in player.Inventory.Items)
        {
            if (item.ItemSlot <= InventoryConstants.LastEquippableItemSlotIndex) continue;
            if (item.Definition is null) continue;
            currentItems.Add($"{item.Definition.Group}_{item.Definition.Number}");
        }

        // 与上次快照对比，找出新物品
        if (this._lastInventoryItems.TryGetValue(name, out var prevItems))
        {
            var newItemKeys = currentItems.Where(i => !prevItems.Contains(i)).ToList();
            foreach (var itemKey in newItemKeys)
            {
                var parts = itemKey.Split('_');
                if (!int.TryParse(parts[0], out var g) || !int.TryParse(parts[1], out var n)) continue;
                if (g <= 0) continue;

                var itemName = this.GetItemName(player, g, n);
                var isPotion = g == 14 && n >= 1 && n <= 3;
                var isEquip = g is >= 0 and <= 6;

                var evtType = isPotion ? BehaviorEventType.PotionBought
                    : isEquip ? BehaviorEventType.EquipmentBought
                    : BehaviorEventType.ItemBought;

                // 去重：同一 tick 买的同类物品合并
                var evt = new BehaviorEvent
                {
                    EventType = evtType,
                    CharacterName = name,
                    CharacterClass = playerClass,
                    Level = snapshot.Level,
                    MapNumber = mapNum,
                    MapName = snapshot.CurrentMapName,
                    X = (byte)pos.X,
                    Y = (byte)pos.Y,
                    NpcName = npcInfo.NpcName,
                    NpcNumber = npcInfo.NpcNumber,
                    ItemName = itemName,
                    ItemGroup = g,
                    ItemNumber = n,
                    Quantity = 1,
                    Summary = $"从 {npcInfo.NpcName} 购买 {(isPotion ? "药水" : itemName)}",
                };

                this._eventStore?.AddEvent(evt);
                this.IncrementPending(name);
            }
        }
    }

    /// <summary>
    /// 检测技能学习 — 技能列表比上次多了新技能。
    /// </summary>
    private void DetectSkillLearnEvent(Player player, PlayerSnapshot snapshot, string name, int mapNum, Point pos, int playerClass)
    {
        var skillList = player.SkillList;
        var currentCount = skillList?.SkillCount ?? 0;
        var prevCount = this._lastSkillCount.TryGetValue(name, out var prev) ? prev : 0;

        if (currentCount > prevCount && prevCount > 0)
        {
            // 找出新增的技能
            if (skillList?.Skills is not null)
            {
                var knownSkills = new HashSet<ushort>();
                // 只能检测新增，无法确定哪个是新增，记录所有
                foreach (var entry in skillList.Skills)
                {
                    if (entry.Skill is null) continue;
                    var skillNum = (ushort)entry.Skill.Number;

                    if (!knownSkills.Add(skillNum)) continue;

                    var evt = new BehaviorEvent
                    {
                        EventType = BehaviorEventType.SkillLearned,
                        CharacterName = name,
                        CharacterClass = playerClass,
                        Level = snapshot.Level,
                        MapNumber = mapNum,
                        MapName = snapshot.CurrentMapName,
                        X = (byte)pos.X,
                        Y = (byte)pos.Y,
                        SkillNumber = skillNum,
                        SkillName = entry.Skill.Name,
                        Summary = $"学习技能 {entry.Skill.Name}(#{skillNum})",
                    };

                    this._eventStore?.AddEvent(evt);
                    this.IncrementPending(name);
                }
            }
        }
    }

    /// <summary>
    /// 检测装备变更 — 装备栏物品变化。
    /// </summary>
    private void DetectEquipItemEvent(Player player, PlayerSnapshot snapshot, string name, int mapNum, int playerClass)
    {
        if (player.Inventory is null) return;

        var currentEquip = new Dictionary<int, string>();
        foreach (var item in player.Inventory.Items)
        {
            if (item.ItemSlot > InventoryConstants.LastEquippableItemSlotIndex) continue;
            if (item.Definition is null) continue;
            var defName = item.Definition.Name.ToString();
            currentEquip[item.ItemSlot] = !string.IsNullOrEmpty(defName) ? defName : $"G{item.Definition.Group}N{item.Definition.Number}";
        }

        if (this._lastEquipmentItems.TryGetValue(name, out var prevEquip))
        {
            foreach (var kvp in currentEquip)
            {
                // 这个slot之前没有或者换了不同物品
                if (!prevEquip.ContainsKey(kvp.Key) || prevEquip[kvp.Key] != kvp.Value)
                {
                    var evt = new BehaviorEvent
                    {
                        EventType = BehaviorEventType.ItemEquipped,
                        CharacterName = name,
                        CharacterClass = playerClass,
                        Level = snapshot.Level,
                        MapNumber = mapNum,
                        MapName = snapshot.CurrentMapName,
                        X = (byte)snapshot.Position.X,
                        Y = (byte)snapshot.Position.Y,
                        ItemName = kvp.Value,
                        Summary = $"装备 {kvp.Value} (槽位{kvp.Key})",
                    };

                    this._eventStore?.AddEvent(evt);
                    this.IncrementPending(name);
                }
            }
        }
    }

    /// <summary>
    /// 检测加点事件 — 属性值比上次增加。
    /// </summary>
    private void DetectStatAllocationEvent(PlayerSnapshot snapshot, string name, int mapNum, Point pos, int playerClass)
    {
        if (!this._lastAttributes.TryGetValue(name, out var prev)) return;

        var strDelta = snapshot.Strength - prev.Str;
        var agiDelta = snapshot.Agility - prev.Agi;
        var vitDelta = snapshot.Vitality - prev.Vit;
        var eneDelta = snapshot.Energy - prev.Ene;

        // 任意属性有变化且是正增长
        if (strDelta <= 0 && agiDelta <= 0 && vitDelta <= 0 && eneDelta <= 0) return;

        var evt = new BehaviorEvent
        {
            EventType = BehaviorEventType.StatAllocated,
            CharacterName = name,
            CharacterClass = playerClass,
            Level = snapshot.Level,
            MapNumber = mapNum,
            MapName = snapshot.CurrentMapName,
            X = (byte)pos.X,
            Y = (byte)pos.Y,
            StrengthBefore = prev.Str,
            StrengthAfter = snapshot.Strength,
            AgilityBefore = prev.Agi,
            AgilityAfter = snapshot.Agility,
            VitalityBefore = prev.Vit,
            VitalityAfter = snapshot.Vitality,
            EnergyBefore = prev.Ene,
            EnergyAfter = snapshot.Energy,
        };

        var parts = new List<string>();
        if (strDelta > 0) parts.Add($"力量+{strDelta}");
        if (agiDelta > 0) parts.Add($"敏捷+{agiDelta}");
        if (vitDelta > 0) parts.Add($"体力+{vitDelta}");
        if (eneDelta > 0) parts.Add($"智力+{eneDelta}");

        evt.Summary = $"分配属性点: {string.Join(" ", parts)}";
        this._eventStore?.AddEvent(evt);
        this.IncrementPending(name);
    }

    /// <summary>
    /// 检测地图进入事件。
    /// </summary>
    private void DetectMapEnterEvent(PlayerSnapshot snapshot, string name, int playerClass)
    {
        // 第一次看到此地图时记录（基于LatestSnapshots没有该角色的数据判断）
        if (!this._playerSnapshots.ContainsKey(name))
        {
            if (snapshot.CurrentMapName is null) return;
            var evt = new BehaviorEvent
            {
                EventType = BehaviorEventType.MapEntered,
                CharacterName = name,
                CharacterClass = playerClass,
                Level = snapshot.Level,
                MapNumber = snapshot.MapNumber,
                MapName = snapshot.CurrentMapName,
                X = (byte)snapshot.Position.X,
                Y = (byte)snapshot.Position.Y,
                Summary = $"进入地图 {snapshot.CurrentMapName}",
            };
            this._eventStore?.AddEvent(evt);
            this.IncrementPending(name);
        }
    }

    /// <summary>
    /// 检测BUFF增益事件 — 玩家获得新的 MagicEffect。
    /// </summary>
    private void DetectBuffEvent(Player player, PlayerSnapshot snapshot, string name, int mapNum, Point pos, int playerClass)
    {
        if (player.MagicEffectList?.ActiveEffects is null) return;

        var currentEffects = new HashSet<int>();
        foreach (var effect in player.MagicEffectList.ActiveEffects.Values)
        {
            var def = effect.Definition;
            if (def?.Number > 0)
                currentEffects.Add(def.Number);
        }

        if (this._lastBuffEffects.TryGetValue(name, out var prevEffects))
        {
            var newEffects = currentEffects.Where(id => !prevEffects.Contains(id)).ToList();
            foreach (var effectId in newEffects)
            {
                // Elf Soldier Buff (number 3) is the common attack/defense buff
                var buffName = effectId switch
                {
                    3 => "Elf Soldier 攻防BUFF",
                    _ => $"增益效果#{effectId}",
                };

                var evt = new BehaviorEvent
                {
                    EventType = BehaviorEventType.BuffReceived,
                    CharacterName = name,
                    CharacterClass = playerClass,
                    Level = snapshot.Level,
                    MapNumber = mapNum,
                    MapName = snapshot.CurrentMapName,
                    X = (byte)pos.X,
                    Y = (byte)pos.Y,
                    NpcName = "Elf Soldier",
                    NpcNumber = 257,
                    DialogChoice = 1,
                    DialogChoiceDescription = "获得攻防BUFF(60分钟)",
                    Summary = $"获得 {buffName}",
                };

                this._eventStore?.AddEvent(evt);
                this.IncrementPending(name);
                this._logger.LogDebug("[PBO:Buff] {Name} 获得增益效果 #{Id}: {Buff}",
                    name, effectId, buffName);
            }
        }

        this._lastBuffEffects[name] = currentEffects;
    }

    /// <summary>
    /// 升级检测 — 当前等级 > 上次记录等级。
    /// </summary>
    private void DetectLevelUpEvent(PlayerSnapshot snapshot, string name, int mapNum, Point pos, int playerClass)
    {
        if (this._behaviorRecords.TryGetValue(name, out var record))
        {
            var prevLevel = record.LastSeenLevel;
            if (snapshot.Level > prevLevel && prevLevel > 0)
            {
                var evt = new BehaviorEvent
                {
                    EventType = BehaviorEventType.LevelUp,
                    CharacterName = name,
                    CharacterClass = playerClass,
                    Level = snapshot.Level,
                    MapNumber = mapNum,
                    MapName = snapshot.CurrentMapName,
                    X = (byte)pos.X,
                    Y = (byte)pos.Y,
                    Summary = $"升级到 Lv{snapshot.Level}!",
                };
                this._eventStore?.AddEvent(evt);
                this.IncrementPending(name);
            }
        }
    }

    /// <summary>
    /// 获取当前对话的 NPC 信息（如果有）。
    /// </summary>
    private (short NpcNumber, string? NpcName) GetCurrentNpcInfo(Player player)
    {
        var npc = player.OpenedNpc;
        if (npc?.Definition is null) return (0, null);
        return ((short)npc.Definition.Number, npc.Definition.Designation.ToString() ?? $"NPC#{npc.Definition.Number}");
    }

    /// <summary>
    /// 获取物品名称（通过 GameConfiguration 查找）。
    /// </summary>
    private string GetItemName(Player player, int group, int number)
    {
        try
        {
            var config = player.GameContext?.Configuration;
            if (config?.Items is null) return $"G{group}N{number}";
            var itemDef = config.Items.FirstOrDefault(i => i.Group == group && i.Number == number);
            return itemDef?.Name ?? $"G{group}N{number}";
        }
        catch
        {
            return $"G{group}N{number}";
        }
    }

    /// <summary>
    /// 更新装备缓存。
    /// </summary>
    private void UpdateEquipmentCache(Player player, string name)
    {
        if (player.Inventory is null) return;
        var equip = new Dictionary<int, string>();
        foreach (var item in player.Inventory.Items)
        {
            if (item.ItemSlot > InventoryConstants.LastEquippableItemSlotIndex) continue;
            if (item.Definition is null) continue;
            equip[item.ItemSlot] = item.Definition.Name.ToString() ?? $"#{item.Definition.Number}";
        }
        this._lastEquipmentItems[name] = equip;
    }

    /// <summary>
    /// 更新背包缓存。
    /// </summary>
    private void UpdateInventoryCache(Player player, string name)
    {
        if (player.Inventory is null) return;
        var items = new HashSet<string>();
        foreach (var item in player.Inventory.Items)
        {
            if (item.ItemSlot <= InventoryConstants.LastEquippableItemSlotIndex) continue;
            if (item.Definition is null) continue;
            items.Add($"{item.Definition.Group}_{item.Definition.Number}");
        }
        this._lastInventoryItems[name] = items;
    }

    /// <summary>
    /// 增加待保存事件计数。
    /// </summary>
    private void IncrementPending(string name)
    {
        if (!this._pendingEvents.TryGetValue(name, out var count))
            count = 0;
        this._pendingEvents[name] = count + 1;
    }

    private void CleanupStaleRecords()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        var stale = this._playerPaths
            .Where(kvp => kvp.Value.Count == 0 || kvp.Value[^1].Time < cutoff)
            .Select(kvp => kvp.Key)
            .Take(10)
            .ToList();

        foreach (var name in stale)
        {
            this._playerPaths.Remove(name);
            this._lastResources.Remove(name);
            this._lastEquipmentStats.Remove(name);
        }
    }
}
