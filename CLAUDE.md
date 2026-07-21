# OpenMU 开发规范（CLAUDE.md）

本文件是项目的**规范锚定文件**，按 [Spec/WIKI/SDD/KB/KG 概念框架](../../../CONCEPT-FRAMEWORK-SPEC-WIKI-KB-KG.md) 组织。

> **❗ 必读：AI 系统架构总纲 + OAPS v3.0**
> 所有 AI Player 相关开发，必须先阅读：
> 1. [AI_SYSTEM_ARCHITECTURE.md](AI_SYSTEM_ARCHITECTURE.md) — 三层架构/影子世界/DNA 系统
> 2. [OAPS项目状态](../../../OAPS-project-state.md) — 当前实现状态和文件清单
> 3. [OAPS集成方案](../../../OAPS-INTEGRATION-PLAN.md) — 六层架构定义、心智引擎接口
> **每次会话开始前必须重新阅读**以确保上下文一致。

---

## 规范层（Spec）— 做什么 / 不做什么

### 技术栈
- .NET 10, C# 13
- Entity Framework Core (PostgreSQL)
- Blazor Server (AdminPanel)
- Dapr (分布式，可选)

### ⛔ 铁律（违者立即终止会话）

#### 铁律 1：禁止使用 -demo 模式

**任何时候禁止使用 `-demo` 参数启动服务器。** `-demo` 使用 InMemory 数据库，每次启动重新初始化数据，导致所有 DB 修改丢失。

**正确的启动方式（仅此一种）：**
```
dotnet run --project src/Startup/MUnique.OpenMU.Startup.csproj -p:ci=true -- --autostart -resolveIP:local
```

**理由：**
1. 数据库是 PostgreSQL，所有数据持久化在 DB 中
2. `-demo` 模式绕过 DB → 每次重新初始化 → 数据永远不对
3. 任何修改（spawn_gates.json、ExitGate 坐标等）必须在 PG 模式下验证
4. 之前已多次因 `-demo` 导致定位问题花数小时

#### 铁律 2：不理解前禁止修改代码

**对坐标系统、WalkMap、ReadTerrainData、PathFinder 的任何修改，必须在 AI 能完整解释三处互补关系之后才能动手。**

**三处互补关系（必须同时理解和修改）：**
```
1. ReadTerrainData 存储索引（WalkMap[列, 行])
2. GetIndexOfPoint 索引计算（列 * 256 + 行）
3. 外部 WalkMap 访问（[行, 列]）
```

**违反后果：** "走几步就停"—需要整节会话恢复

#### 铁律 3：修改计划和数据操作须经用户批准

**任何涉及以下操作前，必须提交书面计划给用户批准，然后才能动手：**
- 修改 `ReadTerrainData`、`WalkMap`、`SafezoneMap`、`AIgrid` 相关代码
- 修改数据库结构或执行 DELETE/UPDATE/DROP 操作
- 修改 `Gates.cs`、`GameMapTerrain.cs`、`Player.cs` 中坐标相关逻辑
- 启动带 `-reinit` 参数重建数据库

**例外情况：** 不涉及上述内容的小修小改（如更新 spawn_gates.json、修改注释、新增日志输出）
- SignalR (实时地图)

### 代码规范（强制）
- 遵循 StyleCop 规则（项目已配置 .editorconfig）
- 所有公共 API 必须有 XML 文档注释
- 异步方法必须以 Async 结尾
- 禁止从 Deathway/zTeam/IGCN 等泄露源码复制逻辑（Clean Room 原则）

### 关键函数索引
> [WIKI/90-function-index.md](WIKI/90-function-index.md) — AI 服务端关键函数说明（ReadTerrainData、FindSpawnPoint、PlaceAtGate、PathFinder、Gates.cs 等）

### 架构约束
- 游戏逻辑必须写在 PlugIns/ 目录，通过 MUnique.OpenMU.PlugIns 接口注册
- 网络协议包定义在 Network/Packets/，通过 XML + XSLT 生成
- 数据模型在 DataModel/，持久化在 Persistence/
- 禁止在 GameLogic/ 中直接操作数据库，必须通过 Repository 模式

### 坐标系统（强制 — 每次会话必读，再错永不录用）

> 本规则为项目的**最大历史痛点**，AI 多次在此犯错导致角色出生在野外/走不了路。**任何涉及坐标的代码修改前，必须逐字阅读并严格遵守此规则。**
> 
> **核心事实：当前代码是一个稳定的互补系统。不理解整个链条之前，禁止修改任何一行坐标代码。**

