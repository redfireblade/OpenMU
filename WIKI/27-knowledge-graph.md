# 知识图谱（Knowledge Graph）

## 概述

知识图谱是 AI Player 系统的**第四维**智能层，位于 `src/AIPlayer/KnowledgeGraph/`。它解决了现有知识库的关键缺口：**知识与知识之间没有关系**。

当前架构中，`GameKnowledgeService` 存储了 70+ 个独立事实（地图编号、怪物等级、掉落概率），`WarpPlanner` 使用 BFS 规划地图路由，`DecisionSystem` 依赖规则引擎做决策。但这些组件之间没有统一的、可推理的关系网络。

KG 将所有这些离散信息连接成一个**有向加权图**，使得以下推理成为可能：

| 问题 | 当前实现 | 加入 KG 后的解法 |
|------|----------|-----------------|
| "怎么最快合成翅膀？" | 需多步查表，手动关联 | 一次图查询：`OptimalCraftingChain(startMaterial, targetWing)` |
| "哪个怪最好掉祝福？" | 遍历 `DropSources`，线性搜索 | 图查询：`FindAllReachable(item, DropsAt, weight=1/dropRate)` |
| "任务5的前置要求？" | 手动查阅 QuestDefinition | 向后解析依赖链：`ResolveDependencies(quest5, backward)` |
| "去失乐之塔怎么走？" | WarpPlanner BFS 仅用门图 | 多约束寻路（等级+金币+传送阵+门图混合） |

### 架构定位

```
GameKnowledgeService (静态事实)
       │ 读取 GameConfiguration
       ▼
KnowledgeGraphBuilder ──→ KnowledgeGraph (有向加权图)
       │                        │
       │                        ├──→ QueryEngine (路径/可达/依赖)
       │                        ├──→ DecisionSystem (任务验证)
       │                        ├──→ WarpPlanner (多约束路由)
       │                        ├──→ ScriptExecutor (KG 条件)
       │                        └──→ DynamicMissionGenerator (材料规划)
       │
RuntimeLearner ──→ 经验修正权重 → 周期刷新 → HeartbeatService
```

---

## 1. 图数据模型

### 1.1 节点类型（NodeType）

每个 KG 节点代表一个游戏实体。类型编码共 9 类：

```csharp
// src/AIPlayer/KnowledgeGraph/NodeType.cs
public enum NodeType : byte
{
    Map             = 1,   // 地图 (如 0=勇者大陆, 2=冰风谷)
    Monster         = 2,   // 怪物 (如 0=蜘蛛, 74=死亡骑士)
    Item            = 3,   // 物品 (如 (12,15)=祝福宝石)
    Npc             = 4,   // NPC (如 238=混沌哥布林)
    Quest           = 5,   // 任务 (如 (0,1)=寻找卡隆)
    MiniGameEvent   = 6,   // 小游戏事件 (如 恶魔广场1级)
    Skill           = 7,   // 技能 (如 10=能量球)
    PlayerClass     = 8,   // 职业 (如 0=黑暗骑士, 16=魔剑士)
    CraftingRecipe  = 9,   // 合成配方 (按 ItemCrafting 定义)
}
```

### 1.2 节点 ID 方案

每个节点使用复合 `NodeId`（`long` 类型），编码规则：

```csharp
// src/AIPlayer/KnowledgeGraph/NodeId.cs
public readonly record struct NodeId(long Value)
{
    private const int TypeShift = 56;

    public NodeType Type => (NodeType)((ulong)Value >> TypeShift);
    public int DomainId => (int)(Value & 0x00FFFFFFFFFFFFFF);

    /// <summary>构造复合 ID: (type << 56) | domainId</summary>
    public static NodeId Create(NodeType type, int domainId)
    {
        if (domainId < 0 || domainId > 0x00FFFFFFFFFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(domainId));
        return new NodeId(((long)type << TypeShift) | (uint)domainId);
    }

    /// <summary>从类型+组合键构造 (如物品的 (group, number))</summary>
    public static NodeId Create(NodeType type, int key1, int key2) =>
        Create(type, (key1 << 16) | (key2 & 0xFFFF));
}
```

编码示例：

| 实体 | Type | domainId | NodeId (hex) |
|------|------|----------|-------------|
| 勇者大陆 (Map 0) | Map=1 | 0 | 0x01_0000000000000000 |
| 蜘蛛 (Monster 0) | Monster=2 | 0 | 0x02_0000000000000000 |
| 祝福宝石 (12,15) | Item=3 | 0x000C000F | 0x03_0000000C000F |
| 混沌哥布林 (Npc 238) | Npc=4 | 238 | 0x04_000000000000EE |
| 1转任务 (0,1) | Quest=5 | 0x00000001 | 0x05_000000000000001 |
| 恶魔广场1级 | MiniGameEvent=6 | 0x00000001 | 0x06_000000000000001 |
| 能量球 (Skill 10) | Skill=7 | 10 | 0x07_00000000000000A |

### 1.3 边类型（EdgeType）与语义

```csharp
// src/AIPlayer/KnowledgeGraph/EdgeType.cs
public enum EdgeType : byte
{
    // ─── 地图连接 ───
    ConnectsTo,       // Map ──→ Map: 通过 EnterGate 可步行穿越
    WarpMenuTo,       // Map ──→ Map: 通过传送菜单 (消耗金币)

    // ─── 地图-怪物-NPC ───
    SpawnsOn,         // Monster ──→ Map: 怪物在该地图刷新
    LocatedOn,        // Npc ──→ Map: NPC 位于该地图

    // ─── 掉落 ───
    DropsAt,          // Monster ──→ Item: 怪物掉落物品 (权重=1/dropRate)

    // ─── 合成 ───
    CraftsInto,       // Recipe ──→ Item: 此配方的产出物品
    RequiresMaterial, // Recipe ──→ Item: 配方需要此材料
    HasRecipe,        // Npc ──→ Recipe: NPC 提供此合成配方

    // ─── 任务 ───
    RequiresQuest,    // Quest ──→ Quest: 前置任务
    StartsQuest,      // Npc ──→ Quest: NPC 接取任务
    RequiresKill,     // Quest ──→ Monster: 任务需要击杀怪物
    RequiresItem,     // Quest ──→ Item: 任务需要提交物品
    RewardsItem,      // Quest ──→ Item: 任务奖励物品
    RequiresLevel,    // Quest ──→ (隐含): 等级要求 (编码在边属性中)
    RequiresClass,    // Quest ──→ PlayerClass: 职业要求

    // ─── 事件 ───
    TicketFor,        // Item ──→ MiniGameEvent: 门票物品

    // ─── 交易 ───
    SellsItem,        // Npc ──→ Item: NPC 出售物品
}
```

### 1.4 节点与边数据结构

