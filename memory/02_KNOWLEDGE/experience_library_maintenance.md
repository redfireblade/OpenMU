# 经验库容量模型与维护机制设计

> 日期: 2026-06-18
> 状态: 生效
> 关联: [AI 经验积累与群体学习系统](ai_experience_system.md)

---

## 一、经验库设计原则

1. **越精越好，不是越多越好** — 严格控制总量，每日清理低质量经验
2. **分库分表** — 按知识类别分库，互不干扰
3. **质量评分驱动淘汰** — 每条经验有 `WorthScore`，定期清理尾部
4. **增长红线** — 每日新增 ≤ 总库容的 2%，防止膨胀
5. **定期合并提炼** — 每周自动合并同类项，每月 LLM 重新聚类

---

## 二、知识分类与容量模型（基于 mu.dvg.cn 实际游戏体系）

### 总容量: 12000 条

参考 [奇迹MU小册子](https://mu.dvg.cn/) 的全量知识体系。注意：AI 经验库积累的是 **"AI 角色真实经历过的事件和结果"**（动态行为），不是游戏设计数据（静态资料）。两者互补不冲突。

### 完整分类表

| 分类 | 英文ID | 上限 | AI 经验内容（非静态资料） | 对应 mu.dvg.cn 模块 |
|------|--------|------|--------------------------|-------------------|
| 装备经验 | `equipment_knowledge` | 1200 | 各部位装备价值评估、强化策略、掉落统计、装备选择心得 | 武器/防具/翅膀/项链/戒指/耳环(1~8代)/坐骑(1~7代) |
| 怪物经验 | `monster_knowledge` | 1500 | BOSS击杀策略、黄金怪规律、怪物属性分析、掉落规律 | 地图怪物分布图(全地图) |
| 地图经验 | `map_knowledge` | 1000 | 各图安全区/热点/路径/刷怪效率/等级要求 | 新手区→中级→高级→精英区→元素地图 |
| 合成经验 | `crafting_knowledge` | 800 | 合成配方成功率实际统计、材料路径、最佳方案 | 合成系统(翅膀/镶宝/精通/元素) |
| 职业经验 | `class_knowledge` | 1500 | 各职业加点策略、技能搭配、装备选择、发展方向 | 15大职业专题 |
| 活动经验 | `event_knowledge` | 500 | 活动参与条件、收益评估、入场时机 | 活动时间表 |
| 交易经验 | `trading_knowledge` | 600 | 市场价格趋势、热门物品识别、买卖时机 | 价格走势图 |
| 战斗经验 | `combat_knowledge` | 800 | PK策略、各职业对战经验、胜率统计 | 技能系统/PK |
| 任务经验 | `quest_knowledge` | 600 | 任务流程、奖励评估、效率路径 | 任务系统(一般/主线/日常) |
| 强化经验 | `enhance_knowledge` | 600 | 强化概率实际统计、保护策略、资源规划 | 强化系统(+15/再生/卓越/萤石) |
| 神器经验 | `artifact_knowledge` | 400 | 神器获取、强化、搭配经验 | 神器系统(7种+神器强化石) |
| 套装经验 | `set_knowledge` | 500 | 套装搭配策略、激活条件、收益评估 | 套装系统 |
| 商店经验 | `shop_knowledge` | 400 | NPC商店物品、价格记忆、划算交易 | 商店系统(NPC/X商店/瑞币) |
| 元素经验 | `element_knowledge` | 600 | 元素地图攻略、抗性需求、属性刻印 | 元素区域(阿奎拉斯/毁灭沼泽/炼狱魔宫/卡达玛哈) |
| 攻略经验 | `guide_knowledge` | 600 | AI 总结的高效玩法、自动生成的攻略 | 游戏攻略大全 |
| **合计** | | **12000** | | |

### 经验 vs 资料站的区别

| 维度 | mu.dvg.cn（静态资料） | AI 经验库（动态行为） |
|------|---------------------|-------------------|
| 装备+13成功率 | 固定概率表 | AI 实际合成记录: "我合了20次成功7次" |
| BOSS 属性 | 显示等级/血量/攻击 | "Lv120以下去打黄金龙死了3次" |
| 职业加点 | 推荐方案 + 配装模拟器 | "我智力法师加到这个比例刷怪效率最高" |
| 市场价格 | 价格走势图(每2h更新) | "我在这价位卖出过，也被人低价买走过" |
| 元素地图 | 地图说明 | "我Lv130去阿奎拉斯抗性不够被秒了" |

### 内部标识与中文对照

```
// ===== 内部标识 → 中文对照表 =====
// 参考: mu.dvg.cn 全量知识体系(装备/怪物/地图/合成/职业/技能/任务/活动/强化/神器/套装/商店/元素/攻略)

// ─── 职业 ───
// mu.dvg.cn 职业专题覆盖全15大职业，含职业技能/加点/装备/配装模拟器

// 职业标识
IntWizard       → 智力法师
ManaWizard      → 血法
SpeedWizard     → 敏法
BalancedKnight  → 平衡战士
ForceKnight     → 力血战士
BloodKnight     → 血牛
AgilityElf      → 敏弓
IntElf          → 智弓
ForceMG         → 力魔
IntMG           → 法魔
BalancedDL      → 力智圣
IntSummoner     → 打手召唤
AgilityFighter  → 敏格

// 完整职业列表（15大职业，按游戏版本）
DarkWizard      → 黑暗巫师(魔法师→魔导师→神导师)
DarkKnight      → 黑暗骑士(剑士→骑士→神骑士)
FairyElf        → 精灵(弓箭手→圣射手→神射手)
MagicGladiator  → 魔剑士→剑圣
DarkLord        → 圣导师→祭师
Summoner        → 召唤术师→召唤导师→召唤巫师
RageFighter     → 格斗家(荣光之拳)
RuneMage        → 符文法师
SwiftWind       → 疾风
DreamKnight     → 梦幻骑士
RedBattleKnight → 赤色战士
LightMage       → 光明法师
HolyScholar     → 圣光学者
Alchemist       → 炼金术士
Paladin         → 圣骑士(S21-1-1 全新职业)

// ─── 任务（参考 mu.dvg.cn quest_list.php 完整任务列表）───
// mu.dvg.cn 的任务列表分多页（page=1~30+），覆盖全等级段从Lv15到Lv800+
// 任务结构: ID + 名称 + 分类 + 接取等级 + 地图 + 奖励
// 
// 分类说明:
//   分类2 = 一般任务(杀怪/收集) — 贯穿全等级段的主线
//   分类3 = 等级任务 — 达到指定等级自动触发
//   分类4 = 转职任务 — 1次转职Lv150, 2次Lv220/400, 3次Lv400+, 4次Lv800+
//   分类5 = 地区开启 — 解锁新地图如阿卡伦(Lv300+)
//   日常任务 = 每日可重复
//   成就任务 = 特殊成就奖励
//
// 典型任务示例:
//   ID#446 "处理村庄的劫匪" 分类2  Lv15~25  幻术园
//   ID#451 "冰风谷怪物的补给线" 分类2  Lv36~45  冰风谷
//   ID#457 "清扫地下城" 分类2  Lv66~79  地下城(1~3)
//   ID#466 "掌握地下城的情况(3)" 分类2  Lv111~120  地下城(1~3)
//   ID#473 "请求继续帮助(1)" 分类2  Lv161~165  失落之塔(1~7)
//   ID#481 "持续攻夺结界(1)" 分类2  Lv190~199  失落之塔(1~7)
//   ID#200 "消灭黑暗巨人" 分类2  Lv285+  冰霜之城
//   ID#203 "达到290级" 分类3  Lv285+  坎特鲁废墟
//   ID#206 "前往阿卡伦" 分类5  Lv300+  阿卡伦
//   ID#217 "完成3次转职任务(2次转职)" 分类4  Lv400+  狼魂要塞
//   ID#1017 "完成4次转职任务(3次转职)" 分类4  Lv800+  勇者大陆
//   ID#62 "准备血色城堡竞技上需要的物品(毒步妖×100)" 分类2  Lv1000~1250  坎特鲁遗址
//   ID#65-81 恶魔广场系列（通行证获取/点数/完成奖励）分类2/6/14/15  Lv50~899  幻术园/勇者大陆

// 地图等级标识
Map_Lorencia    → 勇者大陆(新手, 0)
Map_Devias      → 冰风谷(新手, 2)
Map_Noria       → 仙踪林(新手, 3)
Map_Dungeon     → 地下城(中级, 1)
Map_LostTower   → 失落之塔(中级, 6)
Map_Atlans      → 亚特兰蒂斯(中级, 7)
Map_Tarkan      → 塔坎/死亡沙漠(高级, 8)
Map_SkyCity     → 天空之城(高级, 9)
Map_Kanturu     → 坎特鲁(高级, 10+)
Map_Ahilon      → 阿奎拉斯圣殿(元素)
Map_Ruins       → 毁灭沼泽(元素)
Map_Inferno     → 炼狱魔宫(元素)
Map_Kamahala    → 卡达玛哈地下神庙(元素)

// 装备部位标识
Weapon_Sword    → 单手剑
Weapon_Blade    → 双手剑
Weapon_Staff    → 法杖
Weapon_Bow      → 弓
Weapon_Crossbow → 弩
Armor_Helm      → 头盔
Armor_Armor     → 铠甲
Armor_Pants     → 护腿
Armor_Gloves    → 护手
Armor_Boots     → 靴子
Wing_Lv1~Lv5    → 1~5代翅膀
Accessory_Neck  → 项链(含镶宝剑链/套装剑链)
Accessory_Ring  → 戒指(含变身指环/悬石)
Accessory_Earring → 耳环(1~8代)
Mount_Lv1~Lv7   → 坐骑(1~7代，护卫：天鹰/黑王马)

// 元素属性标识
Element_Fire    → 火
Element_Water   → 水
Element_Earth   → 土
Element_Wind    → 风
Element_Dark    → 暗

// 装备品质
Quality_Normal  → 普通
Quality_Excellent → 卓越
Quality_Magic   → 镶宝
Quality_Set     → 套装
Quality_Master  → 精通
Quality_Artifact → 神器
```

### 关联 StatAllocationStrategy 现有代码

```csharp
// src/AIPlayer/StatAllocationStrategy.cs 已定义的 BuildDirection
// 与此分类的 class_knowledge 对应关系：
// class_knowledge 记录 AI 实际加点效果
// StatAllocationStrategy 是系统内置推荐方案
// AI 经验 = "我试了这个方案，刷怪效率确实高"
```

---

## 三、每日合并流程

```
每日定时触发
  ↓
Step 1: 合并过去24h原始日志 → 按(分类+维度)分组聚合
Step 2: 计算每条聚合结果的 WorthScore(0~100)
Step 3: 过滤：WorthScore < 30 的直接丢弃（低价值噪音）
Step 4: 去重：与经验库现有条目比较，相似的合并（提高置信度）
Step 5: 限流：当日新增 ≤ 总库容 × 2%（5000×2%=100条）
        如果超出，保留评分最高的前 N 条
Step 6: 淘汰：库内 WorthScore 最低的 5% 标记为过期
        过期条目再出现时更新置信度，否则 30 天后自动删除
```

---

## 四、周/月维护

| 周期 | 操作 | 方式 |
|------|------|------|
| **每日** | 合并当日日志、限流、淘汰低质量 | 自动代码执行 |
| **每周** | 合并相似条目、消除碎片 | 自动代码执行 |
| **每月** | 调用 LLM 重新聚类、提炼规则 | 上传 `library_dump.json` + LLM 分析 |

### 每周合并

遍历每个分类库，按(地图+怪物+行为类型)作为合并键：

```
合并前:                       合并后:
  hunting_lorencia_43 x3      hunting_lorencia_43 x1
  (等级不同, 成功率不同)        (平均等级+加权成功率)
```

合并策略：
- 置信度取平均值（加权样本量）
- `WorthScore` 取最后一次的计算值
- `LastUpdated` 刷新为合并时间
- 原始 3 条标记为已合并，30 天后删除

### 每月 LLM 维护

1. 导出 `library_dump.json`（5000条的摘要，含评分和置信度）
2. 调用 LLM，prompt:

```
你是一个 MU Online 游戏经验分析师。请分析以下经验库：
1. 找出相似或重复的经验条目，建议合并方案
2. 识别低价值经验（评分<30且置信度<0.5），建议删除
3. 发现经验覆盖的盲区（哪些内容应该被涵盖但没有）
4. 推荐新的规则候选（置信度高、普适性强的经验）

输出格式：合并清单 / 删除清单 / 盲区分析 / 规则推荐
```

3. 根据 LLM 建议更新经验库结构

---

## 五、质量评分公式

```
WorthScore = 成功率权重(40%) + 收益权重(30%) + 击杀效率(15%) + 死亡惩罚(15%)

成功率 = 成功次数 / 总次数          (越高越好 × 40分)
收益   = min(avgGold/10000, 1) × 30 (1万金币=30分封顶)
效率   = min(avgKills/10, 1) × 15   (10只=15分封顶)
死亡   = max(0, 1 - deathRate×5) × 15 (0死=15分，每死一次扣5分)
```

额外惩罚：
- 样本量 < 5 → 总分 × 0.5（样本不足，置信度打折）
- 样本量 = 1 → 不进入经验库（单次偶然事件）

---

## 六、文件结构

```
aiplayer_data/
  experience_db/
    library/                      ← 精炼经验库
      categories.json             ← 分类定义元数据
      map_knowledge.json          ← 地图知识 (max 500)
      monster_knowledge.json      ← 怪物知识 (max 1000)
      crafting_knowledge.json     ← 合成知识 (max 500)
      equipment_knowledge.json    ← 装备知识 (max 500)
      event_knowledge.json        ← 活动知识 (max 300)
      trading_knowledge.json      ← 交易知识 (max 500)
      combat_knowledge.json       ← 战斗知识 (max 500)
      leveling_knowledge.json     ← 升级路线 (max 500)
      shared_experience.json      ← 共享经验 (max 200)
    library_metadata.json         ← 库元数据
    raw/                          ← 原始日志（聚合后7天清理）
    aggregated/                   ← 聚合数据
    archive/                      ← 过期经验归档
```

### 库元数据格式

```json
{
  "version": 4,
  "lastDailyMaintenance": "2026-06-18T04:00:00Z",
  "lastWeeklyMerge": "2026-06-15T04:00:00Z",
  "lastMonthlyReview": "2026-06-01T04:00:00Z",
  "totalEntries": 4523,
  "totalCapacity": 5000,
  "categories": {
    "map_knowledge": { "count": 487, "max": 500 },
    "monster_knowledge": { "count": 983, "max": 1000 },
    ...
  },
  "dailyStats": {
    "date": "2026-06-18",
    "newEntries": 87,
    "prunedEntries": 23,
    "mergedEntries": 12
  }
}
```