#### 现状（不可改 — 系统依赖互补关系）

| 组件 | 当前实现 | 说明 |
|------|----------|------|
| `.att` 文件 | `i = 行 * 256 + 列` | 原版格式，不可改变 |
| `ReadTerrainData` | `WalkMap[i&0xFF=列, i>>8=行]` | **存 `[列, 行]`** —和 .att 原始顺序"不同"但互补 |
| `GetIndexOfPoint` | `(pos.Y << 8) + pos.X` = `列 * 256 + 行` | PathFinder 内部索引，和存储互补 |
| 外部 `WalkMap` 访问 | `[posX=行, posY=列]` = `[行, 列]` | 和存储 `[列, 行]` 互补 |
| `FindSpawnPoint` | `WalkMap[col=Y1, row=X1]` | 内部用 `[列, 行]` 和存储一致 |
| `SpawnGate` / `ExitGate` | `X1=行, Y1=列` | 数据库语义 |

**为什么这样但能工作：** 
- `.att` 文件是行优先(`行*256+列`)，但代码解析时用了列优先索引(`i&0xFF=列`)
- `GetIndexOfPoint` 也用了列优先(`列*256+行`) — 两个"错误"抵消
- 最终：**.att 中 `(行,列)` 的值存到了 `WalkMap[列, 行]`，外部 `[行, 列]` 读到的互补位置恰好和 PathFinder 索引一致**

#### 关键数据流（当前正确的代码）
```
spawn_gates.json → [X=行, Y=列, X=行, Y=列]
    ↓ PlaceAtGate / ClientReady
FindSpawnPoint:  WalkMap[col=Gate.Y1, row=Gate.X1]  ← 内部[列,行]和存储一致
    ↓
返回 Point(row, col) = Point(行, 列)
    ↓
PositionX = point.X = 行, PositionY = point.Y = 列 ✅
```

#### 如果要统一（未来计划），必须同时改三个地方：
```csharp
// 1. ReadTerrainData — 存 [行, 列]
this.WalkMap[i>>8 (=行), i&0xFF (=列)] = ...;

// 2. GetIndexOfPoint — 行优先
return (pos.X << 8) + pos.Y;  // 行 * 256 + 列

// 3. 外部 WalkMap 访问不变 — [posX, posY] = [行, 列] 已经正确
// 4. FindSpawnPoint — WalkMap[row=Gate.X1, col=Gate.Y1]
```

> **⚠️ 在真正理解所有三点之前，禁止做任何统一尝试。当前互补系统是稳定的。**

#### 常见错误模式（AI 反复犯的 — 记住教训）
- ❌ 单独改 `ReadTerrainData` 索引 → "走几步就停"
- ❌ 以为 `Gate.X1=列` → 错，X1=行(PositionX)
- ❌ 以为 `Gate.Y1=行` → 错，Y1=列(PositionY)
- ❌ 用 `-demo` 模式测试 → 数据全部丢失
- ❌ 直接删除 DB 记录而不是更新坐标 → 外键断裂导致传送失效
- ❌ `FindSpawnPoint` 返回 `Point(col, row)` → Position 被设为 (列,行)
- ❌ `ClientReadyAfterMapChangeAsync` 中用 `WalkMap` 检查位置 → 互补索引导致读到错误位置

#### PlaceAtGate 说明（AI 添加的辅助方法）

在 `WarpToAsync`（传送门/地图列表传送）和 `RespawnAtAsync`（死亡复活）中被调用。行为：使用 `FindSpawnPoint` 在出生门区域内随机选择安全区+可行走位置，而非固定在门左上角。

设计意图：避免玩家传到不可行走坐标（雕像/墙壁等）。**不是原始 OpenMU 行为，但有益。** 如需恢复原始行为（固定门左上角），删除 `PlaceAtGate` 中的 `FindSpawnPoint` 调用，直接使用 `new Point(gate.X1, gate.Y1)`。
- ❌ 不要修改 Startup/ 中的硬编码服务注册，除非新增独立插件
- ❌ 不要删除现有迁移（Migrations/），如需改模型，新增迁移
- ❌ 不要引入非 .NET 原生依赖（如 Python 运行时），外部工具通过 CLI 调用
- ❌ 不要跳过 StyleCop 规则（.editorconfig 已配置）
- ❌ 不要在集成测试中硬编码地图坐标 —— 使用动态发现（FindWalkableDestination / FindBlockedTile）
- ❌ 不要修改 Network/Packets/ 中的协议生成文件（由 XML + XSLT 控制）

