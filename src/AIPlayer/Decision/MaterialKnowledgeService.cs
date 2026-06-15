// <copyright file="MaterialKnowledgeService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// 材料掉落知识服务 — Layer 5 helper.
/// 知道某种材料(ItemGroup, ItemNumber)被什么怪物在什么地图掉落，
/// 以及门票合成材料的定义和背包检查。
/// 通过扫描游戏配置(GameConfiguration)的 MonsterSpawns 和 DropItemGroups 数据，
/// fallback 到 ItemNeedAnalyzer 现有的硬编码知识。
/// </summary>
public sealed class MaterialKnowledgeService
{
    private readonly GameConfiguration _config;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MaterialKnowledgeService"/> class.
    /// </summary>
    /// <param name="config">The game configuration.</param>
    /// <param name="logger">The logger.</param>
    public MaterialKnowledgeService(GameConfiguration config, ILogger logger)
    {
        this._config = config;
        this._logger = logger;
    }

    /// <summary>
    /// 获取某种材料被什么怪物在什么地图掉落的信息。
    /// 通过扫描所有地图的 MonsterSpawns → MonsterDefinition → DropItemGroups → PossibleItems 查找。
    /// </summary>
    /// <param name="itemGroup">物品组 (Group).</param>
    /// <param name="itemNumber">物品编号 (Number).</param>
    /// <returns>掉落来源信息列表.</returns>
    public List<DropSourceInfo> GetDropSources(int itemGroup, int itemNumber)
    {
        var results = new List<DropSourceInfo>();
        var seen = new HashSet<(short MonsterNumber, ushort MapNumber)>();

        // 扫描所有地图
        foreach (var map in this._config.Maps)
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

                var monster = spawn.MonsterDefinition;

                // 查找该怪兽的 DropItemGroups 中是否包含目标物品
                foreach (var dropGroup in monster.DropItemGroups)
                {
                    if (dropGroup is null)
                    {
                        continue;
                    }

                    foreach (var itemDef in dropGroup.PossibleItems)
                    {
                        if (itemDef is null)
                        {
                            continue;
                        }

                        if (itemDef.Group == itemGroup && itemDef.Number == itemNumber)
                        {
                            var key = (monster.Number, (ushort)map.Number);
                            if (seen.Add(key))
                            {
                                var info = new DropSourceInfo(
                                    MonsterNumber: monster.Number,
                                    MonsterName: (string)monster.Designation,
                                    MapNumber: (ushort)map.Number,
                                    MapName: (string)map.Name);
                                results.Add(info);

                                this._logger.LogDebug(
                                    "[MaterialKnowledge] 找到掉落来源: {Monster}({MonsterNum}) @ 地图{MapNum}({MapName}) 掉落 {ItemGroup},{ItemNumber}",
                                    monster.Designation,
                                    monster.Number,
                                    map.Number,
                                    map.Name,
                                    itemGroup,
                                    itemNumber);
                            }

                            break; // 找到后无需继续扫描该组的其他物品
                        }
                    }
                }
            }
        }

        this._logger.LogInformation(
            "[MaterialKnowledge] GetDropSources({Group},{Number}) 返回 {Count} 个掉落来源",
            itemGroup,
            itemNumber,
            results.Count);

        return results;
    }

    /// <summary>
    /// 获取门票合成所需的所有材料定义。
    /// 优先从 GameConfiguration 的 ItemCraftings 中提取，
    /// 若配置无对应合成规则，则 fallback 到硬编码知识。
    /// </summary>
    /// <param name="miniGameType">迷你游戏类型.</param>
    /// <param name="gameLevel">游戏等级.</param>
    /// <returns>合成材料列表.</returns>
    public List<CraftingMaterial> GetTicketCraftingMaterials(MiniGameType miniGameType, int gameLevel)
    {
        // 1. 尝试从配置中提取
        var configMaterials = this.TryExtractMaterialsFromConfig(miniGameType, gameLevel);
        if (configMaterials is { Count: > 0 })
        {
            return configMaterials;
        }

        // 2. Fallback: 使用硬编码知识（与 ItemNeedAnalyzer / EventExecutorModule 保持一致）
        this._logger.LogWarning(
            "[MaterialKnowledge] 配置中未找到 {Type} Lv.{Level} 的合成规则，使用硬编码知识",
            miniGameType,
            gameLevel);

        return miniGameType switch
        {
            MiniGameType.BloodCastle => new List<CraftingMaterial>
            {
                new(Group: 12, Number: 15, Name: "Jewel of Chaos", RequiredCount: 1),
                new(Group: 13, Number: 16, Name: "Scroll of Archangel", RequiredCount: 1),
                new(Group: 13, Number: 17, Name: "Blood Bone", RequiredCount: 1),
            },
            MiniGameType.DevilSquare => new List<CraftingMaterial>
            {
                new(Group: 12, Number: 15, Name: "Jewel of Chaos", RequiredCount: 1),
                new(Group: 14, Number: 17, Name: "Devil's Eye", RequiredCount: 1),
                new(Group: 14, Number: 18, Name: "Devil's Key", RequiredCount: 1),
            },
            _ => new List<CraftingMaterial>(),
        };
    }

    /// <summary>
    /// 检查背包是否有该活动门票合成所需的所有材料。
    /// </summary>
    /// <param name="player">AI 玩家实例.</param>
    /// <param name="miniGameDef">迷你游戏定义.</param>
    /// <returns>如果背包有足够的材料返回 true.</returns>
    public bool HasSufficientMaterials(AiPlayer player, MiniGameDefinition miniGameDef)
    {
        if (miniGameDef.TicketItem is null)
        {
            // 不需要门票的活动视为材料充足
            return true;
        }

        var materials = this.GetTicketCraftingMaterials(miniGameDef.Type, miniGameDef.GameLevel);
        if (materials.Count == 0)
        {
            return false;
        }

        var inventory = player.Inventory;
        if (inventory is null)
        {
            return false;
        }

        foreach (var material in materials)
        {
            var count = inventory.Items.Count(i =>
                i.Definition?.Group == material.Group
                && i.Definition?.Number == material.Number
                && i.Durability > 0);

            if (count < material.RequiredCount)
            {
                this._logger.LogDebug(
                    "[MaterialKnowledge] 缺少材料 {Name}({Group},{Number}), 需要 {Need} 个, 已有 {Have} 个",
                    material.Name,
                    material.Group,
                    material.Number,
                    material.RequiredCount,
                    count);
                return false;
            }
        }

        this._logger.LogInformation(
            "[MaterialKnowledge] 背包材料充足, 可以合成 {Type} Lv.{Level} 的门票",
            miniGameDef.Type,
            miniGameDef.GameLevel);

        return true;
    }

    /// <summary>
    /// 尝试从配置中提取指定迷你游戏类型和等级的门票合成材料。
    /// 通过扫描所有 MonsterDefinition.ItemCraftings 查找产出门票物品的合成规则。
    /// </summary>
    private List<CraftingMaterial>? TryExtractMaterialsFromConfig(MiniGameType miniGameType, int gameLevel)
    {
        // 查找对应的 MiniGameDefinition
        var miniGameDef = this._config.MiniGameDefinitions
            .FirstOrDefault(d => d.Type == miniGameType && d.GameLevel == gameLevel);

        if (miniGameDef?.TicketItem is null)
        {
            return null;
        }

        var ticketItem = miniGameDef.TicketItem;

        // 遍历所有 MonsterDefinition 查找包含门票产出规则的 ItemCrafting
        foreach (var monster in this._config.Monsters)
        {
            if (monster is null)
            {
                continue;
            }

            foreach (var crafting in monster.ItemCraftings)
            {
                if (crafting?.SimpleCraftingSettings is null)
                {
                    continue;
                }

                // 检查产出物品中是否包含门票
                var producesTicket = crafting.SimpleCraftingSettings.ResultItems
                    .Any(r => r?.ItemDefinition == ticketItem);

                if (!producesTicket)
                {
                    continue;
                }

                // 提取所需材料
                var materials = new List<CraftingMaterial>();
                foreach (var req in crafting.SimpleCraftingSettings.RequiredItems)
                {
                    if (req is null)
                    {
                        continue;
                    }

                    // 每个 RequiredItem 可能有多个 PossibleItems（通常是同类物品的不同等级/选项）
                    // 取第一个有效物品定义作为代表
                    var possibleItem = req.PossibleItems.FirstOrDefault();
                    if (possibleItem is null)
                    {
                        continue;
                    }

                    // 使用 MinimumAmount 作为所需数量（有则用之，否则默认为 1）
                    var requiredCount = req.MinimumAmount > 0 ? (int)req.MinimumAmount : 1;

                    materials.Add(new CraftingMaterial(
                        Group: possibleItem.Group,
                        Number: possibleItem.Number,
                        Name: (string)possibleItem.Name,
                        RequiredCount: requiredCount));
                }

                if (materials.Count > 0)
                {
                    this._logger.LogInformation(
                        "[MaterialKnowledge] 从配置提取 {Type} Lv.{Level} 合成材料: 来自 {Monster}({MonsterNum}) 的 Crafting #{CraftingNum}",
                        miniGameType,
                        gameLevel,
                        monster.Designation,
                        monster.Number,
                        crafting.Number);

                    return materials;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// 合成材料定义.
/// </summary>
/// <param name="Group">物品组.</param>
/// <param name="Number">物品编号.</param>
/// <param name="Name">物品名称.</param>
/// <param name="RequiredCount">所需数量，默认 1.</param>
public record CraftingMaterial(int Group, int Number, string Name, int RequiredCount = 1);

/// <summary>
/// 掉落来源信息.
/// </summary>
/// <param name="MonsterNumber">怪兽编号.</param>
/// <param name="MonsterName">怪兽名称.</param>
/// <param name="MapNumber">地图编号.</param>
/// <param name="MapName">地图名称.</param>
public record DropSourceInfo(short MonsterNumber, string MonsterName, ushort MapNumber, string MapName);
