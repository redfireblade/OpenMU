# AI Player 系统设计

## 总纲

AI Player 系统是 OpenMU 的智能体扩展，位于 `src/AIPlayer/` 目录。它允许服务器运行由 AI 驱动（非人类玩家）的角色，实现完全自主的游戏内行为。

**核心设计原则：**

- AI Player 继承自标准 `Player`，通过 `AiPlayer.cs` 实现
- 系统采用 **三层架构**：AI 群体决策层 → AI 角色层 → 基础设施层
- 与原始游戏逻辑完全解耦，不修改 `GameLogic/` 核心代码

## 系统架构总览

```
┌─────────────────────────────────────────────────────────────┐
│                    AI 群体决策系统                          │
│  AiPlayerManager / EventBroadcaster / ExperienceAggregator │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────────┐  │
│  │ DNA 工厂  │ │看板分发器│ │事件广播器│ │ 知识汇聚引擎 │  │
│  └──────────┘ └──────────┘ └──────────┘ └──────────────┘  │
└──────────────────────┬──────────────────────────────────────┘
                       │ 初始化 / 广播 / 指令
┌──────────────────────▼──────────────────────────────────────┐
│                    AI 角色层（每角色独立）                    │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────────┐  │
│  │DNA实例   │ │心跳/定时器│ │脚本执行器│ │  看板        │  │
│  │(职业/加点)│ │(400ms)   │ │(1D/DAG) │ │ (任务队列)   │  │
│  └──────────┘ └──────────┘ └──────────┘ └──────────────┘  │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────────┐  │
│  │技能管理器│ │装备管理器│ │行为决策  │ │ 经验记录    │  │
│  │(9技能)   │ │(auto-eq) │ │(规则引擎)│ │ (角色记忆)  │  │
│  └──────────┘ └──────────┘ └──────────┘ └──────────────┘  │
└──────────────────────┬──────────────────────────────────────┘
                       │ 读取 / 存储 / 查询
┌──────────────────────▼──────────────────────────────────────┐
│                   基础设施层                                 │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────────┐  │
│  │ 规则库   │ │ 脚本库   │ │ 知识库   │ │  知识图谱   │  │
│  │(JSON规则)│ │(JSON脚本)│ │(KB模型)  │ │  (KG图)     │  │
│  └──────────┘ └──────────┘ └──────────┘ └──────────────┘  │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐                    │
│  │ 数字地图 │ │ AI记忆库 │ │ LLM接口  │                    │
│  │(AiMap)   │ │(角色/群) │ │(对话/指) │                    │
│  └──────────┘ └──────────┘ └──────────┘                    │
└─────────────────────────────────────────────────────────────┘
```

---

## 第1层：AI 群体决策系统

**核心职责**：管理所有 AI 角色的生命周期，向角色分发任务和事件，汇聚群体经验。

| 组件 | 文件 | 用途 | 状态 |
|------|------|------|------|
| `AiPlayerManager` | `AiPlayerManager.cs` | 角色生命周期管理（创建/加载/停止） | ✅ 实现 |
| `EventWatcherService` | `Decision/EventWatcherService.cs` | 扫描游戏系统事件（恶魔广场等） | ✅ 实现 |
| `IEventBroadcaster` | `Decision/IEventBroadcaster.cs` | 向全体/单 AI 广播事件消息 | ✅ 实现 |
| `ExperienceAggregator` | `Decision/Experience/ExperienceAggregator.cs` | 阶段性地收集所有角色的行为日志，聚合生成规则候选 | ✅ 实现 |
| `ExperienceRuleInjector` | `Decision/Experience/ExperienceRuleInjector.cs` | 将规则候选写入经验规则表 | ✅ 实现 |
| `CharacterBuildDnaRepository` | `Knowledge/Models/CharacterBuildDna.cs` | **DNA 仓库**：所有职业流派定义、加点公式、技能选择 | ✅ 新增 |
| **看板分发器** | — | 将当日计划任务、当前事件写入 AI 看板 | 🔴 待实现 |
| **群体指令通道** | — | 向特定/单/全体 AI 发自定义消息和任务 | 🔴 待实现 |

**决策流程：**
```
① 系统事件（定时器/活动开始）→ EventWatcherService 扫描
② → IEventBroadcaster 向符合条件的 AI 广播
③ → 各 AI 的 HeartbeatService 接收事件 → 写入 MissionBoard
④ → 周期性的 ExperienceAggregator 收集所有角色日志
⑤ → 聚合 → 注入经验规则库
```

---

## 第2层：AI 角色层

**核心职责**：每个 AI 角色独立运行，拥有自己的心跳、脚本执行器、看板和技能管理。

### 角色内部架构