---

## WIKI 层 — 规范的容器

完整 WIKI 文档位于 [WIKI/](WIKI/) 目录，涵盖：

| 章节 | 文件 | 内容 |
|------|------|------|
| 项目概述 | [01-project-overview.md](WIKI/01-project-overview.md) | 引擎类型、版本、核心玩法 |
| 目录结构 | [02-directory-structure.md](WIKI/02-directory-structure.md) | 源码目录树 |
| 网络协议 | [03-network-protocol.md](WIKI/03-network-protocol.md) | 通信方式、加密方式 |
| 游戏逻辑 | [05-game-logic.md](WIKI/05-game-logic.md) | 经验公式、掉落公式 |
| 现有 AI | [06-existing-ai.md](WIKI/06-existing-ai.md) | MonsterAI.cs 行为分析 |
| 资源格式 | [07-resource-formats.md](WIKI/07-resource-formats.md) | .att/.ozt/.bmd 格式 |
| 实体名称 | [08-entity-names.md](WIKI/08-entity-names.md) | 道具/怪物/NPC/地图名称 |
| API 契约 | [09-api-contracts.md](WIKI/09-api-contracts.md) | 公共方法签名 |
| 危险区 | [10-dangerous-zones.md](WIKI/10-dangerous-zones.md) | 禁止修改的文件清单 |
| 扩展点 | [11-extension-points.md](WIKI/11-extension-points.md) | 推荐扩展位置 |
| 服务器架构 | [12-server-architecture.md](WIKI/12-server-architecture.md) | 启动顺序和配置 |
| AI 系统设计 | [16-ai-player-system-design.md](WIKI/16-ai-player-system-design.md) | AI Player 总纲 |
| **⭐ 架构总纲** | [AI_SYSTEM_ARCHITECTURE.md](AI_SYSTEM_ARCHITECTURE.md) | **必读 — 三层架构/影子世界/DNA 系统** |
| AI DNA 与成长 | [28-ai-character-dna-and-growth.md](WIKI/28-ai-character-dna-and-growth.md) | 职业流派、加点策略、9 技能精选 |
| MU 新手指南 | [29-mu-guide-for-ai.md](WIKI/29-mu-guide-for-ai.md) | AI 行为参考（升级路线、药水、拾取） |

> **规则：WIKI 未覆盖的领域，禁止 AI 直接修改。必须先补充 WIKI，再写代码。**

---

## SDD 层（规范驱动开发）— 工程方法论

### 改造目标历史（全部完成 ✅）

所有 Phase 和后续改进已按 SDD 流程（WIKI → 代码 → 验证 → 迭代）完成：

#### Phase 1-10: AI Player Testing Infrastructure
全部完成。详见 [ai_test_client_complete.md](docs/ai_test_client_complete.md)。

#### 后续 4 项改进（全部完成）
- ✅ Item 4: StepOnceAsync 可靠性修复
- ✅ Item 2: TCP 集成测试（TcpDebugClient 连接 + 登录）
- ✅ Item 3: BenchmarkDotNet 性能基准
- ✅ Item 1: GoalModule/InteractionModule 测试

#### 自主行为验证
- ✅ 34 个 AI 测试通过
- ✅ AI 能自主检测怪物、选择目标、走位接近、攻击
- ✅ 生存/战斗模块协同
- ✅ 危险等级评估（跳过过强怪物）

#### DAG 编排库提取（最新完成）
- ✅ `MUnique.OpenMU.Orchestrator` — 13 个文件，零游戏依赖
- ✅ 拓扑排序（Kahn 算法）+ 环检测 + 节点隔离执行
- ✅ 34/34 测试通过，0 错误构建

### SDD 工作流（适用于所有后续开发）

```
Step 0: 更新 WIKI（先写规范）
    ↓
Step 1: Claude Code 读取 WIKI + CLAUDE.md
    ↓
Step 2: 生成代码 → 遵守规范（不改危险区、不碰协议层）
    ↓
Step 3: 构建验证（dotnet build → 0 error）
    ↓
Step 4: 测试验证（dotnet test → 全部通过）
    ↓
Step 5: 不符合规范？→ 回到 Step 2
    ↓
Step 6: 符合规范 → 提交
```

### 代码审查协议（VERDICT）

