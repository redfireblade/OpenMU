# AI 角色 DNA 与成长系统

## 概述

每个 AI 角色在创建时被赋予一份「DNA」—— 一个结构化的角色构建蓝图，决定其职业定位、加点策略、技能选择和行为模式。

## DNA 定义

DNA 包含以下核心字段：

| 字段 | 类型 | 说明 |
|------|------|------|
| `ClassName` | string | 流派名（如「血牛战士」「智敏法师」） |
| `BaseClassNumber` | int | 基础职业编号 |
| `Direction` | BuildDirection | 发展方向枚举 |
| `AttackType` | Melee/Ranged | 近战/远程 |
| `AttackRange` | float | 攻击距离（近战 2.5，远程 6.0） |
| `CoreSkillIds` | short[] | 核心技能编号（最多 9 个） |
| `Phases` | BuildPhase[] | 加点阶段方案 |

## 9 技能精选规则

每个角色最多保留 9 个技能，按角色当前阶段的等级筛选：

| 类别 | 数量 | 选择策略 |
|------|------|----------|
| 基础攻击 | 1 | `SkillType.DirectHit`，冷却最短、攻击力最高的单攻技能 |
| 刷怪群攻 | 1 | `SkillType.AreaSkill*`，范围最大的群攻技能 |
| 强力输出 | 3 | `SkillType.DirectHit`，攻击力前 3 的单攻技能（PK/Boss） |
| 辅助增益 | 3 | `SkillType.Buff`，最有用的增益技能 |
| 备用/被动 | 1 | `SkillType.PassiveBoost` 或 `SkillType.Regeneration` |

## 三基础职业 DNA

### 战士 (Dark Knight) — 血牛战士

- **攻击类型**：近战 / 攻击距离 2.5
- **加点方案**：
  - 1-40 级：2力 1敏 2体（力量够穿装备，敏捷够命中，其余全体力）
  - 41-70 级：1力 1敏 3体
  - 71 级+：全体力
- **核心技能**：地裂斩(62)、升龙击(63)、生命之光(64)
- **战斗方式**：贴近目标→地裂斩群攻→血厚站桩

### 法师 (Dark Wizard) — 智敏法师

- **攻击类型**：远程 / 攻击距离 6.0
- **加点方案**：
  - 1-40 级：3智 2敏（智力加魔攻，敏捷加防御/施法速度）
  - 41-70 级：4智 1敏
  - 71 级+：4智 1敏
- **核心技能**：Fire Blast(74)、黑龙波(30)、陨石(56)
- **战斗方式**：远程 Fire Blast 单点 → 黑龙波群刷 → 守护之魂保命

### 弓箭手 (Fairy Elf) — 敏弓

- **攻击类型**：远程 / 攻击距离 6.0
- **加点方案**：
  - 1-40 级：1力 4敏（力量仅够拿弓，敏捷加攻击/防御/攻速）
  - 41-70 级：1力 9敏
  - 71 级+：全敏
- **核心技能**：多重箭(47)、冰封箭(48)、穿透箭(49)
- **战斗方式**：远程多重箭群攻 → 高攻速风筝

## 初始化流程

```
CreateAiPlayerAsync
  → SelectDNA(classId, direction)        # 选取 DNA
  → CreatePlaceholderData(dna)            # 创建角色（药水 + 新手武器 + DNA 加点）
  → SkillSelector.Select(dna, level)      # 精选 9 技能
  → AiPlayerLogic(dna)                    # 启动行为引擎
    → ScriptExecutor(dna.AttackRange)     # 设置攻击距离
    → SkillBarManager(dna.CoreSkillIds)   # 初始化技能栏
```

## 低等级生存策略（1-15 级）

新手期 AI 采取保守策略：

1. **起始地图**：勇者大陆 (map 0) 安全区
2. **战斗范围**：安全区外围 15 格内，不深入
3. **目标选择**：只攻击等级 ≤ 自身等级 + 2 的怪物
4. **HP 管理**：HP < 60% 立即喝药，HP < 30% 撤回安全区
5. **装备获取**：捡取所有金币和掉落 → auto-equip 换装
6. **升级路径**：1-15 级在勇者大陆外围（蜘蛛、牛怪、哥布林）
7. **转场条件**：等级 ≥ 15 且背包有武器 → 前往仙踪林(Noria)做任务
