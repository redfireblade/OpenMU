# 架构设计规则 (Architecture Design Rules)

> 版本: 1.3
> 状态: 生效
> 最后更新: 2026-06-02

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

**状态: 生效** · 2026-06-16

### 规则

1. **死亡后必须在当前地图安全区的随机可通过位置重生** — 不能传送到其他地图，不能出生在不可行走格（wall/blocked tile）。使用 `WarpToSafezoneAsync` + `GetSpawnGateOfCurrentMapAsync` 获取当前地图安全区出口门，门的矩形区域 `(X1,Y1)-(X2,Y2)` 内的随机坐标即为重生点。
2. **安全区门过滤** — `GetSafezoneGate()` 的筛选逻辑是：`ExitGates.FirstOrDefault(g => g.IsSpawnGate && terrain.SafezoneMap[g.X1, g.Y1])`。即必须是 `IsSpawnGate=true` 且所覆盖的地形格被标记为安全区。
3. **满状态恢复** — 重生后必须将 HP/MP 恢复至满值：遍历 `Stats.IntervalRegenerationAttributes`，将每个 `CurrentAttribute` 设为 `MaximumAttribute` 的值。同时设置 `player.IsAlive = true`。
4. **地图确认** — AI 玩家没有真实客户端，重生后 `CurrentMap` 可能为 null，需要显式调用 `ClientReadyAfterMapChangeAsync()` 确认地图变换完成。
5. **执行器重置** — 重生后必须清空 `_scriptExecutor = null`，防止旧脚本上下文干扰新周期。
6. **死亡计时器** — `_deathStartTime` 在死亡瞬间记录 `DateTime.UtcNow`，5 秒后仍未复活 → 调用 `RespawnPlayerAsync()` 手动触发。
7. **卡死绕过** — 当 `hp > 0` 但 `IsAlive = false` 或 `PlayerState == Dead/Disconnected` 时（如通过 API set-hp 强行补血后），心跳循环应强制恢复 Alive 状态并推进到 EnteredWorld。
8. **不跨地图复活** — 禁止使用 `WarpToAsync(newGate)` 跨图传送。只使用当前地图的 `SafeZoneSpawnGate`。
9. **复活后补给** — 复活后执行 `PostRespawnSupplyAsync()`：找附近商店NPC → 修理全部装备 → 购买药水 → 关闭对话框。

### 违反后果

不遵守此规则（跨地图复活、出生在不可行走格、HP未恢复满）将导致 AI 在安全区也无法正常活动，反复死亡或卡在地形中。

---
