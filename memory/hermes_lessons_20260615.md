---
name: hermes-lessons-20260615
description: 本会话调试经验 — EventWatcher不可见/NotStarted跳过/决策层异步化/跨地图WarpPlanner/CraftingModule泛化/死亡双路径冲突
metadata:
  type: feedback
---

# Hermes 经验教训 — 2026-06-15/17

## Lesson 1: EventWatcher 删除 DEBUG 日志后不可见 ≠ 不工作

## Lesson 1: EventWatcher 删除 DEBUG 日志后不可见 ≠ 不工作

**现象**: 删除第101行 `LogInformation("[EventWatcher] DEBUG: ...")` 后，EventWatcher 完全不可见，但实际仍在运行。

**根因**: 唯一的状态日志就是那行 DEBUG，删除后 NotStarted 状态下没有任何日志输出。

**教训**: 删除 DEBUG 日志前确认有替代的监控手段。EventWatcher 只在状态变化时（LogDebug级别）输出，LogLevel 低于 Info 时在终端不可见。建议保留一个节流的健康日志（每N次扫一次）。

## Lesson 2: 两个地方同时注入事件任务

**现象**: 修复了 `SystemEventScanner` 跳过 NotStarted 任务，但 EventExec 仍然在 NotStarted 时触发。

**根因**: 活动任务从**两个源头**注入:
1. `SystemEventScanner.Scan()` — 已修复 → 跳过 NotStarted
2. `MissionBoardService.InitializeAsync()` Phase 2 — 启动时无条件加载所有 MiniGame

**教训**: 搜索所有 "event_" 前缀的注入点。一个修复不够时，检查是否存在多条注入途径。

## Lesson 3: 同步方法改为异步时连锁影响

**现象**: `Scan()` → `ScanAsync()` 导致调用链 `SelectCurrentTask` → `GenerateMissions` 都需要改为 async。

**根因**: 异步传播效应 — 同步方法调用 async 需要 await，而 await 需要调用者也成为 async。

**教训**: 一开始就应该设计为 `ValueTask` 而非同步 `List<>`。架构层输入输出点（决策循环的 Layer 3）天然适合异步。

## Lesson 4: 断线重连引用 AiPlayerManager 的陷阱

**现象**: `AiPlayerLogic.TryReconnectAsync` 尝试访问 `AiPlayerManager` 但无法获取引用。

**根因**: AiPlayerLogic 不持有 AiPlayerManager 引用，且 `IAiService` 通过插件的 `PlugInManager.GetStrategy` 获取。

**教训**: 在 AiPlayerLogic 内无法可靠重建 AI 角色。当前策略是保存进度并停止当前循环，等待上层（GameServerContainer/AiPlayerManager）检测到 AI 停止后重新创建。这是合理的降级方案。

## Lesson 5: WarpPlanner 接入决策层

**关键设计**: WarpPlanner 已存在于 ScriptExecutor 中（懒初始化），但未暴露给 HeartbeatService。

**方案**: 在 HeartbeatService 中创建 `WarpPlanner` 实例，并实现 `ExecuteCrossMapWarpAsync` 方法。采用 `BehaviorContext.ActiveWarpRoute/WarpInProgress/TargetMapNumber` 状态共享机制。

**关键点**: `BehaviorContext` 是跨 ScriptExecutor / HeartbeatService 共享的状态容器，Warp 状态放置在其中而非各自维护。

## Lesson 6: CraftingModule 泛化 — 状态关联

**原设计**: CraftingModule 写死 ChaosGoblin (NPC #238) + 混沌宝石 (12,15)。

**泛化方案**:
- 从 MissionItem.Id 按约定解析目标 NPC: `craft_ticket_DevilSquare_2` → NPC #237 (Charon)
- `craft_chaos_weapon` → NPC #238 (Chaos Goblin)
- 跨地图时返回 `StepResult.NoTarget` + 设置 `TargetMapNumber`

**关键约束**: `CraftingModule` 无法直接访问 `BehaviorContext`（它只有 IGameAdapter 引用）。需要通过 `_player.Logic.TargetMapNumber` 间接设置。

## Lesson 7: 双复活路径冲突 — ScriptExecutor 与 HeartbeatService 独立工作

**现象**: 所有死亡循环保护在服务器上无效果 - 4个AI全部永久死亡，`[DeathLoop]` 和 `[DeathStrategy]` 日志从未出现。

**根因**: 两条复活路径互不知晓：

1. **ScriptExecutor wait_respawn** — 在 ScriptExecutor.TickAsync 中执行，5秒后 `WarpToSafezoneAsync` + 恢复 HP，但不发布事件
2. **HeartbeatService BeatAsync** — 在心跳循环中执行，5秒后 `RespawnPlayerAsync`，但ScriptExecutor先复活了，HeartbeatService收不到`hp<=0`

ScriptExecutor 复活后不发布 `DeathEvent`/`RespawnEvent` → HeartbeatService 的 `_respawnDeathStreak` 始终为 0 → 死亡循环检测不触发。

**修复**: ScriptExecutor 复活后调用 `PublishDeathEvent()` / `PublishRespawnEvent()` 通知 HeartbeatService。

## Lesson 8: _consecutiveDeaths 被杀怪重置 → 死亡计数器永远升不上去

**现象**: ScriptExecutor 有 `_consecutiveDeaths` 死亡计数，每次击杀怪物 line 440 将其归零。结果：死亡 1 次 → 复活 → 打几个怪 → 计数器归零 → 永远不会达到 3/5 次升级。

**教训**: 击杀 ≠ 打破死亡循环。击杀只证明 AI 能打怪，不证明 AI 不会 5 分钟后再死一次。死亡循环计数器应该只由死亡策略自身（换图/换热点）或 hot-reload 时归零。

**修复**: 删除 line 440 的 `_consecutiveDeaths = 0`。改为在 `ResetDeathState()` 中归零。

## Lesson 9: 活动触发器启动后活动会「自然结束」— 有时间窗口限制

**现象**: `AutoEventTrigger` 启动 30秒后触发 `DevilSquare.ForceStart()` + `ExecuteTaskAsync()`，活动立即变成 Started。但 1 分钟后活动自然结束（Prepared → Started → NotStarted），因为活动的 `EnterDuration` 和 `TaskDuration` 用的是配置中的真实值。

**教训**: 测试夹具只能验证「自动触发 + 事件检测」链路，无法维持活动长期开放。如果要测试完整入场链路（farm_ticket → 合成 → 入场），需要缩短活动的 `EnterDuration`（在测试配置中），或在活动窗口期内手动触发。

## Lesson 10: NeedFarm 分支跳过了事件任务注入

**现象**: HeartbeatService.OnEventOpen 中，`EventReadiness.NeedFarm` 分支只打日志就 break 了。`InjectFarmTicketMission` 方法存在但从未被调用。

**根因**: 2026-06-15 实现时忘接 NeedFarm → InjectFarmTicketMission 的调用。

**修复**: 在 NeedFarm case 中添加 `InjectFarmTicketMission(miniGameDef)`，然后激活事件任务（`Status = Active`）。

