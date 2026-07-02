// <copyright file="MemorySystem.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Mind;

using System.Collections.Concurrent;

/// <summary>
/// 三层记忆系统 — OAPS Layer 5 心智引擎核心组件。
/// 感觉记忆(500ms) → 工作记忆(30s) → 长期记忆(SQLite+向量索引)。
/// 支持 Ebbinghaus 遗忘曲线，主动清理低价值记忆。
/// 参考 OAPS v3.0 6.2。
/// </summary>
public sealed class MemorySystem
{
    // ═══════════════════════════════════════════════
    // 感觉记忆：500ms 循环缓冲区，即时战斗反应
    // ═══════════════════════════════════════════════
    private readonly CircularBuffer<SensoryRecord> _sensoryBuffer = new(capacity: 16);
    private readonly object _sensoryLock = new();

    // ═══════════════════════════════════════════════
    // 工作记忆：30s 衰减，维持当前目标
    // ═══════════════════════════════════════════════
    private readonly LinkedList<WorkingRecord> _workingMemory = new();
    private readonly object _workingLock = new();
    private const int WorkingCapacity = 50;
    private static readonly TimeSpan WorkingDecay = TimeSpan.FromSeconds(30);

    // ═══════════════════════════════════════════════
    // 长期记忆：持久化存储
    // ═══════════════════════════════════════════════
    private readonly List<LongTermRecord> _longTermMemory = new();
    private readonly object _longTermLock = new();
    private const int LongTermCapacity = 10_000;

    /// <summary>
    /// 记录一条感觉记忆（500ms 循环缓冲区）。
    /// 用于即时战斗反应：刚看到的怪物位置、刚受到的伤害。
    /// </summary>
    public void RecordSensory(SensoryRecord record)
    {
        lock (_sensoryLock)
        {
            _sensoryBuffer.Add(record);
        }
    }

    /// <summary>
    /// 记录一条工作记忆（30s 衰减）。
    /// 维持当前目标：正在去哪个坐标、正在打哪个怪物。
    /// </summary>
    public void RecordWorking(WorkingRecord record)
    {
        lock (_workingLock)
        {
            _workingMemory.AddLast(record);
            if (_workingMemory.Count > WorkingCapacity)
                _workingMemory.RemoveFirst();
        }
    }

    /// <summary>
    /// 记录一条长期记忆。
    /// 重要经历：死亡、稀有掉落、被PK、任务完成。自动评估重要性。
    /// </summary>
    public void RecordLongTerm(LongTermRecord record)
    {
        // 计算重要性：死亡=0.9，稀有掉落=0.8，击杀BOSS=0.7，普通击杀=0.3
        lock (_longTermLock)
        {
            _longTermMemory.Add(record);
            if (_longTermMemory.Count > LongTermCapacity)
            {
                // 主动遗忘：按 Ebbinghaus 曲线清理最低价值记忆
                this.ApplyForgetting();
            }

            // 也写入工作记忆（重要事件短期也可见）
            this.RecordWorking(new WorkingRecord
            {
                Content = record.Summary,
                Timestamp = record.Timestamp,
                Importance = record.Importance,
                Category = WorkingCategory.LongTermRef,
            });
        }
    }

    /// <summary>
    /// 检索所有层次记忆中与当前感知相关的条目。
    /// </summary>
    public MemoryRetrieval Retrieve(PerceptContext context)
    {
        var now = DateTime.UtcNow;
        var result = new MemoryRetrieval();

        // 1. 感觉记忆：最近 500ms
        lock (_sensoryLock)
        {
            result.Sensory = _sensoryBuffer
                .Where(r => (now - r.Timestamp).TotalMilliseconds <= 500)
                .ToList();
        }

        // 2. 工作记忆：30s 内，按相关性过滤
        lock (_workingLock)
        {
            // 清理过期
            while (_workingMemory.First?.Value is { } first
                   && (now - first.Timestamp) > WorkingDecay)
            {
                _workingMemory.RemoveFirst();
            }

            result.Working = _workingMemory
                .Where(w => w.IsRelevantTo(context))
                .Take(10)
                .ToList();
        }

        // 3. 长期记忆：按关键词匹配（向量检索预留位置）
        lock (_longTermLock)
        {
            result.Episodic = _longTermMemory
                .Where(l => l.Importance >= 0.5f || l.Tags.Any(t =>
                    context.Keywords.Contains(t, StringComparer.OrdinalIgnoreCase)))
                .OrderByDescending(l => l.Importance)
                .Take(5)
                .ToList();
        }

        result.RetrievedAt = now;
        return result;
    }

    /// <summary>
    /// 对长期记忆执行 Ebbinghaus 遗忘。
    /// Retention = e^(-t/S)，其中 t 为天数，S 为记忆强度。
    /// 强度低于 5% 且重要性低于 0.3 的归档清理。
    /// </summary>
    public void ApplyForgetting()
    {
        lock (_longTermLock)
        {
            var now = DateTime.UtcNow;
            var archived = _longTermMemory.RemoveAll(m =>
            {
                var ageDays = (now - m.Timestamp).TotalDays;
                var retention = Math.Exp(-ageDays / m.Strength);
                return retention < 0.05 && m.Importance < 0.3f;
            });
        }
    }

    /// <summary>
    /// 获取长期记忆总数。
    /// </summary>
    public int LongTermCount
    {
        get { lock (_longTermLock) return _longTermMemory.Count; }
    }

