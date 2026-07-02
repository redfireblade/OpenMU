# 架构设计规则 (Architecture Design Rules)

> 版本: 1.4
> 状态: 生效
> 最后更新: 2026-06-18

## AR-01 至 AR-18 (略)

<!-- 此前规则在此省略，仅保存 AR-19 -->

---

## AR-19: 影子地图层 (Shadow Map Layer)

**状态: 生效** · 2026-06-02

### 规则

1. **影子地图层是 AI 侧独立层** — ShadowMapLayer 位于 `MUnique.OpenMU.AIPlayer.Map` 命名空间，不修改 `GameMapTerrain` 或 `GameMap`。
2. **每 AI 每地图一个实例** — 通过 `AiMap.GetOrCreateShadowLayer(Guid ownerId)` 工厂方法获取，键为 AI 的 `AiPlayerId`。
3. **值语义条目** — `ShadowEntry` 是 `record struct`，所有字段 `{ get; init; }`，天然不可变。写入通过 `ConcurrentDictionary.AddOrUpdate` 原子操作。
4. **线程安全** — 所有公共方法线程安全（ConcurrentDictionary + 只读返回拷贝）。
5. **时间衰减** — `MonsterDensity` 衰减率 0.5/秒，阈值 5 以下不抬高成本。`MonsterObservation` 10 秒过期，`PathTrail`/`PathOutcome` 120 秒过期。
6. **成本合并** — 在 `TryWalkToAsync` 中、A* 调用前，通过 `ShadowGridCostMerger.MergeGrid()` 将影子地图信息合并到 AIgrid 副本中。原始 AIgrid 不被修改。
7. **自动收集** — `FindNearestMonster` 每 tick 记录 `MonsterObservation`；成功行走后记录 `PathTrail`；段落切换时记录 `PathOutcome`。
8. **不回溯修改** — `_relocatingToHotspot` 已删除。所有路径优化通过成本层实现，不在执行层打补丁。
9. **性能要求** — `MergeGrid` 必须在 1ms 内完成（当前实测 < 0.5ms）。
10. **权限控制** — 条目可见性通过 `ShadowVisibility` 控制：`AIOnly` / `Public` / `GuildOnly` / `RoleBased`。默认 `AIOnly`。
11. **怪物危险过滤器** — TryWalkToAsync 中 shadow merge 后执行：视野内怪物等级 > AI 等级 + `MaxLevelDiff`(默认15) 则标记 3×3 区域为 127（不可通过）。A* 无解时回退到原始网格（force-through）。
12. **死亡策略升级 (Death Strategy)** — `_consecutiveDeaths` 追踪连续死亡（杀怪重置）。3 次后放弃 force-through 并标记当前热点为 DangerZone；5 次后标记全部热点为 DangerZone 并重置计数，强制 AI 换区域或换地图。

### 文件清单

| 文件 | 说明 |
|------|------|
| `src/AIPlayer/Map/ShadowEntryType.cs` | 条目类型枚举 (11 种) |
| `src/AIPlayer/Map/ShadowVisibility.cs` | 权限枚举 (4 级) |
| `src/AIPlayer/Map/ShadowEntry.cs` | 条目 record struct |
| `src/AIPlayer/Map/ShadowMapLayer.cs` | 影子地图层主类 |
| `src/AIPlayer/Map/ShadowGridCostMerger.cs` | 成本合并器 |
| `src/AIPlayer/Map/AiMap.cs` | 添加 ShadowLayers 集合 + 工厂方法 |
| `src/AIPlayer/Scripting/ScriptExecutor.cs` | TryWalkToAsync/FindNearestMonster/ProcessGoto 集成点 |

### 集成点

```
TryWalkToAsync:
  shadow ← AiMap.ShadowLayers[ownerId]
  mergedGrid ← ShadowGridCostMerger.Merge(aiGrid, shadow, pos, target)
  monsterDangerFilter: 3×3 block for monsters > AI level + maxLevelDiff
  if all algo FAIL on mergedGrid → force-through on originalGrid (Phase 2)
  if force-through causes 3+ deaths → mark target as DangerZone
  path ← AStar.FindPath(pos, target, searchGrid)
  shadow.RecordPathTrail(pos, target, path.Count)

FindNearestMonster:
  for each visible Monster:
    shadow.RecordMonsterObservation(m.Position, m.Definition.Number)

ProcessGoto (paragraph switch):
  shadow.RecordPathOutcome(currentPos, currentPos, success=false)
```