```csharp
// src/AIPlayer/KnowledgeGraph/GraphNode.cs
public sealed class GraphNode
{
    public NodeId Id { get; }
    public string Name { get; }
    public NodeType Type => Id.Type;
    public int DomainId => Id.DomainId;
    public Dictionary<string, object> Attributes { get; } = new();

    // 对于 Map: Attributes["levelRange"] = "1-100", Attributes["expMultiplier"] = 1.0
    // 对于 Monster: Attributes["level"] = 74, Attributes["attackRange"] = 4
    // 对于 Item: Attributes["dropLevel"] = 40, Attributes["group"] = 12, Attributes["number"] = 15
}

// src/AIPlayer/KnowledgeGraph/GraphEdge.cs
public sealed class GraphEdge
{
    public NodeId Source { get; }
    public NodeId Target { get; }
    public EdgeType Type { get; }
    public double Weight { get; }     // 正向权重（寻路用）
    public double InverseWeight { get; } // 反向权重
    public Dictionary<string, object> Attributes { get; } = new();

    // 对于 ConnectsTo: Attributes["gateNumber"] = 1, Attributes["minLevel"] = 0
    // 对于 DropsAt: Attributes["dropRate"] = 0.01, Weight = 100 (1/0.01)
    // 对于 RequiresMaterial: Attributes["requiredCount"] = 2
    // 对于 SellsItem: Attributes["price"] = 10000
}
```

### 1.5 权重模型

权重决定寻路时"最短路径"的含义：

| 边类型 | 权重公式 | 含义 |
|--------|----------|------|
| `ConnectsTo` | `1` (常数) | 每跳成本为1，最短路径=最少换图次数 |
| `WarpMenuTo` | `goldCost / 10000` | 越贵的传送阵成本越高 |
| `DropsAt` | `1 / dropRate` | 掉落率越高权重越低（逆比） |
| `RequiresMaterial` | `1` | 每个材料一步依赖 |
| `RequiresQuest` | `1` | 每个前置任务一步依赖 |
| `RequiresLevel` | `(requiredLevel / playerLevel)^2` | 等级差距越大权重越高 |
| 阻塞边 | `double.PositiveInfinity` | 不可通行（等级/职业不满足） |

当 `Weight = double.PositiveInfinity` 时，该边在 `QueryConstraints` 中被过滤。

---

## 2. 图构建（静态）

### 2.1 KnowledgeGraphBuilder

`KnowledgeGraphBuilder` 是构建期的核心组件。它在服务器启动时运行一次，从 `GameConfiguration` + `GameKnowledgeService` 读取原始数据，生成完整的有向图。

```csharp
// src/AIPlayer/KnowledgeGraph/KnowledgeGraphBuilder.cs
public sealed class KnowledgeGraphBuilder
{
    private readonly KnowledgeGraph _graph = new();
    private readonly GameConfiguration _config;
    private readonly GameKnowledgeService _knowledge;

    public KnowledgeGraphBuilder(GameConfiguration config, GameKnowledgeService knowledge)
    {
        this._config = config;
        this._knowledge = knowledge;
    }

    public KnowledgeGraph Build()
    {
        this.AddMapNodes();
        this.AddMonsterNodes();
        this.AddItemNodes();
        this.AddNpcNodes();
        this.AddQuestNodes();
        this.AddMiniGameEventNodes();
        this.AddSkillNodes();
        this.AddPlayerClassNodes();
        this.AddCraftingRecipeNodes();

        this.BuildMapConnectivity();         // ConnectsTo + WarpMenuTo
        this.BuildMonsterSpawns();            // SpawnsOn
        this.BuildDropRelations();            // DropsAt
        this.BuildCraftingDependencies();     // CraftsInto + RequiresMaterial + HasRecipe
        this.BuildQuestDependencies();        // RequiresQuest + StartsQuest + RequiresKill + RequiresItem + RewardsItem
        this.BuildNpcServices();              // LocatedOn + SellsItem
        this.BuildMiniGameTicketRelations();  // TicketFor
        this.BuildWarpMenuRelations();        // WarpMenuTo from WarpList

        return this._graph;
    }
}
```

### 2.2 地图连接构建（Connectivity）

从 `GameConfiguration.Maps[*].EnterGates` 读取门传送关系，从 `WarpList` 读取传送菜单关系：

```csharp
private void BuildMapConnectivity()
{
    // Phase 1: EnterGates → ConnectsTo 边
    foreach (var map in this._config.Maps)
    {
        if (map?.EnterGates is null) continue;

        foreach (var gate in map.EnterGates)
        {
            if (gate?.TargetGate?.Map is null) continue;

            var from = NodeId.Create(NodeType.Map, map.Number);
            var to = NodeId.Create(NodeType.Map, gate.TargetGate.Map.Number);

            this._graph.AddEdge(new GraphEdge(
                Source: from,
                Target: to,
                Type: EdgeType.ConnectsTo,
                Weight: 1,
                Attributes: new() { ["gateNumber"] = gate.Number,
                                    ["minLevel"] = gate.LevelRequirement }));
        }
    }

    // Phase 2: WarpList → WarpMenuTo 边
    foreach (var warp in this._config.WarpList ?? Enumerable.Empty<WarpInfo>())
    {
        if (warp?.Gate?.Map is null) continue;

        var from = NodeId.Create(NodeType.Map, 0); // Warp 通常从勇者大陆出发
        var to = NodeId.Create(NodeType.Map, warp.Gate.Map.Number);

        this._graph.AddEdge(new GraphEdge(
            Source: from,
            Target: to,
            Type: EdgeType.WarpMenuTo,
            Weight: warp.Costs / 10000.0,
            Attributes: new() { ["goldCost"] = warp.Costs,
                                ["minLevel"] = warp.LevelRequirement }));
    }
}
```

### 2.3 掉落关系构建

从 `DropItemGroups` 读取怪物→物品的掉落映射：

```csharp
private void BuildDropRelations()
{
    foreach (var dropInfo in this._knowledge.DropSources)
    {
        var monsterNode = NodeId.Create(NodeType.Monster, dropInfo.MonsterNumber);
        var itemNode = NodeId.Create(NodeType.Item, dropInfo.ItemGroup, dropInfo.ItemNumber);

        // 权重 = 1 / dropRate (高概率=低权重=优先考虑)
        var weight = dropInfo.DropRate > 0 ? 1.0 / dropInfo.DropRate : double.MaxValue;

        this._graph.AddEdge(new GraphEdge(
            Source: monsterNode,
            Target: itemNode,
            Type: EdgeType.DropsAt,
            Weight: weight,
            Attributes: new() { ["dropRate"] = dropInfo.DropRate,
                                ["mapNumber"] = (int)dropInfo.MapNumber }));
    }
}
```

### 2.4 合成依赖构建

从 `ItemCraftings` 读取配方依赖。每个配方是一个 `CraftingRecipe` 节点：

