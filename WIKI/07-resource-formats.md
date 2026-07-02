# 资源格式

OpenMU 使用多种资源文件格式，以下是最常见的几种。

## .att 文件（地形数据）

地形文件存储为 256×256 字节的网格，每字节表示一个地图坐标的属性：

| 值 | 含义 |
|----|------|
| `0x00` | 不可行走（墙壁、水面、装饰等） |
| `0x01` | 可行走 |

- 文件大小固定：65536 字节（256 × 256）
- 文件名的数字对应地图编号（如 `3.att` = 诺丽亚）
- 读取方法：`byte[,] terrain = new byte[256, 256];` 从文件偏移 0 开始逐字节读取
- 应用场景：寻径算法的输入、怪物和 NPC 的生成坐标验证、安全区域判定

### AI 系统中的转换

`src/AIPlayer/Map/AiMap.cs` 中的 `WalkGrid` 和 `SafezoneGrid` 使用同样大小的 256×256 bool 网格存储地图行走性和安全区信息。AI 系统的数字地图通过 `GameMapTerrain.UpdateAiGridValue()` 从原始地形数据转换而来。

## .ozt 文件（对象/怪物坐标）

OZT 文件存储地图上的对象生成坐标，包括怪物和 NPC：

```
格式: [记录1][记录2]...
每条记录: 
  - 类型 (2 bytes, ushort)
  - X 坐标 (1 byte)
  - Y 坐标 (1 byte)
  - 方向 (1 byte)
  - 范围/半径 (1 byte)
  - 数量 (1 byte)
```

地图中的怪物刷新的精确位置由 `MonsterSpawn` 配置管理（在数据库中而非 OZT 文件中定义），通过 `SpawnArea` 的 X1/Y1/X2/Y2 坐标矩形区域+数量生成。

## .bmd 文件（3D 模型）

BMD 是 MU Online 客户端使用的 3D 模型文件格式：

| 组成部分 | 说明 |
|----------|------|
| 顶点数据 | 3D 模型的坐标和法线 |
| 纹理映射 | UV 坐标和纹理引用 |
| 骨骼动画 | 角色/怪物的骨骼蒙皮动画 |
| 材质定义 | 颜色、透明度、反射参数 |

OpenMU **服务器端** 不需要也不解析 BMD 文件。这些文件仅用于客户端渲染。Web Map（`src/Web/Map/`）使用 Three.js 实现实时地图可视化，使用简化的 SVG/CSS 渲染而非 BMD 模型。

## .txt 配置文件（客户端）

各种客户端配置文件使用 `.txt` 格式存储游戏配置：

| 文件 | 用途 |
|------|------|
| `Gate.txt` | 地图传送门坐标和等级要求 |
| `MoveLevel.txt` | 地图传送等级要求 |
| `MapServer.txt` | 地图-服务器映射配置 |
| `Shop*.txt` | NPC 商店物品列表 |
| `ChaosMachine.txt` | 合成系统配置 |

OpenMU 将这些配置存储在数据库中（`GameConfiguration`），通过实体类管理，而非读取客户端的 TXT 文件。初始化的数据在 `src/Persistence/Initialization/` 中按版本组织。

## 数据初始化

`src/Persistence/Initialization/` 按游戏版本组织初始数据：

| 目录 | 版本 |
|------|------|
| `Version075/` | MU 0.75 版本 |
| `Version095d/` | MU 0.95d 版本 |
| `Version097d/` | MU 0.97d 版本 |
| `VersionSeasonSix/` | Season 6 版本 |

每个版本包含子目录：`Items/`、`Maps/`、`TestAccounts/`、`Events/`。
