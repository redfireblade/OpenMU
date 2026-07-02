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
- Entity Framework Core (PostgreSQL / InMemory)
- Blazor Server (AdminPanel)
- Dapr (分布式，可选)
- SignalR (实时地图)

### 代码规范（强制）
- 遵循 StyleCop 规则（项目已配置 .editorconfig）
- 所有公共 API 必须有 XML 文档注释
- 异步方法必须以 Async 结尾
- 禁止从 Deathway/zTeam/IGCN 等泄露源码复制逻辑（Clean Room 原则）

### 架构约束
- 游戏逻辑必须写在 PlugIns/ 目录，通过 MUnique.OpenMU.PlugIns 接口注册
- 网络协议包定义在 Network/Packets/，通过 XML + XSLT 生成
- 数据模型在 DataModel/，持久化在 Persistence/
- 禁止在 GameLogic/ 中直接操作数据库，必须通过 Repository 模式

### 禁止事项（安全红线）
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
