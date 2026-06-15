// <copyright file="MaterialKnowledgeService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
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
    /// 通过以下方式查找：
    /// 1. 扫描所有地图的 MonsterSpawns → MonsterDefinition → DropItemGroups → PossibleItems
    /// 2. 扫描全局 GameConfiguration.DropItemGroups（事件门票物品等，通过 BaseMapInitializer.RegisterDefaultDropItemGroup 注册）
    /// </summary>
    /// <param name="itemGroup">物品组 (Group).</param>
    /// <param name="itemNumber">物品编号 (Number).</param>
    /// <returns>掉落来源信息列表.</returns>
    public List<DropSourceInfo> GetDropSources(int itemGroup, int itemNumber)
    {
        var results = new List<DropSourceInfo>();
        var seen = new HashSet<(short MonsterNumber, ushort MapNumber, byte ItemLevel)>();

        // 阶段1: 扫描怪物专属 DropItemGroups（已有逻辑保留）
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
                            var itemLevel = dropGroup.ItemLevel ?? 1;
                            var key = (monster.Number, (ushort)map.Number, itemLevel);
                            if (seen.Add(key))
                            {
                                var info = new DropSourceInfo(
                                    MonsterNumber: monster.Number,
                                    MonsterName: (string)monster.Designation,
                                    MapNumber: (ushort)map.Number,
                                    MapName: (string)map.Name,
                                    ItemLevel: itemLevel);
                                results.Add(info);

                                this._logger.LogDebug(
                                    "[MaterialKnowledge] 找到掉落来源(专属): {Monster}({MonsterNum}) @ 地图{MapNum}({MapName}) 掉落 {ItemGroup},{ItemNumber} Lv.{Level}",
                                    monster.Designation,
                                    monster.Number,
                                    map.Number,
                                    map.Name,
                                    itemGroup,
                                    itemNumber,
                                    itemLevel);
                            }

                            break;
                        }
                    }
                }
            }
        }

        // 阶段2: 扫描全局 DropItemGroups（事件门票物品等）
        // 这些通过 BaseMapInitializer.RegisterDefaultDropItemGroup 注册到 GameConfiguration.DropItemGroups
        foreach (var dropGroup in this._config.DropItemGroups)
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

                if (itemDef.Group != itemGroup || itemDef.Number != itemNumber)
                {
                    continue;
                }

                var itemLevel = dropGroup.ItemLevel ?? 1;

                // 如果该 DropItemGroup 有 Monster 限制（特定怪物专属），则按怪物匹配
                if (dropGroup.Monster is { } specificMonster)
                {
                    foreach (var map in this._config.Maps)
                    {
                        if (map is null) continue;
                        foreach (var spawn in map.MonsterSpawns)
                        {
                            if (spawn?.MonsterDefinition != specificMonster) continue;
                            var key = (specificMonster.Number, (ushort)map.Number, itemLevel);
                            if (!seen.Add(key)) continue;

                            results.Add(new DropSourceInfo(
                                MonsterNumber: specificMonster.Number,
                                MonsterName: (string)specificMonster.Designation,
                                MapNumber: (ushort)map.Number,
                                MapName: (string)map.Name,
                                ItemLevel: itemLevel));
                        }
                    }

                    break;
                }

                // 全局掉落：按 MinimumMonsterLevel / MaximumMonsterLevel 匹配
                var minLevel = dropGroup.MinimumMonsterLevel ?? 0;
                var maxLevel = dropGroup.MaximumMonsterLevel ?? byte.MaxValue;

                foreach (var map in this._config.Maps)
                {
                    if (map is null) continue;
                    foreach (var spawn in map.MonsterSpawns)
                    {
                        if (spawn?.MonsterDefinition is null) continue;
                        var monster = spawn.MonsterDefinition;

                        // 跳过 NPC（无战斗能力、无等级的怪不参与掉落匹配）
                        if (monster.Attributes is null || monster.Attributes.Count == 0)
                        {
                            continue;
                        }

                        byte monsterLevel;
                        try
                        {
                            monsterLevel = (byte)monster[Stats.Level];
                        }
                        catch
                        {
                            // 无 Level 属性的怪物跳过
                            continue;
                        }

                        if (monsterLevel >= minLevel && monsterLevel <= maxLevel)
                        {
                            var key = (monster.Number, (ushort)map.Number, itemLevel);
                            if (!seen.Add(key)) continue;

                            results.Add(new DropSourceInfo(
                                MonsterNumber: monster.Number,
                                MonsterName: (string)monster.Designation,
                                MapNumber: (ushort)map.Number,
                                MapName: (string)map.Name,
                                ItemLevel: itemLevel));

                            this._logger.LogDebug(
                                "[MaterialKnowledge] 找到掉落来源(全局): {Monster}({MonsterNum}) Lv.{MonLv} @ 地图{MapNum}({MapName}) 掉落 {ItemGroup},{ItemNumber} Lv.{ItemLv} (MinMonLv={MinLv})",
                                monster.Designation,
                                monster.Number,
                                monsterLevel,
                                map.Number,
                                map.Name,
                                itemGroup,
                                itemNumber,
                                itemLevel,
                                minLevel);
                        }
                    }
                }

                break; // 一个 DropItemGroup 里的 PossibleItems 已经匹配完
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

    /// <summary>
    /// 获取指定怪物的等级范围(最小和最大等级相同，因为怪物自身只有一个等级)。
    /// 通过 MonsterDefinition[Stats.Level] 属性读取。
    /// </summary>
    /// <param name="monsterNumber">怪物编号.</param>
    /// <returns>包含最小和最大等级的元组，如果找不到怪物或无 Level 属性则返回 null.</returns>
    public (byte MinLevel, byte MaxLevel)? GetMonsterLevelRange(short monsterNumber)
    {
        var monster = this._config.Monsters.FirstOrDefault(m => m.Number == monsterNumber);
        if (monster is null)
        {
            this._logger.LogDebug("[MaterialKnowledge] 未找到怪物 #{Monster}", monsterNumber);
            return null;
        }

        if (monster.Attributes is null || monster.Attributes.Count == 0)
        {
            this._logger.LogDebug("[MaterialKnowledge] 怪物 #{Monster} 无属性定义", monsterNumber);
            return null;
        }

        byte level;
        try
        {
            level = (byte)monster[Stats.Level];
        }
        catch
        {
            this._logger.LogDebug("[MaterialKnowledge] 怪物 #{Monster} 无 Level 属性", monsterNumber);
            return null;
        }

        return (level, level);
    }

    /// <summary>
    /// 根据玩家等级计算应该刷什么等级的门票材料。
    /// 选择 MinimumMonsterLevel &lt;= playerLevel 的最高可用材料等级。
    /// 对于恶魔广场: 扫全局 DropItemGroups 中的 Devil's Eye(14,17)/Devil's Key(14,18)，
    /// 找到 ItemLevel 对应的 MinimumMonsterLevel，选择符合玩家等级的最高等级。
    /// </summary>
    /// <param name="itemGroup">物品组.</param>
    /// <param name="itemNumber">物品编号.</param>
    /// <param name="playerLevel">玩家当前等级.</param>
    /// <returns>最佳材料掉落信息，包含目标等级、怪物编号和地图编号.</returns>
    public TicketDropInfo? GetBestTicketMaterialDropInfo(int itemGroup, int itemNumber, int playerLevel)
    {
        // 收集该物品的所有 DropItemGroup 等级信息
        var levelInfos = new List<(byte ItemLevel, byte MinMonsterLevel)>();
        foreach (var dropGroup in this._config.DropItemGroups)
        {
            if (dropGroup is null) continue;
            foreach (var itemDef in dropGroup.PossibleItems)
            {
                if (itemDef is null) continue;
                if (itemDef.Group != itemGroup || itemDef.Number != itemNumber) continue;

                var itemLevel = dropGroup.ItemLevel ?? 1;
                var minMonsterLevel = dropGroup.MinimumMonsterLevel ?? 0;
                levelInfos.Add((itemLevel, minMonsterLevel));
                break;
            }
        }

        if (levelInfos.Count == 0)
        {
            this._logger.LogWarning("[MaterialKnowledge] 物品 G{Group}N{Number} 无 DropItemGroup 定义", itemGroup, itemNumber);
            return null;
        }

        // 选择 MinimumMonsterLevel <= playerLevel 的最高可用等级
        levelInfos.Sort((a, b) => b.ItemLevel.CompareTo(a.ItemLevel)); // 降序排序，找最高的
        byte targetLevel = 0;
        foreach (var (il, mml) in levelInfos)
        {
            if (mml <= playerLevel)
            {
                targetLevel = il;
                break;
            }
        }

        if (targetLevel == 0)
        {
            this._logger.LogWarning(
                "[MaterialKnowledge] 玩家等级 {Level} 不足以刷任何等级的 G{Group}N{Number} (最低怪物等级={Min})",
                playerLevel,
                itemGroup,
                itemNumber,
                levelInfos.Min(li => li.MinMonsterLevel));
            return null;
        }

        // 找到该目标等级的掉落来源（怪物+地图）
        var sources = this.GetDropSources(itemGroup, itemNumber);
        var matched = sources
            .Where(s => s.ItemLevel == targetLevel)
            .OrderBy(s => s.MonsterNumber) // 稳定排序，选第一个
            .FirstOrDefault();

        if (matched is null)
        {
            this._logger.LogWarning("[MaterialKnowledge] 找到 G{Group}N{Number} Lv.{Level} 但无对应怪物掉落来源", itemGroup, itemNumber, targetLevel);
            return null;
        }

        this._logger.LogInformation(
            "[MaterialKnowledge] 最佳门票材料: G{Group}N{Number} Lv.{Level} 由 {Monster}(#{MonsterNum}) @ 地图#{MapNum} 掉落",
            itemGroup,
            itemNumber,
            targetLevel,
            matched.MonsterName,
            matched.MonsterNumber,
            matched.MapNumber);

        return new TicketDropInfo(
            ItemGroup: itemGroup,
            ItemNumber: itemNumber,
            TargetLevel: targetLevel,
            MonsterNumber: matched.MonsterNumber,
            MapNumber: matched.MapNumber,
            MonsterName: matched.MonsterName,
            MapName: matched.MapName);
    }

    /// <summary>
    /// 获取指定物品所有可用的材料等级（即 DropItemGroup.ItemLevel 集合）。
    /// </summary>
    private HashSet<byte> GetAvailableMaterialLevels(int itemGroup, int itemNumber)
    {
        var levels = new HashSet<byte>();
        foreach (var dropGroup in this._config.DropItemGroups)
        {
            if (dropGroup is null) continue;
            foreach (var itemDef in dropGroup.PossibleItems)
            {
                if (itemDef is null) continue;
                if (itemDef.Group != itemGroup || itemDef.Number != itemNumber) continue;
                levels.Add(dropGroup.ItemLevel ?? 1);
                break;
            }
        }

        return levels;
    }

    /// <summary>
    /// 获取指定物品指定等级的 MinimumMonsterLevel（掉落该材料所需的最低怪物等级）。
    /// </summary>
    private byte GetMinMonsterLevelForItemLevel(int itemGroup, int itemNumber, byte itemLevel)
    {
        foreach (var dropGroup in this._config.DropItemGroups)
        {
            if (dropGroup is null) continue;
            foreach (var itemDef in dropGroup.PossibleItems)
            {
                if (itemDef is null) continue;
                if (itemDef.Group != itemGroup || itemDef.Number != itemNumber) continue;
                if ((dropGroup.ItemLevel ?? 1) == itemLevel)
                {
                    return dropGroup.MinimumMonsterLevel ?? 0;
                }

                break;
            }
        }

        return 0;
    }

    /// <summary>
    /// 获取指定迷你游戏的门票合成材料掉落信息。
    /// 为门票的第一个材料（恶魔眼/卷轴）和第二个材料（恶魔钥匙/血骨）分别计算最佳材料等级。
    /// 注意：两个材料的 TargetLevel 必须相同才能合成。
    /// 搜索范围从门票所需等级向下到1级，选择玩家能刷到的最高等级。
    /// </summary>
    /// <param name="miniGameType">迷你游戏类型.</param>
    /// <param name="gameLevel">游戏等级（对应 MiniGameDefinition.GameLevel）.</param>
    /// <param name="playerLevel">玩家当前等级.</param>
    /// <returns>第一个材料的掉落信息（通常作为代表），含 TargetLevel.</returns>
    public TicketDropInfo? GetTicketMaterialDropInfo(MiniGameType miniGameType, int gameLevel, int playerLevel)
    {
        var materials = this.GetTicketCraftingMaterials(miniGameType, gameLevel);
        if (materials.Count == 0)
        {
            return null;
        }

        // 跳过混沌宝石（12,15）——全局掉落，不按等级
        var eventMaterials = materials.Where(m => !(m.Group == 12 && m.Number == 15)).ToList();
        if (eventMaterials.Count == 0)
        {
            return null;
        }

        // 获取门票所需的物品等级（TicketItemLevel），搜索范围上限
        var miniGameDef = this._config.MiniGameDefinitions
            .FirstOrDefault(d => d.Type == miniGameType && d.GameLevel == gameLevel);
        var maxRequiredLevel = (int)(miniGameDef?.TicketItemLevel ?? byte.MaxValue);

        // 从 maxRequiredLevel 向下搜索，找到玩家能刷到的最高等级
        var firstMaterial = eventMaterials[0];
        for (byte searchLevel = (byte)Math.Min(maxRequiredLevel, byte.MaxValue); searchLevel >= 1; searchLevel--)
        {
            // 检查第一个材料能否达到 searchLevel
            var firstLevels = this.GetAvailableMaterialLevels(firstMaterial.Group, firstMaterial.Number);
            if (!firstLevels.Contains(searchLevel))
            {
                continue;
            }

            // 检查该等级的 MinimumMonsterLevel 是否 <= playerLevel
            var firstMinLevel = this.GetMinMonsterLevelForItemLevel(firstMaterial.Group, firstMaterial.Number, searchLevel);
            if (firstMinLevel > playerLevel)
            {
                continue;
            }

            // 找到第一个材料的掉落来源
            var firstSources = this.GetDropSources(firstMaterial.Group, firstMaterial.Number)
                .Where(s => s.ItemLevel == searchLevel)
                .ToList();
            if (firstSources.Count == 0)
            {
                continue;
            }

            // 验证第二个材料也能达到相同等级
            if (eventMaterials.Count >= 2)
            {
                var secondMaterial = eventMaterials[1];
                var secondLevels = this.GetAvailableMaterialLevels(secondMaterial.Group, secondMaterial.Number);
                if (!secondLevels.Contains(searchLevel))
                {
                    continue;
                }

                var secondMinLevel = this.GetMinMonsterLevelForItemLevel(secondMaterial.Group, secondMaterial.Number, searchLevel);
                if (secondMinLevel > playerLevel)
                {
                    continue;
                }

                var secondSources = this.GetDropSources(secondMaterial.Group, secondMaterial.Number)
                    .Where(s => s.ItemLevel == searchLevel)
                    .ToList();
                if (secondSources.Count == 0)
                {
                    continue;
                }
            }

            // 找到可用等级！用第一个材料的掉落信息
            var source = firstSources[0];
            this._logger.LogInformation(
                "[MaterialKnowledge] 门票材料最佳等级: G{Group}N{Number} Lv.{Level} (MinMonLv={MinLv}) 由 {Monster} @ 地图#{Map} 掉落",
                firstMaterial.Group,
                firstMaterial.Number,
                searchLevel,
                firstMinLevel,
                source.MonsterName,
                source.MapNumber);

            return new TicketDropInfo(
                ItemGroup: firstMaterial.Group,
                ItemNumber: firstMaterial.Number,
                TargetLevel: searchLevel,
                MonsterNumber: source.MonsterNumber,
                MapNumber: source.MapNumber,
                MonsterName: source.MonsterName,
                MapName: source.MapName);
        }

        this._logger.LogWarning(
            "[MaterialKnowledge] 玩家等级 {Level} 不足以刷取 {Type} Lv.{GameLevel} 所需的门票材料 (最高需求等级={MaxLv})",
            playerLevel,
            miniGameType,
            gameLevel,
            maxRequiredLevel);

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
/// <param name="ItemLevel">材料等级（事件门票物品的 DropItemGroup.ItemLevel），默认 1.</param>
public record DropSourceInfo(short MonsterNumber, string MonsterName, ushort MapNumber, string MapName, byte ItemLevel = 1);

/// <summary>
/// 门票材料掉落信息 — 告诉 AI 刷哪个怪、在哪刷、刷什么等级的材料。
/// </summary>
/// <param name="ItemGroup">物品组.</param>
/// <param name="ItemNumber">物品编号.</param>
/// <param name="TargetLevel">要刷的材料等级（对应 DropItemGroup.ItemLevel）.</param>
/// <param name="MonsterNumber">目标怪物编号.</param>
/// <param name="MapNumber">目标地图编号.</param>
/// <param name="MonsterName">目标怪物名称.</param>
/// <param name="MapName">目标地图名称.</param>
public record TicketDropInfo(int ItemGroup, int ItemNumber, byte TargetLevel, short MonsterNumber, ushort MapNumber, string MonsterName, string MapName);