```
┌──────────────────────────────────────────┐
│               AiPlayer                   │
│  ┌────────────────────────────────────┐  │
│  │       AiPlayerLogic (主循环)       │  │
│  │  ┌────────┐ ┌────────┐ ┌────────┐  │  │
│  │  │World   │ │Script  │ │Tick    │  │  │
│  │  │State   │ │Executor│ │Snapshot│  │  │
│  │  │刷新    │ │决策    │ │记录    │  │  │
│  │  └────────┘ └────────┘ └────────┘  │  │
│  └────────────────────────────────────┘  │
│                                          │
│  ┌────────┐ ┌────────┐ ┌────────────┐   │
│  │ Skill  │ │ Equip  │ │ Mission    │   │
│  │ Bar    │ │ Compare│ │ Board      │   │
│  │ Manager│ │ Service│ │ (任务队列) │   │
│  └────────┘ └────────┘ └────────────┘   │
│                                          │
│  ┌────────┐ ┌────────┐ ┌────────────┐   │
│  │ Combat │ │Item    │ │ Character  │   │
│  │ Skill  │ │Pickup  │ │ Memory     │   │
│  │Service │ │Manager │ │ (经验记录) │   │
│  └────────┘ └────────┘ └────────────┘   │
└──────────────────────────────────────────┘
```

### 角色关键组件

| 组件 | 用途 | 实现 |
|------|------|------|
| **DNA 实例** | 职业流派、加点公式、核心技能表 | `CharacterBuildDna` |
| **心跳/主循环** | 400ms 定时决策 tick | `AiPlayerLogic.RunLoopAsync()` |
| **ScriptExecutor** | 1D 脚本执行（JSON 优先级链） | `ScriptExecutor.TickAsync()` |
| **HeartbeatService** | DAG 模式决策（规则+子模块） | `HeartbeatService.TickAsync()` |
| **MissionBoard** | 任务看板（当前待办事项） | `MissionBoardService` |
| **SkillBarManager** | 9 技能栏管理 | `SkillBarManager.UpdateSkillBar()` |
| **CombatSkillService** | 战斗技能选择策略 | `CombatSkillService.SelectAttackSkill()` |
| **EquipmentCompareService** | 自动换装（背包→装备槽） | `AutoEquipIfBetterAsync()` |
| **CharacterMemory** | 角色经验记忆（击杀/死亡统计） | `CharacterMemory` |

### 执行模式

| 模式 | 说明 | 适用场景 |
|------|------|----------|
| **1D 模式** | ScriptExecutor + 优先级链 JSON 脚本 | 简单重复行为（单地图狩猎/任务） |
| **DAG 模式** | HeartbeatService + 规则引擎 + 子模块 | 完整全代理模式（默认） |

---

## 第3层：基础设施层

**核心职责**：为上层提供知识、规则、脚本、地图和记忆等基础资源。

| 资源 | 说明 | 文件 |
|------|------|------|
| **规则库** | 基于玩家经验形成的行为规则 | `rules_default.json` + `rules_experience.json` |
| **脚本库** | 从规则生成的 JSON 行为脚本 | `Decision/scripts/` |
| **游戏知识库** | 地图/怪物/物品/NPC/掉落知识 | `GameKnowledgeService` |
| **知识图谱** | 实体关系图（地图连通、掉落链、合成链） | `KnowledgeGraphBuilder` + `KnowledgeGraphQuery` |
| **数字地图（影子世界）** | AI 群体独有的平行世界层：通行网格、信息素、领地、路径点、集合点、个人影子层 | `AiMap` + `AiMapManager` + `ShadowMapLayer` |
| **AI 记忆库** | 角色记忆 + 跨角色账户知识 + 群体经验 | `CharacterMemory` + `AccountKnowledge` + `ExperienceMemory` |
| **LLM 接口** | 与大模型对话生成行为或响应玩家 | 🔴 待实现 |

---

## AI 角色生命周期

```
创建 (CreateAiPlayerAsync)
  1. 选取 DNA (CharacterBuildDna)
  2. 创建角色 (CreatePlaceholderData: 药水 + 新手武器)
  3. 初始化 (InitializeAsync: 登录→选角色→进地图)
  4. 精选技能 (SkillSelector: 9 技能)
  5. 启动主循环 (AiPlayerLogic: 400ms/tick)

运行中
  → WorldState 刷新（怪物、掉落、位置）
  → ScriptExecutor / HeartbeatService 决策
  → 执行选定动作（移动/攻击/拾取/喝药/技能）
  → TickSnapshot 记录

停止 (StopAiPlayerAsync)
  → 保存进度
  → 断开连接
```

---

## DNA 驱动初始化流程

```
CreateAiPlayerAsync(config)
  │
  ├─ 1. SelectDNA(classId, direction)
  │     → CharacterBuildDnaRepository.GetAll()
  │        → ClassName / AttackType / AttackRange / Phases / CoreSkillIds
  │
  ├─ 2. CreatePlaceholderData(dna)
  │     → 基础属性（按 BuildPhase 分配）
  │     → 药水 (slot 12-17) + 新手武器 (slot 0)
  │     → 精选 9 技能（DirectHit×4 + Area×1 + Buff×3 + Passive×1）
  │     → 启动金币
  │
  ├─ 3. InitializeAsync()
  │     → 登录游戏世界
  │
  ├─ 4. AiPlayerLogic(dna)
  │     → ScriptExecutor(dna.AttackRange)  # 设置攻击距离
  │     → SkillBarManager.UpdateSkillBar()  # 写入快捷键槽
  │
  └─ 5. 主循环开始
```