### 验证方法

1. 编译通过（`dotnet build src/AIPlayer/...`）
2. 启动 DebugBot4，观察 `[ShadowMerge]` / `[MonsterObservation]` / `[PathOutcome]` 日志
3. 观察 bot 路径是否绕过怪物密集区
4. 多次重启后路径效率是否提升
5. 观察 `[MonsterDanger]` 日志 — 高等级怪物 3×3 区域是否被标记
6. 观察 `[DeathStrategy]` / `[ForceThru]` 日志 — 连续死亡后是否轮换热点/放弃目标

### 关联文档

- `memory/shadow_map_layer.md` — 完整设计文档
- `memory/three_layer_architecture_integration.md` — L2→L3 路径规划

---

## AR-20: 程序计数器原则 (Program Counter)

**状态: 生效** · 2026-06-03

### 规则

1. **每个 ScriptExecutor 必须持有程序计数器 (PC)** — PC 是一个整数索引，指向当前段落节点数组中的唯一条目。不得在 PC 之外另设"条件评估循环"。
2. **取指-执行-推进管道 (Fetch-Execute-Advance)** — 每 tick 执行管道为：取 PC 指向的节点 → 评估条件一次 → 执行动作 → PC++。不允许每 tick 重新评估所有条件。
3. **段落循环** — 当 PC 到达段落末尾时，自动回绕到段落开头（PC=0）。回绕时刻重置"本周期已完成"的标记。
4. **硬中断优先** — 生存动作（死亡检测、HP 药水、MP 药水）在 PC 取指前检查，不受 PC 影响。这些是 CPU 级中断，不是脚本节点。
5. **任务隔离** — AI 在执行当前脚本期间，外部指令不直接穿透 PC 管道。外部命令进入指令缓存（FIFO, max 5），在每条 PC 指令间隙按序处理。
6. **可观测状态** — 外部系统只能看到 AI 的 `(脚本名, 段落标签, PC, 执行状态, 当前动作名)`。不得看到内部 PC 管道细节。
7. **看门狗** — 同一 PC 停留超过阈值（默认 50 tick ≈ 20秒）时，状态自动切换为 `Stuck`，触发救援流程。

### 违反后果

违反此规则（如继续使用每 tick 条件竞拍模式而非 PC 管道）将导致多 tick 动作（relocate_to_hotspot）无限重触发、战斗逻辑与策略逻辑互相干扰、外部观测混乱。

---

## AR-21: 富结果类型原则 (Rich Result)

**状态: 生效** · 2026-06-03

### 规则

1. **所有动作函数必须返回结构化结果类型** — 实现 `IActionResult` 接口的 `record` 或类。不得返回 `bool`、`void` 或 `string`。
2. **结果类型必须包含状态码枚举** — 枚举值枚举该动作的所有可能结果（Success / InProgress / Failed / Interrupted / AlreadyDone / NotFound / Blocked 等），保证上层能做确定性分支决策。
3. **结果类型必须包含上下文数据** — 如目标坐标、热点索引、玩家位置、耗时、原因说明等。仅状态码不足以支撑外部系统决策。
4. **执行引擎自身也返回富结果** — `TickAsync` 返回 `TickResult` 而非 `bool`。`TickResult` 包含 PC 位置、执行状态、本 tick 执行的动作名、动作结果引用。
5. **结果路由** — 执行引擎必须将富结果路由给三类消费者：(a) PC 逻辑做 advance/branch/retry；(b) 外部观察者做状态面板/AI决策层输入；(c) 看门狗做死机检测。
6. **结果可序列化** — 所有结果类型支持 JSON 序列化，用于日志、调试面板和 AI 决策层分析。

### 违反后果

违反此规则（如继续使用 `bool` / `void` 返回值）将导致"成功"与"被打断"与"已完成"等状态全部坍缩为同一种值，上层无法确定性分支，脚本引擎退化为离散化模糊判断。

---

## AR-22: 空闲熔断原则 (Anti-Idle Circuit Breaker)

**状态: 生效** · 2026-06-04

### 规则

