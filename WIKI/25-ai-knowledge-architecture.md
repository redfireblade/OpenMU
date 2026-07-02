# AI 知识库架构

## 知识库体系

AI 知识库由多层构成，从静态配置到运行期经验逐步积累。

## GameKnowledgeService（静态知识库）

`GameKnowledgeService`（`src/AIPlayer/Knowledge/GameKnowledgeService.cs`）是 AI 的世界知识核心。它在 `AiPlayerManager` 构造时被创建，从 `GameConfiguration` 扫描并加载所有游戏世界数据。

### 加载流程

```csharp
public GameKnowledgeService(GameConfiguration config, ILogger logger)
{
    var maps = this.LoadMaps(config);           // 地图数据
    var monsters = this.LoadMonsters(config, maps); // 怪物数据
    var items = this.LoadItems(config);         // 物品数据
    var npcs = this.LoadNpcs(config, maps);     // NPC 数据
    var quests = this.LoadQuests(config, monsters); // 任务数据
    var miniGames = this.LoadMiniGameEvents(config); // 事件数据
    var dropSources = this.LoadDropSources(config, monsters); // 掉落数据
    var mapConnections = this.LoadMapConnections(config, maps); // 地图连接
}
```

启动完成后打印知识库统计（示例输出）：
```
[GameKnowledge] 加载完成: 15 地图, 120 怪物/NPC, 500+ 物品, 30 NPC, 
                 20 任务, 10 事件, 2000+ 掉落来源, 30 地图连接
```

### 知识库数据结构

| 集合 | 键类型 | 值类型 | 用途 |
|------|--------|--------|------|
| `Maps` | int（MapNumber） | MapInfo | 按编号查地图 |
| `Monsters` | int（MonsterNumber） | MonsterInfo | 按编号查怪物 |
| `Items` | (int Group, int Number) | ItemInfo | 按组号+编号查物品 |
| `Npcs` | int（NpcNumber） | NpcInfo | 按编号查 NPC 及其位置 |
| `Quests` | (int Group, int Number) | QuestInfo | 按组号+编号查任务 |
| `MiniGameEvents` | (MiniGameType, int GameLevel) | MiniGameEventInfo | 按类型+等级查事件 |
| `DropSources` | 列表 | DropSourceInfo | 所有掉落记录 |
| `MapConnections` | 列表 | MapConnectionInfo | 地图传送连接 |

### 查询方法

`GameKnowledgeService` 提供了丰富的查询方法：

| 方法 | 用途 |
|------|------|
| `GetMonstersOnMap(int mapNumber)` | 获取地图上的所有怪物 |
| `FindMonstersByLevel(int min, int max)` | 按等级范围查找怪物 |
| `FindMapsByLevel(int playerLevel)` | 按玩家等级推荐狩猎地图 |
| `GetDropSources(int group, int number)` | 获取物品的所有掉落来源 |
| `GetDropsOfMonster(int monsterNumber)` | 获取怪物掉落的物品列表 |
| `GetMapConnections(int mapNumber)` | 获取地图的传送连接 |
| `GetNpcMap(int npcNumber)` | 查找 NPC 所在的地图 |
| `GetQuestNpc(int group, int number)` | 查找任务对应的 NPC |
| `GetTicketMaterials(MiniGameType, int)` | 获取事件门票材料 |

## KnowledgeAccessService（等级门控）

`KnowledgeAccessService`（`src/AIPlayer/KnowledgeAccessService.cs`）提供重生感知的知识门控：

```csharp
public sealed class KnowledgeAccessService
{
    public KnowledgeAccessService(AiPlayer player, CharacterMemory memory);
    public bool IsUnlocked(string key);
    public IEnumerable<KnowledgeEntry> GetUnlocked();
}
```

**门控逻辑：** `EffectiveLevel = max(currentLevel, MaxLevelAchieved)`

这意味着：
1. AI 可以在低等级时解锁高级知识（如果之前达到过更高等级）
2. 重生（Reset）不会丢失已解锁的知识
3. 门控条件可同时包含等级 + 职业 + 重生次数

## 经验记忆系统

AI 的三条知识积累路径：

### 1. 角色记忆（CharacterMemory）

存储位置：`aiplayer_data/{CharacterName}.json`

```csharp
// src/AIPlayer/CharacterMemory.cs
public sealed class CharacterMemory
{
    // 地图统计（每地图的击杀数、死亡数、经验获取）
    public Dictionary<int, MapStats> MapStats { get; init; }
    // 总击杀数
    public int TotalKills { get; init; }
}
```

- 每个角色独立
- 启动时从 JSON 加载
- 运行时由 `ExperienceMemory` 更新
- AI 停止时自动保存

### 2. 账户知识（AccountKnowledge）

```csharp
// src/AIPlayer/AccountKnowledge.cs
// 跨角色共享的统计信息
```

- 同一账户下的所有 AI 角色共享
- 用于跨角色知识传递
- 存储在 `knowledge_data.json` 中

### 3. 经验记忆（ExperienceMemory）

```csharp
// src/AIPlayer/ExperienceMemory.cs
// 实时环缓冲区 → 60秒聚合 → MapStats
```

- 运行期实时积累
- 环缓冲区（滑动窗口）存储最近经验
- 每 60 秒合并到 `MapStats`
- 用于生成经验规则候选

## 规则生成流程

经验知识通过以下管道转化为可执行的规则：

```
ExperienceMemory（实时采集）
    ↓ 60秒聚合
MapStats（地图统计）
    ↓ Analyze
经验规则候选（置信度 < 1.0）
    ↓ 置信度 ≥ 0.6
rules_experience.json（规则引擎可用）
    ↓ 置信度 ≥ 0.9
rules_default.json（系统级规则）
```

RuleDef 的 `Confidence` 字段控制规则生效级别：

| 置信度 | 状态 | 行为 |
|--------|------|------|
| < 0.6 | 暂存 | 不启用，等待更多样本 |
| 0.6 - 0.9 | 可用 | 写入 `rules_experience.json` |
| ≥ 0.9 | 提升 | 升级为系统级规则 |

## 知识模型定义

所有知识模型位于 `src/AIPlayer/Knowledge/Models/`，使用 C# record 类型：

| 模型 | 用途 | 字段数 |
|------|------|--------|
| `MapInfo` | 地图信息 | 6 |
| `MonsterInfo` | 怪物信息 | 10 |
| `ItemInfo` | 物品信息 | 6 |
| `NpcInfo` | NPC 位置 | 6 |
| `QuestInfo` | 任务定义 | 11 |
| `MiniGameEventInfo` | 事件定义 | 10 |
| `DropSourceInfo` | 掉落来源 | 8 |
| `MapConnectionInfo` | 地图连接 | 5 |
| `BuildDirection` | 角色构建 | - |
| `BuildDirectionInfo` | 构建详情 | - |
| `BuildPhase` | 构建阶段 | - |
