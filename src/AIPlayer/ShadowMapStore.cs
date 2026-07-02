// <copyright file="ShadowMapStore.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// AI 角色个人数字影子地图存储器。
/// 每个 AI 角色一个存储，记录死亡/掉落/活动等地理信息，持久化为 JSON 文件。
/// AI 群体决策系统通过 <see cref="GlobalShadowMap"/> 定期扫描个人地图提取有价值信息。
/// </summary>
public sealed class ShadowMapStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _characterName;
    private readonly ILogger _logger;
    private readonly string _filePath;
    private readonly List<ShadowMapEntry> _entries = new();
    private long _nextId;
    private readonly object _lock = new();

    /// <summary>
    /// 获取所有条目。
    /// </summary>
    public IReadOnlyList<ShadowMapEntry> Entries
    {
        get { lock (_lock) return _entries.ToList().AsReadOnly(); }
    }

    public ShadowMapStore(string characterName, string dataDir, ILogger logger)
    {
        this._characterName = characterName;
        this._logger = logger;
        this._filePath = Path.Combine(dataDir, $"shadowmap_{characterName}.json");
        this.Load();
    }

    /// <summary>
    /// 添加一条死亡记录到个人影子地图。
    /// </summary>
    public ShadowMapEntry RecordDeath(DeathSnapshot snapshot)
    {
        var entry = new ShadowMapEntry
        {
            CharacterName = this._characterName,
            EntryType = ShadowEntryType.Death,
            MapNumber = 0, // TODO: get from player context
            X = snapshot.DeathX,
            Y = snapshot.DeathY,
            QuestGroup = snapshot.QuestGroup,
            QuestNumber = snapshot.QuestNumber,
            Level = snapshot.Level,
            AvgPhysicalAttack = snapshot.AvgPhysicalAttack,
            AvgWizardryAttack = snapshot.AvgWizardryAttack,
            Defense = snapshot.Defense,
            MonsterNumber = snapshot.MonsterNumber,
            MonsterLevel = snapshot.MonsterLevel,
            SkillNumbers = snapshot.SkillNumbers.Count > 0
                ? JsonSerializer.Serialize(snapshot.SkillNumbers)
                : null,
            Description = $"死亡 Lv{snapshot.Level} @({snapshot.DeathX},{snapshot.DeathY}) 怪#{snapshot.MonsterNumber}Lv{snapshot.MonsterLevel}",
            CreatedAt = DateTime.UtcNow,
        };
        return this.AddEntry(entry);
    }

    /// <summary>
    /// 添加一条掉落记录到个人影子地图。
    /// </summary>
    public ShadowMapEntry RecordDrop(ushort mapNum, byte x, byte y, int itemGroup, int itemNumber, string? itemFlags = null)
    {
        var entry = new ShadowMapEntry
        {
            CharacterName = this._characterName,
            EntryType = ShadowEntryType.Drop,
            MapNumber = mapNum,
            X = x,
            Y = y,
            Level = 0, // filled at save time
            ItemGroup = itemGroup,
            ItemNumber = itemNumber,
            ItemFlags = itemFlags,
            Description = $"掉落 Group{itemGroup}/{itemNumber} @({x},{y}) Map{mapNum}",
            CreatedAt = DateTime.UtcNow,
        };
        return this.AddEntry(entry);
    }

    /// <summary>
    /// 添加一条活动记录。
    /// </summary>
    public ShadowMapEntry RecordActivity(ShadowEntryType type, ushort mapNum, byte x, byte y, string description)
    {
        var entry = new ShadowMapEntry
        {
            CharacterName = this._characterName,
            EntryType = type,
            MapNumber = mapNum,
            X = x,
            Y = y,
            Description = description,
            CreatedAt = DateTime.UtcNow,
        };
        return this.AddEntry(entry);
    }

    /// <summary>
    /// 获取指定类型的未提取条目（供群体决策系统扫描）。
    /// </summary>
    public List<ShadowMapEntry> GetUnreadEntries()
    {
        lock (_lock) return _entries.Where(e => !e.ExtractedToGlobal).ToList();
    }

    /// <summary>
    /// 标记条目已被群体地图提取。
    /// </summary>
    public void MarkExtracted(IEnumerable<long> ids)
    {
        var idSet = ids.ToHashSet();
        lock (_lock)
        {
            foreach (var entry in _entries.Where(e => idSet.Contains(e.Id)))
            {
                entry.ExtractedToGlobal = true;
            }
        }
        this.Save();
    }

    private ShadowMapEntry AddEntry(ShadowMapEntry entry)
    {
        lock (_lock)
        {
            entry.Id = ++this._nextId;
            _entries.Add(entry);
        }
        this.Save();
        return entry;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(this._filePath)) return;
            var json = File.ReadAllText(this._filePath);
            if (string.IsNullOrWhiteSpace(json)) return;

            var loaded = JsonSerializer.Deserialize<List<ShadowMapEntry>>(json, JsonOptions);
            if (loaded is null || loaded.Count == 0) return;

            lock (_lock)
            {
                _entries.Clear();
                _entries.AddRange(loaded);
                _nextId = _entries.Max(e => e.Id) + 1;
            }

            this._logger.LogDebug("[ShadowMap] 加载了 {Count} 条记录 for {Name}", loaded.Count, this._characterName);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "[ShadowMap] 加载失败 for {Name}: 文件损坏？", this._characterName);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(this._filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            List<ShadowMapEntry> snapshot;
            lock (_lock) snapshot = _entries.ToList();

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(this._filePath, json);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "[ShadowMap] 保存失败 for {Name}", this._characterName);
        }
    }
}
