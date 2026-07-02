# API 契约

## IAiService 接口

定义在 `src/AIPlayer/IAiService.cs`，是 AI Player 系统的**公共生命周期管理接口**。

```csharp
public interface IAiService
{
    /// 创建并生成一个 AI 玩家
    ValueTask<AiPlayerCreateResult> CreateAiPlayerAsync(AiPlayerCreateConfig config);

    /// 从数据库加载已有角色作为 AI 玩家
    ValueTask<AiPlayerCreateResult> LoadAiPlayerAsync(string characterName);

    /// 停止并移除一个 AI 玩家
    ValueTask<bool> StopAiPlayerAsync(Guid playerId);

    /// 设置 AI 玩家的目标地图（触发跨地图导航）
    ValueTask<bool> SetAiPlayerTargetMapAsync(Guid playerId, ushort mapNumber);

    /// 获取所有活跃 AI 玩家的快照
    ValueTask<IReadOnlyCollection<AiPlayerState>> GetAiPlayersAsync();

    /// 获取指定 AI 玩家的状态
    ValueTask<AiPlayerState?> GetAiPlayerStateAsync(Guid playerId);
}
```

**实现类：** `AiPlayerManager`（`src/AIPlayer/AiPlayerManager.cs`），作为单例注册到 DI 容器。

## IAiDebugService 接口

定义在 `src/AIPlayer/IAiDebugService.cs`，是 AI Player 的**调试扩展接口**，为 Blazor Debug Dashboard 提供每玩家步进控制、状态注入和详细快照。

```csharp
public interface IAiDebugService
{
    /// 获取指定 AI 玩家的详细调试数据
    AiPlayerDebugData? GetDebugData(Guid playerId);

    /// 获取所有活跃 AI 玩家的调试数据
    IReadOnlyCollection<AiPlayerDebugData> GetAllDebugData();

    /// 单步执行一个行为 tick（仅对步进模式玩家有效）
    ValueTask<TickSnapshot?> StepOnceAsync(Guid playerId);

    /// 设置玩家当前 HP
    ValueTask SetHpAsync(Guid playerId, uint hp);

    /// 设置玩家当前 MP
    ValueTask SetMpAsync(Guid playerId, uint mp);

    /// 设置玩家角色等级（并调整升级点数）
    ValueTask SetLevelAsync(Guid playerId, int level);

    /// 设置玩家位置
    ValueTask SetPositionAsync(Guid playerId, byte x, byte y);

    /// 设置属性值
    ValueTask SetStatAttributeAsync(Guid playerId, Guid attributeDefinitionId, float value);

    /// 设置玩家金钱
    ValueTask SetMoneyAsync(Guid playerId, uint money);
}
```

### 执行模式

```csharp
public enum ExecutionMode
{
    Mode1D,    // 脚本驱动模式：优先级链执行器替代 DAG 模块
    DAG,       // DAG 模式：默认全代理模式（MindEngine + DAG 执行器）
    Degraded,  // 降级模式：DAG 执行器抛出异常，回退到 PC Pipeline
}
```

### AI 调试数据快照

`AiPlayerDebugData`（record 类型）包含完整的运行期快照：

| 字段 | 类型 | 说明 |
|------|------|------|
| PlayerId | Guid | 玩家唯一 ID |
| CharacterName | string | 角色名 |
| CurrentMapId | ushort | 当前地图编号 |
| CurrentHealth / MaximumHealth | uint | 生命值信息 |
| CurrentMana / MaximumMana | uint | 魔力值信息 |
| Level | int | 角色等级 |
| PositionX / PositionY | byte | 当前坐标 |
| TickNumber | int | 已执行的 tick 数 |
| SurvivalLevel | SurvivalLevel | 生存评估等级 |
| LastDecisions | IReadOnlyList\<ModuleDecision\>? | 最近模块决策 |
| ScriptName / ScriptPosition | string? | 脚本执行位置 |
| TickTotalUs / TickWorldRefreshUs / ... | long | 性能计时 |
| TicksPerSecond / AvgTickUs / P95TickUs | double | 性能统计 |
| DashboardTodos / DashboardState / ... | string? | 调试仪表板状态 |

## AI Web API 端点

AI 系统通过 Blazor Server 管理面板提供 Web 调试接口（`/aidebug`）：

| 端点 | 方法 | 用途 |
|------|------|------|
| `/api/ai/state` | GET | 获取所有 AI 玩家状态列表 |
| `/api/ai-map/{id}/pheromones` | GET | 获取数字地图信息素热力图 |
| `/api/ai/reload-scripts` | POST | 热重载行为脚本 |
| `/api/ai/player/{id}/step` | POST | 单步调试 |

## AiPlayerState 状态快照

```csharp
public sealed record AiPlayerState(
    Guid PlayerId,
    string CharacterName,
    int Level,
    ushort CurrentMapId,
    bool IsAlive,
    DateTime StartTimestamp,
    // ... 详细状态字段
);
```

## IBehaviorSubModule 接口

定义在 `src/AIPlayer/Decision/IBehaviorSubModule.cs`，是可插拔任务执行器的通用接口：

```csharp
public interface IBehaviorSubModule
{
    string ModuleId { get; }
    ValueTask<StepResult> ExecuteStepAsync(MissionItem item);
}
```

内置注册模块（通过 `ScriptRegistry.RegisterDefaultModules()`）：

| 模块 ID | 类 | 用途 |
|---------|-----|------|
| `quest_executor` | QuestExecutor | 任务执行器 |
| `crafting_executor` | CraftingModule | 合成执行器 |
| `event_executor` | EventExecutorModule | 事件执行器 |
| `material_farm` | MaterialFarmModule | 材料刷取器 |
| `vault_executor` | VaultModule | 仓库执行器 |
| `item_farm` | ItemFarmModule | 物品刷取器 |
| `survival` | SurvivalMode | 生存模式 |
| `pet_handler` | PetHandlerModule | 宠物处理 |
| `item_pickup_manager` | ItemPickupManager | 物品拾取管理器 |

## IEventBroadcaster 接口

定义在 `src/AIPlayer/Decision/IEventBroadcaster.cs`，事件活动广播：

```csharp
public interface IEventBroadcaster
{
    void OnEventOpen(MiniGameDefinition eventDef);
    void OnEventReminder(MiniGameDefinition eventDef);
    void OnEventClosed(MiniGameDefinition eventDef);
}
```
