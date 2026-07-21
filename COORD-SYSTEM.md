# 坐标系统完整规范

> **地位**: 本文档取代 CLAUDE.md 中坐标系统节的所有旧内容
> **维护**: 2026-07-22
> **核心事实**: WalkMap[X(row), Y(col)] 是经反复验证的正确约定，禁止改动

## 一、存储格式

### 1.1 文件与 DB 存储统一标准

所有持久化地形数据使用**相同的格式**（称为"服务端格式"或".att 原始格式"）：

```
3字节头 [0x00, 0xFF, 0xFF] + 65536字节地形属性值
i = 行 * 256 + 列（线性索引）
TerrainData[i+3] = 属性值          // 偏移3跳过头
```

地形属性值含义：

| 值 | 含义 |
|----|------|
| 0x00 | 空地（可走、非安全区） |
| 0x01 | 安全区（可走、安全区） |
| 0x04 | NOMOVE（不可走，墙） |
| 0x08 | NOGROUND（不可走，无地面纹理） |
| 0x0C | NOMOVE+NOGROUND |
| 0xFF | 地图外 |

### 1.2 行走性判定规则（服务端 & 客户端一致）

```csharp
walkable = value != 0xFF && (value & 0x54) == 0;
// 0x54 = NOMOVE(0x04) | WATER(0x10) | HEIGHT(0x40)
// 不包含 NOGROUND(0x08) — 客户端鼠标点击行走也只查 NOMOVE
safezone = (value & 0x01) != 0;
```

### 1.3 DB 存储（2026-07-22 后）

DB `config."GameMapDefinition"."TerrainData"` 中的地形数据已 **左转90度**（相对于原始 `.att` 文件），以匹配 ExitGate 坐标的方向：

```
左转90度变换:
  dst[新列 * 256 + (255 - 新行)] = src[行 * 256 + 列]
```

这意味着 DB 地形数据可以直接用门坐标（行大门，Y=列）渲染无需额外旋转。

## 二、四类程序的坐标处理方式

### 2.1 服务端运行时（GameLogic）

#### 启动流程

```
- Startup/Program.cs — 启动时从嵌入资源读取 .att 文件
- TerrainUpdateHelper — 读 Terrain{N}.att（服务端格式）
       → 设置 GameMapDefinition.TerrainData（DB 也存这份数据）
- GameMapTerrain(definition) 构造 — 读 TerrainData → 执行 ReadTerrainData
```

#### `GameMapTerrain.ReadTerrainData` — 地形加载

```csharp
// 输入: terrainData 字节流（服务端格式，i = row*256 + col）
// 转换:
byte x = (byte)(i & 0xFF);          // x = 列 (col)
byte y = (byte)((i >> 8) & 0xFF);   // y = 行 (row)

// 存储: WalkMap[列, 行], SafezoneMap[列, 行], AIgrid[列, 行]
this.WalkMap[x, y] = value != 0xFF && (value & 0x54) == 0;
this.SafezoneMap[x, y] = (value & 0x01) != 0;
this.UpdateAiGridValue(x, y);  // AIgrid[x, y]
```

这里形成了**第一层互补**：`WalkMap[列, 行]` 的存储形式——因为 `x=i&0xFF=列，y=i>>8=行`——等价于 `WalkMap[col, row]`。

#### `WalkToAsync`
- 玩家点击行走，客户端发送 `(X=行, Y=列)`，服务端构造 `Point{ X=行, Y=列 }`
- 服务端用 **WalkMap[X(行), Y(列)]** 检查该点是否可行走
- 虽然 WalkMap 存储是 `[列,行]`，但这里用 `[行,列]`——形成了**第二层互补**
- 两层互补抵消后，数据访问恰好正确

#### `PlaceAtGate`
- 在门区域内随机取坐标 `(X=行, Y=列)`（与 ExitGate 定义一致）
- 用 `AIgrid[X(行), Y(列)]` 检查，AIgrid 存储也是 `[列,行]`——同样互补
- 跨地图传送时，`CurrentMap` 可能为 null，改为读 `gate.Map.TerrainData` 的原始字节

#### `ClientReadyAfterMapChangeAsync`
- 读角色存储的 `PositionX(行)`, `PositionY(列)`  
- WalkMap[X(行), Y(列)] 验证

### 2.2 门编辑器（_spawn/SpawnEditor）

#### 地形读取
```csharp
// 读取客户端加密文件并解密
// 再用 [列, 行] 交换索引读取:
v = terrain[列 * 256 + 行]
```

#### 关键区别——交换索引

服务端用 `i = 行*256 + 列` 读，再用 `x=i&0xFF=列, y=i>>8=行` 存为 `[列,行]`。  
门编辑器直接用 `terrain[列 * 256 + 行]` 读——效果等价于把存储的 `[列,行]` 直接读取。

