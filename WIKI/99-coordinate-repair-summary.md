# 地形与坐标系统 — 问题记录与规范（含历史摘要）

> **维护**: 2026-07-22
> **相关铁律**: `SERVERS/OPENMU/CLAUDE.md` → 坐标系统节 + 铁律节
> **函数索引**: `WIKI/90-function-index.md`
> **旧版全文**: `_archive/terrain-fix-v1.md`、`_archive/terrain-coord-system-v1.md`

---

## 一、坐标系统（当前稳定状态）

> ⚠️ **这是互补系统，改任何一个不及其余必崩。详见 `CLAUDE.md` → 坐标系统**

| 组件 | 实际行为 | 互补关系 |
|------|----------|---------|
| `.att` 文件 | `i = 行 * 256 + 列` | — |
| `ReadTerrainData` | `WalkMap[i&0xFF=列, i>>8=行]` | 和 .att "相反" |
| `GetIndexOfPoint` | `(pos.Y<<8)+pos.X` = `列*256+行` | 和 ReadTerrainData 一致 |
| 外部 `WalkMap` 访问 | `[posX=行, posY=列]` | 和存储 `[列,行]` 互补 |
| `FindSpawnPoint` | `WalkMap[col=Gate.Y1, row=Gate.X1]` | 内部用 `[列,行]` |
| 返回 `Point(row, col)` | `Point.X=行, Point.Y=列` | 配合 `PositionX/Y` |

---

## 二、问题修复记录（近 10 次）

### 2.1 角色走几步就停 (2026-07-21)
- **原因**: 单独修改 `ReadTerrainData` 索引（`[列,行]→[行,列]`），未同步改 `GetIndexOfPoint`
- **修复**: 恢复 `ReadTerrainData` 为原始 `[列,行]` 存储，不动 `GetIndexOfPoint`
- **文件**: `GameMapTerrain.cs::ReadTerrainData`, `FullGridNetwork.cs::GetIndexOfPoint`

### 2.2 仙踪林出生在野外 (2026-07-21)
- **原因**: `AccountInitializerBase.cs:232-233` 创建角色时直接设坐标，不经过 `FindSpawnPoint`；`ClientReadyAfterMapChangeAsync` 的 WalkMap 检查用互补索引读到错误位置
- **修复**: 强制 `ClientReadyAfterMapChangeAsync` 每次都调 `FindSpawnPoint`；改为 `Point(col,row)→Point(row,col)`
- **文件**: `Player.cs::ClientReadyAfterMapChangeAsync`, `GameMapTerrain.cs::FindSpawnPoint`

### 2.3 地图列表传送失败 (2026-07-21)
- **原因**: 删除了 ExitGate 表记录导致 WarpInfo 外键断裂
- **修复**: 重建正确坐标的 ExitGate + WarpInfo；`-reinit` 重建配置表
- **教训**: ❌ 不要 DELETE，应该 UPDATE 坐标

### 2.4 冰风谷安全区有怪物 (2026-07-21)
- **原因**: `MarkSafeRect` 传参的 `(row, col)` 顺序和 `ReadTerrainData` 的 `[列,行]` 存储不匹配
- **修复**: `MarkSafeRect` 参数改为 `(列, 行, 列, 行)` 和存储一致，或通过 `spawn_gates.json` 覆盖

### 2.5 地形图和场景不匹配 (2026-07-20)
- **原因**: `.att` 文件被错误修改 + `ReadTerrainData` 判定规则从 `value < 10` 改为 `(value & 0x5C) == 0`
- **修复**: 从 git 历史恢复原始 `.att` 文件（a4c5dfc9 版本）
- **影响**: 47 张地图全部恢复

### 2.6 地图传送后闪烁/回弹 (2026-07-20)
- **原因**: 删除 `MarkSafeRectangle` 后部分地图失去安全区判断；`WarpToSafezoneAsync` 强制拉回
- **修复**: 引入 `spawn_gates.json` + `FindSpawnPoint` 替代硬编码安全区

