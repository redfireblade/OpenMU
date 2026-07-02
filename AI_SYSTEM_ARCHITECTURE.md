# AI 系统架构总纲

> **地位**：本文档是 AI Player 系统的最高架构说明，所有 AI 相关开发必须以本文档为参考。每次会话前必读。
> 
> **对应 WIKI**：[WIKI/16-ai-player-system-design.md](WIKI/16-ai-player-system-design.md) | [WIKI/23-spatial-cognition-and-digital-map.md](WIKI/23-spatial-cognition-and-digital-map.md) | [WIKI/28-ai-character-dna-and-growth.md](WIKI/28-ai-character-dna-and-growth.md)

---

## 一、系统架构总览

AI Player 系统采用**三层架构**：

```
┌─────────────────────────────────────────────────────────────┐
│                    AI 群体决策系统                          │
│  管理所有AI角色生命周期 / 分发任务事件 / 汇聚群体经验      │
│  AiPlayerManager / EventBroadcaster / ExperienceAggregator │
│  DNA工厂 / 看板分发器 / 群体指令通道                       │
└──────────────────────┬──────────────────────────────────────┘
                       │ 初始化 / 广播 / 指令
┌──────────────────────▼──────────────────────────────────────┐
│                    AI 角色层（每角色独立）                    │
│  独立心跳(400ms) / 脚本执行器(1D/DAG) / 看板 / 技能/装备   │
│  AiPlayerLogic / ScriptExecutor / HeartbeatService          │
│  SkillBarManager / CombatSkillService / MissionBoard        │
└──────────────────────┬──────────────────────────────────────┘
                       │ 读取 / 存储 / 查询
┌──────────────────────▼──────────────────────────────────────┐
│                   基础设施层                                 │
│  规则库 / 脚本库 / 知识库 / 知识图谱 / 数字地图 / 记忆库   │
│  RuleEngine / GameKnowledgeService / KnowledgeGraph         │
│  AiMap(影子世界) / CharacterMemory / 规则库/脚本库         │
└─────────────────────────────────────────────────────────────┘
```

---

## 二、核心概念：影子世界（数字地图）

**数字地图不是单纯的导航工具，它是 AI 社会的平行世界空间。**

详见 [WIKI/23-spatial-cognition-and-digital-map.md](WIKI/23-spatial-cognition-and-digital-map.md)

### 四层结构

| 层 | 功能 | 技术实现 |
|----|------|---------|
| **地理层** | 通行性、信息素、领地、路径点 | `AiMap.WalkGrid/PheromoneGrid/TerritoryGrid/Waypoints` |
| **社会层** | 帮会暗号、秘密堂口、家族信息库、群体任务 | 待实现（基于 AiMap 的标记系统） |
| **记忆层** | 历史记录、规则库/脚本库/数据库的活水源 | 待实现（信息素 → 规则沉淀链路） |
| **神经层** | 每个 AI 一个节点，整体一张动态感知网络 | 待实现（个体 → 群体 → 迭代演化） |

### 为什么必须是地图而不是树？

```
树状信息网络：去中心化但无空间索引，检索复杂
     ↕
基于坐标的影子世界：坐标即索引，天然可检索、可聚合、可可视化
```

就像用城市地图导航 vs 用电话本导航。

### 群体智能（信息素机制）

```
AI-A 发现好刷怪点  → 留下信息素
    → AI-B 读到信息素 → 也来刷 → 留下更多信息素
        → AI-C 跟来 → 蜂群效应
```

没有中央指令、不依赖数据库，纯粹靠信息素涨落自然形成群体认知。

---

## 三、AI 角色 DNA 系统

详见 [WIKI/28-ai-character-dna-and-growth.md](WIKI/28-ai-character-dna-and-growth.md)

### 定义

每个 AI 角色创建时被赋予 DNA，决定：

| 字段 | 说明 |
|------|------|
| 职业流派 | 血牛战士 / 智敏法师 / 敏弓 |
| 攻击类型 | 近战 2.5 / 远程 6.0 |
| 加点公式 | 每级的力/敏/体/智权重 |
| 核心技能 | 最多 9 个精选技能 |

### 9 技能精选规则

| 类别 | 数量 | 策略 |
|------|------|------|
| 基础攻击 | 1 | DirectHit，最短冷却+最高伤害 |
| 群攻刷怪 | 1 | AreaSkill，范围最大 |
| 强力输出 | 3 | DirectHit，伤害前 3（PK/Boss） |
| 辅助增益 | 3 | Buff，最有用的 3 个 |
| 被动/备用 | 1 | PassiveBoost / Regeneration |

### 三基础职业 DNA