#### Correctness Gate
- [ ] 寻径算法：路径步进必须相邻（Chebyshev distance = 1），终点必须到达目标
- [ ] 地图转换：WorldTile[,] → byte[,] 编码公式必须与 GameMapTerrain.UpdateAiGridValue 一致
- [ ] 异步操作：所有 I/O 方法以 Async 结尾，正确使用 ConfigureAwait(false)
- [ ] 状态机：PlayerState 转换遵循合法路径（LoginScreen → Authenticated → CharacterSelection → ...）

#### Style Gate
- [ ] StyleCop 零警告（SA1201, SA1402, SA1611 等已修复，不引入新警告）
- [ ] 每个文件一个 public 类型（SA1402）
- [ ] 构造函数在属性之前（SA1201）
- [ ] XML 文档注释完整（SA1611）

#### Test Gate
- [ ] 新功能有对应测试
- [ ] 集成测试使用真实地形数据（.att 文件）
- [ ] 路径验证检查：walkability、adjacency、bounds、end-at-target、MaxPathLength ≤ 128
- [ ] 边界情况覆盖：blocked start, blocked end, same start/end, safezone handling

---

## 知识层（KB + KG）

### 现有知识库（KB）— KnowledgeBase.cs

项目已实现静态知识库 `KnowledgeBase`（[src/AIPlayer/KnowledgeBase.cs](../src/AIPlayer/KnowledgeBase.cs)），包含 70+ 条目：

| 类别 | 条目数 | 用途 |
|------|--------|------|
| 地图知识 | 15 张地图 | 等级区间、标签、是否为事件地图 |
| 加点方案 | 每职业 3 阶段 | 加点比例配置 |
| 物品知识 | 10+ 物品类 | 拾取门控（宝石、装备、卷轴） |
| 职业知识 | 5 职业 | 基础属性、推荐 Build |
| 事件知识 | 5+ 事件 | 日程表、等级要求 |
| 合成知识 | 5+ 配方 | 材料需求 |
| 生存知识 | 10+ 条目 | HP/MP 管理策略 |
| 战斗知识 | 10+ 条目 | 技能选择、目标优先 |
| 经济知识 | 10+ 条目 | 物价、交易策略 |
| 强化知识 | 5+ 条目 | 装备强化策略 |

通过 `KnowledgeAccessService` 实现重生感知的门控访问：
- `EffectiveLevel = max(currentLevel, MaxLevelAchieved)` — 知识不会因重生丢失
- 等级门控 + 职业限制 + 重生次数要求

### 经验记忆系统（运行期知识积累）

三条知识来源：

```
角色记忆 (CharacterMemory JSON) ──→ 个人经验（MapStats）
    ↓ merge on startup                ↓ blend    
账户知识 (AccountKnowledge JSON) ──→ 跨角色共享统计 ←── 知识门控狩猎推荐
    ↓                                ↑
经验记忆 (ExperienceMemory) ──────→ 实时环缓冲区 → 60s 合并 → MapStats
```

### 知识图谱（KG）— 未来方向

当前知识为独立事实（KB），尚无关系网络（KG）。下一步演进：

```python
# 知识图谱推理示例（未来）
graph.query("合成翅膀最优路径")
# → 祝福宝石×2 → 灵魂宝石×2 → 蜘蛛掉落(地下城)
# → 创造宝石×1 → 卡隆合成(仙踪林)
# → 等级380 → 失乐之塔挂机
```

知识图谱的具体应用场景：
- 地图连通关系 → 跨地图导航最优路径
- 怪物掉落链 → "刷什么怪最快获得目标物品"
- 合成依赖链 → "从现有材料到目标装备的最短路径"
- 任务依赖图 → "完成任务A需要先完成哪些前置任务"

---

## 子代理分工（SDD 流程中的角色）

| 子代理 | 在 SDD 流程中的角色 | 对应 OpenMU 场景 |
|--------|-------------------|-----------------|
| **Explore** | WIKI 阅读 + 源码探索 | 搜索 `FindPath` 调用链、`GameMapTerrain` 用法、协议包定义 |
| **Plan** | 写规范设计文档 | 设计 AI NPC 架构、寻径策略、测试方案 |
| **CodeReview** | Step 3 审查 | 审查 pathfinding、WorldMap、AiPlayer 代码变更 |
| **Debug** | Step 3+4 问题定位 | 分析测试失败（如寻径算法返回 null、地形编码不一致） |
| **General** | Step 2 代码生成 | 实现 WorldMapBuilder、TerrainToAIGridConverter、测试类 |