### 2.7 怪物穿墙进安全区 (2026-07-20)
- **原因**: 旧规则 `value < 10` 将值4(NOMOVE)判为可行走
- **修复**: 统一 `(value & 0x5C) == 0` 判定规则，与客户端一致

### 2.8 OZJ/OZT 打包格式废弃 (2026-07-20)
- **内容**: 删除 OZJ/OZT/OZB 打包加载代码，直接加载标准 JPG/TGA/BMP
- **文件**: `GlobalBitmap.cpp`, `ZzzTexture.cpp`
- **注意**: 保留 .OZJ 文件作为 JPG 加载失败的 fallback

### 2.9 TerrainHeight.OZB 损坏修复 (2026-07-20)
- **原因**: 文件只有 66618 字节，需要 66620 字节
- **修复**: 从 `D:\DOWNLOAD\1.03K\Client\data` 复制完好版本
- **地图**: World7/31/74/75

### 2.10 服务端/客户端地形不一致 (2026-07-20)
- **修复**: 解密 47 张加密 EncTerrain 文件，替换服务端 Resources，更新数据库
- **验证**: 客户端解密版 == Resources文件 == 数据库 TerrainData

---

## 三、经验教训总结

| # | 教训 | 说明 |
|---|------|------|
| 1 | **不理解不修改** | 坐标系统是三重互补，改一个必须改全部 |
| 2 | **禁止 `-demo`** | InMemory 模式 → 数据永远丢失 |
| 3 | **不要 DELETE DB 记录** | 应该 UPDATE，检查外键 |
| 4 | **两套 Gates.cs** | `SERVERS/OPENMU` 和 `AiBotServer` 都要改 |
| 5 | **`AccountInitializerBase`** | 角色创建不经过 `FindSpawnPoint` |
| 6 | **`PlaceAtGate` 行为变更** | 随机选安全区内位置，不是固定门左上角（已验证有益） |
| 7 | **提交计划后才改代码** | 涉及坐标/DB 的操作必须先给用户批准 |

---

## 四、文件存储位置

| 用途 | 路径 |
|------|------|
| 服务端嵌入资源 | `SERVERS/OPENMU/src/Persistence/Initialization/Resources/Terrain{N}.att` |
| 客户端加密地形 | `mu103/out/build/Release/src/Data/World{N}/EncTerrain{N}.att` |
| 编辑器源文件 | `AiBotServer/src/TerrainEditor/TerrainSourceA/World{N}/` |
| 地图编号映射 | `Terrain{N}.att → World{N} → Number = N-1` |

### .att 文件格式

```csharp
[3B头: 00 FF FF] + [65536B 地形数据: 256×256 格, 每格1B]
walkable = value != 0xFF && (value & 0x5C) == 0;  // 0x5C=NOMOVE|NOGROUND|WATER|HEIGHT
safezone = (value & 0x01) != 0;
```

| 值 | 含义 | 可行走 |
|----|------|--------|
| 0 | 空地 | ✅ |
| 1 | 安全区 | ✅ |
| 4 | NOMOVE(墙) | ❌ |
| 5 | 安全区+墙 | ❌ |
| 0xFF | 地图外 | ❌ |

---

## 五、2026-07-22 重大修复

### 5.1 行走掩码错误 (0x5C → 0x54)
- **症状**: 冰风谷/地下城等"不能动"，但地形数据100%匹配
- **原因**: 掩码包含 NOGROUND(0x08)，客户端鼠标点击只检查 NOMOVE(0x04)
- **修复**: `GameMapTerrain.ReadTerrainData` 掩码 → 0x54

### 5.2 WalkMap 索引约定确认
- WalkMap 存储 `[列,行]`，但所有代码访问用 `[X(row),Y(col)]`
- AI 多次改成 `[Y,X]` 均导致正确变错误——禁止改动

### 5.3 地形数据来源错误
- DB 地形来自错误源（编辑器 vs 客户端运行时），Map N 应对应 World N+1
- 修复: 直接从客户端 EncTerrain 解密写入 DB

