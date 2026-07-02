// <copyright file="BehaviorEventStore.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// 行为事件存储 — 收集、持久化、查询玩家的离散行为事件。
///
/// 与 <see cref="PlayerBehaviorObserver"/> 的区别：
/// - PBO 每 ~1.2 秒做一次周期性快照，从资源变化推断行为
/// - BehaviorEventStore 记录的是"确实发生了什么"的离散事件
///   （如：与NPC对话选了选项1、购买了火球术技能书、学习了技能等）
///
/// 数据流向：
///   PlayerBehaviorObserver.DetectXxx() → BehaviorEvent → BehaviorEventStore
///   → BehaviorPlanGenerator → LevelingPlan → AI 角色读取执行
/// </summary>
public sealed class BehaviorEventStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    /// <summary>
    /// 所有事件按角色名分组。
    /// </summary>
    private readonly Dictionary<string, List<BehaviorEvent>> _eventsByCharacter = new();

    /// <summary>事件 ID 计数器。</summary>
    private long _nextEventId;

    /// <summary>
    /// 获取所有角色名列表。
    /// </summary>
    public IReadOnlyCollection<string> CharacterNames
    {
        get { lock (this._lock) return this._eventsByCharacter.Keys.ToList().AsReadOnly(); }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="BehaviorEventStore"/> class.
    /// </summary>
    public BehaviorEventStore(string dataDir, ILogger logger)
    {
        this._filePath = Path.Combine(dataDir, "behavior_events.json");
        this._logger = logger;
        this.Load();
    }

    /// <summary>
    /// 添加一个行为事件。
    /// </summary>
    public void AddEvent(BehaviorEvent evt)
    {
        lock (this._lock)
        {
            if (!this._eventsByCharacter.TryGetValue(evt.CharacterName, out var list))
            {
                list = new List<BehaviorEvent>();
                this._eventsByCharacter[evt.CharacterName] = list;
            }

            // 去重：同一角色在 30 秒内相同类型+NPC+等级的合并
            var duplicate = list.LastOrDefault(e =>
                e.EventType == evt.EventType
                && e.NpcNumber == evt.NpcNumber
                && e.Level == evt.Level
                && (DateTime.UtcNow - e.Timestamp).TotalSeconds < 30);

            if (duplicate is not null)
            {
                // 更新数量（购买类）或跳过
                if (evt.Quantity.HasValue)
                {
                    duplicate.Quantity = (duplicate.Quantity ?? 0) + (evt.Quantity ?? 1);
                }
                return;
            }

            evt.Timestamp = DateTime.UtcNow;
            list.Add(evt);

            // 裁剪：每个角色最多保留 10000 条事件
            if (list.Count > 10000)
            {
                list.RemoveRange(0, list.Count - 10000);
            }
        }
    }

    /// <summary>
    /// 获取指定角色的所有事件（按时间升序）。
    /// </summary>
    public IReadOnlyList<BehaviorEvent> GetEvents(string characterName)
    {
        lock (this._lock)
        {
            if (this._eventsByCharacter.TryGetValue(characterName, out var list))
            {
                return list.OrderBy(e => e.Timestamp).ToList().AsReadOnly();
            }
            return Array.Empty<BehaviorEvent>();
        }
    }

    /// <summary>
    /// 获取指定角色在指定等级范围内的事件。
    /// </summary>
    public IReadOnlyList<BehaviorEvent> GetEventsInLevelRange(string characterName, int minLevel, int maxLevel)
    {
        lock (this._lock)
        {
            if (this._eventsByCharacter.TryGetValue(characterName, out var list))
            {
                return list
                    .Where(e => e.Level >= minLevel && e.Level <= maxLevel)
                    .OrderBy(e => e.Timestamp)
                    .ToList()
                    .AsReadOnly();
            }
            return Array.Empty<BehaviorEvent>();
        }
    }

    /// <summary>
    /// 获取指定角色的事件统计摘要。
    /// </summary>
    public string GetStatsSummary(string characterName)
    {
        lock (this._lock)
        {
            if (!this._eventsByCharacter.TryGetValue(characterName, out var list) || list.Count == 0)
                return "暂无行为事件数据";

            var byType = list.GroupBy(e => e.EventType)
                .ToDictionary(g => g.Key, g => g.Count());

            var npcInteractions = list.Count(e => e.EventType is BehaviorEventType.NpcTalk or BehaviorEventType.NpcDialogChoice);
            var purchases = list.Count(e => e.EventType is BehaviorEventType.ItemBought or BehaviorEventType.PotionBought or BehaviorEventType.EquipmentBought);
            var skillsLearned = list.Count(e => e.EventType == BehaviorEventType.SkillLearned);
            var equipment = list.Count(e => e.EventType == BehaviorEventType.ItemEquipped);
            var statAllocs = list.Count(e => e.EventType == BehaviorEventType.StatAllocated);
            var kills = list.Count(e => e.EventType == BehaviorEventType.MonsterKilled);

            var maxLevel = list.Max(e => e.Level);
            var levelUpCount = list.Count(e => e.EventType == BehaviorEventType.LevelUp);
            var mapsVisited = list.Where(e => e.EventType == BehaviorEventType.MapEntered).Select(e => e.MapName).Distinct().Count();

            return $"Lv{maxLevel} | 事件总数: {list.Count} | " +
                   $"NPC交互: {npcInteractions}次 | 购买: {purchases}次 | " +
                   $"学习技能: {skillsLearned}个 | 换装: {equipment}次 | " +
                   $"加点: {statAllocs}次 | 升级: {levelUpCount}次 | " +
                   $"击杀记录: {kills}次 | 到访地图: {mapsVisited}张";
        }
    }

    /// <summary>
    /// 获取所有角色的最新等级。
    /// </summary>
    public Dictionary<string, int> GetMaxLevels()
    {
        lock (this._lock)
        {
            return this._eventsByCharacter
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Max(e => e.Level));
        }
    }

    /// <summary>
    /// 删除指定角色的所有事件。
    /// </summary>
    public void ClearCharacter(string characterName)
    {
        lock (this._lock)
        {
            this._eventsByCharacter.Remove(characterName);
        }
    }

    /// <summary>
    /// 保存到磁盘。
    /// </summary>
    public void Save()
    {
        try
        {
            lock (this._lock)
            {
                var dir = Path.GetDirectoryName(this._filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var snapshot = new EventStoreData
                {
                    EventsByCharacter = this._eventsByCharacter.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value.OrderBy(e => e.Timestamp).ToList()),
                };

                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                File.WriteAllText(this._filePath, json);
            }
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "[BehaviorEventStore] Save failed");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(this._filePath)) return;
            var json = File.ReadAllText(this._filePath);
            if (string.IsNullOrWhiteSpace(json)) return;

            var data = JsonSerializer.Deserialize<EventStoreData>(json, JsonOptions);
            if (data?.EventsByCharacter is null) return;

            lock (this._lock)
            {
                this._eventsByCharacter.Clear();
                foreach (var kvp in data.EventsByCharacter)
                {
                    this._eventsByCharacter[kvp.Key] = kvp.Value.OrderBy(e => e.Timestamp).ToList();
                }
            }
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "[BehaviorEventStore] Load failed, starting fresh");
        }
    }

    /// <summary>
    /// JSON 序列化用的容器。
    /// </summary>
    private sealed class EventStoreData
    {
        public Dictionary<string, List<BehaviorEvent>> EventsByCharacter { get; set; } = new();
    }
}