```csharp
private void BuildCraftingDependencies()
{
    foreach (var monster in this._config.Monsters)
    {
        if (monster?.ItemCraftings is null) continue;

        foreach (var crafting in monster.ItemCraftings)
        {
            if (crafting is null) continue;

            var recipeId = crafting.GetHashCode(); // 或使用持久标识
            var recipeNode = NodeId.Create(NodeType.CraftingRecipe, recipeId);
            var npcNode = NodeId.Create(NodeType.Npc, monster.Number);

            // Npc → HasRecipe → Recipe
            this._graph.AddEdge(new GraphEdge(
                Source: npcNode, Target: recipeNode,
                Type: EdgeType.HasRecipe, Weight: 1));

            // Recipe → CraftsInto → ResultItem
            if (crafting.ResultItemDefinition?.ItemDefinition is { } resultItem)
            {
                var resultNode = NodeId.Create(NodeType.Item, resultItem.Group, resultItem.Number);
                this._graph.AddEdge(new GraphEdge(
                    Source: recipeNode, Target: resultNode,
                    Type: EdgeType.CraftsInto, Weight: 1));
            }

            // Recipe → RequiresMaterial → MaterialItem
            foreach (var req in crafting.RequiredItems ?? Enumerable.Empty<ItemCraftingRequiredItem>())
            {
                if (req?.ItemDefinition is null) continue;

                var materialNode = NodeId.Create(NodeType.Item, req.ItemDefinition.Group, req.ItemDefinition.Number);
                this._graph.AddEdge(new GraphEdge(
                    Source: recipeNode, Target: materialNode,
                    Type: EdgeType.RequiresMaterial, Weight: 1,
                    Attributes: new() { ["requiredCount"] = req.MinimumAmount }));
            }
        }
    }
}
```

### 2.5 任务依赖构建

从 `QuestDefinitions` 读取任务间的前置关系和材料需求：

```csharp
private void BuildQuestDependencies()
{
    foreach (var monster in this._config.Monsters)
    {
        if (monster?.Quests is null) continue;

        foreach (var quest in monster.Quests)
        {
            if (quest is null) continue;

            var questNode = NodeId.Create(NodeType.Quest, (int)quest.Group, (int)quest.Number);

            // Npc → StartsQuest → Quest
            if (quest.QuestGiver is { } giver)
            {
                var npcNode = NodeId.Create(NodeType.Npc, giver.Number);
                this._graph.AddEdge(new GraphEdge(
                    Source: npcNode, Target: questNode,
                    Type: EdgeType.StartsQuest, Weight: 1));
            }

            // Quest → RequiresQuest → PrerequisiteQuest
            foreach (var prereq in quest.RequiredQuests ?? Enumerable.Empty<QuestDefinition>())
            {
                var prereqNode = NodeId.Create(NodeType.Quest, (int)prereq.Group, (int)prereq.Number);
                this._graph.AddEdge(new GraphEdge(
                    Source: questNode, Target: prereqNode,
                    Type: EdgeType.RequiresQuest, Weight: 1));
            }

            // Quest → RequiresKill → Monster
            foreach (var killReq in quest.RequiredMonsterKills ?? Enumerable.Empty<QuestMonsterKillRequirement>())
            {
                if (killReq?.Monster is null) continue;
                var monsterNode = NodeId.Create(NodeType.Monster, killReq.Monster.Number);
                this._graph.AddEdge(new GraphEdge(
                    Source: questNode, Target: monsterNode,
                    Type: EdgeType.RequiresKill, Weight: 1,
                    Attributes: new() { ["requiredCount"] = killReq.MinimumNumber }));
            }

            // Quest → RequiresItem → Item
            foreach (var itemReq in quest.RequiredItems ?? Enumerable.Empty<QuestItemRequirement>())
            {
                if (itemReq?.Item is null) continue;
                var itemNode = NodeId.Create(NodeType.Item, itemReq.Item.Group, itemReq.Item.Number);
                this._graph.AddEdge(new GraphEdge(
                    Source: questNode, Target: itemNode,
                    Type: EdgeType.RequiresItem, Weight: 1,
                    Attributes: new() { ["requiredCount"] = itemReq.MinimumNumber }));
            }

            // Quest → RewardsItem → Item
            foreach (var reward in quest.Rewards ?? Enumerable.Empty<QuestReward>())
            {
                if (reward?.ItemReward?.Definition is null) continue;
                var rewardDef = reward.ItemReward.Definition;
                var itemNode = NodeId.Create(NodeType.Item, rewardDef.Group, rewardDef.Number);
                this._graph.AddEdge(new GraphEdge(
                    Source: questNode, Target: itemNode,
                    Type: EdgeType.RewardsItem, Weight: 1,
                    Attributes: new() { ["rewardCount"] = reward.Value }));
            }
        }
    }
}
```

### 2.6 NPC 位置与服务构建

```csharp
private void BuildNpcServices()
{
    foreach (var npc in this._knowledge.Npcs.Values)
    {
        var npcNode = NodeId.Create(NodeType.Npc, npc.Number);

        // LocatedOn → Map
        var mapNode = NodeId.Create(NodeType.Map, npc.MapNumber);
        this._graph.AddEdge(new GraphEdge(
            Source: npcNode, Target: mapNode,
            Type: EdgeType.LocatedOn, Weight: 1,
            Attributes: new() { ["x"] = npc.SpawnX, ["y"] = npc.SpawnY }));
    }
}
```

### 2.7 构建统计

构建完成后输出统计信息：

```
[KnowledgeGraph] 构建完成:
  Nodes:    15 Map, 120 Monster, 520 Item, 30 Npc, 20 Quest, 8 Event,
            50 Skill, 5 Class, 12 CraftingRecipe = 780 总节点
  Edges:    35 ConnectsTo, 25 WarpMenuTo, 2000+ DropsAt,
            40 CraftsInto/RequiresMaterial, 60 Quest dependency,
            30 LocatedOn, 30 TicketFor = 2200+ 总边
  内存占用: ~2.5 MB (节点) + ~1.5 MB (边) = ~4 MB
```

---

## 3. 查询引擎

### 3.1 IKnowledgeGraphQuery 接口

```csharp
// src/AIPlayer/KnowledgeGraph/IKnowledgeGraphQuery.cs
public interface IKnowledgeGraphQuery
{
    /// <summary>
    /// 在加权图中寻找从 start 到 end 的最短路径（Dijkstra）。
    /// </summary>
    PathResult? FindShortestPath(NodeId start, NodeId end, QueryConstraints? constraints = null);

    /// <summary>
    /// 从 start 节点沿指定边类型扩展，返回所有可达节点及距离。
    /// </summary>
    IReadOnlyDictionary<NodeId, double> FindAllReachable(
        NodeId start,
        EdgeType edgeType,
        int maxDepth = 5,
        QueryConstraints? constraints = null);

    /// <summary>
    /// 解析依赖链：从 target 节点向前或向后遍历依赖边。
    /// backward=true: target 依赖什么（如任务前置、合成材料）
    /// backward=false: 什么依赖于 target（如什么配方产出此物品）
    /// </summary>
    DependencyChain ResolveDependencies(NodeId target, bool backward = true, int maxDepth = 10);

    /// <summary>
    /// 查找距离指定位置最近的满足条件的节点。
    /// </summary>
    NodeId? FindNearest(NodeId origin, EdgeType edgeType, Func<GraphNode, bool> predicate, int maxDepth = 10);
}
```

### 3.2 Dijkstra 加权最短路径