### 5.4 PlaceAtGate 跨地图传送
- 换图时 `CurrentMap` 为 null，地形验证跳过 → 纯随机落点
- 修复: 用 `gate.Map.TerrainData` 验证

### 5.5 出生判断死循环
- `ClientReadyAfterMapChangeAsync` 要求 SafeZone+WalkMap → 无安全区地图死循环
- 修复: 只检查 WalkMap，找不到全图扫描兜底

### 5.6 部分出生门 100% 不可行走
- Kanturu1/Arena/IT 等事件地图门区域全墙
- 已识别，需逐个修正门坐标

---

---

## 六、三个工具的坐标变换对照表（2026-07-22 最终确认）

### 核心事实

服务端 DB 存储的 TerrainData 是 `.att` 格式原始数据（`index = row*256+col`）。三个工具各自用不同的坐标索引方式，最终都正确显示为匹配游戏画面的方向。

### 1. 服务端代码（运行时坐标处理）

**数据存储**：DB `TerrainData`（服务端格式）= 3B头 + 65536B 地形数据，已左转90度（2026-07-22旋转），与门坐标方向一致。

#### `GameMapTerrain.ReadTerrainData` — 地形加载

```csharp
// .att 索引: i = row*256 + col
// ReadTerrainData 存储: WalkMap[col, row]
//   实际上: WalkMap[x=i&0xFF = 列, y=i>>8 = 行]
byte x = (byte)(i & 0xFF);   // = 列
byte y = (byte)((i >> 8) & 0xFF); // = 行
WalkMap[x, y] = value != 0xFF && (value & 0x54) == 0;
SafezoneMap[x, y] = (value & 0x01) != 0;
```

#### `WalkToAsync` — 行走判定（Player.cs:1410）

```csharp
// target 来自客户端发送的行走目标点
// target.X = 行, target.Y = 列（MU协议约定）
var canWalkToTarget = currentMap.Terrain.WalkMap[target.X, target.Y];
//                                WalkMap[行, 列]
//
// 行=列：WalkMap 存储是 [列,行]，但这里用 [X(行),Y(列)] 访问。
// 这是一个历史形成的互补约定——WalkMap 的列索引和 X(行) 恰好对应，
// 行索引和 Y(列) 对应。实际上在 [列,行] 存储中查 [行,列] 是反的。
// 但所有现存代码都用这个方式，改 [Y,X] 会导致全地图行走停止。
// ⛔ 禁止改为 [target.Y, target.X]！
```

#### `PlaceAtGate` — 出生点定位（Player.cs:2013）

```csharp
// 从门区域随机取坐标
x = (byte)Rand.NextInt(gate.X1, gate.X2);  // 行
y = (byte)Rand.NextInt(gate.Y1, gate.Y2);  // 列

// AIgrid 用 [列,行] 存储，访问时也用 [列,行]（和 WalkMap 互补约定一致）
if ((terrain.AIgrid[x, y] & 1) != 1)  // AIgrid[行, 列]
{
    var safePoint = terrain.GetRandomCoordinate(new Point(x, y), 5);
    if ((terrain.AIgrid[safePoint.X, safePoint.Y] & 1) == 1) // AIgrid[行,列]
    { ... }
}

// 注意: 跨地图传送时 CurrentMap.Terrain 可能为 null，
// 改用 gate.Map.TerrainData 读取目标地图的原始地形字节
```

#### `ClientReadyAfterMapChangeAsync` — 换图后位置修正（Player.cs:1165）

