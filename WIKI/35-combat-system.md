# 战斗系统完整链路

## 处理链层次

```
网络层 HitHandlerPlugIn(TargetedSkillHandlerPlugIn)
  → 动作层 HitAction(TargetedSkillDefaultPlugin)
    → 接口层 IAttackable.AttackByAsync
      → 伤害计算层 AttackableExtensions.CalculateDamageAsync
        → 命中结算层 HitAsync / TryHit
          → 死亡处理层 OnDeathAsync
            → 经验/掉落/重生
          → 广播层 ForEachWorldObserverAsync
```

## 安全区 3 层保护

| 层次 | 文件 | 行号 | 条件 |
|------|------|------|------|
| 普攻动作层 | `HitAction.cs` | 41-45 | `player.IsAtSafezone()` → return |
| 技能动作层 | `TargetedSkillDefaultPlugin.cs` | 78-81 | `player.IsAtSafezone()` → return |
| 伤害计算层 | `AttackableExtensions.cs` | 53-56 | `defender.IsAtSafezone()` → return HitInfo(0,0) |

额外：`HitAction.cs` 47-50 行 `target.IsAtSafezone()` → return (静默忽略)

## CalculateDamageAsync 12 步公式链

```
1. 安全区检查 → defender.IsAtSafezone() → return HitInfo(0,0)
2. 命中判定 → AttackRate vs DefenseRate → 最低3%
3. 暴击/卓越/破防判定 → 随机Roll
4. 防御力计算 → defense = defender.Attributes[defenseAttr] × DefenseDecrement
5. 基础伤害获取 → GetBaseDmg: 物理/魔法/诅咒/芬里尔
6. 物理伤害终值 → 暴击取最大、卓越×1.2、普通随机
7. 魔法/诅咒终值 → 同上逻辑
8. 公用加成 → GreaterDamage、Berserker、护甲减伤、最小等级伤害、技能乘区
9. Soul Barrier → 减伤最高90%
10. PvP专用 → FinalDamageIncreasePvp
11. Combo/Double → 附加Combo伤害、双倍Roll
12. 护盾分割 → GetHitInfo: shieldRatio clamped 0-1
```

## 怪物 AI 攻击路径

```
BasicMonsterIntelligence.Timer Tick
→ ResolveTargetAsync (保持或搜索目标)
→ SearchNextTargetAsync: 距离最近 + 不在安全区 + 活跃
→ 在攻击范围? → Monster.AttackAsync(target)
  → target.AttackByAsync(monster, null, false)
  → ForEachWorldObserverAsync(ShowMonsterAttackAnimation)
  → 有AttackSkill→TryApplyElementalEffects
→ 在视野但超攻击范围? → WalkToAsync(目标附近)
→ 无目标? → RandomMoveAsync
```

## 怪物 AI 目标过滤

```csharp
// 从观察者中筛选:
a.IsActive() && !a.IsAtSafezone() && !(Player && IsInvisible)
```

## 死亡处理

### 怪物死亡
```
HitAsync → TryHit → OnDeathAsync
→ ObjectGotKilled广播 → 经验分配 → 掉落生成 → 延迟重生
```

### 玩家死亡
```
HitAsync(护盾优先) → OnDeathAsync
→ TryAdvanceTo(Dead) → StopWalking
→ ObjectGotKilled广播
→ 3秒延迟 → 重生回安全区
```

## 关键文件

| 文件 | 行号 |
|------|------|
| `src/GameLogic/AttackableExtensions.cs CalculateDamageAsync` | 50-294 |
| `src/GameLogic/PlayerActions/HitAction.cs HitAsync` | 22-72 |
| `src/GameLogic/PlayerActions/Skills/TargetedSkillDefaultPlugin.cs` | 全文件 |
| `src/GameLogic/NPC/BasicMonsterIntelligence.cs TickAsync` | 206-262 |
| `src/GameLogic/NPC/Monster.cs AttackAsync` | 118-129 |
| `src/GameLogic/NPC/AttackableNpcBase.cs AttackByAsync` | 全文件 |
| `src/GameLogic/Player.cs OnDeathAsync` | 2279 |
| `src/GameLogic/Player.cs RespawnAtAsync` | 1101 |
| `src/GameServer/MessageHandler/HitHandlerPlugInBase.cs` | 全文件 |