```csharp
// src/AIPlayer/KnowledgeGraph/KnowledgeGraphPathFinder.cs
internal sealed class KnowledgeGraphPathFinder
{
    private readonly KnowledgeGraph _graph;

    /// <summary>
    /// Dijkstra 算法，支持边过滤和约束。
    /// </summary>
    public PathResult? FindShortestPath(
        NodeId start, NodeId end,
        Func<GraphEdge, bool>? edgeFilter = null)
    {
        var distances = new Dictionary<NodeId, double>();
        var previous = new Dictionary<NodeId, (NodeId Node, GraphEdge Edge)>();
        var pq = new PriorityQueue<NodeId, double>();

        distances[start] = 0;
        pq.Enqueue(start, 0);

        while (pq.TryDequeue(out var current, out var currentDist))
        {
            if (current == end)
            {
                return this.ReconstructPath(previous, start, end);
            }

            if (currentDist > distances.GetValueOrDefault(current, double.MaxValue))
                continue;

            foreach (var edge in this._graph.GetOutgoingEdges(current))
            {
                if (edgeFilter?.Invoke(edge) == false) continue;
                if (double.IsPositiveInfinity(edge.Weight)) continue;

                var neighbor = edge.Target;
                var newDist = currentDist + edge.Weight;

                if (newDist < distances.GetValueOrDefault(neighbor, double.MaxValue))
                {
                    distances[neighbor] = newDist;
                    previous[neighbor] = (current, edge);
                    pq.Enqueue(neighbor, newDist);
                }
            }
        }

        return null; // 不可达
    }
}
```

### 3.3 BFS 非加权可达性

```csharp
// src/AIPlayer/KnowledgeGraph/KnowledgeGraphPathFinder.cs (续)
public IReadOnlyDictionary<NodeId, int> FindReachableBfs(
    NodeId start,
    Func<GraphEdge, bool>? edgeFilter = null,
    int maxDepth = 10)
{
    var result = new Dictionary<NodeId, int>();
    var queue = new Queue<(NodeId Node, int Depth)>();
    var visited = new HashSet<NodeId>();

    queue.Enqueue((start, 0));
    visited.Add(start);

    while (queue.TryDequeue(out var current))
    {
        if (current.Depth >= maxDepth) continue;

        foreach (var edge in this._graph.GetOutgoingEdges(current.Node))
        {
            if (edgeFilter?.Invoke(edge) == false) continue;
            if (visited.Contains(edge.Target)) continue;

            visited.Add(edge.Target);
            var depth = current.Depth + 1;
            result[edge.Target] = depth;
            queue.Enqueue((edge.Target, depth));
        }
    }

    return result;
}
```

### 3.4 依赖链解析器

```csharp
// src/AIPlayer/KnowledgeGraph/DependencyChain.cs
public sealed class DependencyChain
{
    public NodeId Root { get; init; }
    public List<DependencyLink> Links { get; init; } = new();
    public bool IsComplete { get; set; }

    /// <summary>判断所有叶子节点是否为原始资源（不由任何边产出）</summary>
    public bool AllLeavesArePrimitive =>
        this.Links.All(l => l.Children.Count == 0 || l.Direction == DependencyDirection.Forward);

    public IReadOnlyList<NodeId> LeafNodes =>
        this.Links.Where(l => l.Children.Count == 0).Select(l => l.Target).ToList();
}

public sealed class DependencyLink
{
    public NodeId Target { get; init; }
    public EdgeType ViaEdge { get; init; }
    public double Weight { get; init; }
    public List<DependencyLink> Children { get; init; } = new();
    public DependencyDirection Direction { get; init; }
}

public enum DependencyDirection { Forward, Backward }
```

依赖解析递归逻辑：

```csharp
public DependencyChain ResolveDependencies(NodeId target, bool backward, int maxDepth)
{
    var chain = new DependencyChain { Root = target };
    this.ResolveRecursive(target, backward, chain.Links, new HashSet<NodeId>(), 0, maxDepth);
    return chain;
}

private void ResolveRecursive(
    NodeId nodeId,
    bool backward,
    List<DependencyLink> links,
    HashSet<NodeId> visited,
    int depth,
    int maxDepth)
{
    if (depth >= maxDepth) return;
    if (!visited.Add(nodeId)) return;

    // 选择向前的边 (产出) 或向后的边 (依赖)
    var edges = backward
        ? this._graph.GetIncomingEdges(nodeId)   // 什么依赖这个节点
        : this._graph.GetOutgoingEdges(nodeId);  // 这个节点依赖什么

    foreach (var edge in edges)
    {
        var depNode = backward ? edge.Source : edge.Target;
        var link = new DependencyLink
        {
            Target = depNode,
            ViaEdge = edge.Type,
            Weight = edge.Weight,
            Direction = backward ? DependencyDirection.Backward : DependencyDirection.Forward,
        };
        links.Add(link);
        this.ResolveRecursive(depNode, backward, link.Children, visited, depth + 1, maxDepth);
    }
}
```

### 3.5 QueryConstraints

```csharp
// src/AIPlayer/KnowledgeGraph/QueryConstraints.cs
public sealed class QueryConstraints
{
    /// <summary>最大搜索深度（默认 10）</summary>
    public int MaxDepth { get; init; } = 10;

    /// <summary>玩家当前等级（用于等级门控过滤）</summary>
    public int? PlayerLevel { get; init; }

    /// <summary>玩家职业（用于职业要求过滤）</summary>
    public PlayerClass? PlayerClass { get; init; }

    /// <summary>允许的边类型白名单（null=全部允许）</summary>
    public HashSet<EdgeType>? AllowedEdgeTypes { get; init; }

    /// <summary>禁止的边类型黑名单</summary>
    public HashSet<EdgeType>? BlockedEdgeTypes { get; init; }

    /// <summary>玩家当前金币（用于传送费用过滤）</summary>
    public int? PlayerMoney { get; init; }

    /// <summary>自定义边谓词（叠加过滤）</summary>
    public Func<GraphEdge, bool>? EdgePredicate { get; init; }

    /// <summary>将约束转换为边过滤函数</summary>
    public Func<GraphEdge, bool> ToEdgeFilter()
    {
        return edge =>
        {
            if (this.AllowedEdgeTypes?.Contains(edge.Type) == false) return false;
            if (this.BlockedEdgeTypes?.Contains(edge.Type) == true) return false;
            if (this.EdgePredicate?.Invoke(edge) == false) return false;

            // 等级门控
            if (this.PlayerLevel.HasValue &&
                edge.Attributes.TryGetValue("minLevel", out var minLevelObj) &&
                minLevelObj is int minLevel &&
                this.PlayerLevel.Value < minLevel)
                return false;

            // 金币门控
            if (this.PlayerMoney.HasValue &&
                edge.Attributes.TryGetValue("goldCost", out var costObj) &&
                costObj is int goldCost &&
                this.PlayerMoney.Value < goldCost)
                return false;

            return true;
        };
    }
}
```

### 3.6 PathResult

```csharp
// src/AIPlayer/KnowledgeGraph/PathResult.cs
public sealed class PathResult
{
    public List<PathStep> Steps { get; init; } = new();
    public double TotalWeight { get; set; }
    public NodeId Start { get; init; }
    public NodeId End { get; init; }
    public bool IsFound => this.Steps.Count > 0;
}

public sealed class PathStep
{
    public NodeId Node { get; init; }
    public EdgeType ViaEdge { get; init; }
    public double CumulativeWeight { get; set; }
    public IReadOnlyDictionary<string, object>? EdgeAttributes { get; init; }
}
```