1. **每个 ScriptExecutor 必须持有空闲计数器** — `_idleTickCount` 每 tick 无条件递增，有进展时 `ResetIdleTimer()` 归零。
2. **进展信号全覆盖** — 以下事件必须调用 ResetIdleTimer()：击杀怪物、找到新目标、行走到达、接任务成功、提交任务成功、复活。
3. **软硬双层阈值** — 空闲计数器超软限(idleSoftLimitTicks, 默认300)时强制复位 CurrentTarget 触发巡逻；超硬限(idleHardLimitTicks, 默认900)时进入冷却(idleRecoveryTicks, 默认75)，冷却期间不执行任何动作。
4. **冷却后自动恢复** — 硬熔断不是永久终止。冷却结束后计数器清零，下一 tick 正常执行。
5. **冷启动保护** — 硬熔断触发时同时清空 CurrentTarget、hotspot、noMonsterTickCount，防止冷却结束后因同一原因立即再次熔断。
6. **与 AR-20 看门狗正交工作** — 看门狗监测 PC 停留时间（毫秒级，单节点级）；熔断器监测全执行器进展（秒级，全局级）。两者互补不重复。

### 违反后果

违反此规则（缺少熔断保护）将导致 AI 在无目标/无任务/无怪物时继续全速空转，每秒 2-3 次空 tick。与其他内存泄漏因素（异常堆积、日志缓存、SQLite WAL）叠加后，9.5 小时内即可耗尽 128 GB RAM 触发 OOM 崩溃。

---

## AR-30: 死亡重生规则 (Death Respawn Rule)

**状态: 生效** · 2026-06-16 · 更新: 2026-06-16 (双路径协调 + 死亡循环保护)

### 规则

1. **死亡后必须在当前地图安全区的随机可通过位置重生** — 不能传送到其他地图，不能出生在不可行走格（wall/blocked tile）。使用 `WarpToSafezoneAsync` + `GetSpawnGateOfCurrentMapAsync` 获取当前地图安全区出口门，门的矩形区域 `(X1,Y1)-(X2,Y2)` 内的随机坐标即为重生点。
2. **安全区门过滤** — `GetSafezoneGate()` 的筛选逻辑是：`ExitGates.FirstOrDefault(g => g.IsSpawnGate && terrain.SafezoneMap[g.X1, g.Y1])`。即必须是 `IsSpawnGate=true` 且所覆盖的地形格被标记为安全区。
3. **满状态恢复** — 重生后必须将 HP/MP 恢复至满值：遍历 `Stats.IntervalRegenerationAttributes`，将每个 `CurrentAttribute` 设为 `MaximumAttribute` 的值。同时设置 `player.IsAlive = true`。
4. **地图确认** — AI 玩家没有真实客户端，重生后 `CurrentMap` 可能为 null，需要显式调用 `ClientReadyAfterMapChangeAsync()` 确认地图变换完成。
5. **执行器重置** — 重生后必须清空 `_scriptExecutor = null`，防止旧脚本上下文干扰新周期。
6. **死亡计时器** — `_deathStartTime` 在死亡瞬间记录 `DateTime.UtcNow`，5 秒后仍未复活 → 调用 `RespawnPlayerAsync()` 手动触发。
7. **卡死绕过** — 当 `hp > 0` 但 `IsAlive = false` 或 `PlayerState == Dead/Disconnected` 时（如通过 API set-hp 强行补血后），心跳循环应强制恢复 Alive 状态并推进到 EnteredWorld。

### AR-30a: 双复活路径协调规则

**状态: 生效** · 2026-06-17

**背景**: AIPlayer 系统存在两条独立的复活路径，必须协调工作：

| 路径 | 触发者 | 执行者 | 方式 |
|------|--------|--------|------|
| **ScriptExecutor wait_respawn** | 脚本节点 | ScriptExecutor.TickAsync | 5秒后 WarpToSafezoneAsync + 满血 |
| **HeartbeatService BeatAsync** | 心跳主循环 | HeartbeatService.RespawnPlayerAsync | 5秒后 WarpToSafezoneAsync + 满血 |

