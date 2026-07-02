# AI 角色游戏知识规则化分析报告

> 日期: 2026-06-18
> 目的: 梳理哪些游戏内容需要做成"条件→脚本"规则，填入规则表
> 关联文档: [游戏公告/事件体系深度分析](02_KNOWLEDGE/game_announcement_system.md)

---

## 一、现状：已规则化的内容（8项）

以下已有规则定义在 `RuleEngine.LoadDefaultRules()` 和 `rules_default.json` 中：

| 规则ID | 条件 | 脚本 | 状态 |
|--------|------|------|------|
| `survival_hp` | 血量 < 60% | survival | ✅ 生效 |
| `inventory_cleanup` | 空格 < 4 | survival→回城清理 | ✅ 生效 |
| `auto_equip` | 背包有可装备物品 | survival→换上 | ✅ 生效 |
| `learn_skill` | 背包有技能书(G15) | survival→学习 | ✅ 生效 |
| `allocate_stat` | 有未分配属性点 | survival→加点 | ✅ 生效 |
| `craft_wing2` | 有洛克之羽+混沌 | crafting_executor | ✅ 生效 |
| `craft_wing3` | 有神鹰羽毛+火种+混沌 | crafting_executor | ✅ 生效 |
| `craft_ticket` | 有恶魔广场通行证材料 | crafting_executor | ✅ 生效 |
| `craft_chaos_weapon` | 有混沌+装备 | crafting_executor | ✅ 生效 |
| `craft_equip_upgrade` | 有祝福/灵魂/混沌 | crafting_executor | ✅ 生效 |
| `quest_auto` | 一直执行 | quest_executor | ✅ 生效 |
| `survival_farm` | 兜底 | survival→刷怪 | ✅ 生效 |

---

## 二、已有知识库但未规则化的内容（需补规则）

### 2.1 金币拾取

**已有能力:**
- `WorldState.DropsInRange` 中 **包含 `DroppedMoney`**（`AiPlayerLogic.cs:344` 调用 `map.GetDropsInRange`）
- 但 `ItemPickupManager` 只处理物品（`DroppedItem`），**不处理金币**（`DroppedMoney`）

**需要加的规则:**
```json
{
  "ruleId": "pickup_gold",
  "priority": 8,
  "category": "inventory",
  "condition": "gold_on_ground",
  "scriptId": "survival",   // 或新脚本 pickup_executor
  "minLevel": 0, "maxLevel": 0,
  "parameters": { "minGold": "100" },
  "description": "扫地板金币"
}
```

**现有条件** `gold_on_ground` 不存在，需在 `RuleEngine.MeetsCondition` 中新增。

**涉及文件:** `RuleEngine.cs`（加条件）、`ItemPickupManager.cs`（加金币拾取逻辑）

---

### 2.2 黄金怪 (Golden Monster) 狩猎

**游戏机制:**
- 系统会通过**公告消息**广播黄金怪刷新："黄金怪物出现在 xxx"
- 黄金怪是精英怪，属性比普通怪高，但掉落更好
- AI 需要：收到公告 → 判断怪物等级是否匹配 → 决定是否去

**现有能力:**
- ✅ `GameAdapter` 可以收聊天消息（`AiViewPlugInContainer.ChatBuffer`）
- ✅ `HeartbeatService` 已接入 `DrainChatMessages` → `TransactionMonitor.ProcessChatMessage`
- ✅ `GameKnowledgeService` 可以查怪物等级/地图
- ❌ **无黄金怪公告解析规则**
- ❌ **无"去不去"决策规则**

**需要的规则:**
```json
{
  "ruleId": "hunt_golden_monster",
  "priority": 5,
  "category": "hunt",
  "condition": "golden_monster_alert",
  "scriptId": "golden_monster_hunter",
  "minLevel": 0, "maxLevel": 0,
  "parameters": { "levelDiff": "15" },
  "description": "黄金怪刷新→如等级匹配→去狩猎"
}
```

**新组件需求:**
- `ChatMessageRuleEvaluator` — 解析系统公告，匹配黄金怪/BOSS 刷新消息模式
- `黄金怪狩猎脚本` — 新 `IBehaviorSubModule` 或扩展 `ItemFarmModule`

---

### 2.3 BOSS 狩猎

**游戏机制:**
- 各大地图有 BOSS（如：地下城魔王巴洛克、冰风谷寒冰魔等）
- BOSS 有固定刷新周期（重生时间从几十分钟到几小时不等）
- BOSS 掉率高，掉落极品装备
- 系统公告 BOSS 刷新："魔王巴洛克已出现在地下城"