---

## 4. 集成点

KG 的集成采用**非侵入式**设计：现有组件可选地通过 KG 增强决策能力，不修改原始接口。

### 4.1 DecisionSystem 集成

当规则引擎产生一个任务候选（如"合成翅膀"），`DecisionSystem` 使用 KG 做快速可行性验证：

```csharp
// DecisionSystem 的任务选择扩展
private bool ValidateMissionViaKg(MissionItem mission, IKnowledgeGraphQuery kg)
{
    if (!mission.TargetItem.HasValue) return true; // 非物品型任务跳过

    var itemNode = NodeId.Create(NodeType.Item, mission.TargetItem.Value.Group, mission.TargetItem.Number);

    // 检查玩家是否已有材料（通过依赖链）
    var depChain = kg.ResolveDependencies(itemNode, backward: true, maxDepth: 3);
    return depChain.AllLeavesArePrimitive || this.PlayerHasMaterials(depChain.LeafNodes);
}
```

### 4.2 WarpPlanner 集成

KG 作为 `WarpPlanner` 的回退方案。当纯门图 BFS 无法找到可行路径时，KG 提供多约束路由：

```csharp
// WarpPlanner 的 KG 回退
public WarpRoute? ComputeRouteWithKg(
    short fromMap, short toMap, int playerLevel, int playerMoney,
    IKnowledgeGraphQuery kg)
{
    var fromNode = NodeId.Create(NodeType.Map, fromMap);
    var toNode = NodeId.Create(NodeType.Map, toMap);

    var constraints = new QueryConstraints
    {
        PlayerLevel = playerLevel,
        PlayerMoney = playerMoney,
        AllowedEdgeTypes = new() { EdgeType.ConnectsTo, EdgeType.WarpMenuTo },
    };

    var path = kg.FindShortestPath(fromNode, toNode, constraints);
    if (path is null) return null;

    return this.ToWarpRoute(path);
}
```

### 4.3 ScriptExecutor 集成

行为脚本中可以直接使用 KG 条件，通过 `kg_*` 前缀的变量注入：

```json
{
  "condition": {
    "type": "kg_query",
    "query": "can_craft",
    "params": { "targetItem": { "group": 12, "number": 30 } }
  },
  "action": {
    "type": "navigate",
    "target": "chaos_goblin",
    "reason": "合成玛雅武器"
  }
}
```

脚本引擎中注册的 KG 条件函数：

| 函数名 | 签名 | 用途 |
|--------|------|------|
| `kg_can_craft` | `(group, number) → bool` | 玩家是否拥有合成所需的所有材料 |
| `kg_best_farm_map` | `(group, number) → mapNumber` | 刷指定物品的最佳地图（最高掉率加权） |
| `kg_quest_prerequisites` | `(questGroup, questNumber) → list` | 任务前置清单 |
| `kg_nearest_npc` | `(npcNumber) → (mapNumber, x, y)` | 最近的 NPC 位置 |

### 4.4 DynamicMissionGenerator 集成

`DynamicMissionGenerator` 使用 KG 做材料 farm 规划：

```csharp
// 在 ItemNeedAnalyzer 中，当玩家缺合成材料时触发 KG 规划
private IEnumerable<MissionItem> PlanMaterialFarming(
    NodeId targetItem, IKnowledgeGraphQuery kg)
{
    var deps = kg.ResolveDependencies(targetItem, backward: true, maxDepth: 5);

    foreach (var leaf in deps.LeafNodes)
    {
        if (leaf.Type != NodeType.Item) continue;

        // 找掉落此物品的怪物
        var sources = kg.FindAllReachable(
            leaf, EdgeType.DropsAt, maxDepth: 2);

        // 找怪物所在地图
        foreach (var (monsterNode, _) in sources)
        {
            var maps = kg.FindAllReachable(
                monsterNode, EdgeType.SpawnsOn, maxDepth: 1);

            foreach (var (mapNode, _) in maps)
            {
                yield return new MissionItem
                {
                    Type = MissionType.FarmItem,
                    TargetItem = (leaf.DomainId >> 16, leaf.DomainId & 0xFFFF),
                    TargetMap = mapNode.DomainId,
                    Source = "kg_material_planner",
                };
            }
        }
    }
}
```

---

## 5. 运行期学习

KG 不是纯静态的——它通过运行期观察修正边的权重，使图逐渐逼近真实游戏环境。

### 5.1 掉落率观察收集器

```csharp
// src/AIPlayer/KnowledgeGraph/KnowledgeGraphRuntimeLearner.cs
public sealed class KnowledgeGraphRuntimeLearner
{
    private readonly KnowledgeGraph _graph;
    private readonly ConcurrentDictionary<(int Monster, int ItemGroup, int ItemNumber), DropObservation> _observations = new();

    /// <summary>
    /// 记录一次掉落观察。
    /// </summary>
    public void ObserveDrop(int monsterNumber, int itemGroup, int itemNumber)
    {
        var key = (monsterNumber, itemGroup, itemNumber);
        var obs = this._observations.GetOrAdd(key, _ => new DropObservation());
        obs.TotalKills++;
        obs.DropCount++;
    }

    /// <summary>
    /// 记录一次击杀（无掉落），用于计算经验概率。
    /// </summary>
    public void ObserveKill(int monsterNumber)
    {
        // 更新该怪物所有观察的基数
        foreach (var kvp in this._observations)
        {
            if (kvp.Key.Monster == monsterNumber)
            {
                kvp.Value.TotalKills++;
            }
        }
    }
}

internal sealed class DropObservation
{
    public int TotalKills;    // 总击杀基数
    public int DropCount;     // 掉落次数
    public double EmpiricalRate => this.TotalKills > 0
        ? (double)this.DropCount / this.TotalKills
        : 0;
}
```

### 5.2 贝叶斯混合（Bayesian Blending）

运行期观察到经验概率后，与原始配置概率混合：

```csharp
public double GetBlendedDropRate(DropObservation obs, double configuredRate)
{
    const double BlendFactor = 0.3; // 配置权重的贡献比

    // Bayesian 混合: blended = alpha * configured + (1-alpha) * empirical
    // alpha 随样本量衰减（更多数据→更信任经验）
    var alpha = Math.Max(BlendFactor, 1.0 - obs.TotalKills / 1000.0);
    var empiricalRate = obs.EmpiricalRate;

    var blended = (alpha * configuredRate) + ((1 - alpha) * empiricalRate);

    this._logger.LogDebug(
        "[KGRuntime] 贝叶斯混合: 怪物={Monster} 物品=({G},{N}) " +
        "配置率={Config:P2} 经验率={Emp:P2} 混合率={Blended:P2} alpha={Alpha:P2}",
        ...);

    return blended;
}
```

混合参数经验：