**规则**:
1. **ScriptExecutor 必须先于 HeartbeatService 执行复活** — 因为 ScriptExecutor 在 TickCoreAsync 内部被调用，先于 HeartbeatService.BeatAsync。
2. **两条路径必须发布事件** — ScriptExecutor 复活后必须通过 `PublishDeathEvent()/PublishRespawnEvent()` 通知 HeartbeatService，保证 `_respawnDeathStreak` 正确更新。
3. **不论哪条路径复活，结果相同** — 都是 `WarpToSafezoneAsync` → `ClientReadyAfterMapChangeAsync` → HP 满 → `IsAlive=true`。
4. **HeartbeatService 的死亡循环检测依赖事件** — `_respawnDeathStreak` 在 OnDeath 中递增、OnMonsterKilled 中归零、OnRespawn 中检查 ≥3 次触发换图。
5. **HeartbeatService 的 PostRespawnSupplyAsync 只在 HeartbeatService 复活路径运行** — ScriptExecutor 复活后不补给药水/修装备（脚本通常有独立的补给逻辑）。

### AR-30b: 死亡循环保护规则 (Death Loop Protection)

**状态: 生效** · 2026-06-17

**问题**: AI 在同一高危地图反复死亡 — 复活 → 回到同一热点 → 再次死亡。

**保护机制**:

| 层 | 阈值 | 动作 | 拥有者 |
|----|------|------|--------|
| L1 | 连续死 3 次 | 设置 TargetMapNumber=3 (Devias) | ScriptExecutor + HeartbeatService |
| L2 | 连续死 5 次 | 强制停止当前脚本 + 换图 Devias | ScriptExecutor |
| L3 | 快速死亡 ≥3 次 (30秒窗口) | 清空看板 + 换图 Devias | HeartbeatService OnRespawn |

**关键约束**:
1. **`_consecutiveDeaths` 不在击杀时重置** — 击杀不等于打破死亡循环。`_consecutiveDeaths` 只在 `ResetDeathState()`（hot-reload/脚本正常完成）时归零。
2. **`_respawnDeathStreak` 由 HeartbeatService 的 OnDeath/OnMonsterKilled 管理** — 成功击杀怪物自动归零（HP 归零 → 破环证明）。
3. **ScriptExecutor 必须发布死亡/复活事件** — 否则 HeartbeatService 看不到任何状态变化。
4. **换图后不保证存活** — 只是打破当前死亡循环。AI 决策系统需要自行评估新地图的生存能力（见 AR-31 热点选择规则）。
8. **不跨地图复活** — 禁止使用 `WarpToAsync(newGate)` 跨图传送。只使用当前地图的 `SafeZoneSpawnGate`。
9. **复活后补给** — 复活后执行 `PostRespawnSupplyAsync()`：找附近商店NPC → 修理全部装备 → 购买药水 → 关闭对话框。

### 违反后果

不遵守此规则（跨地图复活、出生在不可行走格、HP未恢复满）将导致 AI 在安全区也无法正常活动，反复死亡或卡在地形中。

---

## AR-32: 双客户路径数据源抽象 (Dual-Client Data Source Abstraction)

**状态: 生效** · 2026-06-18

### 背景

AIPlayer 系统面向两类客户：

| 客户类型 | 数据源 | 接入方式 | 信息完整度 |
|---------|--------|---------|-----------|
| **游戏厂商/运营商 (OEM)** | 服务器内部 API | hook `GameContext`、`PeriodicTaskBasePlugIn` 状态机 | 结构化数据全量（MapId、怪物列表、配置参数等） |
| **玩家/陪玩 (End-User)** | 客户端可见信息 | 屏幕识别/封包解析/日志分析 | 纯文本消息 + 客户端状态 |

**核心约束**: 决策和执行层必须统一，只有数据源感知层（绿色层）分叉。

### 规则

1. **红色层（数据源感知层）可互换** — AI 系统与游戏世界的接口必须抽象为 `IGameEventSource`，OEM 模式下由服务器内部事件驱动，玩家模式下由客户端信息驱动。**上层（决策、执行、经验）完全不知道下层数据来源。**

2. **黄色层（公告/事件识别层）随模式不同实现复杂度不同** — OEM 模式下直接收到结构化 `InvasionGameServerState`，玩家模式下需要从文本解析 → 匹配公告模式 → 经经验库辅助确认，才能得到相同的结构化事件。

3. **绿色层（决策+执行+经验）完全统一** — 不论 OEM 还是玩家模式，规则引擎、脚本执行器、经验积累系统、群体学习系统完全一致。

### 架构分层

