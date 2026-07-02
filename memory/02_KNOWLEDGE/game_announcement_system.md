# 游戏公告/事件体系深度分析

> 日期: 2026-06-18
> 目的: 全量分析 OpenMU 服务器端公告产生机制，设计 AI 群体级事件订阅方案
> 状态: 初版待验证

---

## 一、总览：公告体系全景

所有系统定时公告都通过 **`IPeriodicTaskPlugIn` 插件点 + `PeriodicTaskBasePlugIn` 状态机** 产生。

游戏每秒调用一次 `ExecutePeriodicTasks()`，遍历所有注册的 `IPeriodicTaskPlugIn`，驱动各自的状态机。

### 状态机

```
NotStarted → Prepared → Started → NotStarted (循环)
```

`Prepared` = 预告阶段（发公告），`Started` = 执行阶段（刷怪/开门），`NotStarted` = 闲置等待下次排程。

### 公告分类

```
                    ┌─ 入侵类: 黄金怪、红龙     (定时户外刷怪，直接打)
公告产生源 ── 系统定时 ─┼─ 副本类: 血色、恶魔、死亡城堡 (定时开门，副本实例)
                    └─ 增益类: 欢乐时光          (定时开，全局经验加成)
```

### 核心文件

| 文件 | 作用 |
|------|------|
| `GameLogic/PlugIns/IPeriodicTaskPlugIn.cs` | 每秒执行接口 |
| `GameLogic/PlugIns/PeriodicTasks/PeriodicTaskBasePlugIn.cs` | 状态机基类 |
| `GameLogic/PlugIns/PeriodicTasks/PeriodicTaskConfiguration.cs` | 排程配置 |
| `GameLogic/GameContext.cs` | `_tasksTimer` 驱动 + 4个全局广播方法 |
| `GameLogic/IGameContext.cs` | 全局广播接口定义 |
| `Interfaces/IGameServer.cs` | MessageType 枚举 |

---

## 二、入侵类事件

### 特征
- 定时在**户外地图**刷新特殊怪物
- 玩家直接去地图打即可，**不需要门票、不需要传送**
- 事件结束自动清理存活怪物
- 发公告同时刷怪（公告不是"通知"，而是事件机制的一部分）

### 2.1 黄金怪物入侵 (Golden Invasion)

**[BaseInvasionPlugIn.cs](src/GameLogic/PlugIns/InvasionEvents/BaseInvasionPlugIn.cs)** | **[GoldenInvasionPlugIn.cs](src/GameLogic/PlugIns/InvasionEvents/GoldenInvasionPlugIn.cs)**

**性质**: 系统定时自动触发，玩家无需操作，直接去刷怪地图打即可。

**产生机制**:
```
定时器到点(1s)
  → PeriodicTaskBasePlugIn.ExecuteTaskAsync()
    → 检查 IsItTimeToStart() [Timetable + 5秒窗口]
    → OnPrepareEventAsync() → 状态: NotStarted → Prepared
      → 随机选一张地图 (PossibleMaps池)
      → 状态: InvasionGameServerState.MapId = 随机值
    → OnPreparedAsync()
      → ForEachPlayer 调用 TrySendStartMessageAsync() [金色公告]
      → ForEachPlayer 调用 TrySendMapEventStateUpdateAsync() [小地图红点]
    → 等待 PreStartMessageDelay(3秒)
    → 状态: Prepared → Started
    → OnStartedAsync()
      → SpawnMobsOnSelectedMapAsync() [在选中地图刷黄金龙x10]
      → SpawnMobsOnMapsAsync() [在各固定地图刷对应黄金种]
    → 等待 TaskDuration(5分钟)
    → 状态: Started → NotStarted
    → OnFinishedAsync()
      → 发小地图事件关闭
      → 清理所有存活怪物
```

**涉及实体**:

| 怪物 | 编号 | 数量 | 地图 |
|------|------|------|------|
| 黄金破坏者 (Golden Budge Dragon) | #43 | 20 | Lorencia (#0) |
| 黄金哥布林 (Golden Goblin) | #78 | 20 | Noria (#3) |
| 黄金士兵 (Golden Soldier) | #54 | 20 | Devias (#2) |
| 黄金泰坦 (Golden Titan) | #53 | 10 | Devias (#2) |
| 黄金维帕 (Golden Vepar) | #81 | 20 | Atlans (#7) |
| 黄金蜥蜴王 (Golden Lizard King) | #80 | 10 | Atlans (#7) |
| 黄金车轮 (Golden Wheel) | #83 | 20 | Tarkan (#8) |
| 黄金坦塔罗斯 (Golden Tantallos) | #82 | 10 | Tarkan (#8) |
| **黄金龙** (Golden Dragon) | #79 | 10 | 选中地图 |

