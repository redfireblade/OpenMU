// <copyright file="EdgeType.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Identifies the type of a directed edge (relationship) between two nodes in the knowledge graph.
/// </summary>
public enum EdgeType : byte
{
    /// <summary>
    /// Map -> Map: traversal via a gate (walkable connection).
    /// </summary>
    ConnectsTo,

    /// <summary>
    /// Map -> Map: traversal via the warp menu (no walking required).
    /// </summary>
    WarpMenuTo,

    /// <summary>
    /// Monster -> Map: the monster spawns on this map.
    /// </summary>
    SpawnsOn,

    /// <summary>
    /// Npc -> Map: the NPC is located on this map.
    /// </summary>
    LocatedOn,

    /// <summary>
    /// Monster -> Item: the monster drops this item.
    /// </summary>
    DropsAt,

    /// <summary>
    /// Item -> Item: the source item crafts into the target item (via a recipe).
    /// </summary>
    CraftsInto,

    /// <summary>
    /// CraftingRecipe -> Item: the recipe requires this material item.
    /// </summary>
    RequiresMaterial,

    /// <summary>
    /// Item -> CraftingRecipe: this item has an associated crafting recipe.
    /// </summary>
    HasRecipe,

    /// <summary>
    /// Quest -> Quest: the target quest is a prerequisite for the source quest.
    /// </summary>
    RequiresQuest,

    /// <summary>
    /// Npc -> Quest: this NPC starts the given quest.
    /// </summary>
    StartsQuest,

    /// <summary>
    /// Quest -> Monster: completing the quest requires killing this monster.
    /// </summary>
    RequiresKill,

    /// <summary>
    /// Quest -> Item: completing the quest requires collecting this item.
    /// </summary>
    RequiresItem,

    /// <summary>
    /// Quest -> Item: completing the quest rewards this item.
    /// </summary>
    RewardsItem,

    /// <summary>
    /// Quest -> Skill: completing the quest rewards this skill.
    /// </summary>
    RewardsSkill,

    /// <summary>
    /// * -> (virtual level node): the entity requires a minimum character level.
    /// </summary>
    RequiresLevel,

    /// <summary>
    /// * -> PlayerClass: the entity is restricted to the given player class.
    /// </summary>
    RequiresClass,

    /// <summary>
    /// Item -> MiniGameEvent: this item serves as an entry ticket for the event.
    /// </summary>
    TicketFor,

    /// <summary>
    /// Npc -> Item: this NPC sells the given item.
    /// </summary>
    SellsItem,

    /// <summary>
    /// F13: WorkerProfile -> Map/Monster/Item: this worker specializes in this target.
    /// </summary>
    SpecializesIn,
}