```csharp
posX = SelectedCharacter.PositionX;  // 行
posY = SelectedCharacter.PositionY;  // 列

// WalkMap / SafezoneMap 用 [X(row), Y(col)] 访问（与 WalkToAsync 一致）
if (!terrain.WalkMap[posX, posY])     // WalkMap[行, 列]
{
    // 附近搜索可行走位置
    for (int dx = -radius; dx <= radius; dx++)
    for (int dy = -radius; dy <= radius; dy++)
    {
        int tx = posX + dx;  // 行
        int ty = posY + dy;  // 列
        if (terrain.WalkMap[tx, ty])  // WalkMap[行, 列]
        {
            SelectedCharacter.PositionX = (byte)tx;  // 行
            SelectedCharacter.PositionY = (byte)ty;  // 列
        }
    }
}
```

#### 总结：为什么存储 [列,行] 但访问 [行,列]？

这是 `ReadTerrainData` 中一个意外的互补结果：
```
.att索引: i = row*256 + col
WalkMap存储: WalkMap[i&0xFF, i>>8] = WalkMap[列, 行]
WalkMap访问: WalkMap[X(行), Y(列)]
```
虽然看起来反了，但把所有 WalkMap 调用整理后，这个互补恰好使 Gate 坐标（`X1=行, Y1=列`）和外部 Point（`X=行, Y=列`）不需要任何额外转换就能直接用来查地形。**所有历史代码都遵循这个约定。**

**2026-07-22 经验验证**：AI 三次尝试改为 `[Y,X]`（在逻辑上看起来更正确），每次导致全部地图"不能动"或"走几步就停"，回退后恢复正常。结论：**即使不理解也要保持现状。**

### 2. 门编辑器 `_spawn/SpawnEditor`

数据来源: `TerrainSourceA/World{N}/EncTerrain{N}.att`（解密后）

```
// 读取
v = terrain[列 × 256 + 行]            // 交换索引 = 自然右转90度

// 渲染 (SetPixel(x, y))
x = 列(水平), y = 行(垂直)
SetPixel(x × 像素格, y × 像素格, 颜色)

// 门坐标 (来自 DB ExitGate: X1=行, Y1=列)
sx = Y1(列) × 像素格                  // 水平位置 = 列
sy = X1(行) × 像素格                  // 垂直位置 = 行
width  = (Y2 - Y1 + 1) × 像素格
height = (X2 - X1 + 1) × 像素格
```

结论: 地形读取时的 `[列,行]` 交换索引 + SetPixel(x=列,y=行) = 不旋转，直接显示游戏正确画面。

### 3. 地形编辑器 `TerrainEditor`

与门编辑器完全相同：

```
// 读取
v = rawData[列 × TS + 行]

// 渲染
SetPixel(x=列, y=行, 颜色)

// 门坐标
sx = Y1(列) × 像素格, sy = X1(行) × 像素格
```

❗`rawData[列 × TS + 行]` 的交换索引使渲染无需额外旋转。

### 4. HTML 地图门查看工具 `/tmp/terrain_compare`

```
// 直接读 DB TerrainData（服务端格式）
v = td[3 + 行 × 256 + 列]

// 左转90度渲染
SetPixel(行 × 像素格, 列 × 像素格, 颜色)     // 行→水平, 列→垂直

// 门坐标（匹配左转90度）
sx = 行 × 像素格, sy = 列 × 像素格
width = (X2-X1+1) × 像素格, height = (Y2-Y1+1) × 像素格
```

结论: 与 DB 原始索引 `[行,列]` 一致，通过左转90度渲染匹配游戏画面。

### 5. DB 地形旋转（2026-07-22）

之前 DB TerrainData = `.att` 原始方向 → 与门坐标左转90度错位。
现在 DB TerrainData 已左转90度，与门坐标方向一致。

```
左转90度变换: dst[新列 × 256 + (255 - 新行)] = src[行 × 256 + 列]
```

---

## 七、归档说明

旧版完整文档已移到 `_archive/`：
- `TERRAIN-FIX.md` (旧) → `_archive/terrain-fix-v1.md` — 早期修复记录（出生在水里、地图闪烁、Portal 回弹）
- `TERRAIN-COORD-SYSTEM.md` (旧) → `_archive/terrain-coord-system-v1.md` — .att 格式详解、像素/纹理格式说明、AI BOT 通信协议
