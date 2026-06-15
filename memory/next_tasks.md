# next_tasks.md — 下一阶段任务列表

> 最后更新: 2026-06-15
> 状态: 生效

---

## 项目状态概要

AI角色多任务优先级循环体系已基本建设完成。决策引擎是 6 层架构（Active → Unblock → Failed → GenerateMissions → Pending → Fallback），配合 EventWatcherService 主动广播、EventInterruptService 抢占决策、VaultService 仓库存取、CraftingModule/EventExecutorModule 执行模块。

当前已验证：6层决策循环、事件状态扫描（NotStarted正确跳过/不注入）、仓库检查、死循环兜底、DropsInRange管线、ScavengeForCrafting合成扫描。

**已完成的全天运行保障**:
- NPC对话超时自动关闭(40tick)
- 断线重连检测(3次确认+30s冷却)
- WarpPlanner接入决策层(ExecuteCrossMapWarp多跳路由)
- CraftingModule泛化(多配方NPC+跨地图NoTarget联动)
- CraftingModule决策联动(NoTarget→传送→继续任务)
- 主动嗑药HP/MP(TryConsumePotionsAsync, 2s冷却防浪费)
- 复活补给(PostRespawnSupplyAsync: 修装备+买10瓶药)
- 商店买药(BuyPotionsAsync/NpcInteractionService)
- 装备修理(RepairAllEquipmentAsync)
- 任务道具合成链路修复(统一ChaosGoblin, 按类型选mixType)
- 服务器端到端验证(EventWatcher/EventScanner/自动复活)
- 死亡状态卡死修复(IsAlive+PlayerState.Dead检测绕过)
- Web UI Decision Log可见决策记录(RecordDecision三条路径全接入)
- 行走tick不覆盖空白snapshot(保持上一tick决策日志)

## 本会话验证结果 (2026-06-16)

**服务器实测证实:**
1. ✅ **死亡修复生效** — DecisionTest AI从永久死亡恢复(Tick 1820+, HP 980/980)
2. ✅ **活动中断链完整** — EventWatcher检测到Chaos Castle 3开放 → EventOpenEvent → OnEventOpen → ShouldInterruptForEvent → Inject farm_ticket任务
3. ✅ **Web UI Decision Log可见** — quest_executor/heartbeat等决策记录正确显示
4. ✅ **3个ScriptExec AI正常运行** — 狩猎/嗑药/巡逻/交任务全链路
5. ✅ **自动嗑药再次确认** — 6次独立嗑药记录(Small+Medium Healing Potion)
6. ⚠️ **合成端到端未完整测试** — 注入farm_ticket任务成功，但AI缺少合成材料和金币(Chaos Castle入场费15万)

## 下一阶段任务 (按优先级排序)

### ⚠️ 当前: P0/P1/P2/P3 全部完成 — 核心链路已验证

### P0: 活动入场全链路端到端验证

**目标**: 将活动开放时间表改为立即触发（或调整系统时钟），确认 EventOpenEvent → 中断 → 门票检查 → 入场成功 全链路。

**检查项**:
- [ ] EventWatcher 检测到 NotStarted → Started 变化，发布 EventOpenEvent
- [ ] OnEventOpen 收到事件，ShouldInterruptForEvent 决定中断当前任务
- [ ] 当前任务标记为 Suspended（非 Failed）
- [ ] GetEventReadiness 返回 Ready 或 NeedVault/NeedTicket
- [ ] 条件满足时激活事件任务 → EventExecutorModule.ExecuteStepAsync → EnterMiniGameAction
- [ ] 入场成功（CurrentMiniGame != null），返回 Completed

**调试日志预期**:
```
[EventWatcher] 🔔 入场窗口打开: Devil Square Lv.2
[EventInterrupt] ✅ 决定中断当前任务 ...
[EventExec] 调用 EnterMiniGameAction 进入 ...
[EventExec] 成功进入 DevilSquare Lv.2
```

---

### P1: ScavengeForCrafting 接入 DynamicMissionGenerator

**目标**: `ItemNeedAnalyzer.ScavengeForCrafting()` 已实现翅膀/混沌武器/装备升级检测，需要接入 `DynamicMissionGenerator.GenerateMissions()` 使其周期性注入合成任务。

**文件**:
- `Decision/DynamicMissionGenerator.cs` — 在 Layer 3 的事件扫描之后调用 `_itemAnalyzer.ScavengeForCrafting()`
- `Decision/ItemNeedAnalyzer.cs` — 确保 ScavengeForCrafting 已完整实现

