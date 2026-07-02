# AI 材料需求链路分析 — 数据库到决策的完整链条

> 版本: 1.0
> 日期: 2026-06-17
> 目的: 分析从数据库表（怪物掉落/合成配方）到 AI 角色决策（打什么怪/刷什么材料）的完整数据链路

---

## 一、数据源头：游戏配置数据库表

MU 服务器在 `GameConfiguration` 内存对象中维护了完整的游戏配置数据，来源于数据库/配置文件。以下是 AI 决策相关的核心数据表：

### 1.1 掉落表链
```
GameConfiguration.DropItemGroups
  ├── ItemLevel (byte) — 材料等级（如恶魔眼 Lv.1~7）
  ├── MinimumMonsterLevel (byte) — 掉落该材料所需的最低怪物等级
  ├── MaximumMonsterLevel (byte) — 掉落该材料的最高怪物等级
  ├── Monster (MonsterDefinition?) — 若不为 null，仅该怪物掉落
  └── PossibleItems (List<ItemDefinition>)
        ├── Group (int) — 物品组（如 14=恶魔眼/恶魔钥匙）
        └── Number (int) — 物品编号（如 17=恶魔眼, 18=恶魔钥匙）
```

### 1.2 怪物表链
```
GameConfiguration.Monsters (List<MonsterDefinition>)
  ├── Number (short) — 怪物编号
  ├── Designation — 怪物名称
  ├── Attributes — 怪物属性（含 Stats.Level）
  ├── DropItemGroups (List<DropItemGroup>) — 该怪物专属掉落
  └── ItemCraftings (List<ItemCrafting>) — 该怪物（NPC）的合成配方

GameConfiguration.Maps (List<GameMapDefinition>)
  └── MonsterSpawns (List<MonsterSpawnArea>)
        └── MonsterDefinition — 该地图刷新的怪物
```

### 1.3 合成配方表链
```
MonsterDefinition.ItemCraftings (List<ItemCrafting>)
  ├── Number — 配方编号
  └── SimpleCraftingSettings
        ├── RequiredItems (List<RequiredItem>)
        │     ├── PossibleItems (List<ItemDefinition>) — 所需材料之一
        │     └── MinimumAmount — 所需数量
        └── ResultItems (List<ResultItem>)
              └── ItemDefinition — 产出物品
```

### 1.4 活动门票配置
```
GameConfiguration.MiniGameDefinitions
  ├── Type (MiniGameType) — 活动类型（DevilSquare/BloodCastle/ChaosCastle）
  ├── GameLevel — 活动等级（1~7）
  ├── TicketItem (ItemDefinition?) — 门票物品
  ├── TicketItemLevel (byte) — 门票所需材料等级
  ├── EntranceFee (int) — 入场费
  ├── MinimumCharacterLevel — 最低角色等级
  └── MaximumCharacterLevel — 最高角色等级
```

---

## 二、当前 AI 数据链路实现

### 2.1 已实现的链路

```
[查询角色背包/仓库]
     ↓ Inventory.Items / Account.Vault.Items
ItemNeedAnalyzer
     ↓ ScavengeForCrafting() 
检查：翅膀材料？混沌武器材料？装备升级材料？
     ↓ 硬编码 (Group, Number)
生成 craft_* 任务 → CraftingModule 执行合成

[查询游戏配置]
     ↓ GameConfiguration.DropItemGroups + MonsterSpawns
MaterialKnowledgeService
     ↓ GetDropSources(itemGroup, itemNumber)
返回：怪物#520 @ 地图#69 掉落材料 G13N16 Lv.3
     ↓
InjectFarmTicketMission
     ↓ 写入 MissionItem.Context
MaterialFarmModule 执行：走去地图#69 → 打怪物#520 → 检查背包
```

### 2.2 缺失的链路

```
当前缺失：
ItemNeedAnalyzer 发现 "我需要合成翅膀"
  → 需要 Feather of Condor(13,11)
  → 背包没有 → 需要去刷
  → 但 ItemNeedAnalyzer 不会调用 MaterialKnowledgeService 查掉落
  → 也不会生成 farm_* 任务 → AI 永远不会主动打翅膀材料

MaterialFarmModule 知道 "怎么刷材料"
  → 但它需要 MissionItem.Context 提供 ItemGroup/ItemNumber/MonsterNumber/MapNumber
  → 需要上层调用方提供这些参数
  → 当前只有 InjectFarmTicketMission 提供（仅限门票）
```

---

## 三、核心架构缺失：材料需求知识库

### 3.1 需求

需要一个统一的 **材料需求知识库（MaterialRequirementService）**，整合以下能力：

