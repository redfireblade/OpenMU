// <copyright file="NodeType.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Identifies the type of a node in the knowledge graph.
/// Each value corresponds to a category of entity in the game world.
/// </summary>
public enum NodeType : byte
{
    /// <summary>
    /// A game map (e.g., Lorencia, Dungeon, Atlans).
    /// </summary>
    Map = 1,

    /// <summary>
    /// A monster definition.
    /// </summary>
    Monster = 2,

    /// <summary>
    /// An item definition (e.g., a weapon, armor, gem).
    /// </summary>
    Item = 3,

    /// <summary>
    /// A non-player character that offers services or quests.
    /// </summary>
    Npc = 4,

    /// <summary>
    /// A quest definition.
    /// </summary>
    Quest = 5,

    /// <summary>
    /// A mini-game event (e.g., Blood Castle, Devil Square).
    /// </summary>
    MiniGameEvent = 6,

    /// <summary>
    /// A skill definition.
    /// </summary>
    Skill = 7,

    /// <summary>
    /// A player character class (e.g., Dark Wizard, Blade Knight).
    /// </summary>
    PlayerClass = 8,

    /// <summary>
    /// A crafting recipe used at NPCs for item combination.
    /// </summary>
    CraftingRecipe = 9,

    /// <summary>
    /// F13: An AI worker's capability profile node.
    /// Stores empirical performance scores across different task types.
    /// Maps to Fugu's per-worker empirical performance data.
    /// </summary>
    WorkerProfile = 10,
}
