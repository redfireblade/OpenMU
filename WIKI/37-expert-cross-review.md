# 交叉审查报告 — 程序专家

## 核心结论

**当前 OpenMU 架构是为"有限数量的 Monster + 人类玩家"设计的，不是为"大量自主 AI 角色"设计的。**

## 最关键的交叉发现

### 1. AI角色被绑定在"怪物框架"内（严重）

```
当前架构: 只有 Monster 实现了 IAttackable + IAttacker 组合
→ AI角色无法使用完整玩家技能系统（怪物技能仅单效果）
→ AI角色无法使用玩家背包、装备、物品
→ AI角色的视野完全依赖 IBucketMapObserver（Monster不实现）
→ AI角色无法进行交易、组队、聊天
```

**建议**: 创建独立的 `AiPlayer` 类，实现 `IAttackable, IAttacker, IBucketMapObserver, ISupportWalk, IMovable`，不是 Monster 也不是 Player。

### 2. 16步步数限制是共享瓶颈（严重）

```
Walker._currentWalkSteps = new WalkingStep[16]
→ 玩家和怪物共享同一个硬限制
→ 长距离追逐会中断
→ 无路径重算机制
```

### 3. 怪物不观察世界（严重）

```
Monster不实现 IBucketMapObserver
→ 没有 AOI 事件回调
→ 无法"看到"周围发生了什么
→ AI角色如果要感知世界，必须独立实现感知层
```

### 4. Timer 线程模型风险（重要）

```
BasicMonsterIntelligence 使用 System.Threading.Timer
→ AI Tick 在线程池线程执行
→ MoveObjectOnMapAsync 使用 AsyncLock
→ Walker 也在后台跑
→ 数百个 Timer 对线程池压力大
```

### 5. 报告间的关键矛盾（已修正）

| 误读 | 纠正 |
|------|------|
| SafezoneMap null → 全图安全 | SafezoneMap 永不为 null（CLR 初始化 false）|
| PacketPipeReaderBase 是路由 | 它是管道链最后一个环节+拆包器，路由是 MainPacketRouter |
| InfoRange 无默认值 | GameConfigurationInitializerBase 初始化为 12 |

## AI 角色技术实现建议

1. 创建独立 `AiPlayer` 类（非 Monster 非 Player）
2. 实现 `IBucketMapObserver` 获得 AOI 感知
3. 使用 `List<WalkingStep>` 替代 `new WalkingStep[16]`，移除硬限制
4. 用 `PeriodicTimer` 替代 `System.Threading.Timer`
5. 实现仇恨系统作为决策基础
6. 构建 `IAiPerception` 感知层
