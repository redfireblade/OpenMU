// <copyright file="KnowledgeGraphBuilder.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

using System.Linq;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Configuration.Quests;

/// <summary>
/// Builds the static knowledge graph from <see cref="GameConfiguration"/> at startup.
/// Produces all nodes and edges that represent the game world: maps, monsters, items,
/// NPCs, quests, crafting recipes, skills, and their relationships.
/// The graph is constructed in two phases: all nodes are added first (phase 1),
/// then all edges are added (phase 2). This ordering ensures that <see cref="KnowledgeGraph.AddEdge"/>
/// never fails due to a missing node.
/// </summary>
public sealed class KnowledgeGraphBuilder
{
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnowledgeGraphBuilder"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostic output during graph construction.</param>
    public KnowledgeGraphBuilder(ILogger? logger = null)
    {
        this._logger = logger;
    }

    /// <summary>
    /// Builds the complete static knowledge graph from game configuration.
    /// Phase 1 adds all nodes (maps, monsters, items, NPCs, quests, skills, crafting recipes).
    /// Phase 2 adds all edges (connections, warps, spawns, drops, crafting, quests, NPC locations).
    /// </summary>
    /// <param name="config">The game configuration containing all map, monster, item definitions.</param>
    /// <param name="knowledge">The game knowledge service with pre-loaded lookup dictionaries.</param>
    /// <returns>A fully populated <see cref="KnowledgeGraph"/> representing the game world.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="config"/> or <paramref name="knowledge"/> is <c>null</c>.</exception>
    public KnowledgeGraph Build(GameConfiguration config, GameKnowledgeService knowledge)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(knowledge);

        var graph = new KnowledgeGraph();

        // Phase 1: Add all nodes (graph.AddNode requires nodes to exist before edges reference them)
        AddMapNodes(graph, config, knowledge);
        AddMonsterNodes(graph, knowledge);
        AddItemNodes(graph, knowledge);
        AddNpcNodes(graph, knowledge);
        AddQuestNodes(graph, knowledge);
        AddSkillNodes(graph, config);
        AddCraftingRecipeNodes(graph, config);

        // Phase 2: Add all edges between nodes
        AddMapConnectionEdges(graph, config);
        AddWarpMenuEdges(graph, config);
        AddSpawnEdges(graph, config, knowledge);
        AddDropEdges(graph, knowledge);
        AddCraftingEdges(graph, config);
        AddQuestEdges(graph, config, knowledge);
        AddNpcLocationEdges(graph, knowledge);

        this._logger?.LogInformation(
            "[KG] Built graph: {Nodes} nodes, {Edges} edges",
            graph.NodeCount,
            graph.EdgeCount);

