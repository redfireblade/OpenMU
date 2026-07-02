# 扩展点

OpenMU 提供多种扩展机制，确保在不修改核心代码的前提下扩展功能。

## 插件系统（PlugIns）

`src/PlugIns/` 目录定义了插件系统的基础设施，核心接口：

```csharp
public interface ICustomPlugIn<TPlugIn>
    where TPlugIn : class
{
    // 插件标识
}
```

### 插件注册流程

1. 实现插件接口（如 `IChatCommandPlugIn`、`IInvasionEventPlugIn`）
2. 通过 `MUnique.OpenMU.PlugIns` 特性标记
3. 自动被 `PlugInContainer` 发现和注册

### 内置插件目录

| 目录 | 用途 |
|------|------|
| `src/GameLogic/PlugIns/ChatCommands/` | 聊天命令插件（如 `/warp`、`/add` 等） |
| `src/GameLogic/PlugIns/InvasionEvents/` | 入侵事件插件 |
| `src/GameLogic/PlugIns/PeriodicTasks/` | 周期任务插件 |
| `src/GameLogic/PlugIns/UnlockCharacterClass/` | 解锁角色职业 |
| `src/GameLogic/PlugIns/WanderingMerchants/` | 流浪商人插件 |

## IBehaviorSubModule 扩展

`src/AIPlayer/Decision/IBehaviorSubModule.cs` 定义了 AI 行为子模块的可插拔接口：

```csharp
public interface IBehaviorSubModule
{
    string ModuleId { get; }
    ValueTask<StepResult> ExecuteStepAsync(MissionItem item);
}
```

### 注册新模块

通过 `ScriptRegistry`（`src/AIPlayer/Decision/ScriptRegistry.cs`）注册：

```csharp
// 在 Startup 或模块初始化时：
ScriptRegistry.Register<MyCustomModule>("my_custom_module");
// 或使用非泛型重载：
ScriptRegistry.Register("my_custom_module", typeof(MyCustomModule));
```

`ScriptRegistry.RegisterDefaultModules()` 在 `ScriptLibrary` 静态构造函数中自动调用，注册 9 个内置模块。

### 模块生命周期

1. `ScriptLibrary` 从 JSON 规则配置中读取脚本 ID
2. 通过 `ScriptRegistry.GetType(scriptId)` 获取模块 CLR 类型
3. 每个 AI 角色独立创建模块实例（`Activator.CreateInstance`）
4. 在心跳循环中调用 `ExecuteStepAsync` 执行任务

## AI 行为脚本（JSON 扩展）

AI 行为脚本系统支持通过 JSON 配置自定义行为。

### 脚本文件位置

`src/AIPlayer/Decision/scripts/` — 内置行为脚本：

| 文件 | 用途 |
|------|------|
| `devias_hunting.json` | 冰风谷狩猎脚本 |
| `event_participation.json` | 事件参与脚本 |
| `lorencia_hunting.json` | 洛伦狩猎脚本 |
| `lost_tower_hunting.json` | 失落之塔狩猎脚本 |
| `survival_emergency.json` | 生存应急脚本 |

`src/AIPlayer/Scripts/` — 规则配置：

| 文件 | 用途 |
|------|------|
| `scripts-rules.json` | 规则配置（条件→脚本映射） |
| `scripts-rules-examples.json` | 规则配置示例 |

### 脚本热重载

通过 Web API `/api/ai/reload-scripts` 热重载脚本，无需重启服务器。

### 规则系统

规则定义在 `RuleDef`（`src/AIPlayer/Decision/RuleDef.cs`）中，采用记录类型：

```csharp
public record RuleDef(
    string RuleId,           // 规则唯一标识
    int Priority,            // 优先级（越小越优先）
    string Category,         // 分类（equip/skill/craft/stat/event/inventory/survival）
    string Condition,        // 条件表达式
    string ScriptId,         // 条件满足时调用的脚本 ID
    int MinLevel,            // 最低触发等级
    int MaxLevel,            // 最高触发等级
    string? RequiredClass,   // 限制职业
    string? ItemRequired,    // 需要什么物品
    string Description,      // 规则说明
    Dictionary<string, string>? Parameters  // 脚本参数
);
```

规则引擎 `RuleEngine`（`src/AIPlayer/Decision/RuleEngine.cs`）评估所有注册规则，选择优先级最高的匹配规则生成 `MissionItem`。

## 数据初始化扩展

`src/Persistence/Initialization/` 按游戏版本组织。如需新增物品、地图、怪物等配置：

- 不修改现有初始化文件
- 新建 `Updates/` 文件或新增版本目录
- 通过 EF Core 迁移（Migration）记录变更
