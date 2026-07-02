// <copyright file="QuestPathRecord.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

/// <summary>
/// 角色在某任务某地图上的一次完整执行记录（路径 + 收益）。
/// 每个角色每个任务每个地图保留最近 10 次记录。
/// AI 群体决策系统每日/周/月分析这些记录，提取最优路径到群体数字影子地图。
/// </summary>
public sealed class QuestPathRecord
{
    /// <summary>任务组号。</summary>
    public short QuestGroup { get; set; }

    /// <summary>任务编号。</summary>
    public short QuestNumber { get; set; }

    /// <summary>地图编号。</summary>
    public ushort MapNumber { get; set; }

    /// <summary>角色名。</summary>
    public string CharacterName { get; set; } = string.Empty;

    /// <summary>角色等级。</summary>
    public int Level { get; set; }

    /// <summary>路线坐标数组（从安全区到目标区）。</summary>
    public List<string> PathPoints { get; set; } = new();

    /// <summary>起点 X。</summary>
    public byte StartX { get; set; }

    /// <summary>起点 Y。</summary>
    public byte StartY { get; set; }

    /// <summary>终点 X。</summary>
    public byte EndX { get; set; }

    /// <summary>终点 Y。</summary>
    public byte EndY { get; set; }

    /// <summary>总步数。</summary>
    public int StepCount { get; set; }

    /// <summary>总死亡次数。</summary>
    public int DeathCount { get; set; }

    /// <summary>任务击杀进度（如 5/10）。</summary>
    public string? KillProgress { get; set; }

    /// <summary>捡到的总钱数。</summary>
    public int TotalMoney { get; set; }

    /// <summary>捡到的道具数。</summary>
    public int TotalItems { get; set; }

    /// <summary>任务道具数。</summary>
    public int QuestItems { get; set; }

    /// <summary>卓越装备数。</summary>
    public int ExcellentItems { get; set; }

    /// <summary>本次用时（秒）。</summary>
    public double DurationSeconds { get; set; }

    /// <summary>记录时间。</summary>
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    /// <summary>路径评分（越低越好，参考蚂蚁信息素模型）。</summary>
    public double PathScore { get; set; }
}

/// <summary>
/// 每个角色每个任务每个地图的路径记录缓存（最多 10 条）。
/// 超出时淘汰最旧的一条。
/// </summary>
public sealed class QuestPathHistory
{
    /// <summary>最大保存条数。</summary>
    private const int MaxRecords = 10;

    private readonly List<QuestPathRecord> _records = new();
    private readonly object _lock = new();

    /// <summary>任务组号。</summary>
    public short QuestGroup { get; }

    /// <summary>任务编号。</summary>
    public short QuestNumber { get; }

    /// <summary>地图编号。</summary>
    public ushort MapNumber { get; }

    /// <summary>当前记录数。</summary>
    public int Count { get { lock (_lock) return _records.Count; } }

    /// <summary>获取所有记录（只读副本）。</summary>
    public IReadOnlyList<QuestPathRecord> Records
    {
        get { lock (_lock) return _records.ToList().AsReadOnly(); }
    }

    public QuestPathHistory(short questGroup, short questNumber, ushort mapNumber)
    {
        this.QuestGroup = questGroup;
        this.QuestNumber = questNumber;
        this.MapNumber = mapNumber;
    }

    /// <summary>
    /// 添加一条新记录。超出 10 条时移除最旧的。
    /// </summary>
    public void AddRecord(QuestPathRecord record)
    {
        lock (_lock)
        {
            _records.Add(record);
            if (_records.Count > MaxRecords)
            {
                _records.RemoveAt(0); // 移除最旧的
            }
        }
    }

    /// <summary>
    /// 获取最佳路径（步数最短、死亡最少得分最高的）。
    /// </summary>
    public QuestPathRecord? GetBestPath()
    {
        lock (_lock)
        {
            if (_records.Count == 0) return null;
            return _records.OrderBy(r => r.PathScore).First();
        }
    }

    /// <summary>
    /// 计算所有路径的综合评分用于群体决策。
    /// </summary>
    public double GetAverageScore()
    {
        lock (_lock)
        {
            if (_records.Count == 0) return 0;
            return _records.Average(r => r.PathScore);
        }
    }
}