| 能力 | 说明 | 当前状态 |
|------|------|----------|
| 查询合成配方 | 从 ItemCraftings 读取所需材料 | ✅ MaterialKnowledgeService + MaterialRequirementService |
| 检查背包库存 | 背包中有什么材料、多少数量 | ✅ MaterialRequirementService.AnalyzeTarget |
| 检查仓库库存 | 仓库中有什么材料 | ✅ MaterialRequirementService.AnalyzeTarget |
| 掉落来源查询 | 知道什么怪物掉什么材料 | ✅ MaterialKnowledgeService 完整 |
| 等级匹配 | 选择玩家能刷的最高可用材料等级 | ✅ MaterialRequirementService.FindBestDropSource |
| 生成 farm 任务 | 缺失材料 → 注入 farm_* 任务 | ✅ MissionBoardService Phase 6（全配方覆盖） |
| 生成 craft 任务 | 材料已够 → 注入 craft_* 任务 | ✅ CraftingModule |
| **看板初始化时自动分析** | 登录时注入 farm_mat_* 任务到看板 | ✅ **MaterialRequirementService + BoardState.MaterialNeeds** |

### 3.2 统一决策流程

```
ItemNeedAnalyzer / EventInterruptService
     ↓ "我需要物品 X（目标）"
MaterialRequirementService
     ↓ "物品 X 需要材料 A(level=L1), B(level=L2)"
     ↓ "背包有 A，仓库有 B，但等级不对"
     ↓ "A Lv.1 够了，B 需要 Lv.3 但背包只有 Lv.2"
     ↓ 查 DropItemGroups → "B Lv.3 由怪物#412 @地图#33 掉落，MinMonLv=68"
     ↓ 查玩家等级=50 → 不够打
     ↓ "降级：B Lv.2 由怪物#306 @地图#33 掉落，MinMonLv=45"
     ↓ "等级够了！生成 farm 任务"
分发 farm_* 任务 → MaterialFarmModule 执行

材料收集完 → craft_* 任务 → CraftingModule 执行合成
```

---

## 四、需要新建的组件

### 4.1 MaterialRequirementService（新）

```
class MaterialRequirementService {
  // 核心方法
  List<MaterialGap> AnalyzeGaps(TargetItem target);
  // 返回 [{Material: 材料A, Status: InInventory}, {Material: 材料B, Status: NeedFarm, DropSource: ...}]
  
  // 辅助
  List<MaterialItem> GetRequiredMaterials(ItemDefinition targetItem);
  // 查 ItemCraftings → 合成所需材料列表（递归深入原材料）
  
  MaterialStatus CheckInventory(SpanningPlayer player, MaterialItem material);
  // InInventory | InVault | NeedFarm | NeedCraftFirst
  
  DropSourceInfo? FindBestDropSource(MaterialItem material, int playerLevel);
  // 调 MaterialKnowledgeService.GetBestTicketMaterialDropInfo 泛化版
}
```

### 4.2 MaterialRequirementService 的数据结构

```csharp
/// <summary>目标物品定义，如"我要一个恶魔广场门票"或"我要一个翅膀"</summary>
public record TargetItem(int ItemGroup, int ItemNumber, byte TargetLevel = 0);

/// <summary>材料需求 — 合成一件物品需要的某个原材料</summary>
public record MaterialItem(
    int Group, int Number, string Name,
    byte RequiredLevel, int RequiredCount);

/// <summary>材料缺口分析结果</summary>
public record MaterialGap(
    MaterialItem Material,
    MaterialStatus Status,
    int CurrentCount,
    int CurrentLevel,
    DropSourceInfo? DropSource);

/// <summary>材料状态枚举</summary>
public enum MaterialStatus {
    InInventory,      // 背包中有足够的
    InVault,          // 仓库中有，需取
    NeedFarm,         // 无处可得，需要刷
    NeedCraftFirst,   // 需要先合成中间产物
    LevelTooLow,      // 等级不够刷
}

/// <summary>掉落来源（复用现有 DropSourceInfo）</summary>
public record DropSourceInfo(
    short MonsterNumber, string MonsterName,
    ushort MapNumber, string MapName, byte ItemLevel);
```

### 4.3 各模块改造