| 样本数 | alpha | 行为 |
|--------|-------|------|
| 0（无数据） | 1.0 | 100% 信任配置 |
| 100 | 0.9 | 90% 配置 + 10% 经验 |
| 500 | 0.5 | 各 50% |
| 1000+ | 0.3 | 30% 配置 + 70% 经验（稳定态） |

### 5.3 周期权重更新

`HeartbeatService` 每 N 个心跳触发一次 KG 权重更新：

```csharp
// HeartbeatService 中的 KG 更新
private readonly KnowledgeGraphRuntimeLearner _kgLearner;

// 每 60 个 tick (~60 秒) 刷新一次
private async ValueTask UpdateKgWeightsAsync()
{
    if (this._tickCount % 60 != 0) return;

    var updates = this._kgLearner.ComputeWeightUpdates();
    foreach (var (edgeId, newWeight) in updates)
    {
        this._knowledgeGraph.UpdateEdgeWeight(edgeId, newWeight);
    }

    this._logger.LogDebug("[KGRuntime] 权重更新: {Count} 条边已刷新", updates.Count);
}
```

### 5.4 持久化

运行期观察数据通过 `CharacterMemory` JSON 持久化：

```csharp
// CharacterMemory 扩展
public sealed class CharacterMemory
{
    // ... 现有字段 ...

    /// <summary>KG 运行期观察数据</summary>
    public KgObservationData? KgObservations { get; init; }
}

public sealed class KgObservationData
{
    public List<SerializedDropObservation> DropObservations { get; init; } = new();
    public DateTime LastWeightUpdate { get; init; }
}

public sealed class SerializedDropObservation
{
    public int MonsterNumber { get; init; }
    public int ItemGroup { get; init; }
    public int ItemNumber { get; init; }
    public int TotalKills { get; set; }
    public int DropCount { get; set; }
}
```

保存位置：`aiplayer_data/{CharacterName}.json` → `KgObservations` 字段。

---

## 6. 性能设计

KG 是频繁查询的数据结构，性能设计是核心关注点。

### 6.1 线程安全

```csharp
public sealed class KnowledgeGraph
{
    private readonly ConcurrentDictionary<NodeId, GraphNode> _nodes = new();
    private readonly ConcurrentDictionary<NodeId, List<GraphEdge>> _outgoingEdges = new();

    public void AddEdge(GraphEdge edge)
    {
        // 节点自动注册
        this._nodes.TryAdd(edge.Source, ...);
        this._nodes.TryAdd(edge.Target, ...);

        // 出边列表追加
        this._outgoingEdges.AddOrUpdate(
            edge.Source,
            _ => new List<GraphEdge> { edge },
            (_, list) => { lock (list) list.Add(edge); return list; });
    }

    public IReadOnlyList<GraphEdge> GetOutgoingEdges(NodeId node)
    {
        if (this._outgoingEdges.TryGetValue(node, out var list))
        {
            lock (list) return list.ToArray(); // 快照返回
        }
        return Array.Empty<GraphEdge>();
    }
}
```

### 6.2 边不可变性

`GraphEdge` 是不可变对象。权重更新通过**原子替换**实现：

```csharp
public void UpdateEdgeWeight((NodeId Source, NodeId Target, EdgeType Type) edgeId, double newWeight)
{
    if (!this._outgoingEdges.TryGetValue(edgeId.Source, out var list)) return;

    lock (list)
    {
        var index = list.FindIndex(e =>
            e.Target == edgeId.Target && e.Type == edgeId.Type);
        if (index >= 0)
        {
            var old = list[index];
            list[index] = new GraphEdge(
                Source: old.Source,
                Target: old.Target,
                Type: old.Type,
                Weight: newWeight,
                Attributes: old.Attributes);
        }
    }
}
```

### 6.3 LRU 查询缓存

频繁查询结果缓存，使用 `System.Runtime.Caching.MemoryCache`：

```csharp
public sealed class KnowledgeGraphCache
{
    private readonly MemoryCache _cache = new("KnowledgeGraphCache");
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    /// <summary>缓存键 = "path|{start}|{end}|{constraintHash}"</summary>
    public PathResult? GetCachedPath(NodeId start, NodeId end, QueryConstraints? constraints)
    {
        var key = this.BuildCacheKey("path", start, end, constraints);
        return this._cache.Get(key) as PathResult;
    }

    public void CachePath(NodeId start, NodeId end, QueryConstraints? constraints, PathResult result)
    {
        var key = this.BuildCacheKey("path", start, end, constraints);
        this._cache.Set(key, result, new CacheItemPolicy
        {
            SlidingExpiration = DefaultTtl,
            Priority = CacheItemPriority.Default,
        });
    }
}
```

LRU 缓存预期命中率：

| 查询类型 | 预期命中率 | 原因 |
|----------|-----------|------|
| 地图路由 | ~80% | 常用地图对（如勇者→冰风谷）频繁查询 |
| 掉落来源 | ~60% | 热门物品（宝石、装备）查询多 |
| 合成链 | ~40% | 每次合成行为唯一，但常见配方缓存佳 |
| 任务依赖 | ~90% | 任务依赖图几乎不变 |

### 6.4 内存预算

| 组件 | 估算 | 条件 |
|------|------|------|
| 节点 (780个) | 780 × ~3 KB = ~2.3 MB | 每个节点含名称+属性字典 |
| 边 (2200条) | 2200 × ~0.5 KB = ~1.1 MB | 每条边含源+目标+类型+权重+属性 |
| 索引 (出边表) | ~4 KB | ConcurrentDictionary 开销 |
| LRU 缓存 | ~10 MB | 默认最多保留 500 条缓存结果 |
| 运行期观察 | ~0.5 MB | 每怪物×物品的观察计数器 |
| **总计** | **~14 MB** | 在 500 MB 健康阈值内，可忽略 |

### 6.5 构建性能

`KnowledgeGraphBuilder.Build()` 预期耗时：

| 阶段 | 耗时 | 说明 |
|------|------|------|
| 节点注册 | < 5 ms | 780 个节点纯内存操作 |
| 地图连接 | < 1 ms | ~60 条边 |
| 掉落关系 | 20-50 ms | ~2000 条边，主要耗在 DropSources 遍历 |
| 合成依赖 | 2-5 ms | ~40 条边 |
| 任务依赖 | 2-5 ms | ~60 条边 |
| **总计** | **30-65 ms** | 服务器启动时一次构建 |

构建只在服务器启动时执行一次，不阻塞主循环。

---

## 7. 实现计划

### P0：核心模型 + 构建器（高优先级）

