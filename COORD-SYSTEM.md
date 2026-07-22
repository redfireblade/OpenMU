# 坐标系统完整规范 (v2.0)

> **维护**: 2026-07-23
> **核心约定**: 全系统统一 `WalkMap[X(row), Y(col)]` — 存储和访问一致，不再互补

---

## 一、统一约定

**2026-07-23 重大修正**: 将 ReadTerrainData 的存储从 `WalkMap[col, row]` 改为 `WalkMap[row, col]`，与所有外部代码的 `WalkMap[X=row, Y=col]` 访问约定一致。

**旧版本（S32）**: 存储 `[col, row]`，访问 `[row, col]` → 互补但易出错  
**新版本（S33）**: 存储和访问都是 `[row, col]` → 统一

---

## 二、存储格式

### 2.1 TerrainData (DB / .att 文件)

```
3字节头 [0x00, 0xFF, 0xFF] + 65536字节地形属性值
i = 行 * 256 + 列（.att 文件线性索引）
```

### 2.2 地形属性值

| 值 | 含义 |
|----|------|
| 0x00 | 空地（可走、非安全区） |
| 0x01 | 安全区（可走、安全区） |
| 0x04 | NOMOVE（不可走，墙） |
| 0x08 | NOGROUND（无地面纹理，但可走） |
| 0xFF | 地图外 |

### 2.3 行走性判定

```csharp
walkable = value != 0xFF && (value & 0x54) == 0;
// 0x54 = NOMOVE(0x04) | WATER(0x10) | HEIGHT(0x40)
// 不含 NOGROUND(0x08) — 匹配客户端鼠标点击行走判定
safezone = (value & 0x01) != 0;
```

### 2.4 ReadTerrainData（统一存储）

```csharp
for (int i = 0; i < data.Length; i++)
{
    byte x = (byte)(i & 0xFF);  // col
    byte y = (byte)((i >> 8) & 0xFF); // row
    // 存储为 [y=row, x=col] 以匹配所有外部代码的 WalkMap[row, col] 访问
    this.WalkMap[y, x] = value != 0xFF && (value & 0x54) == 0;
    this.SafezoneMap[y, x] = (value & 0x01) != 0;
    this.UpdateAiGridValue(y, x);
}
```

---

## 三、坐标约定

### 3.1 Point 语义

```
Point.X = 行 (row, north-south)
Point.Y = 列 (col, east-west)
```

### 3.2 WalkMap / SafezoneMap / AIgrid 访问

**全系统统一使用 `[X, Y]` 即 `[row, col]`**:

```csharp
WalkMap[target.X, target.Y]      // WalkToAsync — ✅ 统一
SafezoneMap[pos.X, pos.Y]        // IsAtSafezone — ✅ 统一
AIgrid[px, py]                   // PlaceAtGate — ✅ 统一
```

**已禁止的旧写法**（2026-07-23 全部修复）:
```csharp
// ❌ WalkMap[target.Y, target.X] — 旧互补系统，已废弃
// ❌ SafezoneMap[obj.Position.Y, obj.Position.X] — 已修复
// ❌ AIgrid[step.To.Y, step.To.X] — 已修复
```

### 3.3 Gate 坐标

```
ExitGate: X1=行, Y1=列
EnterGate: X1/X2=行, Y1/Y2=列
PlaceAtGate: px=gate.X1(行), py=gate.Y1(列)
```

---

## 四、地形数据来源

### 4.1 服务端嵌入资源

`src/Persistence/Initialization/Resources/Terrain{N}.att`

来源：客户端 EncTerrain 解密 → 转置 → 写入服务器格式

### 4.2 DB 存储

`config.GameMapDefinition.TerrainData` (bytea, 65539 字节)

与嵌入资源一致。服务器启动时通过 `UpdateTerrainFromResources` 从嵌入资源加载。

### 4.3 编辑器显示约定

编辑器使用 `terrain[col * 256 + row]`（列优先），屏幕显示需要转置 `screen(row, col)`。

---

## 五、完整修复清单 (2026-07-23)

| 类别 | 修复 | 文件数 |
|------|------|:--:|
| ReadTerrainData | `[x,y]→[y,x]`, 掩码 `0x5C→0x54` | 2 |
| WalkMap/SafezoneMap | `[Y,X]→[X,Y]` 全系统 | 18 |
| WarpToAsync | CurrentMap 提前清空 | 2 |
| PlaceAtGate | 3圈+10随机+回退兜底 | 2 |
| 地形来源 | EncTerrain 解密→DB+Resources | 51 地图 |
| 075_ 删除 | 统一前缀+删文件 | 7 |