#### 渲染
```csharp
// SetPixel(x=列(水平), y=行(垂直), 颜色)
SetPixel(列 * 像素格 + dx, 行 * 像素格 + dy, 颜色)
```

`[列,行]` 的读取 + `SetPixel(x=列,y=行)` = **不旋转、不翻转换直接显示游戏正确画面**。

这是因为客户端 EncTerrain 的数据方向本身就和服务端 `ReadTerrainData` 的 `[列,行]` 存储一致。

#### 门坐标绘制
```csharp
// DB ExitGate: X1=行, Y1=列
sx = Y1(列) * 像素格    // 水平 = 列
sy = X1(行) * 像素格    // 垂直 = 行
sw = (Y2-Y1+1) * 像素格
sh = (X2-X1+1) * 像素格
```

门坐标用的是游戏视角的行列，和渲染用的 `[列,行]` 恰好匹配。

### 2.3 HTML 门查看工具（/tmp/terrain_compare）

#### 地形读取
```csharp
// 直接从 DB 读（DB 已左转90度）
v = td[3 + 行 * 256 + 列]
// 采用与 .att 相同的 i = 行*256 + 列 索引
```

#### 渲染
```csharp
// 左转90度渲染
SetPixel(行 * 像素格, 列 * 像素格, 颜色)
// 行→水平方向, 列→垂直方向
```

#### 门坐标
```csharp
sx = 行 * 像素格    // 水平 = 行
sy = 列 * 像素格    // 垂直 = 列
```

这里没有互补——因为 DB 已左转90度，所以直接 `[行,列]` 索引 + 左转90度渲染 = 匹配游戏画面。

### 2.4 客户端（C++ HeadlessClient）

#### 加密地形加载
```csharp
// Decrypt EncTerrain → TerrainWall[65536]
TerrainWall[i] = decrypted[4 + i];  // 跳过4B头
// TerrainWall 是 WORD 数组，但属性值在低字节
```

#### 行走判定
```csharp
// 鼠标点击：
if ((TerrainWall[i] & TW_NOMOVE) != TW_NOMOVE) // 只查 0x04
    AllowWalk();
// 不查 NOGROUND(0x08)、WATER(0x10)、HEIGHT(0x40)
```

#### 渲染
```csharp
// 地面纹理
if ((TerrainWall[i] & TW_NOGROUND) == TW_NOGROUND) skip render;

// 地形索引: TERRAIN_INDEX(x, y) = x + y * TERRAIN_SIZE
// x = 水平(列), y = 垂直(行)
```

## 三、变换对照表

| 程序 | 读取索引 | 存储/内存 | 渲染 | 门坐标映射 | 旋转 |
|------|----------|-----------|------|-----------|------|
| 服务端 `ReadTerrainData` | `i=row*256+col` | `[col, row]` | 不渲染 | `[X(row),Y(col)]` 互补访问 | 无 |
| 服务端 `WalkToAsync` | — | `WalkMap[col,row]` | — | `WalkMap[X(row),Y(col)]` | 无 |
| 门编辑器 | `[col*256+row]` 交换 | 直接显示 | `x=col, y=row` | `sx=col, sy=row` | 不旋转（交换索引抵消） |
| HTML工具 | `[row*256+col]` | DB 已旋转 | `x=row, y=col` | `sx=row, sy=col` | 左转90度 |
| 客户端 | `i` 线性 | `TerrainWall[i]` | `x, y` 游戏原生 | `TerrainWall[i]` | 游戏原生 |

## 四、数据流与存储位置

```
EncTerrain{N}.att（客户端加密）
  ↓ 解密
TerrainSourceA/World{N}/（编辑器源文件，供编辑器使用）
  ↓ 解密 + [列,行] 索引
编辑器显示
  ↓ 保存
spawn_gates.json（编辑器配置，存储出生门坐标 [行, 列, 行, 列]）
  ↓ + DB ExitGate
游戏服务端运行时
  ↓ ReadTerrainData → WalkMap[列,行]
  ↓ WalkToAsync：WalkMap[X(行),Y(列)]
玩家行走
  ↑
客户端 EncTerrain 解密 → TerrainWall[i] → NOMOVE 检查
```

## 五、核心规则摘要

1. ⛔ **WalkMap[X(row), Y(col)] 禁止改为 [Y,X]**
2. ⛔ **服务端 `0x54` 掩码禁止改回 `0x5C`**
3. ⛔ **DB TerrainData 已左转90度，禁止再旋转**
4. ⛔ **门编辑器用 `[列,行]` 索引，HTML 用 `[行,列]`+左转90度，两者不等价**
5. ⛔ **跨地图 PlaceAtGate 不能依赖 CurrentMap.Terrain，用 gate.Map.TerrainData**
