# AI 服务端关键函数索引

> 本文件记录项目中 AI 添加的关键函数、其作用、调用链和注意事项。供后续编程参考。

## 坐标系统相关

### `GameMapTerrain.cs`

#### `ReadTerrainData`
- **位置**: `GameMapTerrain.cs`
- **作用**: 从 `.att` 文件解析地形数据，填充 `WalkMap`、`SafezoneMap`、`AIgrid`
- **索引公式**: `.att` = `行 * 256 + 列` → `WalkMap[i&0xFF=列, i>>8=行]`（互补存储）
- **⚠️ 禁止修改** — `GetIndexOfPoint` 和所有外部访问依赖此互补关系
- 详见 `CLAUDE.md` → `坐标系统` 节

#### `FindSpawnPoint`
- **位置**: `GameMapTerrain.cs`
- **作用**: 在出生门区域内找一个安全区+可行走的坐标
- **内部**: `WalkMap[col=Gate.Y1, row=Gate.X1]` — 用 `[列, 行]` 和存储一致
- **返回**: `Point(row, col)` = `Point(行=PositionX, 列=PositionY)`
- **调用方**: `PlaceAtGate`、`ClientReadyAfterMapChangeAsync`
- **降级逻辑**: 先 20 次随机找安全区+可走 → 再 20 次随机找可走 → 返回 `null`

#### `GetRandomCoordinate`
- **位置**: `GameMapTerrain.cs`
- **作用**: 在指定点周围半径内找随机可行走坐标
- **注意**: 当前无任何代码调用此函数（AI BOT 迁移后可能使用）
- **索引**: `WalkMap[col, row]` — 内部用 `[列, 行]` 和存储一致

### `Player.cs`

#### `PlaceAtGate` (AI 添加)
- **位置**: `Player.cs`
- **作用**: 统一通过 `FindSpawnPoint` 定位出生坐标
- **调用方**: `WarpToAsync`, `RespawnAtAsync`
- **行为**: 在出生门区域内随机选安全区+可走位置，而非固定门左上角
- **设计意图**: 避免玩家传到不可行走坐标（雕像/墙壁）
- **不是原始 OpenMU 行为，但有益**
- 如需恢复原始行为：删除 `FindSpawnPoint` 调用，直接 `new Point(gate.X1, gate.Y1)`

#### `ClientReadyAfterMapChangeAsync`
- **位置**: `Player.cs`
- **作用**: 客户端确认地图加载完毕后调用
- **修改**: 已改为每次都调 `FindSpawnPoint`（不依赖 `WalkMap` 互补检查）
- **原本行为**: 检查 `WalkMap[px, py]`，不可走才调 `FindSpawnPoint`

#### `WarpToAsync`
- **位置**: `Player.cs`
- **作用**: 传送玩家到指定门（地图切换/传送）
- **调用链**: → `PlaceAtGate` → `FindSpawnPoint`
- **等待**: 客户端 F3 12 包后调 `ClientReadyAfterMapChangeAsync`

#### `RespawnAtAsync`
- **位置**: `Player.cs`
- **作用**: 玩家死亡后复活
- **调用链**: → `PlaceAtGate` → `FindSpawnPoint`

### `GameMapDefinitionExtensions.cs`

#### `GetSafezoneGate`
- **位置**: `PlayerActions/GameMapDefinitionExtensions.cs`
- **作用**: 获取地图的安全区出生门
- **优先级**: `spawn_gates.json` → `ExitGate.IsSpawnGate`
- **注意**: `spawn_gates.json` 的坐标格式 `[行, 列, 行, 列]`

## 出生门系统

### `SpawnGateConfig`
- **位置**: `GameLogic/SpawnGateConfig.cs`
- **作用**: 从 `spawn_gates.json` 加载出生门配置
- **加载时机**: 服务器启动时（`Program.cs`）
- **JSON 格式**: `{"mapId": [[行, 列, 行, 列]]}`

### `SpawnEditor` (工具)
- **位置**: `_spawn/Program.cs`
- **作用**: 可视化编辑出生门区域
- **保存**: JSON 格式 `[行, 列, 行, 列]`（和 `SpawnGateConfig` 一致）

## 地形编辑器 (TerrainEditor)

