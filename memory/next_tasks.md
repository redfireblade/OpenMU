## 下一阶段任务（按优先级排序）

### P1: ItemPickupManager 拾取顺序优化 — 卓越装备优先

**问题**: 当前拾取逻辑按距离排序，可能捡垃圾而不先捡卓越。需在捡取决策前优先识别卓越/古代/技能书等有价值物品。

**文件**:
- `Decision/ItemPickupManager.cs` — `ExecuteStepAsync` 中选择逻辑
- `Decision/ValueAssessmentService.cs` — 可复用价值分级

**方案**: `moneyDrops`/`itemDrops` 排序增加价值因素：卓越/古代/带技能武器优先，其次按距离。

---

### P1: 活动入场全链路端到端验证

**目标**: 确认 EventOpenEvent → 中断 → 门票检查 → 合成打材料 → 入场成功 全链路闭环。

**检查项**:
- [ ] EventWatcher 检测到 NotStarted → Started 变化，发布 EventOpenEvent
- [ ] OnEventOpen 收到事件，ShouldInterruptForEvent 决定中断当前任务
- [ ] 当前任务标记为 Suspended（非 Failed）
- [ ] GetEventReadiness 返回 Ready 或 NeedVault/NeedTicket
- [ ] 条件满足时激活事件任务 → EventExecutorModule.ExecuteStepAsync → EnterMiniGameAction
- [ ] 入场成功（CurrentMiniGame != null），返回 Completed

---

### P1: 长时间运行稳定性 — 防 OOM 内存泄漏扫描

**目标**: 扫描已知的内存泄漏风险点（日志缓存、异常堆积、SQLite WAL）。

**检查项**:
- [ ] `EventBus._queue` 是否有上限（防事件堆积）
- [ ] 日志等级是否在生产模式可调为 `Information`（当前大量 Debug 日志）
- [ ] SQLite WAL 文件大小是否增长失控
- [ ] `ExperienceService` 的 ActionLog 列表是否有上限

**参考**: `memory/hermes_task_dailylimit.md` (AR-22 空闲熔断)

---

### P2: GoalScheduler 数据持久化

**目标**: GoalScheduler 的目标进度当前是内存状态，重启后丢失。需要 JSON 序列化到 `aiplayer_data/` 目录。

**文件**:
- `Decision/GoalScheduler.cs` — 实现 `Save()`/`Load()`

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
| 事件广播 | `Decision/EventWatcherService.cs` | ✅ |
| 自动换装 | `Decision/EquipmentCompareService.cs`, `Scripting/ScriptExecutor.cs` | ✅ 脚本模式+决策模式 |
| 属性成长 | `StatAllocationStrategy.cs` | ⚠️ 声明顺序修复 |
| 背包清理 | `Decision/InventoryManagerService.cs` | ✅ |
| 技能学习 | `Decision/SkillLearnService.cs` | ✅ |
| 经验系统 | `Decision/Experience/*` | ⚠️ 需长期验证 |
| 规则引擎 | `Decision/RuleEngine.cs` | ✅ |
| 影子地图 | `Map/Shadow*` | ✅ |
| BOSS检测 | `Core/NativeExecutionService.cs` | ✅ |
| 物品评分 | `Decision/ValueAssessmentService.cs` | ✅ |
| 合成模块 | `Decision/CraftingModule.cs` | ⚠️ 需端到端验证 |
| 事件执行 | `Decision/EventExecutorModule.cs` | ⚠️ 需端到端验证 |