```
OEM 模式                             玩家模式
─────────                            ────────
                                         屏幕识别/封包解析/日志
                                             ↓
IGameEventSource (OEM 实现)           IGameEventSource (玩家实现)
  ┌──────────────────┐                 ┌──────────────────────┐
  │ PeriodicTask     │                 │ 文本公告 → 公告模式库  │
  │ BasePlugIn hook  │                 │ → 公告日志 → 经验辅助  │
  │ → 结构化事件     │                 │ → 推测结构化事件       │
  └──────────────────┘                 └──────────────────────┘
           ↓ 结构化事件                          ↓ 结构化事件(推测)
┌──────────────────────────────────────────────────────────────┐
│                     AI 核心层 (统一)                          │
│                                                              │
│  Event → EventHandlers → BoardState → RuleEngine              │
│       → MissionItem → 执行 → ActionLog                        │
│       → 群体经验库 → 规则提炼                                  │
└──────────────────────────────────────────────────────────────┘
```

### 接口定义

```csharp
/// <summary>
/// 事件源抽象 ── OEM模式/玩家模式共用此接口。
/// 实现者负责将原始数据（结构化或文本）转换为标准结构化事件。
/// </summary>
public interface IGameEventSource
{
    /// <summary>入侵事件（黄金怪/红龙等户外定时刷怪事件）</summary>
    event Action<InvasionEvent>? OnInvasion;
    
    /// <summary>副本入口开放事件（血色/恶魔/死亡城堡）</summary>
    event Action<MiniGameEvent>? OnMiniGameOpen;
    
    /// <summary>增益事件（欢乐时光等全局加成）</summary>
    event Action<BuffEvent>? OnBuffActive;
    
    /// <summary>BOSS 刷新事件（由公告或扫描触发）</summary>
    event Action<BossSpawnEvent>? OnBossSpawn;
    
    /// <summary>系统公告（无法解析为以上类型时 fallback）</summary>
    event Action<SystemMessageEvent>? OnSystemMessage;
}
```

### OEM 实现要点

- 直接 hook `PeriodicTaskBasePlugIn` 基类加 `StateChanged` 事件
- `InvasionEvent` 直接从 `InvasionGameServerState` + `InvasionMobSpawn[]` 提取
- `MiniGameEvent` 直接从 `MiniGameDefinition` 配置提取
- 所有字段 100% 精确（怪物编号、地图编号、等级范围、时长）

### 玩家模式实现要点

- 从 `IShowMessagePlugIn.ShowMessageAsync()` 流入的文本或屏幕 OCR 文字出发
- 公告模式库收集常见文本模板：`"[{mapName}] Golden Invasion!"`、`"Blood Castle entrance..."` 
- 第一次遇到新公告 → 记录到公告日志 → 无法结构化时由经验系统逐步总结
- 公告模式的识别准确度随样本量增加（见 `02_KNOWLEDGE/ai_experience_system.md`）
- 公告日志累积足够后，出 `RuleCandidate` 补到公告模式库

### 经验系统的双路径价值

| | OEM 模式 | 玩家模式 |
|---|---------|---------|
| 公告解析 | 不需要经验，直接读结构化数据 | **需要经验系统**来识别文本公告的模式 |
| 掉落知识 | 读 DropItemGroups 即知 | 击杀后才知，靠日志积累 |
| 地图知识 | 读 MapDefinition 即知 | 走过才知，靠影子地图层 |
| 怪物属性 | 读 MonsterDefinition 即知 | 攻击后采集，靠观察记录 |
| 规则生成 | 基于完整数据分析 | 基于概率统计，置信度较低 |

### 文件清单

| 文件 | 说明 |
|------|------|
| `src/AIPlayer/EventSource/IGameEventSource.cs` | 事件源接口定义 |
| `src/AIPlayer/EventSource/OemEventSource.cs` | OEM 模式实现（hook 服务器 API） |
| `src/AIPlayer/EventSource/ClientEventSource.cs` | 玩家模式实现（文本解析+经验辅助） |
| `src/AIPlayer/EventSource/Models/InvasionEvent.cs` | 标准化事件模型 |
| `src/AIPlayer/EventSource/Models/MiniGameEvent.cs` | (同上) |
| `src/AIPlayer/EventSource/Models/BuffEvent.cs` | (同上) |
| `src/AIPlayer/EventSource/Models/BossSpawnEvent.cs` | (同上) |

### 关联文档

- `memory/02_KNOWLEDGE/game_announcement_system.md` — 游戏公告体系分析
- `memory/02_KNOWLEDGE/ai_experience_system.md` — 经验积累与群体学习
- `memory/knowledge_to_rules_analysis.md` — 规则化需求总览