| 文件 | 路径 | 行数估算 | 内容 |
|------|------|---------|------|
| `NodeType.cs` | `src/AIPlayer/KnowledgeGraph/NodeType.cs` | 20 | 枚举定义 |
| `EdgeType.cs` | `src/AIPlayer/KnowledgeGraph/EdgeType.cs` | 40 | 枚举定义 |
| `NodeId.cs` | `src/AIPlayer/KnowledgeGraph/NodeId.cs` | 40 | 复合 ID struct |
| `GraphNode.cs` | `src/AIPlayer/KnowledgeGraph/GraphNode.cs` | 50 | 节点数据结构 |
| `GraphEdge.cs` | `src/AIPlayer/KnowledgeGraph/GraphEdge.cs` | 60 | 边数据结构 |
| `KnowledgeGraph.cs` | `src/AIPlayer/KnowledgeGraph/KnowledgeGraph.cs` | 120 | 图容器（线程安全） |
| `KnowledgeGraphBuilder.cs` | `src/AIPlayer/KnowledgeGraph/KnowledgeGraphBuilder.cs` | 350 | 从 GameConfiguration 构建 |
| `KnowledgeGraphPathFinder.cs` | `src/AIPlayer/KnowledgeGraph/KnowledgeGraphPathFinder.cs` | 200 | Dijkstra + BFS |
| `KnowledgeGraphQuery.cs` | `src/AIPlayer/KnowledgeGraph/KnowledgeGraphQuery.cs` | 150 | 查询接口实现 |
| `QueryConstraints.cs` | `src/AIPlayer/KnowledgeGraph/QueryConstraints.cs` | 80 | 约束定义 |
| `PathResult.cs` | `src/AIPlayer/KnowledgeGraph/PathResult.cs` | 50 | 路径结果 |
| `DependencyChain.cs` | `src/AIPlayer/KnowledgeGraph/DependencyChain.cs` | 100 | 依赖链模型 |
| **小计** | **12 文件** | **~1260 行** | |

### P1：集成（中优先级）

| 修改 | 文件 | 改动 |
|------|------|------|
| 注册 KG 服务 | `GameKnowledgeService.cs` | 新增 `KnowledgeGraph` 属性，构建器调用 |
| 任务验证 | `DecisionSystem.cs` | `ValidateMissionViaKg` 方法注入 |
| 路由回退 | `WarpPlanner.cs` | `ComputeRouteWithKg` 回退方法 |
| KG 条件注册 | `ScriptExecutor.cs` | `kg_*` 函数注册到脚本上下文 |
| 材料规划 | `DynamicMissionGenerator.cs` | `PlanMaterialFarming` KG 调用 |

### P2：运行期学习（低优先级）

| 文件 | 路径 | 内容 |
|------|------|------|
| `KnowledgeGraphRuntimeLearner.cs` | `src/AIPlayer/KnowledgeGraph/KnowledgeGraphRuntimeLearner.cs` | 观察收集 + 贝叶斯混合 |
| 修改 `HeartbeatService.cs` | `src/AIPlayer/Decision/HeartbeatService.cs` | 周期触发 `UpdateKgWeightsAsync` |
| 修改 `CharacterMemory.cs` | `src/AIPlayer/CharacterMemory.cs` | `KgObservations` 持久化字段 |

---

## 8. 查询示例

### 8.1 "合成翅膀的最优路径？"

问题：玩家有祝福宝石×2、灵魂宝石×2、创造宝石×1，想合成玛雅龙斧（翅膀合成前提）。

```csharp
// 玩家当前物品: Bless(12,15)×2, Soul(12,16)×2, Creation(12,22)×1
// 目标: 玛雅武器 (12,30) → 然后合翅膀

var targetItem = NodeId.Create(NodeType.Item, 12, 30); // 玛雅雷神
var chain = kg.ResolveDependencies(targetItem, backward: true, maxDepth: 5);

// 输出依赖链:
// ── 玛雅雷神 (12,30)
//     └─ CraftsInto ← 玛雅合成配方 Recipe#3
//        ├─ RequiresMaterial → 祝福宝石 (12,15) ×2
//        ├─ RequiresMaterial → 灵魂宝石 (12,16) ×2
//        ├─ RequiresMaterial → 玛雅之石 (12,22) ×1
//        └─ HasRecipe ← 混沌哥布林 NPC#238
//           └─ LocatedOn → 仙踪林 Map#3

// 玩家有: 祝福×3 (够), 灵魂×2 (够), 玛雅×1 (够)
// → 结论: 可直接合成，去仙踪林找混沌哥布林
```

### 8.2 "哪个怪物掉祝福宝石最好？"

```csharp
var blessNode = NodeId.Create(NodeType.Item, 12, 15);
var sources = kg.FindAllReachable(blessNode, EdgeType.DropsAt, maxDepth: 1);

// 按权重升序排列（权重=1/dropRate，即掉率最高者排第一）
var bestSources = sources
    .OrderBy(kvp => kvp.Value)
    .Take(5);

// 输出:
// 1. 死亡骑士 (Monster 74) 在 地下城2层 Map#1:  weight=50 (dropRate=2.0%)
// 2. 毒牛怪 (Monster 78)  在 地下城2层 Map#1:  weight=67 (dropRate=1.5%)
// 3. 暗杀者 (Monster 80)  在 地下城2层 Map#1:  weight=100 (dropRate=1.0%)
// 4. 骷髅王 (Monster 84)  在 地下城2层 Map#1:  weight=125 (dropRate=0.8%)
// 5. 巴洛克 (Monster 87)  在 地下城3层 Map#2:  weight=200 (dropRate=0.5%)

// → 推荐: 去地下城2层刷死亡骑士掉祝福
```

### 8.3 "任务5有哪些前置要求？"

```csharp
var questNode = NodeId.Create(NodeType.Quest, 0, 5); // 第1转任务
var deps = kg.ResolveDependencies(questNode, backward: true, maxDepth: 10);

// 输出依赖链:
// ── Quest(0,5): 第1转 (黑暗骑士觉醒)
//    ├─ RequiresQuest → Quest(0,4): 寻找卡隆
//    │  ├─ RequiresKill → 骷髅王 (Monster 84) ×10
//    │  ├─ RequiresItem → 暗黑之石 (14,20) ×1
//    │  │  └─ DropsAt ← 骷髅王 (Monster 84) [dropRate=0.5%]
//    │  └─ StartsQuest ← 冰风谷向导 NPC#225
//    │     └─ LocatedOn → 冰风谷 Map#2
//    ├─ RequiresKill → 暗杀者 (Monster 80) ×20
//    ├─ RequiresItem → 帝王之书 (14,21) ×1
//    │  └─ DropsAt ← 死亡骑士 (Monster 74) [dropRate=1.0%]
//    └─ StartsQuest ← 巴特 (转职引导) NPC#229
//       └─ LocatedOn → 勇者大陆 Map#0

// → 玩家需要: 完成 Quest(0,4), 杀20暗杀者, 收集1×帝王之书
```

### 8.4 "从勇者大陆到失乐之塔的最短路径？"

```csharp
var start = NodeId.Create(NodeType.Map, 0);  // 勇者大陆
var end = NodeId.Create(NodeType.Map, 31);   // 失乐之塔

var constraints = new QueryConstraints
{
    PlayerLevel = 350,  // 需要350级才能进失乐之塔
    PlayerMoney = 50000,
    AllowedEdgeTypes = new() { EdgeType.ConnectsTo, EdgeType.WarpMenuTo },
};

var path = kg.FindShortestPath(start, end, constraints);

// 输出路径:
// 勇者大陆 (0) ─ConnectsTo→ 仙踪林 (2) ─ConnectsTo→ 亚特兰蒂斯 (7)
//   ─ConnectsTo→ 死亡沙漠 (9) ─ConnectsTo→ 失乐之塔 (31)
// 总跳数: 4, 等级要求: max(0,0,60,350) = 350 ✅
// 金币消耗: 0 (全程步行，未使用传送阵)

// 如果 playerLevel=150:
// 等级约束 → ConnectsTo(沙漠→失乐之塔, minLevel=350) 被过滤
// 回退方案：最优不可能 → 提示需升到350级
```

