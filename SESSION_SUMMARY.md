# Session 总结 — AI 系统架构重建与基础生存实现

**日期**：2026-06-24 | **状态**：Phase 1 进行中

---

## 阶段成果

### 1. 架构文档体系（全新/重写）

| 文档 | 变更 | 说明 |
|------|------|------|
| `AI_SYSTEM_ARCHITECTURE.md` | **新增** | AI 系统最高架构说明，三层架构/影子世界/DNA 系统/演化路线 |
| `WIKI/23-spatial-cognition-and-digital-map.md` | **重写** | 影子世界四层结构（地理/社会/记忆/神经），社会化 AI 公共空间 |
| `WIKI/16-ai-player-system-design.md` | **重写** | 三层架构总览，DNA 驱动初始化流程，9 技能精选规则，Phase 路线图 |
| `WIKI/28-ai-character-dna-and-growth.md` | **新增** | DNA 系统设计，三职业流派/加点公式/技能表/低等级生存策略 |
| `WIKI/29-mu-guide-for-ai.md` | **新增** | MU 新手攻略作为 AI 行为参考 |
| `CLAUDE.md` | **更新** | 顶部加入 `AI_SYSTEM_ARCHITECTURE.md` 必读提示 |

### 2. 代码修改（AIPlayer 编译 0 错误）

| 修改 | 文件 | 说明 |
|------|------|------|
| DNA 系统 | `CharacterBuildDna.cs` (新增) | 三职业完整 DNA：加点公式 + 技能ID + 攻击类型 |
| 裸体 9 级 | `AiPlayerManager.cs` | 仅药水 + 新手武器 + 精选 8 技能 + 启动金币 |
| 新手武器 | `AiPlayerManager.cs` | 按职业给基础武器（Kris/Light Spear/Short Bow） |
| 精选技能 | `AiPlayerManager.cs` | 1基础+3增强+1群攻+3Buff，去掉了拖死 AI 的几十个 BUFF |
| 技能栏初始化 | `ScriptExecutor.cs` | 构造时调用 SkillBarManager 写快捷键槽 |
| 攻击距离按职业 | `ScriptExecutor.cs` | 读取 DNA，近战 2.5 / 远程 6.0 |
| 攻击 5 连击 | `ScriptExecutor.cs` | AttackTargetAsync 每次打 5 下 |
| `is_surrounded` 条件 | `ScriptExecutor.cs` | 3格内≥4只怪物检测 |
| `map_check` 提前 | `basic_hunting_loop.json` | @quest_hunt 先 warp 再 relocate |
| `break_out` 突围 | `basic_hunting_loop.json` | attack→break_out→approach，被围就移动 |
| `buff_check` 放尾 | `basic_hunting_loop.json` | 战斗/补给/巡逻→最后加Buff |
| 多任务循环 | `basic_hunting_loop.json` | @submit_quest 完成后 `goto @start` |

---

## 核心问题与解决方案

### 问题 1：新手武器没拿到
**症状**：AI 武器槽(Slot=0)被 Bolt(弩箭)占据，无真正武器
**根因**：`starterDef` 筛选 `DropLevel == 0` 而非 `Number == 0`，Bolt(G4N7,DropLevel=0) 优先于 Short Bow(G4N0)
**修复**：改为 `Number == 0`，正确匹配各职业基础武器

### 问题 2：AI 永远到不了蜘蛛区
**症状**：9 级 AI 创建 → warp Noria → 走不到蜘蛛区 → 死在路上 → 复活 Lorencia → warp Noria → 死循环
**根因**：`@start` 检测 `no_active_quest` 立即跳 `@accept_quest`，AI 飞 Noria 后怪物太强(骷髅/森林巨人等)，裸装走不到蜘蛛区
**状态**：**未解决** — 需要修改 `@start` 段落，等级<15 时优先在 Lorencia 外围狩猎

### 问题 3：升级不加点
**症状**：AI 升级后 LevelUpPoints 未分配
**根因**：ScriptExecutor 主循环中无加点逻辑
**状态**：**待实现** — 需添加 Phase 1.8 根据 DNA 的 BuildPhase 分配属性

### 问题 4：BUFF 无限循环
**症状**：AI 学了所有非大师技能 → 几十个 BUFF 轮流转 → 永远到不了攻击节点
**修复**：精选 8 技能（1+3+1+3）+ buff_check 移到最后 ✅

### 问题 5：装备槽被药水占
**症状**：药水创建在 slot=0，占了武器槽
**修复**：药水改从 slot=12 起 ✅

### 问题 6：Sandbox 无法杀进程
**症状**：旧 dotnet 进程锁定 DLL，无法部署新代码
**方案**：每次需手动 `taskkill /f /pid X` 或重启终端

---

## 下一阶段任务

### Phase 1：基础生存（继续）

| 优先级 | 任务 | 方法 | 产出 |
|--------|------|------|------|
| 🥇 | **低等级生存** | `@start` 加等级检查，等级<15 时留在 Lorencia 外围打哥布林/蜘蛛升级 | AI 不会在 9 级冲 Noria 送死 |
| 🥇 | **升级自动加点** | ScriptExecutor Phase 1.8：`LevelUpPoints > 0` 时按 DNA BuildPhase 分配 | AI 升级自动变强 |
| 🥈 | **战斗验证** | 启动服务器 3 职业 AI 在 Lorencia 外围打怪 | A*路径 OK，包围检测 OK，Hit 日志出现 |
| 🥈 | **死亡复苏回狩猎** | @recover 段落完善：死后 warp 回原地图原位 | 死亡循环不丢进度 |
| 🥉 | **地图转场** | 等级≥15 且有装备后自动前往 Noria 做任务 | 低→中地图过渡 |

### Phase 2：角色成长

| 任务 | 说明 |
|------|------|
| 技能随等级重选 | 达到阶段阈值时重新精选 9 技能 |
| 装备自动换装 | auto-equip 链路完善，掉落→鉴定→换装 |
| 多任务链 | 完成后自动检查接取下一个任务 |
| 数字地图社会层 | 帮会暗号、秘密堂口、群体任务部署 |

### Phase 3：群体智能

| 任务 | 说明 |
|------|------|
| 影子世界驱动决策 | AI 通过读取信息素决定站位/路线 |
| 群体经验汇聚 | 经验规则自动注入规则库 |
| 帮会系统 | AI 组队、领地分配、协作狩猎 |

---

## 快速启动

```bash
# 重启终端后首次部署
cd /d E:\mu_ai\MU_VER_1\SERVERS\OPENMU
dotnet run --project src/Startup/MUnique.OpenMU.Startup.csproj -p:ci=true -- --autostart -demo -resolveIP:local

# 30秒后创建 3 职业 AI
curl "http://localhost/api/ai/create?name=Knight&classId=4&mapId=0&mode=1d"
curl "http://localhost/api/ai/create?name=Wizard&classId=0&mapId=0&mode=1d"
curl "http://localhost/api/ai/create?name=Elf&classId=8&mapId=0&mode=1d"

# 查看 AI 状态
curl -s http://localhost/api/ai/state | python3 -c "import json,sys;d=json.load(sys.stdin);[print(f'{p[\"name\"]}: lv={p[\"level\"]} map={p[\"mapId\"]} pos=({p.get(\"x\",\"?\")},{p.get(\"y\",\"?\")}) sp={p.get(\"scriptPosition\",\"?\")}') for p in d['players']]"
```