        return graph;
    }

    /// <summary>
    /// Adds a node to the graph and logs a warning if the node already exists (duplicate).
    /// </summary>
    /// <param name="graph">The knowledge graph.</param>
    /// <param name="node">The node to add.</param>
    /// <param name="context">A description of the entity for logging purposes.</param>
    private void AddNodeSafe(KnowledgeGraph graph, GraphNode node, string context)
    {
        if (!graph.AddNode(node))
        {
            this._logger?.LogWarning("[KG] Duplicate node skipped: {Context} ({Id})", context, node.Id);
        }
    }

    /// <summary>
    /// Adds a directed edge to the graph. Logs a warning if the edge cannot be added
    /// because either the source or target node does not exist.
    /// </summary>
    /// <param name="graph">The knowledge graph.</param>
    /// <param name="edge">The edge to add.</param>
    /// <param name="context">A description of the relationship for logging purposes.</param>
    private void AddEdgeSafe(KnowledgeGraph graph, GraphEdge edge, string context)
    {
        if (!graph.AddEdge(edge))
        {
            this._logger?.LogWarning(
                "[KG] Edge skipped (missing node): {Context} — {Source} -> {Target} ({Type})",
                context,
                edge.Source,
                edge.Target,
                edge.Type);
        }
    }

    // ========================================================================
    // Phase 1: Node Creation
    // ========================================================================

    /// <summary>
    /// Creates a <see cref="GraphNode"/> for every <see cref="GameMapDefinition"/> in the configuration.
    /// Each map node includes level range and experience multiplier properties.
    /// </summary>
    private static void AddMapNodes(KnowledgeGraph graph, GameConfiguration config, GameKnowledgeService knowledge)
    {
        foreach (var map in config.Maps)
        {
            if (map is null)
            {
                continue;
            }

            var monsters = knowledge.GetMonstersOnMap(map.Number);
            var minLevel = monsters.Count > 0 ? monsters.Min(m => m.Level) : 0;
            var maxLevel = monsters.Count > 0 ? monsters.Max(m => m.Level) : 0;

            graph.AddNode(new GraphNode(
                NodeId.ForMap(map.Number),
                map.Name.ToString() ?? $"Map {map.Number}",
                NodeType.Map)
            {
                Properties = new Dictionary<string, object>
                {
                    ["MinLevel"] = minLevel,
                    ["MaxLevel"] = maxLevel,
                    ["ExpMultiplier"] = map.ExpMultiplier,
                },
            });
        }
    }

    /// <summary>
    /// Creates a <see cref="GraphNode"/> for every known monster from the knowledge service.
    /// Each monster node includes a level property.
    /// </summary>
    private static void AddMonsterNodes(KnowledgeGraph graph, GameKnowledgeService knowledge)
    {
        foreach (var monster in knowledge.Monsters.Values)
        {
            graph.AddNode(new GraphNode(
                NodeId.ForMonster((short)monster.Number),
                monster.Name,
                NodeType.Monster)
            {
                Properties = new Dictionary<string, object>
                {
                    ["Level"] = monster.Level,
                },
            });
        }
    }

    /// <summary>
    /// Creates a <see cref="GraphNode"/> for every known item from the knowledge service.
    /// Each item node includes drop level and slot information.
    /// </summary>
    private static void AddItemNodes(KnowledgeGraph graph, GameKnowledgeService knowledge)
    {
        foreach (var item in knowledge.Items.Values)
        {
            var props = new Dictionary<string, object>
            {
                ["DropLevel"] = item.DropLevel,
            };

            if (item.ItemSlot is not null)
            {
                props["ItemSlot"] = item.ItemSlot;
            }

            graph.AddNode(new GraphNode(
                NodeId.ForItem(item.Group, item.Number),
                item.Name,
                NodeType.Item)
            {
                Properties = props,
            });
        }
    }

    /// <summary>
    /// Creates a <see cref="GraphNode"/> for every known NPC from the knowledge service.
    /// </summary>
    private static void AddNpcNodes(KnowledgeGraph graph, GameKnowledgeService knowledge)
    {
        foreach (var npc in knowledge.Npcs.Values)
        {
            graph.AddNode(new GraphNode(
                NodeId.ForNpc((short)npc.Number),
                npc.Name,
                NodeType.Npc));
        }
    }

    /// <summary>
    /// Creates a <see cref="GraphNode"/> for every known quest from the knowledge service.
    /// The label includes the quest name for readability.
    /// </summary>
    private static void AddQuestNodes(KnowledgeGraph graph, GameKnowledgeService knowledge)
    {
        foreach (var quest in knowledge.Quests.Values)
        {
            graph.AddNode(new GraphNode(
                NodeId.ForQuest(quest.QuestGroup, quest.QuestNumber),
                $"Quest {quest.QuestGroup}-{quest.QuestNumber}: {quest.Name}",
                NodeType.Quest));
        }
    }

    /// <summary>
    /// Creates a <see cref="GraphNode"/> for every skill definition in the game configuration.
    /// </summary>
    private static void AddSkillNodes(KnowledgeGraph graph, GameConfiguration config)
    {
        foreach (var skill in config.Skills)
        {
            if (skill is null)
            {
                continue;
            }

            graph.AddNode(new GraphNode(
                NodeId.ForSkill(skill.Number),
                skill.Name.ToString() ?? $"Skill {skill.Number}",
                NodeType.Skill));
        }
    }

    /// <summary>
    /// Creates a <see cref="GraphNode"/> for every crafting recipe (<see cref="ItemCrafting"/>)
    /// in the game configuration.
    /// </summary>
    private static void AddCraftingRecipeNodes(KnowledgeGraph graph, GameConfiguration config)
    {
        foreach (var crafting in config.Monsters.SelectMany(m => m.ItemCraftings))
        {
            if (crafting is null)
            {
                continue;
            }

            graph.AddNode(new GraphNode(
                NodeId.ForCraftingRecipe((short)crafting.Number),
                crafting.Name.ToString() ?? $"Crafting {crafting.Number}",
                NodeType.CraftingRecipe));
        }
    }

    // ========================================================================
    // Phase 2: Edge Creation
    // ========================================================================

    /// <summary>
    /// Adds <see cref="EdgeType.ConnectsTo"/> edges between maps based on enter gates.
    /// Each gate that has a target gate with a map creates a connection from the current map
    /// to the target map. Weight is 0 (free traversal via walking).
    /// </summary>
    private static void AddMapConnectionEdges(KnowledgeGraph graph, GameConfiguration config)
    {
        foreach (var map in config.Maps)
        {
            if (map is null)
            {
                continue;
            }

            var sourceMapId = NodeId.ForMap(map.Number);
            if (!graph.HasNode(sourceMapId))
            {
                continue;
            }

            foreach (var enterGate in map.EnterGates)
            {
                if (enterGate?.TargetGate?.Map is null)
                {
                    continue;
                }

                var targetMapId = NodeId.ForMap(enterGate.TargetGate.Map.Number);
                if (!graph.HasNode(targetMapId))
                {
                    continue;
                }

                graph.AddEdge(new GraphEdge(
                    EdgeType.ConnectsTo,
                    sourceMapId,
                    targetMapId,
                    Weight: 0)
                {
                    Properties = new Dictionary<string, object>
                    {
                        ["GateNumber"] = (int)enterGate.Number,
                        ["LevelRequirement"] = (int)enterGate.LevelRequirement,
                    },
                });
            }
        }
    }

    /// <summary>
    /// Adds <see cref="EdgeType.WarpMenuTo"/> edges from warp NPC maps to destination maps
    /// based on the warp list in the game configuration.
    /// Weight is set to the gold cost of the warp.
    /// </summary>
    private static void AddWarpMenuEdges(KnowledgeGraph graph, GameConfiguration config)
    {
        if (config.WarpList is null || config.WarpList.Count == 0)
        {
            return;
        }

        // Find maps where a warp NPC (JuliaWarpMarketServer) is located.
        // These serve as source maps for warp menu edges.
        var warpSourceMaps = FindWarpNpcMaps(config);

        foreach (var warp in config.WarpList)
        {
            if (warp?.Gate?.Map is null)
            {
                continue;
            }

            var targetMapId = NodeId.ForMap(warp.Gate.Map.Number);
            if (!graph.HasNode(targetMapId))
            {
                continue;
            }

            foreach (var srcMapNumber in warpSourceMaps)
            {
                var sourceMapId = NodeId.ForMap(srcMapNumber);
                if (!graph.HasNode(sourceMapId))
                {
                    continue;
                }

                graph.AddEdge(new GraphEdge(
                    EdgeType.WarpMenuTo,
                    sourceMapId,
                    targetMapId,
                    Weight: warp.Costs)
                {
                    Properties = new Dictionary<string, object>
                    {
                        ["WarpIndex"] = warp.Index,
                        ["WarpName"] = warp.Name.ToString() ?? string.Empty,
                        ["LevelRequirement"] = warp.LevelRequirement,
                    },
                });
            }
        }
    }

    /// <summary>
    /// Finds the map numbers where warp NPCs (with <see cref="NpcWindow.JuliaWarpMarketServer"/>)
    /// are located. If no such NPC is found, falls back to map 0 (Lorencia).
    /// </summary>
    /// <param name="config">The game configuration.</param>
    /// <returns>A set of map numbers where warp NPCs are located.</returns>
    private static HashSet<int> FindWarpNpcMaps(GameConfiguration config)
    {
        var warpNpcNumbers = new HashSet<short>();
        foreach (var monsterDef in config.Monsters)
        {
            if (monsterDef is not null && monsterDef.NpcWindow == NpcWindow.JuliaWarpMarketServer)
            {
                warpNpcNumbers.Add(monsterDef.Number);
            }
        }

        if (warpNpcNumbers.Count == 0)
        {
            // Fallback to Lorencia (map 0) as the default warp location
            return new HashSet<int> { 0 };
        }

        var warpSourceMaps = new HashSet<int>();
        foreach (var map in config.Maps)
        {
            if (map is null)
            {
                continue;
            }

            foreach (var spawn in map.MonsterSpawns)
            {
                if (spawn?.MonsterDefinition is null)
                {
                    continue;
                }

                if (warpNpcNumbers.Contains(spawn.MonsterDefinition.Number))
                {
                    warpSourceMaps.Add(map.Number);
                }
            }
        }

        if (warpSourceMaps.Count == 0)
        {
            warpSourceMaps.Add(0);
        }

        return warpSourceMaps;
    }

    /// <summary>
    /// Adds <see cref="EdgeType.SpawnsOn"/> edges from each monster to the maps where it spawns.
    /// Uses the knowledge service's quick lookup for monsters on each map.
    /// </summary>
    private static void AddSpawnEdges(KnowledgeGraph graph, GameConfiguration config, GameKnowledgeService knowledge)
    {
        foreach (var map in config.Maps)
        {
            if (map is null)
            {
                continue;
            }

            var mapId = NodeId.ForMap(map.Number);
            if (!graph.HasNode(mapId))
            {
                continue;
            }

            var monsters = knowledge.GetMonstersOnMap(map.Number);
            foreach (var monster in monsters)
            {
                var monsterId = NodeId.ForMonster((short)monster.Number);
                if (!graph.HasNode(monsterId))
                {
                    continue;
                }

                graph.AddEdge(new GraphEdge(EdgeType.SpawnsOn, monsterId, mapId, Weight: 0));
            }
        }
    }

    /// <summary>
    /// Adds <see cref="EdgeType.DropsAt"/> edges from each monster to the items it drops.
    /// Weight is <c>1.0 / dropRate</c> so that lower drop rates produce higher weights
    /// (less preferred by shortest-path algorithms).
    /// </summary>
    private static void AddDropEdges(KnowledgeGraph graph, GameKnowledgeService knowledge)
    {
        foreach (var drop in knowledge.DropSources)
        {
            var monsterId = NodeId.ForMonster(drop.MonsterNumber);
            if (!graph.HasNode(monsterId))
            {
                continue;
            }

            var itemId = NodeId.ForItem(drop.ItemGroup, drop.ItemNumber);
            if (!graph.HasNode(itemId))
            {
                continue;
            }

            // Avoid division by zero: a drop rate of 0 becomes weight 1 (neutral).
            var weight = drop.DropRate > 0 ? 1.0 / drop.DropRate : 1.0;
            if (double.IsInfinity(weight) || double.IsNaN(weight))
            {
                weight = double.MaxValue;
            }

            graph.AddEdge(new GraphEdge(EdgeType.DropsAt, monsterId, itemId, weight)
            {
                Properties = new Dictionary<string, object>
                {
                    ["DropRate"] = drop.DropRate,
                },
            });
        }
    }

    /// <summary>
    /// Adds crafting-related edges:
    /// <see cref="EdgeType.HasRecipe"/> from each result item to the recipe,
    /// <see cref="EdgeType.RequiresMaterial"/> from each recipe to its required items,
    /// and <see cref="EdgeType.CraftsInto"/> from each recipe to its result items.
    /// </summary>
    private static void AddCraftingEdges(KnowledgeGraph graph, GameConfiguration config)
    {
        foreach (var crafting in config.Monsters.SelectMany(m => m.ItemCraftings))
        {
            if (crafting?.SimpleCraftingSettings is null)
            {
                continue;
            }

            var recipeId = NodeId.ForCraftingRecipe((short)crafting.Number);
            if (!graph.HasNode(recipeId))
            {
                continue;
            }

            var settings = crafting.SimpleCraftingSettings;

            // RequiresMaterial: recipe -> required item
            foreach (var requiredItem in settings.RequiredItems)
            {
                if (requiredItem is null)
                {
                    continue;
                }

                foreach (var possibleItem in requiredItem.PossibleItems)
                {
                    if (possibleItem is null)
                    {
                        continue;
                    }

                    var itemId = NodeId.ForItem((int)possibleItem.Group, (int)possibleItem.Number);
                    if (!graph.HasNode(itemId))
                    {
                        continue;
                    }

                    graph.AddEdge(new GraphEdge(EdgeType.RequiresMaterial, recipeId, itemId)
                    {
                        Properties = new Dictionary<string, object>
                        {
                            ["Quantity"] = (int)requiredItem.MinimumAmount,
                        },
                    });
                }
            }

            // HasRecipe: result item -> recipe
            // CraftsInto: recipe -> result item
            foreach (var resultItem in settings.ResultItems)
            {
                if (resultItem?.ItemDefinition is null)
                {
                    continue;
                }

                var itemId = NodeId.ForItem((int)resultItem.ItemDefinition.Group, (int)resultItem.ItemDefinition.Number);
                if (!graph.HasNode(itemId))
                {
                    continue;
                }

                graph.AddEdge(new GraphEdge(EdgeType.HasRecipe, itemId, recipeId));
                graph.AddEdge(new GraphEdge(EdgeType.CraftsInto, recipeId, itemId));
            }
        }
    }

    /// <summary>
    /// Adds edges related to quests:
    /// <see cref="EdgeType.StartsQuest"/> from NPC to quest,
    /// <see cref="EdgeType.RequiresKill"/> from quest to required monster kills,
    /// <see cref="EdgeType.RequiresItem"/> from quest to required items,
    /// <see cref="EdgeType.RewardsItem"/> from quest to item rewards,
    /// and <see cref="EdgeType.RewardsSkill"/> from quest to skill rewards.
    /// </summary>
    private static void AddQuestEdges(KnowledgeGraph graph, GameConfiguration config, GameKnowledgeService knowledge)
    {
        var processedQuests = new HashSet<long>();

        foreach (var monsterDef in config.Monsters)
        {
            if (monsterDef?.Quests is null || monsterDef.Quests.Count == 0)
            {
                continue;
            }

            foreach (var quest in monsterDef.Quests)
            {
                if (quest is null)
                {
                    continue;
                }

                var questNodeId = NodeId.ForQuest((int)quest.Group, (int)quest.Number);
                if (!graph.HasNode(questNodeId))
                {
                    continue;
                }

                // Prevent duplicate edge processing for the same quest
                if (!processedQuests.Add(questNodeId.Value))
                {
                    continue;
                }

                // StartsQuest: NPC (quest giver) -> Quest
                var npcNodeId = NodeId.ForNpc(monsterDef.Number);
                if (graph.HasNode(npcNodeId))
                {
                    graph.AddEdge(new GraphEdge(EdgeType.StartsQuest, npcNodeId, questNodeId));
                }

                // RequiresKill: Quest -> Monster
                foreach (var killReq in quest.RequiredMonsterKills)
                {
                    if (killReq?.Monster is null)
                    {
                        continue;
                    }

                    var monsterId = NodeId.ForMonster(killReq.Monster.Number);
                    if (!graph.HasNode(monsterId))
                    {
                        continue;
                    }

                    graph.AddEdge(new GraphEdge(EdgeType.RequiresKill, questNodeId, monsterId)
                    {
                        Properties = new Dictionary<string, object>
                        {
                            ["MinimumKills"] = killReq.MinimumNumber,
                        },
                    });
                }

                // RequiresItem: Quest -> Item
                foreach (var itemReq in quest.RequiredItems)
                {
                    if (itemReq?.Item is null)
                    {
                        continue;
                    }

                    var itemId = NodeId.ForItem((int)itemReq.Item.Group, (int)itemReq.Item.Number);
                    if (!graph.HasNode(itemId))
                    {
                        continue;
                    }

                    graph.AddEdge(new GraphEdge(EdgeType.RequiresItem, questNodeId, itemId)
                    {
                        Properties = new Dictionary<string, object>
                        {
                            ["Quantity"] = itemReq.MinimumNumber,
                        },
                    });
                }

                // RewardsItem / RewardsSkill: Quest -> Reward
                foreach (var reward in quest.Rewards)
                {
                    if (reward is null)
                    {
                        continue;
                    }

                    if (reward.RewardType == QuestRewardType.Item && reward.ItemReward?.Definition is not null)
                    {
                        var rewardItemId = NodeId.ForItem(
                            (int)reward.ItemReward.Definition.Group,
                            (int)reward.ItemReward.Definition.Number);
                        if (graph.HasNode(rewardItemId))
                        {
                            graph.AddEdge(new GraphEdge(EdgeType.RewardsItem, questNodeId, rewardItemId)
                            {
                                Properties = new Dictionary<string, object>
                                {
                                    ["Quantity"] = reward.Value,
                                },
                            });
                        }
                    }
                    else if (reward.RewardType == QuestRewardType.Skill && reward.SkillReward is not null)
                    {
                        var skillId = NodeId.ForSkill(reward.SkillReward.Number);
                        if (graph.HasNode(skillId))
                        {
                            graph.AddEdge(new GraphEdge(EdgeType.RewardsSkill, questNodeId, skillId));
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Adds <see cref="EdgeType.LocatedOn"/> edges from each NPC to the map where it is located.
    /// </summary>
    private static void AddNpcLocationEdges(KnowledgeGraph graph, GameKnowledgeService knowledge)
    {
        foreach (var npc in knowledge.Npcs.Values)
        {
            var npcId = NodeId.ForNpc((short)npc.Number);
            if (!graph.HasNode(npcId))
            {
                continue;
            }

            var mapId = NodeId.ForMap(npc.MapNumber);
            if (!graph.HasNode(mapId))
            {
                continue;
            }

            graph.AddEdge(new GraphEdge(EdgeType.LocatedOn, npcId, mapId));
        }
    }
}