### 8.5 "附近有仓库NPC吗？"

```csharp
var myMap = NodeId.Create(NodeType.Map, player.CurrentMapNumber);
var warehouseNpc = NodeId.Create(NodeType.Npc, 232); // Warehouse Keeper

// 检查地图上是否有仓库NPC
var npcOnMap = kg.FindAllReachable(myMap, EdgeType.LocatedOn, maxDepth: 1)
    .Any(kvp => kvp.Key == warehouseNpc);

if (!npcOnMap)
{
    // 找最近的有仓库的地图
    var nearest = kg.FindNearest(myMap, EdgeType.ConnectsTo,
        predicate: n =>
        {
            var npcs = kg.FindAllReachable(n.Id, EdgeType.LocatedOn, maxDepth: 1);
            return npcs.Any(k => k.Key == warehouseNpc);
        },
        maxDepth: 3);

    // → 输出: 最近的有仓库的地图是勇者大陆 (Map 0)，可通过Gate 1到达
}
```

### 8.6 "哪些配方能制造这个材料？"

```csharp
var itemNode = NodeId.Create(NodeType.Item, 12, 30); // 玛雅雷神

// 前向依赖：什么产出此物品
var forwardChain = kg.ResolveDependencies(itemNode, backward: false, maxDepth: 2);

// 输出:
// ── 玛雅雷神 (12,30)
//    └─ CraftsInto ← 配方#12: 玛雅武器合成
//    └─ CraftsInto ← 配方#24: 合翅膀原料

// → 结论: 玛雅雷神是翅膀合成原料之一
```

### 8.7 脚本化查询（ScriptExecutor 中使用）

`can_craft` 条件脚本示例：

```json
{
  "type": "script",
  "name": "craft_maya_weapon",
  "steps": [
    {
      "condition": {
        "type": "kg_can_craft",
        "params": { "group": 12, "number": 30 }
      },
      "on_true": {
        "type": "navigate",
        "target": "chaos_goblin",
        "action": "craft",
        "params": { "recipe": "maya_weapon" }
      },
      "on_false": {
        "type": "kg_best_farm_map",
        "params": { "group": 12, "number": 30 },
        "then": {
          "type": "navigate",
          "target": "$result",
          "action": "farm"
        }
      }
    }
  ]
}
```

`best_farm_map` 脚本化查询示例：

```json
{
  "condition": {
    "type": "kg_best_farm_map",
    "params": {
      "itemGroup": 12,
      "itemNumber": 15,
      "playerLevel": 200
    },
    "resultVar": "blessFarmMap"
  },
  "action": {
    "type": "set_variable",
    "name": "targetMap",
    "value": "$blessFarmMap"
  }
}
```

脚本引擎内部实现（简化）：

```csharp
// ScriptExecutor 扩展注册
private void RegisterKgFunctions()
{
    this._context.Functions["kg_can_craft"] = (args) =>
    {
        var group = (int)args[0];
        var number = (int)args[1];
        var itemNode = NodeId.Create(NodeType.Item, group, number);
        var chain = this._kg.ResolveDependencies(itemNode, backward: true, maxDepth: 5);
        return chain.AllLeavesArePrimitive;
    };

    this._context.Functions["kg_best_farm_map"] = (args) =>
    {
        var group = (int)args[0];
        var number = (int)args[1];
        var itemNode = NodeId.Create(NodeType.Item, group, number);

        var sources = this._kg.FindAllReachable(itemNode, EdgeType.DropsAt, maxDepth: 1);
        var best = sources
            .OrderBy(kvp => kvp.Value) // lowest weight = highest drop rate
            .FirstOrDefault();

        if (best.Key == default) return -1;

        // Find the map this monster spawns on
        var maps = this._kg.FindAllReachable(best.Key, EdgeType.SpawnsOn, maxDepth: 1);
        return maps.Count > 0 ? maps.First().Key.DomainId : -1;
    };
}
```

---

## 9. 测试策略

| 测试类型 | 覆盖内容 | 方法 |
|----------|----------|------|
| 单元测试 | NodeId 编码/解码 | `NodeId.Create(NodeType.Map, 0)` → Value=0x01_0000000000000000 |
| 单元测试 | 图构建（Mock GameConfiguration） | `KnowledgeGraphBuilder` 构建小规模图 |
| 单元测试 | Dijkstra 最短路径 | 已知最优解的图验证 |
| 单元测试 | BFS 可达性 | 指定深度限制 |
| 单元测试 | 依赖链解析 | 多级递归验证 |
| 单元测试 | QueryConstraints 过滤 | 等级/金币/边类型过滤 |
| 集成测试 | 使用完整 GameConfiguration | 构建真实知识图谱并验证 |
| 性能测试 | 内存 / 查询延迟 | BenchmarkDotNet |

---

## 附录：关键文件清单

| 文件 | 计划路径 | 优先级 |
|------|---------|--------|
| `NodeType.cs` | `src/AIPlayer/KnowledgeGraph/NodeType.cs` | P0 |
| `EdgeType.cs` | `src/AIPlayer/KnowledgeGraph/EdgeType.cs` | P0 |
| `NodeId.cs` | `src/AIPlayer/KnowledgeGraph/NodeId.cs` | P0 |
| `GraphNode.cs` | `src/AIPlayer/KnowledgeGraph/GraphNode.cs` | P0 |
| `GraphEdge.cs` | `src/AIPlayer/KnowledgeGraph/GraphEdge.cs` | P0 |
| `KnowledgeGraph.cs` | `src/AIPlayer/KnowledgeGraph/KnowledgeGraph.cs` | P0 |
| `KnowledgeGraphBuilder.cs` | `src/AIPlayer/KnowledgeGraph/KnowledgeGraphBuilder.cs` | P0 |
| `KnowledgeGraphPathFinder.cs` | `src/AIPlayer/KnowledgeGraph/KnowledgeGraphPathFinder.cs` | P0 |
| `KnowledgeGraphQuery.cs` | `src/AIPlayer/KnowledgeGraph/KnowledgeGraphQuery.cs` | P0 |
| `QueryConstraints.cs` | `src/AIPlayer/KnowledgeGraph/QueryConstraints.cs` | P0 |
| `PathResult.cs` | `src/AIPlayer/KnowledgeGraph/PathResult.cs` | P0 |
| `DependencyChain.cs` | `src/AIPlayer/KnowledgeGraph/DependencyChain.cs` | P0 |
| `KnowledgeGraphRuntimeLearner.cs` | `src/AIPlayer/KnowledgeGraph/KnowledgeGraphRuntimeLearner.cs` | P2 |
| `KnowledgeGraphCache.cs` | `src/AIPlayer/KnowledgeGraph/KnowledgeGraphCache.cs` | P0 |