### `TryDecryptEncTerrain`
- **位置**: `TerrainEditor/MainForm.cs`
- **作用**: 解密客户端加密地形文件 `EncTerrain{N}.att`
- **算法**: MapFileDecrypt（XOR 16B key + rolling subtraction）→ BuxConvert（XOR 3B）
- **注意**: 解密后的头部 Ver=4 不是 0 时表明加密密钥不匹配

### `LoadWorld`
- **位置**: `TerrainEditor/MainForm.cs`
- **作用**: 加载地图数据（客户端模式或服务端模式）
- **客户端模式**: 解密 `EncTerrain{N}.att`
- **服务端模式**: 读取明文 `Terrain{N}.att`

## PathFinding

### `PathFinder.FindPath`
- **位置**: `Pathfinding/PathFinder.cs`
- **签名**: `FindPath(Point start, Point end, byte[,] terrain, bool includeSafezone)`
- **terrain**: 传入 `map.Terrain.AIgrid`
- **Point 语义**: X=行=PositionX, Y=列=PositionY

### `BaseGridNetwork.Prepare`
- **位置**: `Pathfinding/BaseGridNetwork.cs`
- **作用**: 初始化寻路网格
- **`_gridWidth`**: `grid.GetUpperBound(0)+1` = 第一维 = 行数 = 256
- **`_gridHeight`**: `grid.GetUpperBound(1)+1` = 第二维 = 列数 = 256

### `BaseGridNetwork.GetPossibleNextNodes`
- **位置**: `Pathfinding/BaseGridNetwork.cs`
- **作用**: 返回当前节点的可行走邻居
- **索引**: `grid[newX, newY]` = `grid[node.X + dx, node.Y + dy]`
- **注意**: 新节点来自 `node.X` (行), `node.Y` (列)

### `FullGridNetwork.GetIndexOfPoint`
- **位置**: `Pathfinding/FullGridNetwork.cs`
- **实现**: `(position.Y << 8) + position.X` = `列 * 256 + 行`
- **⚠️ 和 `.att` 索引相反** — 这是互补系统的关键部分，禁止单独修改
- **影响**: 此函数如果改为 `(pos.X << 8) + pos.Y`，则必须同步改 `ReadTerrainData`

### `ScopedGridNetwork.GetIndexOfPoint`
- **位置**: `Pathfinding/ScopedGridNetwork.cs`
- **实现**: 类似 `FullGridNetwork`，用列优先索引

## Gates 初始化

### `Gates.cs`
- **位置**: `Persistence/Initialization/VersionSeasonSix/Gates.cs`
- **作用**: 定义所有地图的 ExitGate（传送门）和 EnterGate（入口）
- **两个副本**: `SERVERS/OPENMU/.../Gates.cs` 和 `AiBotServer/.../Gates.cs`
- **注意**: 两处都要改，否则数据库会被重新初始化覆盖

### `AccountInitializerBase`
- **位置**: `Persistence/Initialization/VersionSeasonSix/TestAccounts/AccountInitializerBase.cs`
- **第 232-233 行**: 创建角色时直接设 `PositionX` 和 `PositionY`，**不经过 `FindSpawnPoint`**
- **⚠️**: 这是角色出生位置不正确的潜在来源之一

## 数据库

### `spawn_gates.json`
- **格式**: `{"mapId": [[行, 列, 行, 列]]}`
- **同步路径**: Debug 和 Release 以及服务器运行目录都要同步
- **加载点**: `Program.cs` 中 `SpawnGateConfig.Load()`

### `ExitGate` 表
- **X1**: 行(PositionX)
- **Y1**: 列(PositionY)
- **⚠️ DELETE 前检查外键**: WarpInfo、EnterGate 等可能引用

### `WarpInfo` 表
- **Index**: 传送索引（客户端使用）
- **GateId**: 引用 `ExitGate.Id`
- **⚠️ 删除 ExitGate 前必须处理关联的 WarpInfo**

## 客户端加密

### `EncTerrain{N}.att`
- **位置**: `mu103/out/build/Release/src/Data/World{N}/`
- **格式**: 131076B（WORD 格式）或 65540B（BYTE 格式）
- **头**: 4B（Version + MapId + Width + Height），期望 Version=0, Width=255, Height=255
- **解密**: MapFileDecrypt(key, data) → BuxConvert(dec)
- **密钥**: 16B XOR key + rolling `wMapKey`
- **⚠️ 当前解密算法可能不匹配** — 解密后头 Ver=4 不是 0，需要确认密钥版本
