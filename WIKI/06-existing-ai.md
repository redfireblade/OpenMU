# 现有 AI 系统

OpenMU 中存在三种 "自动行为" 机制，区分它们的边界对开发至关重要。

## 1. MonsterAI（原始怪物 AI）

怪物行为由有限状态机（FSM）控制，定义在 `src/GameLogic/NPC/` 中。

### FSM 状态转换

```
Idle → Walk → Attack → Chase → Return
  ↑      ↑       ↓        ↓        ↓
  └──────┴───────┴────────┴────────┘
```

| 状态 | 行为 |
|------|------|
| Idle | 静止等待，响应视野内玩家 |
| Walk | 按巡逻路径移动 |
| Attack | 在攻击范围内自动攻击目标 |
| Chase | 追击脱离攻击范围但仍在视野内的目标 |
| Return | 目标脱离视野或死亡后返回初始位置 |

### 关键属性

| 属性 | 含义 |
|------|------|
| `ViewRange` | 视野范围（触发 Chase） |
| `AttackRange` | 攻击范围（触发 Attack） |
| `RespawnDelay` | 死亡后重生延迟 |

怪物行为不受 AI Player 系统控制，属于底层游戏逻辑编码的硬行为。

## 2. OfflinePlayer（挂机系统）

`src/GameLogic/Offline/OfflinePlayer.cs` 是官方的离线挂机机制（MU Helper 的幕后实现），继承自 `Player`。

| 特性 | 说明 |
|------|------|
| 自动攻击 | 锁定最近的怪物自动攻击 |
| 自动拾取 | 拾取掉落物品 |
| 无需客户端 | 玩家离线后角色仍留在服务器 |

OfflinePlayer 和 AiPlayer 都继承自 `Player`，但 AiPlayer 拥有完整的决策流水线（知识→规则→脚本→执行）。

## 3. MU Helper

`src/GameLogic/MuHelper/` 实现官方辅助系统：

| 组件 | 用途 |
|------|------|
| `MuHelperLogic` | 辅助逻辑驱动器 |
| `MuHelperConfig` | 辅助配置（技能、坐标等） |
| `MuHelperStatus` | 辅助状态管理 |

MU Helper 是客户端可选的挂机功能，所有玩家都可以手动启用。

## 4. AI Player（AI 控制玩家）

`src/AIPlayer/` 是 OpenMU 的 AI 扩展层，不属于原始游戏逻辑。

| 对比 | MonsterAI | OfflinePlayer | MU Helper | AI Player |
|------|-----------|---------------|-----------|-----------|
| 代码位置 | GameLogic/NPC | GameLogic/Offline | GameLogic/MuHelper | AIPlayer/ |
| 决策方式 | FSM 硬编码 | 简单循环 | 配置驱动 | 知识+规则+脚本 |
| 自定义程度 | 低 | 低 | 中 | 高 |
| 扩展接口 | 无 | 无 | 限 | IAiService |

**规则：** AI Player 系统代码必须保持与原始游戏逻辑分离。修改 AI Player 代码不得影响 `GameLogic/` 中的原始行为。