| 模块 | 改动 | 状态 |
|------|------|------|
| **ItemNeedAnalyzer** | `ScavengeForCrafting()` → 不再只硬编码查背包，改为调用 MaterialRequirementService.AnalyzeGaps() | ❌ 待做 |
| **InjectFarmTicketMission** | 注入 farm 任务的逻辑提取到 MaterialRequirementService，供所有合成场景复用 | ❌ 待做 |
| **EventInterruptService** | `GetEventReadiness()` 的 NeedFarm/NeedTicket 分支 → 使用新服务的 AnalyzeGaps 增强判断 | ❌ 待做 |
| **CraftingModule** | 合成时若有材料等级不匹配 → 调用 MaterialRequirementService 生成 farm_* 任务 | ❌ 待做 |
| **MaterialFarmModule** | 不改（它已经通用化了，只需要 Context 参数） | ✅ |
| **MaterialRequirementService** | 新建 ⭐ | ✅ **已完成** |
| **MissionBoardService Phase 6** | `InitializeAsync()` 结束时扫描材料缺口注入看板 | ✅ **已完成** |
| **BoardState.MaterialNeeds** | 看板新增材料需求知识集合 | ✅ **已完成** |

---

## 五、数据流举例：恶魔广场门票全链路

```
阶段0: EventWatcher 检测到 Devil Square Lv.2 开放
    ↓ EventOpenEvent
阶段1: EventInterruptService.GetEventReadiness()
    ↓ 检查背包：无门票 → 检查材料：无恶魔眼 + 无恶魔钥匙
    ↓ NeedFarm
阶段2: InjectFarmTicketMission()
    ↓ MaterialKnowledgeService.GetTicketMaterialDropInfo(DevilSquare, 2, playerLevel=50)
    ↓ 分析: 恶魔眼(14,17) Lv.2 由怪物#39@地图#13掉落，MinMonLv=30
    ↓ 输出: TicketDropInfo(Group=14, Number=17, Level=2, Monster=#39, Map=#13)
阶段3: MissionItem farm_ticket_DevilSquare_2
    ├── Module: "material_farm"
    └── Context: { ItemGroup=14, ItemNumber=17, TargetLevel=2, MonsterNumber=39, MapNumber=13 }
阶段4: MaterialFarmModule.ExecuteStepAsync()
    ├── CheckMap: 是否在#13? 不在 → 设置 TargetMapNumber=13 → 跨地图传送
    ├── FindMonster: 找#39 → 找不到 → 巡逻
    ├── AttackMonster: 找到 → 连续攻击
    └── CheckInventory: 每10tick检查背包G14N17 Lv.2 ≥ 1 → 满足 → Completed
阶段5: farm_ticket 完成 → 事件任务的前置依赖解除
    ↓ 事件任务变为候选
阶段6: EventExecutorModule.ExecuteStepAsync()
    ├── CheckTicket: 无门票但背包有材料 → HandleMissingTicket
    ├── HandleMissingTicket: 注入 craft_ticket_DevilSquare_2
    └── CraftingModule: 去ChaosGoblin → 合成 → 门票成功
阶段7: EventExecutorModule.ExecuteStepAsync() 再次
    ├── CheckTicket: 有门票 → EnterMiniGameAction
    └── 入场成功！
```

---

## 六、目前已有的（无需改动）

| 组件 | 覆盖场景 | 文件 |
|------|----------|------|
| MaterialKnowledgeService | 掉落来源查询（任何物品） | `MaterialKnowledgeService.cs` |
| MaterialKnowledgeService | 合成材料提取（从配置） | `MaterialKnowledgeService.cs` |
| MaterialFarmModule | 定向刷材料（通用） | `MaterialFarmModule.cs` |
| CraftingModule | 合成执行（通用） | `CraftingModule.cs` |
| InjectFarmTicketMission | 门票材料注入 | `HeartbeatService.cs` |
| ScavengeForCrafting | 翅膀/武器/装备升级检测 | `ItemNeedAnalyzer.cs` |

## 七、需要新建/改造的

| 组件 | 改动 | 文件 |
|------|------|------|
| **MaterialRequirementService** | 新建：统一的材料缺口分析 + farm 任务注入 | 新文件 |
| **ItemNeedAnalyzer** | 改造：ScavengeForCrafting 调用新服务 | `ItemNeedAnalyzer.cs` |
| **HeartbeatService** | 改造：InjectFarmTicketMission 改为调新服务泛化版 | `HeartbeatService.cs` |

---

## 八、总结

目前架构已经有：
1. ✅ **知道"什么怪掉什么材料"**（MaterialKnowledgeService.GetDropSources）
2. ✅ **知道"怎么刷材料"**（MaterialFarmModule — 通用）
3. ✅ **知道"怎么合成"**（CraftingModule — 通用）

缺失的是：
4. ❌ **缺一个统一入口问"我要做物品 X，我还缺什么材料？"** — 这就是 MaterialRequirementService
5. ❌ **缺从"缺材料"到"刷材料"的自动任务链** — 当前只有门票走通了全链路

**优先实现 MaterialRequirementService + 泛化 InjectFarmTicketMission，然后接入 ItemNeedAnalyzer.ScavengeForCrafting。**