**现有能力:**
- ✅ `GameKnowledgeService.Monsters` — 含所有怪物定义（包括 BOSS）
- ✅ `GameKnowledgeService.FindMonstersByLevel(min, max)` — 按等级筛选
- ✅ `ScriptExecutor` 已有 `travel_to_map` / `warp_to_map` / `find_nearest_monster`
- ❌ **无 BOSS 定义标记**（MonsterInfo 没有 IsBoss 字段）
- ❌ **无 BOSS 刷新公告解析**
- ❌ **无 BOSS 刷新计时器**

**需要的规则:**
```json
{
  "ruleId": "hunt_boss",
  "priority": 4,
  "category": "hunt",
  "condition": "boss_alert",
  "scriptId": "survival",
  "minLevel": 30, "maxLevel": 0,
  "parameters": { "minBossLevel": "30", "maxLevelDiff": "20" },
  "description": "BOSS刷新→可行则去"
}
```

**新组件需求:**
- `MonsterInfo` 加 `IsBoss` 标记（从 `ObjectKind == "Boss"` 或名称匹配）
- `BossTrackerService` — 追踪 BOSS 刷新时间和位置
- BOSS 公告解析规则

---

### 2.4 材料需求→自动刷材料全链路

**现状:**
- ✅ 门票材料全链路已走通（MaterialFarmModule → CraftingModule）
- ✅ `MaterialKnowledgeService` 知道"什么怪掉什么"
- ✅ `GameKnowledgeService` 有完整的 `GetDropSources(itemGroup, itemNumber)`
- ❌ **ItemNeedAnalyzer.ScavengeForCrafting() 只检测翅膀/武器/升级，不会自动触发 farm**
- ❌ **缺少"我想做 X → 查我缺什么 → 去刷 → 合成"的规则**

**需要的规则（新增合成检测条件）：**

| 规则 | 条件 | 说明 |
|------|------|------|
| `auto_craft_wing_detect` | `has_wing_materials` | 背包+仓库扫描→有材料就去合成 |
| `auto_craft_anything` | `has_any_craftable` | 只要有可合成的高价值物品→去合 |
| `auto_farm_missing_mats` | `missing_craft_materials` | 想合成但缺材料→去刷 |
| `auto_check_vault` | `has_vault_materials` | 仓库有材料→去取 |

**新组件需求:**
- `MaterialRequirementService`（已在 material_requirement_analysis.md 设计，未实现）

---

### 2.5 看板公告(系统信息)解析

**游戏机制:**
- 系统公告格式："[公告] 活动【恶魔广场】将在 5 分钟后开始！"
- "黄金怪物刷新在勇者大陆..."
- "魔王巴洛克出现在地下城..."
- "血色城堡入场券已开放抽奖..."

**现有能力:**
- ✅ 聊天消息流已接入（`ChatMessageReceived` 事件）
- ✅ `TransactionMonitor` 已处理一部分（交易消息）
- ❌ **无系统公告分类路由**
- ❌ **无公告→规则匹配器**

**需要的新组件:**
- `SystemMessageRouter` — 接收所有聊天消息，分类：交易 / 活动公告 / BOSS公告 / 黄金怪公告 / 系统提示
- 每种类型触发不同规则

---

### 2.6 活动（MiniGame）参与决策增强

**现状:**
- ✅ `EventWatcherService` → `EventOpenEvent` → 中断当前任务 → 注入 event_ 任务
- ✅ `EventInterruptService.ShouldInterruptForEvent()` 已做基础判断
- ❌ **决策简陋：当前只判断等级范围，没有综合考虑：**
  - 当前 HP/MP 状态
  - 当前有无门票
  - 入场费够不够
  - 活动收益比（经验还是装备）
  - 距离活动地点远近
  - 当前任务重要性

**需要的规则增强:**
```json
{
  "ruleId": "decide_join_event",
  "priority": 3,
  "category": "event",
  "condition": "event_open_and_ready",
  "scriptId": "event_executor",
  "minLevel": 15, "maxLevel": 0,
  "parameters": { 
    "minHpPercent": "50", 
    "requiredMoney": "150000",
    "preferExpEvents": "true" 
  },
  "description": "活动开放→状态满足→参加"
}
```

---

### 2.7 跨地图移动策略

**现状:**
- ✅ `WarpPlanner` 能规划跨地图路由
- ✅ `ScriptExecutor` 有 `travel_to_map` / `warp_to_map` 动作
- ❌ **无"什么时候应该换地图"的规则**
- ❌ **无根据等级推荐狩猎地图的动态规则**

**需要的规则:**
```json
{
  "ruleId": "auto_relocate_map",
  "priority": 50,
  "category": "survival",
  "condition": "level_ge",
  "scriptId": "warp_to_hunt_map",
  "minLevel": 80, "maxLevel": 0,
  "parameters": { "level": "80", "targetMap": "69" },
  "description": "等级≥80→去失落之塔刷"
}
```

