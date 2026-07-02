# 任务认知系统

## 概述

AI 的任务认知系统使 AI 角色能够自主接取、执行和提交游戏任务（Quest），无需人工干预。系统由三个核心组件组成：

- **MissionBoardService** — 任务看板初始化，填充可用任务列表
- **DynamicMissionGenerator** — 动态任务生成，组合事件扫描和物品分析
- **QuestExecutor** — 任务执行器（IBehaviorSubModule 实现）

## MissionBoardService

`MissionBoardService`（`src/AIPlayer/Decision/MissionBoardService.cs`）是任务看板初始化器。AI 角色登录时调用一次 `InitializeAsync`，扫描 `GameConfiguration` 中的任务定义，填充 `BoardState`。

### 配额配置

不同类型的任务有各自的激活配额：

```csharp
private static readonly QuestCategoryQuota[] DefaultQuotas = new[]
{
    new() { Category = QuestCategory.MainStory, MaxActive = 3, EffectiveLevel = 0 },
    new() { Category = QuestCategory.SideQuest,  MaxActive = 5, EffectiveLevel = 150 },
    new() { Category = QuestCategory.Daily,      MaxActive = 3, EffectiveLevel = 0 },
    new() { Category = QuestCategory.Random,     MaxActive = 2, EffectiveLevel = 220 },
    new() { Category = QuestCategory.InstanceEvent, MaxActive = 2, EffectiveLevel = 0 },
};
```

### BoardState

看板核心状态 `BoardState`（`src/AIPlayer/Decision/BoardState.cs`）是 AI 的"事实中心"，包含：

- 世界事件状态（已触发的事件列表）
- 任务状态（可用/活跃/已完成）
- 角色快照（当前地图、等级、位置）
- 看板待办列表（DashboardTodos）

## DynamicMissionGenerator

`DynamicMissionGenerator`（`src/AIPlayer/Decision/DynamicMissionGenerator.cs`）在每次心跳的 `SelectCurrentTask` 决策前被调用，动态生成临时任务。

### 组件协作

```
DynamicMissionGenerator
 ├── SystemEventScanner    — 扫描系统事件状态（恶魔广场、血色城堡等）
 ├── ItemNeedAnalyzer     — 分析道具需求（缺少药水、箭矢、材料等）
 └── GoalScheduler        — 目标规划器（注入目标任务）
```

### 防抖机制

- 事件扫描：每 10 秒执行一次
- 道具分析：每 5 秒执行一次
- 自动清理已完成的动态任务（ID 前缀为 `event_`、`item_`、`craft_`）

### 任务类型

| 任务类型 | 前缀 | 来源 | 示例 |
|----------|------|------|------|
| 事件 | `event_` | SystemEventScanner | "参加恶魔广场" |
| 道具 | `item_` | ItemNeedAnalyzer | "购买生命药水" |
| 合成 | `craft_` | MaterialFarmModule | "收集合成材料" |
| 目标 | `goal_` | GoalScheduler | "升到 200 级" |

## QuestExecutor

`QuestExecutor`（实现 `IBehaviorSubModule`，`ModuleId = "quest_executor"`）是任务执行的核心模块，定义在 `src/AIPlayer/Decision/IBehaviorSubModule.cs` 中。

### 执行流水线

```
ExecuteStepAsync(MissionItem item)
  │
  ├─ 阶段1：没接任务 → TryAcceptQuestAsync
  │    ├→ 找到 NPC → 走路过去 → 对话接任务
  │    └→ NPC不在 → 巡逻搜索
  │
  ├─ 阶段2：条件满足 → TrySubmitQuestAsync
  │    ├→ 找到 NPC → 打开对话 → 提交任务
  │    ├→ 需要ClientAction → 执行 ClientAction
  │    └→ 关闭对话
  │
  └─ 阶段3：条件未满足 → TryHuntAsync
       ├→ 搜索击杀目标怪物（先近 20 格，后远 60 格）
       ├→ 找到 → 走路接近 → 攻击（最多 10 次连击）
       └→ 找不到 → 巡逻搜索
```

### 任务状态码

| 状态 | 含义 |
|------|------|
| `InProgress` | 执行中，下次 tick 继续 |
| `Completed` | 当前步骤完成 |
| `Failed` | 执行失败（NPC 不存在、等级不够等） |
| `NoTarget` | 目标不在视野内 |

### 多段任务支持

QuestExecutor 支持通过 `GoalScheduler` 实现多段任务链：

```
目标: "收集 +7 白金套装"
 ├── 阶段1: 刷失落之塔获取白金组件
 ├── 阶段2: 收集合成材料（宝石、强化材料）
 ├── 阶段3: 找铁匠合成 +7 强化
 └── 阶段4: 装备收集到的物品
```

## 任务知识来源

任务定义从 `GameKnowledgeService` 加载：

```csharp
public IReadOnlyDictionary<(int Group, int Number), QuestInfo> Quests { get; }
```

`QuestInfo` 模型（`src/AIPlayer/Knowledge/Models/QuestInfo.cs`）包含：

| 字段 | 说明 |
|------|------|
| QuestGroup / QuestNumber | 任务唯一标识 |
| Name | 任务名称 |
| MinLevel / MaxLevel | 等级要求 |
| Repeatable | 是否可重复 |
| StartNpcNumber | 开启任务的 NPC |
| StartMoney | 接任务所需金钱 |
| RequiredKillsDescription | 击杀要求描述 |
| RequiredItemsDescription | 道具要求描述 |
| RewardsDescription | 奖励描述（经验/道具/属性点） |
