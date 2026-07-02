# AI 人格模型与成长系统

## PersonalityProfile

AI 角色的行为偏好由 `PersonalityProfile`（`src/AIPlayer/PersonalityProfile.cs`）定义，包含 5 个维度的人格特征：

```csharp
public sealed class PersonalityProfile
{
    public double Greed { get; set; }           // 贪婪度 — 影响拾取和交易决策
    public double RiskTolerance { get; set; }    // 风险容忍 — 影响是否挑战高等级怪物
    public double Caution { get; set; }          // 谨慎度 — 影响逃跑和回城阈值
    public double Aggression { get; set; }       // 攻击性 — 影响主动攻击频率
    public double Efficiency { get; set; }       // 效率偏好 — 影响资源使用策略
}
```

### 维度说明

| 维度 | 低值 (0.0) | 高值 (1.0) | 影响范围 |
|------|------------|------------|----------|
| Greed | 只捡宝石/高级装备 | 所有物品全捡 | 拾取策略、交易决策 |
| RiskTolerance | 刷同等级或更低怪物 | 挑战 +20 级怪物 | 狩猎场选择、BOSS 挑战 |
| Caution | 血量低于 50% 才逃跑 | 血量低于 90% 即撤退 | 生存策略、回城决策 |
| Aggression | 被动等怪靠近 | 主动搜索并追击 | 巡逻路径、攻击决策 |
| Efficiency | 不在意药水消耗 | 精确控制资源使用 | 药水购买、技能选择 |

### 默认配置

```csharp
public static PersonalityProfile Balanced() => new();
// 所有维度默认为 0.0（平衡型）
```

可通过 `PersonalityProfile` 构造函数创建自定义人格：`new PersonalityProfile { Greed = 0.8, Aggression = 0.6 }`。

## 行为矩阵

AI 的行为决策通过以下矩阵组合实现：

```
Personality → RuleEngine → Script → IBehaviorSubModule
    ↑                            ↓
WorldState ←──────────────── Executor
```

- **PersonalityProfile** 影响 `RuleEngine` 中规则的条件评估权重
- 高 Aggression 的 AI 更可能选择"主动寻怪"规则
- 低 RiskTolerance 的 AI 不会选择"BOSS 挑战"规则
- 人格特征在创建后可通过 `KnowledgeAccessService` 调节

## 成长系统

### 等级门控知识

AI 角色的知识访问通过 `KnowledgeAccessService`（`src/AIPlayer/KnowledgeAccessService.cs`）实现等级门控：

```csharp
public sealed class KnowledgeAccessService
{
    public KnowledgeAccessService(AiPlayer player, CharacterMemory memory);
    public bool IsUnlocked(string key);  // 检查指定知识是否已解锁
    public IEnumerable<KnowledgeEntry> GetUnlocked();  // 获取已解锁的知识列表
}
```

门控逻辑基于 `EffectiveLevel` 计算：

```
EffectiveLevel = max(currentLevel, MaxLevelAchieved)
```

特点：
- 知识不会因重生丢失（`MaxLevelAchieved` 记录历史最高等级）
- 等级门控 + 职业限制 + 重生次数要求（三级约束）
- 不同等级区间的 AI 选择不同的狩猎策略和拾取门控

### 等级阶段

| 阶段 | 等级范围 | 行为特征 |
|------|----------|----------|
| 新手期 | 1-80 | 安全区域狩猎、低强度怪物、基本拾取 |
| 成长期 | 80-220 | 中级地图、开始参与事件、合成基础装备 |
| 高阶期 | 220-350 | 高级地图、BOOS 狩猎、高级合成 |
| 顶级期 | 350+ | 全部内容解锁、血堡/恶魔广场活跃参与 |

### 经验记忆系统

AI 角色有三种知识来源，在 `src/AIPlayer/` 中实现：

```
CharacterMemory JSON → 个人经验（地图统计、击杀统计）
    ↓ merge on startup
AccountKnowledge JSON → 跨角色共享统计
    ↑ blend
ExperienceMemory → 实时环缓冲区 → 60s 合并 → MapStats
```

- `CharacterMemory.cs` — 角色级记忆（每角色独立）
- `AccountKnowledge.cs` — 账户级知识（跨角色共享）
- `ExperienceMemory.cs` — 运行期实时记忆（环缓冲区，60秒聚合一次）
