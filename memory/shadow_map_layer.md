# 影子地图层 (Shadow Map Layer) — 数字第一图层

> 版本: 1.0 — 已实现 (Phase A)  
> 状态: 活动  
> 关联规则: [AR-19 影子地图层](architecture_design_rules.md#ar-19-影子地图层shadow-map-layer)  
> 实现文件: `src/AIPlayer/Map/ShadowMapLayer.cs`, `ShadowGridCostMerger.cs`, `ShadowEntry.cs`, `ShadowEntryType.cs`, `ShadowVisibility.cs`

## 概述

影子地图层是 AI 决策系统的**数字第一图层**——一个与游戏地图同尺寸(256×256)的知识图谱覆盖层。每个地图格点指向一个不同类型信息条目的数组，条目之间通过 DAG 边互相引用，构成完整的知识图谱。

与游戏地图的核心区别：

| 维度 | 游戏地图 (GameMapTerrain) | 影子地图 (ShadowMapLayer) |
|------|--------------------------|--------------------------|
| 数据 | 二元(可走/不可走) | 多维(类型+权限+负载) |
| 视角 | 全局客观 | 每个 AI 各自维护 |
| 动态性 | 静态(仅加载时更新) | 持续学习(每次观测写入) |
| 权限 | 无 | AIOnly/GuildOnly/Public/RoleBased |
| 结构 | 网格 | 网格 + DAG |

## 数据模型

### ShadowEntry (条目) — record struct

每个格点的条目包含：

```csharp
public readonly record struct ShadowEntry
{
    public Guid Id { get; init; }             ← 全局唯一
    public byte X { get; init; }              ← 地图坐标 (0-255)
    public byte Y { get; init; }              ← 地图坐标 (0-255)
    public ShadowEntryType Type { get; init; }  ← 信息类型
    public short SubType { get; init; }       ← 子类/分支 (按类型不同含义)
    public ShadowVisibility Visibility { get; init; }  ← 权限
    public Guid? OwnerId { get; init; }       ← 创建者 AI
    public Guid? GuildId { get; init; }       ← 帮会
    public DateTime CreatedAt { get; init; }  ← 创建时间
    public DateTime? ExpiresAt { get; init; } ← 过期时间 (自动衰减)
    public IReadOnlyList<Guid> References { get; init; } ← DAG 出边
    public byte[]? Payload { get; init; }     ← 类型专用负载
}
```

注意：ShadowEntry 是 `record struct`（值语义），每次写入/更新通过 AddOrUpdate 原子操作完成，无需外部锁。`TeamId` 字段当前未实现；队伍过滤通过 GuildId 和 OwnerId 的组合实现。

### ShadowEntryType (信息类型)

|| Type | 含义 | SubType 含义 | Payload 格式 |
||------|------|-------------|-------------|
|| 1 - MonsterObservation | 怪物观测记录 | MonsterNumber | (空) |
|| 2 - MonsterDensity | 怪物聚集密度热力 | - | float (4 bytes, BitConverter) |
|| 3 - PathTrail | 走过的路径轨迹 | 步数 | (空) |
|| 4 - PathOutcome | 路径执行结果 | 0=失败/1=成功 | (空) |
|| 5 - NavigationMarker | 导航标记 | 1=绕行点/2=危险区/3=捷径 | (空) |
|| 6 - DangerZone | 危险区标记 | 危险等级(1-5) | (空) |
|| 7 - QuestMarker | 任务相关位置 | QuestGroup<<8\|QuestNumber | (空) |
|| 8 - GuildMarker | 帮会路径暗号 | 帮会ID | (空) |
|| 9 - Bulletin | 公告板 | 公告类型 | (空) |
|| 10 - TaskNote | 个人任务笔记 | - | (空) |
|| 11 - Landmark | 个人地标 | 地标类型 | (空) |

### ShadowVisibility (权限)

| Value | 名称 | 含义 |
|-------|------|------|
| 0 | Public | 所有 AI 可见 |
| 1 | AIOnly | 仅创建者 AI |
| 2 | GuildOnly | 同帮会成员可见 |
| 3 | RoleBased | 特定角色（预留） |

### DAG 结构

条目通过 References 字段构成有向无环图：

```
[MonsterDensity@(150,130) density=0.8]
    │
    ├──→ [PathOutcome{failed}@(150,130)]
    │         │
    │         └──→ [PathTrail@route_A] → [PathOutcome{success}@route_A]
    │
    └──→ [NavigationMarker{bypass}@(145,135)]
              │
              └──→ [PathTrail@route_B] → [PathOutcome{success}@route_B]
```

查询 API 提供 `GetReachableEntries(entryId, callerId)` → `List<ShadowEntry>`，沿 References 边递归查询，每条边检查权限。

## 路径成本集成

### 流程

```
TryWalkToAsync(pos, target):
  1. 获取 AIgrid (byte[,]) ← from GameMapTerrain
  2. 获取 ShadowMapLayer (当前 AI)
  3. mergedGrid = ShadowGridCostMerger.Merge(AIgrid, shadow, pos, target)
  4. path = AStar.FindPath(pos, target, mergedGrid, ...)
  5. WalkToAsync(path)
  6. shadow.RecordPathTrail(pos, target, path)
```

### 合并规则

| 影子条目 | 对 grid[x,y] 影响 |
|---------|------------------|
| MonsterDensity > 5 | grid[x,y] += density (最高 127) |
| PathOutcome{failure} | grid[x,y] *= 2 (最高 127) |
| DangerZone | grid[x,y] = 127 |
| NavigationMarker{bypass} | grid[x,y] = 1 (最低成本) |
| 多条同时存在 | 取最大值 |

### 动态衰减

MonsterDensity 随时间衰减：

```
effective_density = max(0, raw_density - decayRate * elapsedSeconds)
```

- `decayRate` = 0.5 / 秒
- 阈值 5 以下视为"正常密度"，不加成本
- 这样临时聚集的怪物不会永久改变路径

## 信息收集周期

### FindNearestMonster (每 tick)
→ 扫描视野内所有怪物 → `RecordMonsterObservation(x, y, monsterNumber)`
→ MonsterObservation 聚合到 MonsterDensity (累加后衰减)

### TryWalkToAsync (每次行走)
→ 成功后 → `RecordPathTrail(from, to, steps)`

### quest_conditions_met / 段落切换
→ 如果最近 30s 内有路径记录但条件未满足 → `RecordPathOutcome{false}`
→ 如果条件满足 → `RecordPathOutcome{true}`

## 线程安全

- `ShadowMapLayer` 使用 `ConcurrentDictionary<long, List<ShadowEntry>>` 存储
- 写操作使用 `AddOrUpdate` 原子操作，内部拷贝 List 后追加再替换（无锁，但保证内存可见性）
- 读操作返回拷贝的 `List<ShadowEntry>`（通过 LINQ ToList），不存在读写竞争
- `ShadowEntry` 是 `record struct`（值语义），所有字段只读，天然不可变

## 性能预算

| 操作 | 预算 | 说明 |
|------|------|------|
| AddEntry | < 0.01ms | 一次字典插入 |
| GetEntries(x,y) | < 0.01ms | O(1) 字典查询 |
| GetEntriesInRect | < 0.1ms | 查询面积 < 50×50 区域 |
| MergeGrid | < 0.5ms | 克隆 256×256 grid + 遍历条目 |
| RecordMonsterObservation | < 0.02ms | 一次字典插入 |

## 怪物危险过滤器 (Monster Danger Filter)

在 TryWalkToAsync 中，阴影地图合并后执行实时怪物实力评估：

### 流程

```
searchGrid = ShadowGridCostMerger.MergeGrid(aiGrid, shadow, ...)
for each visible Monster:
    if monster.Level > ai.Level + MaxLevelDiff:
        mark 3×3 block around monster as 127 (impassable)
AStar.FindPath(pos, target, searchGrid)
```

- `MaxLevelDiff` 默认 15（可通过脚本 `maxLevelDiff` 参数配置）
- 每次 TryWalkToAsync 都重新评估，反映实时怪物分布
- 只对 AI 视野内的怪物做标记（非地图全局）
- 这样 A* 自然避开高等级怪物区域，AI 等级提升后"解锁"更多区域

### Force-through 回退

如果怪物危险过滤器导致所有路径算法无解：

```
Phase 1: searchGrid (shadow + danger) → A* → 有路径 → 走安全路径
Phase 2: searchGrid 无解 → originalGrid (无 shadow/danger) → A* → 强行通过
Phase 3: 仍然无解 → incremental step (贪心逐像素接近)
```

- Phase 2 是 "强行通过"：放弃怪物过滤，用原始网格找路。AI 可能被强怪杀死。
- Phase 3 是最坏情况回退：逐像素逼近目标，直到遇到障碍

## 死亡策略升级 (Death Strategy)

游戏设计上，强怪会随机出现在热点区域以驱动玩家升级/购买装备。AI 角色需要死亡适应策略：

### 连续死亡追踪

```csharp
private int _consecutiveDeaths;  // 每次杀怪重置为 0
```

- 每次死亡 `_consecutiveDeaths++`
- 每次成功杀怪 `_consecutiveDeaths = 0`
- 机制独立于 force-through，适用于任何死因

### 升级策略

| 连续死亡 | 行为 | 效果 |
|---------|------|------|
| 1-2 | 正常复活继续狩猎 | 不改变策略 |
| 3+ | 强制轮换热点 + 放弃 force-through | 热点在影子地图标记 DangerZone(10min) |
| 5+ | 全部热点标记 DangerZone(15min)，重置计数 | AI 必须在当前地图寻找新区域或换地图 |

### 实现位置

- `wait_respawn` action (ScriptExecutor.cs): 死亡计数 + 日志
- `RelocateToHotspotAsync`: 策略升级决策 + DangerZone 批量标记
- Kill detection (TickAsync): 成功杀怪后重置计数

### 预设参数

| 参数 | 默认值 | 说明 |
|------|--------|------|
| 放弃强制穿越 | 连续死亡 ≥ 3 | 停止 Phase 2 force-through |
| 放弃全部热点 | 连续死亡 ≥ 5 | 将全部热点标记为 DangerZone |
| DangerZone 过期 | 10-15 分钟 | 过期后 AI 重新尝试该区域 |

这些预设遵循 AR-02（离散化优先），后续可根据需要调整为连续参数配置。所有标志存储在影子地图中，支持跨 AI 可见性（Public 类型标记可通知同帮会 AI 避让）。

Phase B 中，L2 Orchestrator 将通过读取 ShadowMapLayer 来做出高级路径决策：
- 选择哪条路线前往任务目标
- 判断某区域是否安全
- 协调多 AI 避免踩踏

当前（Phase A）影子地图直接在 L3 ScriptExecutor 中使用，但接口已设计为可被 L2 调用。