---

## 技能精选规则（9 技能上限）

每个角色最多保留 9 个技能，按类别和优先级选择：

| # | 类别 | 数量 | 选择策略 | 用途 |
|---|------|------|----------|------|
| 1 | 基础攻击 | 1 | `DirectHit`，最短冷却+最高伤害 | 日常打怪 |
| 2 | 群攻刷怪 | 1 | `AreaSkill`，范围最大 | 群刷练级 |
| 3-5 | 强力输出 | 3 | `DirectHit`，伤害前 3 | PK/Boss |
| 6-8 | 辅助增益 | 3 | `SkillType.Buff` | 加防/加攻/加血 |
| 9 | 被动/备用 | 1 | `PassiveBoost` 或 `Regeneration` | 引擎需求 |

---

## 5 层行为流水线（单角色视角）

```
Knowledge (知识层)
    ↓ 查询
Rules (规则层) — RuleEngine + RuleDef
    ↓ 匹配
Scripts (脚本层) — ScriptExecutor + BehaviorScript (1D) / HeartbeatService (DAG)
    ↓ 调度
Execution (执行层) — IBehaviorSubModule 实现类（QuestExecutor 等）
    ↑ 行为反馈
Debug (调试层) — IAiDebugService + TickSnapshot
```

| 层 | 组件 | 用途 |
|----|------|------|
| 知识层 | `GameKnowledgeService`, `KnowledgeAccessService`, `PersonalityProfile`, `ExperienceMemory` | 查询游戏世界知识、人格配置、经验记忆 |
| 规则层 | `RuleEngine`, `RuleDef`, `rules_default.json`, `rules_experience.json` | 评估规则、选择最高优先级规则、生成 MissionItem |
| 脚本层 | `ScriptExecutor`, `BehaviorScript`, `HeartbeatService`, `ScriptRegistry` | 执行 1D 或 DAG 行为脚本 |
| 执行层 | `QuestExecutor`, `CraftingModule`, `EventExecutorModule` 等 9 模块 | 执行具体游戏动作（接任务/合成/战斗） |
| 调试层 | `IAiDebugService`, `TickSnapshot`, `/aidebug` 页面 | 单步调试、性能统计 |

---

## 相关文档

### 核心设计
| 文档 | 说明 |
|------|------|
| [28-ai-character-dna-and-growth.md](28-ai-character-dna-and-growth.md) | AI 角色 DNA 系统（职业流派、加点策略、9 技能精选、低等级生存） |
| [22-ai-personality-and-growth.md](22-ai-personality-and-growth.md) | AI 人格模型与行为矩阵 |
| [24-quest-cognition-system.md](24-quest-cognition-system.md) | 任务认知系统 |

### 知识体系
| 文档 | 说明 |
|------|------|
| [25-ai-knowledge-architecture.md](25-ai-knowledge-architecture.md) | 知识库架构（GameKnowledgeService、经验记忆、规则引擎） |
| [27-knowledge-graph.md](27-knowledge-graph.md) | 知识图谱（实体关系、路径推理、查询 API） |

### 空间与导航
| 文档 | 说明 |
|------|------|
| [23-spatial-cognition-and-digital-map.md](23-spatial-cognition-and-digital-map.md) | 空间认知与数字地图（AiMap、信息素、领地） |

### 游戏行为参考
| 文档 | 说明 |
|------|------|
| [29-mu-guide-for-ai.md](29-mu-guide-for-ai.md) | 《奇迹MU》新手攻略 — AI 行为参考（升级路线、药水、拾取） |
| [06-existing-ai.md](06-existing-ai.md) | 现有原生 MonsterAI 行为分析 |

### 调试与测试
| 文档 | 说明 |
|------|------|
| [26-reliability-patterns.md](26-reliability-patterns.md) | 可靠性模式与 Agent 测试体系 |

---

## 阶段实现路线

### Phase 1：基础生存（当前阶段）
- [ ] AI 角色 9 级裸体出生 + 新手武器
- [ ] 基础近战/远程战斗（连击机制）
- [ ] 药水自动使用（HP<60%/MP<30%）
- [ ] 金币拾取和掉落识别
- [ ] auto-equip 换装链路
- [ ] 死亡→复苏→返回狩猎

### Phase 2：角色成长
- [ ] 按 DNA 加点公式自动分配属性
- [ ] 逐级精选 9 技能
- [ ] 地图等级判断和转场
- [ ] 群体经验汇聚和规则注入

### Phase 3：智能决策
- [ ] MissionBoard 任务看板
- [ ] DAG 模式全代理行为
- [ ] EventBroadcaster 事件驱动
- [ ] 知识图谱路径推理

### Phase 4：高级能力
- [ ] LLM 接口（对话/指令）
- [ ] 跨角色协作
- [ ] 自动合成/强化
- [ ] PK 和 Boss 策略