- 地图池（随机选1）：Lorencia(#0) / Devias(#2) / Noria(#3)

**消息**: `"[{mapName}] Golden Invasion!"` → `MessageType.GoldenCenter`

**配置**:
```csharp
Timetable = [00:00, 04:00, 08:00, 12:00, 16:00, 20:00]  // 每4小时
PreStartMessageDelay = 3s
TaskDuration = 5min
```

**附带行为**:
- 刷约110只黄金怪（各固定地图 + 选中地图额外10只黄金龙）
- 小地图事件指示器（`MapEventType.GoldenDragonInvasion`）
- 掉落由怪物配置的 DropItemGroups 决定
- 事件结束时清理所有存活怪物

---

### 2.2 红龙入侵 (Red Dragon Invasion)

**[RedDragonInvasionPlugIn.cs](src/GameLogic/PlugIns/InvasionEvents/RedDragonInvasionPlugIn.cs)**

**性质**: 与黄金入侵同机制，数据不同。

**涉及实体**:
- 地图池：Lorencia(#0) / Devias(#2) / Noria(#3)
- 怪物：MonsterDef#44 红龙(Red Dragon) x5（仅选中地图）

**配置**:
```csharp
Timetable = [02:00, 08:00, 14:00, 20:00]  // 每6小时，起始02:00
PreStartMessageDelay = 3s
TaskDuration = 10min
```

**消息**: `"[{mapName}] Red Dragon Invasion!"` → `MessageType.GoldenCenter`

---

## 三、副本类事件

### 特征
- 定时开放入口，玩家**必须入场**（在限定时间内传送到副本地图）
- **需要门票 + 入场费**
- 副本是独立实例，内部有多个等级（Lv.1~7）
- **不**在大地图上刷怪
- 公告是倒计时通知，不是"开打了"通知

### 3.1 血色城堡 (Blood Castle)

**[BloodCastleStartPlugIn.cs](src/GameLogic/PlugIns/PeriodicTasks/BloodCastleStartPlugIn.cs)** | **[MiniGameStartBasePlugIn.cs](src/GameLogic/PlugIns/PeriodicTasks/MiniGameStartBasePlugIn.cs)**

**产生机制**:
```
定时器到点(1s)
  → NotStarted → 检查时间
  → Prepared(空操作，不发任何消息)
  → Started
    → 创建 MiniGameContext (副本实例)
    → 启动 SendOpenedNotificationsAsync()
      → 循环：每分钟 SendGlobalNotificationAsync("还剩X分钟")
      → 入场关门时：SendGlobalNotificationAsync("入口已关闭")
  → 持续 TaskDuration → Finished
```

**与入侵类的关键区别**:
- OnPreparedAsync 是空操作（**不**发入侵那种"事件开始了"的消息）
- 使用 `SendGlobalNotificationAsync` 而非逐个 `ShowMessageAsync`
- **不刷怪**、**不追踪小地图事件标志**
- 公告是"提醒你入场"而非"怪物已刷新"

**消息**: `"Blood Castle entrance is open and closes in {0} minute(s)."` (逐分钟递减)

**配置**: 每10分钟检查排程，入口开放20分钟，入场窗口约1分钟

---

### 3.2 恶魔广场 (Devil Square)

**[DevilSquareStartPlugIn.cs](src/GameLogic/PlugIns/PeriodicTasks/DevilSquareStartPlugIn.cs)**

与血色城堡**同机制**，仅配置不同：
- MiniGameType: DevilSquare
- 入口开放25分钟

---

### 3.3 死亡城堡 (Chaos Castle)

**[ChaosCastleStartPlugIn.cs](src/GameLogic/PlugIns/PeriodicTasks/ChaosCastleStartPlugIn.cs)**

与血色城堡**同机制**，仅配置不同：
- MiniGameType: ChaosCastle
- 入口开放15分钟

---

## 四、增益类事件

### 4.1 欢乐时光 (Happy Hour)

**[HappyHourPlugIn.cs](src/GameLogic/PlugIns/PeriodicTasks/HappyHourPlugIn.cs)**

**性质**: 定时自动开启，玩家**无需任何操作**，全局经验倍率自动生效。

**机制**:
```
Prepared → ForEachPlayer 发送金色公告
Started → Stats.ExperienceRate 乘法器 +1.5 (可配置)
Finished → 乘法器恢复为1.0
```

**特点**:
- 实现了 `IPlayerStateChangedPlugIn`，玩家进入世界时自动应用倍率
- 不影响任何怪物、地图、NPC

**消息**: `"Happy Hour event has been started!"` → `MessageType.GoldenCenter`

**配置**: 每6小时（00:05, 06:05, 12:05, 18:05），持续1小时

---

## 五、通用全局广播（框架级 API）

**[GameContext.cs](src/GameLogic/GameContext.cs)** 提供了 4 个全局广播方法，供**插件和 GM 命令**调用，不产生系统定时事件：

| 方法 | 消息类型 | 用途 |
|------|---------|------|
| `SendGlobalMessageAsync(string, MessageType)` | GoldenCenter / BlueNormal / GuildNotice | GM 发公告 |
| `SendGlobalNotificationAsync(string)` | GoldenCenter(SendGlobalMessageAsync封装) | GM 金色公告，以"!"开头的消息去前缀 |
| `SendGlobalChatMessageAsync(sender, msg, ChatMessageType)` | 聊天框 | GM 聊天消息 |
| `ShowGlobalLocalizedMessageAsync(msgType, key, args)` | 按玩家语言翻译 | 系统本地化消息 |

---

## 六、AI 事件结构化设计

### 关键架构结论

**AI 群体层不应该解析文本消息**，而应该监听 **`PeriodicTaskBasePlugIn` 的状态机变化**。游戏在拼字符串之前已经有完整的结构化数据（`InvasionGameServerState` / `MiniGameDefinition`）。

### 6.1 入侵事件结构

```csharp
/// <summary>
/// 入侵事件 — 黄金怪/红龙在户外地图刷新。
/// AI行为: 判断等级是否匹配 → 决定是否传送过去打。
/// </summary>
record InvasionEvent(
    MapEventType Type,            // GoldenDragonInvasion / RedDragonInvasion
    ushort MapId,                 // 地图编号（随机选的）
    string MapName,               // 地图名称
    List<(short MonsterId, int Count)> Mobs, // 怪物列表 [{黄金龙, 10}]
    TimeSpan Duration,            // 持续时长
    int RecommendedLevel,         // 推荐等级（由地图平均怪物等级推算）
    bool IsActive                 // true=正在开放, false=已结束
);
```

### 6.2 副本事件结构

```csharp
/// <summary>
/// 副本事件 — 血色/恶魔/死亡城堡入口开放。
/// AI行为: 检查等级范围→检查门票/入场费→决定是否入场。
/// </summary>
record MiniGameEvent(
    MiniGameType Type,            // BloodCastle / DevilSquare / ChaosCastle
    int GameLevel,                // 副本等级（1~7）
    int MinLevel,                 // 最低角色等级
    int MaxLevel,                 // 最高角色等级
    int EntranceFee,              // 入场费（金币）
    int? TicketItemGroup,         // 门票物品组
    int? TicketItemNumber,        // 门票物品编号
    int EnterDuration,            // 入口开放时长（分钟）
    MiniGameState State           // NotStarted / Prepared / Started / Closed
);
```

### 6.3 增益事件结构

```csharp
record BuffEvent(
    BuffType Type,                // HappyHour
    float ExperienceMultiplier,   // 经验加成倍率
    TimeSpan Duration,            // 持续时长
    bool IsActive                 // true=生效中, false=已结束
);
```

### 6.4 IEventBroadcaster 扩展

```csharp
interface IEventBroadcaster
{
    // === 原有（保持不变）===
    void OnEventOpen(MiniGameType type, int gameLevel, string name, int entranceFee);
    void OnEventReminder(MiniGameType type, int gameLevel, string name, int minutesLeft);
    void OnEventClosed(MiniGameType type, int gameLevel, string name);

    // === 新增入侵事件 ===
    void OnInvasionPrepared(MapEventType type, ushort mapId, string mapName,
                            IReadOnlyList<InvasionMobSpawn> mobs, TimeSpan duration);
    void OnInvasionStarted(MapEventType type, ushort mapId);
    void OnInvasionFinished(MapEventType type);

    // === 新增增益事件 ===
    void OnBuffStarted(BuffType type, float multiplier, TimeSpan duration);
    void OnBuffFinished(BuffType type);
}
```

---

## 七、架构连接方案

### 7.1 连接点：在 PeriodicTaskBasePlugIn 加状态事件

```csharp
// PeriodicTaskBasePlugIn.cs（修改）
public event Action<PeriodicTaskState, TState>? StateChanged;

// 在 ExecuteTaskAsync 每次状态切换后触发
// OnPreparedAsync() 之后: StateChanged?.Invoke(PeriodicTaskState.Prepared, state);
// OnStartedAsync() 之后:   StateChanged?.Invoke(PeriodicTaskState.Started, state);
// OnFinishedAsync() 之后:  StateChanged?.Invoke(PeriodicTaskState.NotStarted, state);
```

### 7.2 数据流

```
GameContext._tasksTimer(每秒)
  ↓
PeriodicTaskBasePlugIn.ExecuteTaskAsync()
  ↓ 状态迁移
StateChanged 事件
  ↓
AI SystemEventBridge（新建）
  ↓ 识别 TState 类型 → 提取结构化数据
  ↓
IEventBroadcaster 扩展方法
  ↓
AiPlayerManager（遍历在线AI，按等级/类型过滤）
  ↓
每个 AI 的 HeartbeatService.OnInvasionStarted()
  ↓
EventHandlers → 生成 MissionItem → 看板
  ↓
规则引擎评估：等级匹配？每日限额？当前状态？
  ↓ 资格检查（GameAdapter.CanKillBossToday）
  ↓ 决策：去/不去
```

### 7.3 每日限额检查

对于"打了几次就不掉血"的 BOSS 机制，**游戏引擎没有内置实现**。

需要在游戏服务器端新增 `IAttackableGotHitPlugIn` 插件来拦截伤害：

```csharp
[PlugIn("BOSS每日击杀限制", "限制每个玩家每天只能击杀指定BOSS限定的次数")]
class BossDailyKillLimitPlugIn : IAttackableGotHitPlugIn
{
    // 数据结构: Dictionary<(int playerId, short monsterNumber), int> _dailyKills
    // 每日重置: DateTime.UtcNow.Date 变更时清空

    void AttackableGotHit(IAttackable attackable, IAttacker attacker, HitInfo hitInfo)
    {
        if (attackable is not Monster monster) return;
        if (!IsBossMonster(monster.Definition.Number)) return;
        if (DailyKillExceeded(attacker, monster.Definition.Number))
        {
            // 伤害置 0
            hitInfo.HealthDamage = 0;
            hitInfo.ShieldDamage = 0;
        }
    }
}
```

AI 层检查接口：
```csharp
// GameAdapter 新增
bool CanDamageMonsterToday(short monsterNumber);
// 内部调用插件的计数检查
```

---

## 八、涉及的所有游戏实体索引

### 怪物索引

| 编号 | 名称 | 所属事件 | 地图 |
|------|------|---------|------|
| #43 | Golden Budge Dragon (黄金破坏者) | 黄金入侵 | Lorencia |
| #44 | Red Dragon (红龙) | 红龙入侵 | 随机图 |
| #53 | Golden Titan (黄金泰坦) | 黄金入侵 | Devias |
| #54 | Golden Soldier (黄金士兵) | 黄金入侵 | Devias |
| #78 | Golden Goblin (黄金哥布林) | 黄金入侵 | Noria |
| #79 | Golden Dragon (黄金龙) | 黄金入侵 | 选中地图 |
| #80 | Golden Lizard King (黄金蜥蜴王) | 黄金入侵 | Atlans |
| #81 | Golden Vepar (黄金维帕) | 黄金入侵 | Atlans |
| #82 | Golden Tantallos (黄金坦塔罗斯) | 黄金入侵 | Tarkan |
| #83 | Golden Wheel (黄金车轮) | 黄金入侵 | Tarkan |

### 地图索引

| 编号 | 名称 | 事件 |
|------|------|------|
| #0 | Lorencia (勇者大陆) | 黄金入侵随机池 + 固定刷怪 |
| #2 | Devias (冰风谷) | 黄金入侵随机池 + 固定刷怪 |
| #3 | Noria (诺丽亚) | 黄金入侵随机池 + 固定刷怪 |
| #7 | Atlans (亚特兰斯) | 黄金入侵固定刷怪 |
| #8 | Tarkan (塔坎) | 黄金入侵固定刷怪 |

### MiniGame 类型索引

| 枚举值 | 名称 | 配置 |
|--------|------|------|
| DevilSquare | 恶魔广场 | 每10分钟排程，25分钟时长 |
| BloodCastle | 血色城堡 | 每10分钟排程，20分钟时长 |
| ChaosCastle | 死亡城堡 | 每10分钟排程，15分钟时长 |

### MessageType 枚举

```csharp
enum MessageType {
    GoldenCenter = 0,   // 屏幕中央金色通知
    BlueNormal = 1,     // 左侧蓝色通知
    GuildNotice = 2,    // 居中绿色公会公告
}
```

---

## 九、总结：核心设计原则

1. **不解析文本** — 游戏在拼字符串前已有结构化数据，从 `PeriodicTaskBasePlugIn` 的 `TState` 对象取
2. **一次接口全覆盖** — 所有定时事件共享 `StateChanged` 事件，按 `TState` 类型路由
3. **AI 群体层负责任务注入** — `SystemEventBridge` 负责"发现事件→结构化→广播"，不负责"去不去"的决策
4. **每个 AI 各自决策** — 看板收到结构化事件 → 规则引擎评估（等级/每日限额/当前状态）→ 决定
5. **每日限额在游戏引擎拦截** — `IAttackableGotHitPlugIn` 插件使超限 BOSS 不掉血，AI 调用前置检查接口避免白跑
