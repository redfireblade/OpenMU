# 游戏逻辑

## 玩家状态机

玩家连接后经历严格的状态转换路径，定义在 `PlayerState` 中：

```
LoginScreen → Authenticated → CharacterSelection → 
    → EnterGame → EnteredGameMap → 
        ├── Ready (正常游戏状态)
        ├── TradeRequested / TradeOpened / TradeCompleted
        ├── DuelRequested / DuelStarted
        ├── PartyRequested / PartyMembers
        └── ShopRequested / ShopOpened
```

**重要：** 状态转换路径定义了游戏合法性。任意状态转换的修改可能导致客户端/服务器不同步、角色卡死或利用漏洞。

实现文件：
- `src/GameLogic/PlayerState.cs` — 状态定义
- `src/GameLogic/PlayerActions/` — 各状态下的玩家操作

## 地图系统

游戏地图由 `GameMapTerrain` 管理，核心数据结构：

- **256×256 网格** — 每格对应游戏世界中的一个坐标点
- **地形属性** — 行走性（Walkable）、安全区（Safezone）、阻挡（Blocked）
- **AI 网格同步** — `GameMapTerrain.UpdateAiGridValue()` 将地形转换为 AI 可读的 `AiGrid` 格式

### .att 地形文件

地图地形数据存储在 `.att` 文件中，格式为 256×256 字节网格：
- `0x00` — 不可行走（水面、墙壁等）
- `0x01` — 可行走

## 经验公式

经验计算公式从 `KnowledgeBase`（静态知识库）读取，核心公式：

```
击杀经验 = 基础经验 × 经验倍率 × 组队加成 × VIP 加成
```

基础经验由 `MonsterDefinition` 中的属性决定，经验倍率由 `MapDefinition.ExpMultiplier` 控制。

`GameKnowledgeService`（`src/AIPlayer/Knowledge/GameKnowledgeService.cs`）在启动时扫描服务器配置并加载所有地图、怪物、物品知识，提供查询接口：
- `FindMapsByLevel(level)` — 根据等级推荐狩猎地图
- `FindMonstersByLevel(min, max)` — 根据等级范围查找怪物

## 掉落公式

掉落系统通过 `DropItemGroup` 配置实现，分三级：

| 级别 | 范围 | 配置位置 |
|------|------|----------|
| 1 — 全部怪物 | 全局掉落 | `GameConfiguration.DropItemGroups` |
| 2 — 地图专属 | 地图内全部怪物 | `MapDefinition.DropItemGroups` |
| 3 — 怪物专属 | 特定怪物 | `MonsterDefinition.DropItemGroups` |

优先级：怪物专属 > 地图专属 > 全局掉落。掉落判定基于 `DropItemGroup.Chance`（概率）和 `PossibleItems` 列表。

## 位置计算

位置在游戏世界中使用 `(byte X, byte Y)` 坐标，取值范围 0–255。`Point` 结构体用于路径计算。

AI 系统路径查找使用多个算法（见 `src/Pathfinding/Algorithms/`）：
- **A\*** — 默认算法，快速稳定
- **Weighted A\*** — 加权寻径
- **Ant Colony** — 蚁群算法（探索性路径）
- **Diffusion** — 扩散算法
- **Genetic** — 遗传算法
- **Random Walk** — 随机漫步