---

## 三、需要新增的支持组件

| 组件 | 作用 | 优先级 |
|------|------|--------|
| `MonsterInfo.IsBoss` 标记 | 在 Knowledge/Models/ 里加字段，加载时识别 BOSS | P1 |
| `ChatSystemMessageRouter` | 把聊天消息分类分发到不同规则 | P0 |
| `GoldenMonsterRuleEval` | 黄金怪公告解析+等级匹配+去不去决策 | P0 |
| `BossTrackerService` | 追踪 BOSS 刷新时间和位置 | P1 |
| `MaterialRequirementService` | 材料缺口分析（已设计未实现） | P1 |
| `GoldPickupExtension` | 金币拾取逻辑 | P1 |
| `MapLevelRecommender` | 根据等级推荐狩猎地图 | P2 |

---

## 四、规则表扩展计划（JSON）

### 立即可以加的规则（无需新组件）

```json
// 捡金币 - 需给 ItemPickupManager 加金币处理
{ "ruleId": "pickup_gold", "priority": 8, "category": "inventory", 
  "condition": "always", "scriptId": "item_pickup_manager",
  "minLevel": 0, "maxLevel": 0, "description": "扫地板金币" }

// 跨地图移动
{ "ruleId": "relocate_at_80", "priority": 50, "category": "survival",
  "condition": "level_ge", "scriptId": "survival",
  "minLevel": 80, "maxLevel": 0, 
  "parameters": { "level": "80" },
  "description": "等级≥80换高级地图" }

// 活动参与增强
{ "ruleId": "event_chaos_castle", "priority": 3, "category": "event",
  "condition": "always", "scriptId": "event_executor", 
  "minLevel": 30, "maxLevel": 0,
  "description": "混沌城堡开放→参加" }
```

### 需新组件后才可加的规则

```json
// 黄金怪 - 需 ChatSystemMessageRouter
{ "ruleId": "hunt_golden", "priority": 5, "category": "hunt",
  "condition": "golden_monster_alert", "scriptId": "new_golden_monster_module",
  "minLevel": 15, "maxLevel": 0,
  "parameters": { "maxLevelDiff": "15" },
  "description": "黄金怪刷新→可行则去" }

// BOSS - 需 BossTrackerService
{ "ruleId": "hunt_boss", "priority": 4, "category": "hunt",
  "condition": "boss_alert", "scriptId": "survival",
  "minLevel": 30, "maxLevel": 0,
  "parameters": { "maxLevelDiff": "20" },
  "description": "BOSS刷新→可行则去" }

// 材料自动刷 - 需 MaterialRequirementService
{ "ruleId": "auto_farm_materials", "priority": 35, "category": "craft",
  "condition": "missing_craft_materials", "scriptId": "material_farm",
  "minLevel": 15, "maxLevel": 0,
  "description": "合成缺材料→去刷" }
```

---

## 五、建议的优先级和实施顺序

| 顺序 | 内容 | 工作量 | 收益 |
|------|------|--------|------|
| 1 | **聊天消息路由** — 分类公告/BOSS/黄金怪/系统消息 → 触发对应事件 | 1天 | ⭐⭐⭐ 基础能力 |
| 2 | **金币拾取** — ItemPickupManager 加 DroppedMoney 处理 + 规则 | 0.5天 | ⭐⭐⭐ AI自给自足 |
| 3 | **黄金怪狩猎** — 规则 + 狩猎脚本 | 2天 | ⭐⭐⭐⭐ 收益高 |
| 4 | **MaterialRequirementService** — 材料缺口全链路 | 2-3天 | ⭐⭐⭐⭐⭐ 核心能力 |
| 5 | **BOSS 狩猎** — BOSS 标记 + 追踪 + 规则 | 2天 | ⭐⭐⭐⭐ 高收益 |
| 6 | **跨地图推荐** — 按等级自动换图 | 1天 | ⭐⭐⭐ 持续成长 |
| 7 | **活动参与深度决策** — 多因子判断 | 1天 | ⭐⭐ 已有基础 |

---

## 六、总结：已有 vs 需要

```
已有(规则表12条+代码)                   需要(建议+20条规则)
─────────────────────────────          ─────────────────────────────
✅ 低血喝药                             🔲 黄金怪狩猎
✅ 背包清理                             🔲 BOSS 刷新狩猎
✅ 自动换装/学技能/加点                  🔲 金币拾取
✅ 翅膀/武器/门票合成                    🔲 材料缺口自动刷
✅ 活动入场检测                          🔲 系统公告解析→路由
✅ 任务执行                             🔲 跨地图等级推荐
✅ 生存刷怪(兜底)                       🔲 活动参与深度决策
```