**约束**:
- ScavengeForCrafting 每 10 秒扫一次（复用 ItemScanInterval）
- 合成任务的 Priority 按价值排序：翅膀(18)→门票(21)→混沌武器(25)→装备升级(27)

---

### P1: ItemPickupManager DropsInRange 数据管线

**目标**: `BehaviorContext.WorldState.DropsInRange` 当前未被填充，`ItemPickupManager` 无法实际看到地面掉落物。需要从 GameMap 中获取掉落数据。

**方案**: 在 `AiPlayerLogic.TickCoreAsync()` 中构建 `WorldState` 时，添加 DropsInRange 的填充：
```csharp
DropsInRange = map.GetDropsInRange(pos, pickupRange)?.ToList() ?? new(),
```

**文件**:
- `AiPlayerLogic.cs` — WorldState 构建处
- `ItemPickupManager.cs` — 删除 ScanMapForDropsAsync 的 fallback

---

### P1: 删除 EventWatcher DEBUG 日志

**目标**: 第99行的 `[EventWatcher] DEBUG` 日志是调试用的，每 10 秒刷一次，运行稳定后应删除。

**文件**:
- `Decision/EventWatcherService.cs` 第100行

---

### P2: GoalScheduler 数据持久化

**目标**: GoalScheduler 的目标进度当前是内存状态，重启后丢失。需要序列化到文件/数据库。

**方案**: 在 `GoalScheduler` 中实现 `Save()`/`Load()` 方法，使用 JSON 序列化保存到 `aiplayer_data/` 目录。

---

### P2: 世界频道市场采集长期稳定运行

**目标**: `TransactionMonitor` 的正则解析当前只覆盖"出/收"基本模式。需要更全面的模式覆盖。

**扩展**:
- 支持更多货币单位（RMB、元、f7、+0-1）
- 支持"打包价"、"半价处理"等促销模式
- 支持附属性描述（+7、幸运、卓越等）
- 缺失的物品名→Key 映射匹配（目前 20+ 条目，需要扩充到 100+）

---

### P3: AI 群体智能 (Swarm/GM)

**目标**: 多个 AI 角色之间的协调（组队、交易、情报共享）。

- 组队打BOSS、副本
- AI-player 间交易（用 MarketPriceService 定价）
- 共享地图热点情报（影子地图层已支持 GuildOnly/RoleBased）

---

## 架构设计规则引用

| 规则 | 内容 | 影响 |
|------|------|------|
| AR-24 | 看板 = 中央状态机 | 所有外部输入分类更新看板，决策系统读取 |
| AR-25 | 决策 = 多维度加权 | 当前简化版 SelectNext，后续迭代为加权评分引擎 |
| AR-26 | 三层脚本 | 看板任务(MissionItem) → 行为脚本(BehaviorScript) → 原子调用(游戏API) |
| AR-23 | 每 tick 只看板选一个 MissionItem | 禁止段落自执行 |
| AR-20 | L3 直调游戏 API | 像 NPC AI 一样直接调，不造包装层 |

## AIPlayer 决策子系统文件清单

| 子系统 | 核心文件 | 状态 |
|--------|---------|------|
| 6层决策引擎 | `Decision/HeartbeatService.cs` | ✅ |
| 看板 | `Decision/BoardState.cs`, `Decision/MissionItem.cs`, `Decision/MissionBoardService.cs` | ✅ |
| 目标任务 | `GoalScheduler.cs` (根目录 Decision/) | ✅ |
| 事件广播 | `Decision/EventWatcherService.cs` | ✅ (待删除DEBUG) |
| 事件抢占 | `Decision/EventInterruptService.cs` | ✅ |
| 仓库 | `Decision/VaultService.cs`, `Decision/VaultModule.cs` | ✅ |
| 执行模块 | `QuestExecutor`, `SurvivalMode`, `ItemFarmModule` | ✅ |
| 合成 | `Decision/CraftingModule.cs` | ✅ |
| 副本入场 | `Decision/EventExecutorModule.cs` | ✅ |
| 背包管理 | `Decision/InventoryManagerService.cs` | ✅ |
| 拾取 | `Decision/ItemPickupManager.cs` | ⚠️ (DropsInRange空) |
| 价值评估 | `Decision/ValueAssessmentService.cs` | ✅ |
| 市场校准 | `Decision/MarketPriceService.cs`, `Decision/TransactionMonitor.cs` | ✅ |
| 道具分析 | `Decision/ItemNeedAnalyzer.cs`, `Decision/DynamicMissionGenerator.cs` | ✅ (待接Scavenge) |