---

## 知识来源扩充（2026-05-25 从 Kimi Agent 合并文档整合）

以下从外部设计文档（`Kimi_Agent_合并QUEST-COGNITION`）提取的知识领域已整合到 WIKI/：

| 新知识领域 | WIKI 文件 | 来源文档 |
|-----------|----------|---------|
| AI 人格模型与行为矩阵 | `WIKI/22-ai-personality-and-growth.md` | AI-PLAYER-DESIGN-v2.0 |
| 空间认知与数字地图 | `WIKI/23-spatial-cognition-and-digital-map.md` | AI-COGNITION-v2.2 |
| 任务认知与执行系统 | `WIKI/24-quest-cognition-system.md` | QUEST-COGNITION-v2.3 |
| 知识库架构与 AI 协作 | `WIKI/25-ai-knowledge-architecture.md` | AI-COGNITION + MASTER-GUIDE |
| 可靠性模式与 Agent 体系 | `WIKI/26-reliability-patterns.md` | MASTER-GUIDE + ARCHITECTURE-COMPARISON |

这些文档在后续开发中作为设计参考，不替代当前 WIKI 的规范约束力。

## 当前阶段确认（2026-06-22 更新）

### P0-P2 全部完成 ✅
1. ✅ **P0 — WIKI 可执行化（Spec Kit）** — SpecVerificationTests.cs（4 门禁）
2. ✅ **P1 — 真实服务器集成验证** — 服务器已启动，3 AI 角色运行
3. ✅ **P2 — 性能优化/行为脚本** — 5 个 BehaviorScript JSON + PetHandler + 拾取覆盖
4. ✅ **P3 — Orchestrator 库文档化** — README 674 行 + XML doc + 使用示例
5. ✅ **P4 — NuGet 发布** — 待 MCP Shell 激活后操作

### 基础设施修复
| 修复 | 文件 | 说明 |
|------|------|------|
| IP 解析器崩溃 | `ConfigurableIpResolver.cs` | Custom+null → Loopback 降级 |
| IP 配置覆盖 | `Program.cs:1143-1149` | 移除 _systemConfiguration 参数 |
| TypeScript 编译 | `Web.Map.csproj` | 移除微软 TypeScript.MSBuild |
| PetBehaviour 反射 | `PetHandlerModule.cs` | 内部枚举通过反射调用 |

## Session 11c (2026-06-29~30) 新增

### 服务器修复
| # | 修复 | 文件 | 说明 |
|---|------|------|------|
| 63 | OAPS 自动初始化 | `AiPlayerManager.cs` | PBO 定时器 + EnsureOapsInitialized + Context 触发 |
| 64 | 账号创建 API | `ServerController.cs` | BCrypt 密码哈希，完整角色数据（含技能） |
| 65 | 掉落修复 | `GameConfigurationInitializerBase.cs` | 宝石/卓越掉落组填充 |
| 66 | 地形修复 | `GameMapTerrain.cs`/`Player.cs`/`TerrainUpdateHelper.cs` | 全可走 + `GetRandomCoordinate`增强 + `PlaceAtGate`位运算 |

### 客户端修改（源码级，均未生效于可运行版本）
| # | 修改 | 文件 | 说明 |
|---|------|------|------|
| 67 | N 键小地图 | `NewUIHotKey.cpp` | N 键切换 `INTERFACE_MINI_MAP` |
| 68 | 商店连击购买 | `NewUINPCShop.cpp` | 药水多发，装备单发 |
| 69 | 幻乐园闪烁 | `MapManager.cpp` | AlphaTile→普通纹理 |

### ❌ 未解决问题
- **客户端编译崩溃** — 所有新编译的 Debug/Release 在角色选择后崩溃（`0xC0000005`）
- 崩溃点在 `rInput.IsKeyDown(VK_RETURN)`，`this` 指针被破坏
- 可能原因：ABI 不兼容 / 堆损坏 / CRT 不匹配
- 旧版 Release Main.exe 已被覆盖，不可恢复

### 服务器运行命令
```bash
cd /d E:\mu_ai\MU_VER_1\SERVERS\OPENMU
dotnet run --project src/Startup/MUnique.OpenMU.Startup.csproj -p:ci=true -- --autostart -demo -resolveIP:local
```

### PostgreSQL
便携版已安装: `E:\pgsql\pgsql\bin\`
```bash
E:\pgsql\pgsql\bin\pg_ctl.exe -D E:\pgsql\data start
```
