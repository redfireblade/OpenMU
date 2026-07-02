# 可靠性模式

## 概述

AI Player 系统实现了多种可靠性模式，确保在长时间运行、网络波动和资源压力下保持稳定。

## 熔断器（Circuit Breaker）

AI 系统在多个关键路径上实现了熔断机制：

| 熔断点 | 触发条件 | 恢复方式 | 文件 |
|--------|----------|----------|------|
| DAG 执行器 | 连续异常 | 降级到 Mode1D/Degraded | `AiPlayerLogic.cs` |
| 数据库连接 | 连接失败 | 自动重试循环 | `Persistence/` |
| 地图传送 | 传送失败 | 降级到寻路步行 | `Warp/` |
| NPC 交互 | NPC 不在视野 | 巡逻搜索最大超时 | `Decision/` |

### 执行模式降级

当 DAG 模式执行器发生异常时，AI 自动降级：

```
DAG（全代理模式）
  ↓ 异常
Degraded（降级模式）
  ↓ 脚本可用
Mode1D（脚本驱动模式，优先级链）
```

## 重试机制（Retry）

### 重连策略

`AiPlayerLogic`（`src/AIPlayer/AiPlayerLogic.cs`）实现了断线重连机制：

```csharp
private int _disconnectStreak;                              // 连续断线计数
private DateTime _lastReconnectAttempt = DateTime.MinValue;  // 上次重连时间
private static readonly TimeSpan ReconnectCooldown = TimeSpan.FromSeconds(30);  // 冷却间隔
private const int DisconnectConfirmThreshold = 3;            // 确认阈值（防误判）
```

| 参数 | 值 | 说明 |
|------|-----|------|
| `DisconnectConfirmThreshold` | 3 ticks (~1.2秒) | 连续检测到断开才判定为真实断线 |
| `ReconnectCooldown` | 30 秒 | 重连尝试冷却时间，防止频繁重连 |
| 重连上限 | 无硬限制 | 每次冷却结束后可再次尝试 |

### 数据库重试

持久化层使用 EF Core 默认的重试策略，加上自定义重试逻辑：
- 连接超时：30 秒
- 重试次数：3 次
- 退避策略：指数退避（1s, 2s, 4s）

## 健康检查（HealthCheck）

`HealthCheckService`（`src/AIPlayer/Decision/HealthCheckService.cs`）每心跳检查一次系统健康状态：

```csharp
public sealed class HealthCheckService
{
    public const long MemoryWarningMB = 500;    // 内存警告阈值
    public const long MemoryCriticalMB = 800;   // 内存临界阈值（触发强制 GC）
    public const int MaxBackgroundTasks = 20;   // 最大后台任务数
    
    public HealthReport Check(AITaskManager taskManager);
}
```

### 健康检查维度

| 维度 | 警告条件 | 临界条件 | 动作 |
|------|----------|----------|------|
| 内存 | > 500 MB | > 800 MB | 触发强制 GC |
| 后台任务 | > 15 | > 20 | 清理残留任务 |
| CPU | 持续 > 70% | 持续 > 90% | 降低 tick 频率 |
| 事件队列 | 积压 > 100 | 积压 > 500 | 清空并重置 |

### GC 策略

- 强制 GC 最小间隔：30 秒
- 只在内存超过临界值且距上次 GC 超过间隔时触发
- 触发方式：`GC.Collect(2, GCCollectionMode.Forced)` + `GC.WaitForPendingFinalizers()`

## DAG 编排（Orchestrator）

`MUnique.OpenMU.Orchestrator` 是独立提取的 DAG 编排库（`src/Orchestrator/`），零游戏依赖，可用于任何需要 DAG 执行流程的 .NET 项目。

### 核心组件

| 组件 | 文件 | 用途 |
|------|------|------|
| `ExecutionGraph` | `src/Orchestrator/Core/ExecutionGraph.cs` | DAG 图定义（节点+边） |
| `GraphExecutor` | `src/Orchestrator/Core/GraphExecutor.cs` | 图执行引擎 |
| `IExecutionNode` | `src/Orchestrator/Abstractions/IExecutionNode.cs` | 节点接口 |
| `IExecutionContext` | `src/Orchestrator/Abstractions/IExecutionContext.cs` | 执行上下文 |
| `IEventBus` | `src/Orchestrator/Abstractions/IEventBus.cs` | 事件总线 |
| `ITelemetrySink` | `src/Orchestrator/Abstractions/ITelemetrySink.cs` | 遥测接口 |
| `GraphConfig` | `src/Orchestrator/Config/GraphConfig.cs` | 图配置 |
| `NodeConfig` | `src/Orchestrator/Config/NodeConfig.cs` | 节点配置 |
| `EventBus` | `src/Orchestrator/Context/EventBus.cs` | 事件总线实现 |
| `ExecutionContext` | `src/Orchestrator/Context/ExecutionContext.cs` | 执行上下文实现 |
| `GraphSnapshot` | `src/Orchestrator/Telemetry/GraphSnapshot.cs` | 图快照 |
| `TelemetrySink` | `src/Orchestrator/Telemetry/TelemetrySink.cs` | 遥测接收器 |

### 核心算法

| 算法 | 用途 |
|------|------|
| Kahn 拓扑排序 | 确定节点的执行顺序 |
| 环检测 | 防止循环依赖 |
| 节点隔离执行 | 失败节点不影响其他节点 |

### 验证状态

- 13 个源文件
- 34/34 测试通过
- 0 构建错误
- 完整的 XML 文档注释

## EventWatcher 可靠性

`EventWatcherService`（`src/AIPlayer/Decision/EventWatcherService.cs`）实现了群体级事件监控，不依赖任何单个 AI 的生死：

| 特性 | 说明 |
|------|------|
| 驱动方式 | 独立 Timer，10 秒间隔 |
| 依赖 | IGameContext + IEventBroadcaster |
| 故障隔离 | 单个 AI 的死亡不影响其他 AI 的事件接收 |
| 状态恢复 | EventWatcherService 在 AiPlayerManager 启动时自动初始化 |

## 资源管理

| 资源 | 管理方式 | 异常处理 |
|------|----------|----------|
| CancellationToken | 每个 AI 独立的 CTS | StopAsync 时取消 |
| 文件锁 | aiplayer_data JSON 文件 | 写入失败时日志警告 |
| 后台任务 | AITaskManager 跟踪 | 健康检查清理残留 |
| 网络连接 | 自动断开检测 | 30 秒冷却重连 |
