# 地形与坐标系统 — 问题记录与规范（含历史摘要）

> **维护**: 2026-07-21
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

## 五、归档说明

旧版完整文档已移到 `_archive/`：
- `TERRAIN-FIX.md` (旧) → `_archive/terrain-fix-v1.md` — 早期修复记录（出生在水里、地图闪烁、Portal 回弹）
- `TERRAIN-COORD-SYSTEM.md` (旧) → `_archive/terrain-coord-system-v1.md` — .att 格式详解、像素/纹理格式说明、AI BOT 通信协议