| 职业 | 攻击 | 加点公式 | 核心技能 |
|------|------|---------|---------|
| 血牛战士 | 近战 2.5 | 1-40级 2力1敏2体 → 41-70级 1力1敏3体 → 71+全体力 | 地裂斩(62)、升龙击(63)、生命之光(64) |
| 智敏法师 | 远程 6.0 | 1-40级 3智2敏 → 41+ 4智1敏 | Fire Blast(74)、黑龙波(30)、陨石(56) |
| 敏弓 | 远程 6.0 | 1-40级 1力4敏 → 41+ 全敏 | 多重箭(47)、冰封箭(48)、穿透箭(49) |

---

## 四、AI 角色生命周期

```
CreateAiPlayerAsync(config)
  1. SelectDNA(classId)                   选取角色DNA
  2. CreatePlaceholderData(dna)           创建角色（药水+新手武器+9技能+启动金）
  3. InitializeAsync()                    进入游戏世界
  4. AiPlayerLogic(dna)                   启动主循环（400ms/tick）
     4.1 WorldState 刷新                  感知周围环境
     4.2 ScriptExecutor / HeartbeatService 决策
     4.3 执行动作                         移动/攻击/喝药/拾取/技能
     4.4 TickSnapshot 记录                调试数据
  5. 死亡 → 复活 → 返回狩猎              死亡循环
  6. 任务完成 → 接下一个任务              多任务循环
```

---

## 五、行为流水线（5 层）

```
Knowledge (知识层)     GameKnowledgeService / 知识库 / 知识图谱
    ↓ 查询
Rules (规则层)         RuleEngine + RuleDef（内置规则 + 经验规则）
    ↓ 匹配
Scripts (脚本层)       ScriptExecutor(1D) / HeartbeatService(DAG)
    ↓ 调度
Execution (执行层)     IBehaviorSubModule: QuestExecutor / CraftingModule 等 9 模块
    ↑ 行为反馈
Debug (调试层)         IAiDebugService + TickSnapshot + /aidebug 页面
```

---

## 六、当前实现状态

### 已完成 ✅
- [x] 三层架构框架（AiPlayerManager / AiPlayerLogic / 基础设施）
- [x] 1D 脚本执行引擎（ScriptExecutor + JSON）
- [x] DAG 决策引擎（HeartbeatService + 子模块）
- [x] 数字地图基础（AiMap: WalkGrid/PheromoneGrid/TerritoryGrid/Waypoints）
- [x] 信息素系统（PheromoneGrid + 蒸发）
- [x] 领地标记（TerritoryGrid + 防堆叠）
- [x] ShadowMapLayer（个人影子层）
- [x] DNA 系统（CharacterBuildDna 仓库 + 3 职业定义）
- [x] 新手武器（各职业基础武器）
- [x] 技能精选（9 技能规则）
- [x] 战斗走位（`is_surrounded` + `break_out`）
- [x] 多任务循环（`@submit_quest` → `@start`）
- [x] 技能栏初始化（ScriptExecutor 构造时调用 SkillBarManager）
- [x] 攻击连击（AttackTargetAsync 5 连击）
- [x] 知识图谱（KnowledgeGraph + KG 查询 API）
- [x] 事件广播（EventWatcherService + IEventBroadcaster）
- [x] 经验聚合（ExperienceAggregator + RuleInjector）

### 进行中 🔄
- [ ] 升级自动加点（LevelUpPoints 按 DNA 分配）
- [ ] 低等级生存（1-15 级在 Lorencia 外围狩猎，不冲 Noria）
- [ ] 数字地图社会层（帮会暗号、秘密堂口、群体任务部署）
- [ ] 升级后技能重选（达到阶段阈值刷新 9 技能）

### 待启动 📋
- [ ] 群体指令通道（向特定/单/全 AI 发消息）
- [ ] LLM 接口（对话/指令生成行为）
- [ ] 跨角色协作（组队、分工）
- [ ] 自动合成/强化
- [ ] PK / Boss 策略

---

## 七、演化路线

```
Phase 1: 基础生存（当前）
  9 级裸体 + 新手武器 → 外围打怪升级 → 基本的战斗/拾取/喝药循环

Phase 2: 角色成长
  按 DNA 加点 → 技能随等级重选 → 地图级进阶 → 多任务链

Phase 3: 群体智能
  影子世界社会层 → 群体任务 → 帮会系统 → 信息素自动化决策

Phase 4: 高级能力
  LLM 驱动行为 → 跨角色协作 → 合成/强化 → PK/Boss
```

---

## 八、关键设计原则

1. **AI Player 继承自标准 Player**，与 `GameLogic/` 完全解耦
2. **影子世界是 AI 群体的平行空间**，玩家不可见、不可干涉
3. **没有中央指令**，群体行为通过影子世界的信息素涨落自然涌现
4. **DNA 驱动角色成长**，每个角色有独立的职业/加点/技能蓝图
5. **进化 = 个体经验 + 群体积累 + 迭代**，每一次打怪都在贡献群体知识
