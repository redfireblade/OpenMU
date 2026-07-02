# EventWatcher 独立化 — AI 群体级服务

## 变更总结

### 动机
EventWatcherService 原本在 HeartbeatService（每个 AI 私有）内部运行。当 AI 死亡后心跳停止，事件监控也停止。需要将 EventWatcher 提升为 AI 群体级服务，不依赖任何单个 AI 的生死。

### 架构变更

**Refactored (当前架构):**

| 服务 | 所属 | 驱动方式 | 依赖 |
|------|------|----------|------|
| EventWatcherService | AiPlayerManager | 独立 Timer (10s间隔) | IGameContext + IEventBroadcaster |
| HeartbeatService | AiPlayerLogic (每个AI一个) | BeatAsync 400ms tick | 保留 OnEventOpen/Reminder/Closed 处理逻辑 |

**数据流:**
```
EventWatcherService (Timer扫描MiniGameDef)
    ↓ IEventBroadcaster.OnEventOpen/Reminder/Closed
AiPlayerManager.IEventBroadcaster (等级过滤+遍历activePlayers)
    ↓ 每个匹配等级的AI
HeartbeatService.IEventBroadcaster (看板注入+中断决策)
```

### 修改的文件

| 文件 | 操作 | 说明 |
|------|------|------|
| `src/AIPlayer/Decision/IEventBroadcaster.cs` | **新建** | 三种事件广播方法 |
| `src/AIPlayer/Decision/EventWatcherService.cs` | **重写** | 移除 AiPlayer 依赖，改为 IGameContext + IEventBroadcaster + 独立 Timer |
| `src/AIPlayer/Decision/HeartbeatService.cs` | **修改** | 移除 EventWatcherService 字段和 CheckEventsAsync 调用；新增 IEventBroadcaster 显式实现 |
| `src/AIPlayer/AiPlayerManager.cs` | **修改** | 持有 EventWatcherService 实例；实现 IEventBroadcaster 分发到各 AI |

### 不变的部分
- HeartbeatService 的 `OnEventOpen/OnEventReminder/OnEventClosed` 事件处理逻辑（看板注入、中断决策、仓库取物）保持不变
- EventWatcherService 的状态检测算法（状态迁移、Prepared 推断、定期提醒）保持不变
- AiEventBus 和事件类型（EventOpenEvent/EventReminderEvent/EventClosedEvent）不变
- EventBus 订阅仍然有效，兼容其他可能发布这些事件的方式

### 验证
- `src/AIPlayer` 项目: 0 error, 0 new warning
- `src/Startup` 项目: 0 error
- `tests/MUnique.OpenMU.AIPlayer.IntegrationTests`: 1 passed
