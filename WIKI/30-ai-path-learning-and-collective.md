# AI 路径学习与群体智慧系统

## 概述

AI 角色在执行任务时，会记录每次行走的完整路径（步数坐标）、战斗死亡、收益等信息。这些路径数据保存在个人数字影子地图中，通过群体决策系统每日/周/月整合，提取最优路径到群体数字影子地图，供全体 AI 查询参考。这类似于**蚂蚁信息素模型**——多次执行后最优路径自然浮现。

## 架构

```
┌─────────────────────────────────────────────────────────────┐
│              群体数字影子地图（GlobalShadowMap）             │
│  ├─ 任务最优路径（QuestPathRecord）                          │
│  ├─ 热点区域（HotspotRecord）                                │
│  └─ 危险区域（DangerZoneRecord）                              │
│  ▲ 每日整合 (PathConsolidator)                                │
├─────────────────────────────────────────────────────────────┤
│  AI角色个人路径记忆（PathMemoryStore）                        │
│  ├─ Q18/1 Map3 → 最近10次路径记录                            │
│  ├─ Q18/1 Map2 → 最近10次路径记录                            │
│  └─ ... 每个任务每个地图最近10次                               │
│  ▲ 实时记录 (AiPathRecorder)                                  │
├─────────────────────────────────────────────────────────────┤
│  AI角色执行任务时：                                           │
│  1. 查询 GlobalShadowMap 是否有最优路径                       │
│  2. 有 → 沿路径走（边走边防，遇高怪绕开）                    │
│  3. 无 → 自行探索 → 记录路径                                  │
│  4. 任务完成 → 保存路径到 PathMemoryStore                     │
└─────────────────────────────────────────────────────────────┘
```

## 数据结构

### AiPathRecorder（实时记录）
- `CurrentPath`: List<Point> — 当前行走的坐标序列
- `StartPoint` / `TargetPoint`: 起终点
- `State`: Idle / Recording / Completed / Interrupted
- `RecordStep(Point)`: 记录一步
- `ResumeFrom(Point)`: 中断后从当前位置续建
- `ToShadowEntry()`: 转存为 ShadowMapEntry

### QuestPathRecord（持久存储）
- 任务ID / 地图 / 角色名 / 等级
- PathPoints: List<string> — "x,y" 坐标数组
- StepCount / DeathCount / KillProgress
- TotalMoney / TotalItems / QuestItems / ExcellentItems
- PathScore: 路径评分 = 步数×0.5 + 死亡×10 - 物品×2 - 卓越×5

### PathMemoryStore（每角色每任务10条）
- JSON 持久化到 `pathmemory_{name}.json`
- `GetBestPath()`: 返回评分最高的路径
- `RecordPath(QuestPathRecord)`: 保存并自动淘汰最旧的一条

### PathConsolidator（群体决策）
- `ConsolidateDaily()`: 扫描所有角色的路径记录
- 按任务+地图分组 → 选最优 → 更新 GlobalShadowMap
- 路径坐标点标记为 Hotspot，死亡区域标记为 DangerZone

## 路径评分算法（PathScore）

```
得分 = Steps × 0.5 + Deaths × 10 - Items × 2 - Excellent × 5
```

- 步数越少越好
- 死亡次数影响极大（+10/次），鼓励安全路线
- 捡到物品和卓越装备说明路线效率高（奖励）

## 信息素模型

每次 AI 完成一条路径，路径上的每个坐标点就像留下了一份"信息素"：
- 路径被越多 AI 使用 → 信息素越浓
- 路径死亡越多 → 信息素衰减
- 群体整合时，信息素最浓的路径被推荐给新 AI

这完全不需要中央控制——每个 AI 独立探索、记录，群体自然浮现最优解。

## 启动加载

服务器启动时自动加载：
1. 每个 AI 角色的 `pathmemory_{name}.json` → PathMemoryStore
2. 全局的 `global_shadow_map.json` → GlobalShadowMap
3. PathConsolidator 注册为每日定时任务