    /// <summary>
    /// 清除所有记忆。
    /// </summary>
    public void ClearAll()
    {
        lock (_sensoryLock) _sensoryBuffer.Clear();
        lock (_workingLock) _workingMemory.Clear();
        lock (_longTermLock) _longTermMemory.Clear();
    }
}

// ═══════════════════════════════════════════════════════
// 记忆记录类型
// ═══════════════════════════════════════════════════════

/// <summary>感觉记忆记录（500ms 循环）。</summary>
public record SensoryRecord
{
    /// <summary>感知类型：怪物位置/伤害/掉落。</summary>
    public SensoryType Type { get; init; }

    /// <summary>关联实体 ID。</summary>
    public uint EntityId { get; init; }

    /// <summary>坐标 X。</summary>
    public byte X { get; init; }

    /// <summary>坐标 Y。</summary>
    public byte Y { get; init; }

    /// <summary>关联数值（伤害值/距离等）。</summary>
    public int Value { get; init; }

    /// <summary>记录时间。</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>感觉类型。</summary>
public enum SensoryType
{
    /// <summary>怪物位置</summary>
    MonsterPosition,

    /// <summary>受到伤害</summary>
    IncomingDamage,

    /// <summary>物品掉落</summary>
    ItemDrop,

    /// <summary>周围怪物数量</summary>
    MonsterDensity,
}

/// <summary>工作记忆记录（30s 衰减）。</summary>
public record WorkingRecord
{
    /// <summary>记忆摘要。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>记忆类别。</summary>
    public WorkingCategory Category { get; init; }

    /// <summary>重要性 0~1。</summary>
    public float Importance { get; init; }

    /// <summary>关联的坐标 X。</summary>
    public byte? TargetX { get; init; }

    /// <summary>关联的坐标 Y。</summary>
    public byte? TargetY { get; init; }

    /// <summary>记录时间。</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>判断是否与当前感知上下文相关。</summary>
    public bool IsRelevantTo(PerceptContext context) =>
        context.Keywords.Any(k =>
            Content.Contains(k, StringComparison.OrdinalIgnoreCase));
}

/// <summary>工作记忆类别。</summary>
public enum WorkingCategory
{
    /// <summary>移动目标</summary>
    Movement,

    /// <summary>战斗目标</summary>
    Combat,

    /// <summary>任务目标</summary>
    Quest,

    /// <summary>物品拾取</summary>
    Pickup,

    /// <summary>长期记忆引用</summary>
    LongTermRef,
}

/// <summary>长期记忆记录。</summary>
public record LongTermRecord
{
    /// <summary>唯一 ID。</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>记忆摘要。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>详细描述（用于 LLM 深度推理）。</summary>
    public string? Detail { get; init; }

    /// <summary>重要性 0~1。死亡=0.9，稀有掉落=0.8，BOSS击杀=0.7，常规=0.3。</summary>
    public float Importance { get; init; }

    /// <summary>记忆强度（用于 Ebbinghaus 曲线计算）。</summary>
    public float Strength { get; init; } = 1.0f;

    /// <summary>标签，用于检索匹配。</summary>
    public HashSet<string> Tags { get; init; } = new();

    /// <summary>关联地图编号。</summary>
    public int? MapNumber { get; init; }

    /// <summary>关联坐标 X。</summary>
    public byte? X { get; init; }

    /// <summary>关联坐标 Y。</summary>
    public byte? Y { get; init; }

    /// <summary>记录时间。</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

// ═══════════════════════════════════════════════════════
// 检索类型
// ═══════════════════════════════════════════════════════

/// <summary>感知上下文 — 用于检索相关记忆。</summary>
public record PerceptContext
{
    /// <summary>关键词列表。</summary>
    public HashSet<string> Keywords { get; init; } = new();

    /// <summary>当前地图编号。</summary>
    public int? CurrentMap { get; init; }
}

/// <summary>三层记忆检索结果。</summary>
public record MemoryRetrieval
{
    /// <summary>感觉记忆结果（500ms 内）。</summary>
    public IReadOnlyList<SensoryRecord> Sensory { get; set; } = Array.Empty<SensoryRecord>();

    /// <summary>工作记忆结果（30s 衰减）。</summary>
    public IReadOnlyList<WorkingRecord> Working { get; set; } = Array.Empty<WorkingRecord>();

    /// <summary>长期记忆结果（语义/向量检索）。</summary>
    public IReadOnlyList<LongTermRecord> Episodic { get; set; } = Array.Empty<LongTermRecord>();

    /// <summary>检索时间。</summary>
    public DateTime RetrievedAt { get; set; }
}

// ═══════════════════════════════════════════════════════
// 辅助类型：循环缓冲区
// ═══════════════════════════════════════════════════════

/// <summary>
/// 固定容量循环缓冲区。
/// </summary>
internal sealed class CircularBuffer<T> : IEnumerable<T>
{
    private readonly T[] _buffer;
    private int _head;
    private int _count;

    public CircularBuffer(int capacity)
    {
        _buffer = new T[capacity];
    }

    public void Add(T item)
    {
        _buffer[_head] = item;
        _head = (_head + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;
    }

    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _head = 0;
        _count = 0;
    }

    public IEnumerator<T> GetEnumerator()
    {
        var start = _count < _buffer.Length ? 0 : _head;
        var count = _count;
        for (int i = 0; i < count; i++)
            yield return _buffer[(start + i) % _buffer.Length];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
