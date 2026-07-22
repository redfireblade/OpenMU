# 坐标系统完整规范 v2.0

> **维护**: 2026-07-23
> **唯一真理**: 全系统统一 `WalkMap[row, col]`，存储和访问一致

---

## 一、核心定义

### 1.1 术语

| 术语 | 含义 | 别名 |
|------|------|------|
| **row** | 南北方向 (north-south) | X, PositionX, gate.X1 |
| **col** | 东西方向 (east-west) | Y, PositionY, gate.Y1 |

### 1.2 Point

```csharp
Point(byte X, byte Y)   // X = row (行), Y = col (列)
```

**全系统统一**：`Point.X` 永远是 row，`Point.Y` 永远是 col。禁止混淆。

---

## 二、地形存储

### 2.1 .att 文件格式

```
3 字节头:  0x00 0xFF 0xFF
65536 字节: 地形属性值，按 row-major 存储
线性索引:  i = row * 256 + col
```

### 2.2 属性值

| 值 | 位 | 含义 |
|:--:|-----|------|
| 0 | — | 空地，可走，非安全区 |
| 1 | bit0 | 安全区，可走 |
| 4 | bit2 | NOMOVE，不可走（墙） |
| 5 | bit0+bit2 | 安全区内的墙 |
| 8 | bit3 | NOGROUND，可走（无地面贴图） |
| 12 | bit2+bit3 | NOMOVE+NOGROUND，不可走 |
| 255 | — | 地图外 |

### 2.3 判定规则

```csharp
// 可走判定 — 匹配客户端鼠标点击行走
// 只查 NOMOVE(0x04)、WATER(0x10)、HEIGHT(0x40)，不查 NOGROUND(0x08)
bool walkable = value != 0xFF && (value & 0x54) == 0;

// 安全区判定
bool safezone = (value & 0x01) != 0;
```

---

## 三、内存结构（Run-Time）

### 3.1 ReadTerrainData

```csharp
// GameMapTerrain.cs
private void ReadTerrainData(ReadOnlySpan<byte> data)
{
    for (int i = 0; i < data.Length; i++)
    {
        byte x = (byte)(i & 0xFF);         // col (0-255循环)
        byte y = (byte)((i >> 8) & 0xFF);  // row (每256递增)
        byte value = data[i];

        // 存储为 [y=row, x=col] = [row, col]
        this.WalkMap[y, x]     = value != 0xFF && (value & 0x54) == 0;
        this.SafezoneMap[y, x] = (value & 0x01) != 0;
        this.AIgrid[y, x]      = (byte)((WalkMap[y,x] ? 1 : 0) | (SafezoneMap[y,x] ? 0x80 : 0));
    }
}
```

**结果**: `WalkMap[row, col]`, `SafezoneMap[row, col]`, `AIgrid[row, col]` — 三个数组统一为 `[row, col]` 维度。

### 3.2 AIgrid 编码

```
bit 0  = 可走 (1=walkable)
bit 7  = 安全区 (0x80=safezone)
```

---

## 四、所有坐标访问

### 4.1 服务端

| 函数 | 文件 | 访问方式 | 语义 |
|------|------|----------|------|
| WalkToAsync | Player.cs | `WalkMap[target.X, target.Y]` | `[row, col]` |
| PlaceAtGate | Player.cs | `AIgrid[px, py]` | px=row, py=col |
| ClientReadyAfterMapChangeAsync | Player.cs | `WalkMap[posX, posY]` | `[row, col]` |
| IsAtSafezone | LocateableExtensions.cs | `SafezoneMap[p.X, p.Y]` | `[row, col]` |
| CanWalkOn | GuardIntelligence.cs | `WalkMap[target.X, target.Y]` | `[row, col]` |
| CanWalkOn | BasicMonsterIntelligence.cs | `AIgrid[target.X, target.Y]` | `[row, col]` |
| IsValidSpawnPoint | NonPlayerCharacter.cs | `WalkMap[sp.X, sp.Y]` | `[row, col]` |
| PathFinder | BaseGridNetwork.cs | `grid[newX, newY]` | `[row, col]` |
| 所有技能 | *.cs | `WalkMap[next.X, next.Y]` | `[row, col]` |

### 4.2 Gate

| 字段 | 含义 |
|------|------|
| ExitGate.X1, X2 | row 范围 (127~131) |
| ExitGate.Y1, Y2 | col 范围 (115~119) |
| EnterGate.X1, X2 | row 范围（传送门在地图上的row位置） |
| EnterGate.Y1, Y2 | col 范围（传送门在地图上的col位置） |

### 4.3 客户端 (mu103)

| 结构 | 索引方式 |
|------|----------|
| TerrainWall | `WORD[65536]`, 线性 `y*256 + x` (行优先) |
| 行走判定 | `(TerrainWall[i] & TW_NOMOVE) != TW_NOMOVE` → 只查 0x04 |

客户端完全独立，不读服务端 DB。有自己的 EncTerrain 文件和渲染管线。

### 4.4 AI 系统 (AiBotServer)

| 组件 | 索引 |
|------|------|
| WalkValidator | `_walkMap[col, row]` — 自身闭合，与服务端 WalkMap 不同来源 |
| AStarPathFinder | `pheromone[y*256 + x]` — 从 ai.pheromone_map 加载 |
| ShadowMapRegistry | `GlobalTrail[row*256 + col]` — 行优先 |

AI 系统读取自己的 `ai.pheromone_map`（TerrainEditor 写入），不经过服务端 ReadTerrainData。内部坐标系自洽。

---

## 五、数据流

### 5.1 服务端启动

```
Terrain{N}.att (嵌入资源)
    ↓ Assembly.GetManifestResourceStream
GameMapDefinition.TerrainData (byte[65539])
    ↓ EF Core persist
PostgreSQL config."GameMapDefinition"."TerrainData" (bytea)
    ↓ 启动时读取
GameMapTerrain 构造函数 → ReadTerrainData
    ↓
WalkMap[256,256] + SafezoneMap[256,256] + AIgrid[256,256]
```

### 5.2 地形数据更新

```
EncTerrain{N}.att (客户端加密文件)
    ↓ MapFileDecrypt + BuxConvert 解密
editor[col*256 + row] (65536 字节, 列优先)
    ↓ 转置: server[row*256 + col] = editor[col*256 + row]
Terrain{N}.att (服务端格式, 行优先)
```

### 5.3 编辑器

编辑器使用 `terrain[col * 256 + row]`（列优先索引）。显示时 `屏幕(row, col)` 实现正确的视觉效果。

---

## 六、禁止事项

| ❌ 禁止 | 原因 |
|---------|------|
| `WalkMap[Y, X]` 或 `WalkMap[col, row]` | 与统一约定冲突 |
| `SafezoneMap[obj.Position.Y, obj.Position.X]` | 同上 |
| `AIgrid[target.Y, target.X]` | 同上 |
| `ReadTerrainData` 改用 `[x, y]` | 破坏存储/访问一致性 |
| 修改掩码 `0x54` | 已与客户端对齐 |
| 使用 `075_` 地形前缀 | 已删除，统一标准文件 |
