// <copyright file="PathMemoryStore.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// AI 角色路径记忆存储器。
/// 每个角色每个任务每个地图保留最近 10 次完整路径记录。
/// 路径数据保存到个人数字影子地图，供群体决策系统分析。
///
/// 类似蚂蚁信息素模型：多次行动后最优路径逐渐浮现。
/// AI 群体决策系统每日/周/月分析 -> 最优路径 -> 群体数字地图。
/// </summary>
public sealed class PathMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _characterName;
    private readonly ILogger _logger;
    private readonly string _filePath;

    // key = "questGroup_questNumber_mapNumber"
    private readonly ConcurrentDictionary<string, QuestPathHistory> _histories = new();

    public PathMemoryStore(string characterName, string dataDir, ILogger logger)
    {
        this._characterName = characterName;
        this._logger = logger;
        this._filePath = Path.Combine(dataDir, $"pathmemory_{characterName}.json");
        this.Load();
    }

    /// <summary>
    /// 记录一次路径执行结果。
    /// </summary>
    public void RecordPath(QuestPathRecord record)
    {
        var key = MakeKey(record.QuestGroup, record.QuestNumber, record.MapNumber);
        var history = _histories.GetOrAdd(key, _ =>
            new QuestPathHistory(record.QuestGroup, record.QuestNumber, record.MapNumber));

        // 计算路径评分 = 步数 * 0.5 + 死亡次数 * 10 - 物品数 * 2
        record.PathScore = (record.StepCount * 0.5)
                         + (record.DeathCount * 10.0)
                         - (record.TotalItems * 2.0)
                         - (record.ExcellentItems * 5.0);

        history.AddRecord(record);
        this.Save();

        this._logger.LogInformation(
            "[PathMemory] {Name}: 记录 Q{Group}/{Number} Map{Map} 路径完成 — {Steps}步 {Deaths}死 得分{Score:F1}",
            this._characterName, record.QuestGroup, record.QuestNumber, record.MapNumber,
            record.StepCount, record.DeathCount, record.PathScore);
    }

    /// <summary>
    /// 查询指定任务地图的最佳路径（个人经验）。
    /// </summary>
    public QuestPathRecord? GetBestPath(short questGroup, short questNumber, ushort mapNumber)
    {
        var key = MakeKey(questGroup, questNumber, mapNumber);
        if (_histories.TryGetValue(key, out var history))
        {
            return history.GetBestPath();
        }
        return null;
    }

    /// <summary>
    /// 获取指定任务地图的历史记录。
    /// </summary>
    public QuestPathHistory? GetHistory(short questGroup, short questNumber, ushort mapNumber)
    {
        var key = MakeKey(questGroup, questNumber, mapNumber);
        _histories.TryGetValue(key, out var history);
        return history;
    }

    /// <summary>
    /// 获取所有记录（供群体决策系统扫描）。
    /// </summary>
    public List<QuestPathRecord> GetAllRecords()
    {
        var all = new List<QuestPathRecord>();
        foreach (var history in _histories.Values)
        {
            all.AddRange(history.Records);
        }
        return all;
    }

    private static string MakeKey(short questGroup, short questNumber, ushort mapNumber)
        => $"{questGroup}_{questNumber}_{mapNumber}";

    private void Load()
    {
        try
        {
            if (!File.Exists(this._filePath)) return;
            var json = File.ReadAllText(this._filePath);
            if (string.IsNullOrWhiteSpace(json)) return;

            var records = JsonSerializer.Deserialize<List<QuestPathRecord>>(json, JsonOptions);
            if (records is null) return;

            foreach (var record in records)
            {
                var key = MakeKey(record.QuestGroup, record.QuestNumber, record.MapNumber);
                var history = _histories.GetOrAdd(key, _ =>
                    new QuestPathHistory(record.QuestGroup, record.QuestNumber, record.MapNumber));
                history.AddRecord(record);
            }

            this._logger.LogDebug("[PathMemory] 加载了 {Count} 条路径记录 for {Name}", records.Count, this._characterName);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "[PathMemory] 加载失败 for {Name}", this._characterName);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(this._filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var allRecords = this.GetAllRecords();
            var json = JsonSerializer.Serialize(allRecords, JsonOptions);
            File.WriteAllText(this._filePath, json);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "[PathMemory] 保存失败 for {Name}", this._characterName);
        }
    }
}
