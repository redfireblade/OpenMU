// <copyright file="MaterialRequirementService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;

/// <summary>
/// 材料需求分析服务 — 统一的材料缺口分析入口。
/// 知道"我要做物品X，还需要什么材料、在哪里打"。
/// 登录时被 MissionBoardService.InitializeAsync 调用。
/// </summary>
public sealed class MaterialRequirementService
{
    private readonly GameConfiguration _config;
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;
    private readonly MaterialKnowledgeService _knowledge;

    /// <summary>
    /// Initializes a new instance of the <see cref="MaterialRequirementService"/> class.
    /// </summary>
    /// <param name="config">The game configuration.</param>
    /// <param name="player">The AI player instance.</param>
    /// <param name="adapter">The game adapter for reading player state.</param>
    /// <param name="logger">The logger.</param>
    public MaterialRequirementService(
        GameConfiguration config,
        AiPlayer player,
        IGameAdapter adapter,
        ILogger logger)
    {
        this._config = config;
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
        this._knowledge = new MaterialKnowledgeService(config, logger);
    }

    /// <summary>
    /// 全量分析 — 扫所有已知合成场景，返回材料需求知识列表。
    /// 由 MissionBoardService.InitializeAsync 在登录时调用。
    /// </summary>
    public List<MaterialKnowledgeEntry> AnalyzeAll()
    {
        var results = new List<MaterialKnowledgeEntry>();
        var seen = new HashSet<(int Group, int Number)>();

        // 1) 扫描所有怪物的 ItemCraftings → 找出所有可合成的物品
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

                foreach (var resultItem in crafting.SimpleCraftingSettings.ResultItems)
                {
                    if (resultItem?.ItemDefinition is null)
                    {
                        continue;
                    }

                    var def = resultItem.ItemDefinition;
                    var key = (def.Group, (int)def.Number);
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    var entry = this.AnalyzeTarget(
                        def.Group,
                        def.Number,
                        (string)def.Name,
                        targetLevel: 0);
                    if (entry is not null)
                    {
                        results.Add(entry);
                    }
                }
            }
        }

        // 2) 扫描迷你游戏门票合成需求
        var ticketTargets = this.ScanTicketTargets();
        foreach (var (group, number, name, level) in ticketTargets)
        {
            var key = (group, number);
            if (!seen.Add(key))
            {
                continue;
            }

            var entry = this.AnalyzeTarget(group, number, name, (byte)level);
            if (entry is not null)
            {
                results.Add(entry);
            }
        }

        // 3) 翅膀合成
        var wingTargets = this.ScanWingTargets();
        foreach (var (group, number, name) in wingTargets)
        {
            var key = (group, number);
            if (!seen.Add(key))
            {
                continue;
            }

            var entry = this.AnalyzeTarget(group, number, name);
            if (entry is not null)
            {
                results.Add(entry);
            }
        }

        this._logger.LogInformation(
            "[MaterialRequirement] 全量分析完成: {Count} 个合成目标",
            results.Count);

        return results;
    }

    /// <summary>
    /// 分析特定物品的合成材料缺口。
    /// 递归处理中间产物（如合成恶魔眼需要先有混沌宝石）。
    /// </summary>
    private MaterialKnowledgeEntry? AnalyzeTarget(
        int targetGroup, int targetNumber, string description,
        byte targetLevel = 0)
    {
        var materials = this.GetMaterialsForTarget(targetGroup, targetNumber);
        if (materials.Count == 0)
        {
            this._logger.LogDebug(
                "[MaterialRequirement] {Desc}(G{Group}N{Num}) 无合成配方，跳过",
                description,
                targetGroup,
                targetNumber);
            return null;
        }

        var playerLevel = this._adapter.GetPlayerLevel();
        var entries = new List<MaterialItemState>();

        foreach (var mat in materials)
        {
            var state = new MaterialItemState
            {
                Group = mat.Group,
                Number = mat.Number,
                Name = mat.Name,
                RequiredLevel = mat.RequiredLevel,
                RequiredCount = mat.RequiredCount,
            };

            // 检查背包和仓库
            state.InInventory = this.CountInInventory(mat.Group, mat.Number, mat.RequiredLevel);
            state.InVault = this.CountInVault(mat.Group, mat.Number, mat.RequiredLevel);

            if (state.IsSufficient)
            {
                state.Status = MaterialStatus.Sufficient;
            }
            else if (state.InVault > 0)
            {
                state.Status = MaterialStatus.NeedVault;
            }
            else
            {
                // 查找掉落来源
                var dropSource = this.FindBestDropSource(mat, playerLevel);
                if (dropSource is not null)
                {
                    state.Status = MaterialStatus.NeedFarm;
                    state.FarmMonsterNumber = dropSource.MonsterNumber;
                    state.FarmMonsterName = dropSource.MonsterName;
                    state.FarmMapNumber = dropSource.MapNumber;
                    state.FarmMapName = dropSource.MapName;
                    state.FarmItemLevel = dropSource.ItemLevel;
                }
                else
                {
                    // 检查是否是中间产物（可通过合成获得）
                    var subMaterials = this.GetMaterialsForTarget(mat.Group, mat.Number);
                    if (subMaterials.Count > 0)
                    {
                        state.Status = MaterialStatus.NeedCraftFirst;
                    }
                    else
                    {
                        state.Status = MaterialStatus.Unknown;
                    }
                }
            }

            entries.Add(state);
        }

        return new MaterialKnowledgeEntry
        {
            TargetDescription = description,
            TargetGroup = targetGroup,
            TargetNumber = targetNumber,
            TargetLevel = targetLevel,
            Materials = entries,
        };
    }

    /// <summary>检查背包中某物品的数量（按等级过滤）。</summary>
    private int CountInInventory(int group, int number, byte level)
    {
        var inv = this._player.Inventory;
        if (inv?.Items is null)
        {
            return 0;
        }

        return inv.Items.Count(i =>
            i.Definition?.Group == group &&
            i.Definition?.Number == number &&
            i.Durability > 0 &&
            (level == 0 || i.Level == level));
    }

    /// <summary>检查仓库中某物品的数量（按等级过滤）。</summary>
    private int CountInVault(int group, int number, byte level)
    {
        var vault = this._player.Account?.Vault;
        if (vault?.Items is null)
        {
            return 0;
        }

        return vault.Items.Count(i =>
            i.Definition?.Group == group &&
            i.Definition?.Number == number &&
            i.Durability > 0 &&
            (level == 0 || i.Level == level));
    }

    /// <summary>查合成配方 → 返回所需材料列表。</summary>
    private List<MaterialItem> GetMaterialsForTarget(int group, int number)
    {
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

                var produces = crafting.SimpleCraftingSettings.ResultItems
                    .Any(r => r?.ItemDefinition?.Group == group && r?.ItemDefinition?.Number == number);
                if (!produces)
                {
                    continue;
                }

                var materials = new List<MaterialItem>();
                foreach (var req in crafting.SimpleCraftingSettings.RequiredItems)
                {
                    if (req is null)
                    {
                        continue;
                    }

                    var possibleItem = req.PossibleItems.FirstOrDefault();
                    if (possibleItem is null)
                    {
                        continue;
                    }

                    var requiredCount = req.MinimumAmount > 0 ? (int)req.MinimumAmount : 1;
                    materials.Add(new MaterialItem(
                        Group: possibleItem.Group,
                        Number: possibleItem.Number,
                        Name: (string)possibleItem.Name,
                        RequiredLevel: req.MinimumItemLevel,
                        RequiredCount: requiredCount));
                }

                return materials;
            }
        }

        return new List<MaterialItem>();
    }

    /// <summary>查掉落来源 → 返回最佳来源（等级匹配）。</summary>
    private DropSourceInfo? FindBestDropSource(MaterialItem material, int playerLevel)
    {
        var sources = this._knowledge.GetDropSources(material.Group, material.Number);
        if (sources.Count == 0)
        {
            return null;
        }

        // 优先选 playerLevel 可打的最高等级来源
        var monsterLevels = new Dictionary<short, byte>();
        foreach (var s in sources)
        {
            if (!monsterLevels.ContainsKey(s.MonsterNumber))
            {
                var ml = this._knowledge.GetMonsterLevelRange(s.MonsterNumber);
                if (ml.HasValue)
                {
                    monsterLevels[s.MonsterNumber] = ml.Value.MinLevel;
                }
            }
        }

        var viable = sources
            .Where(s => !monsterLevels.ContainsKey(s.MonsterNumber) || monsterLevels[s.MonsterNumber] <= playerLevel)
            .OrderByDescending(s => s.ItemLevel)
            .ThenBy(s => s.MonsterNumber)
            .ToList();

        return viable.Count > 0 ? viable[0] : sources[0];
    }

    /// <summary>
    /// 合成后重评估 — 检查目标物品是否已在背包(合成成功)。
    /// 如果合成失败(消耗了材料但没出目标物品)，重新分析材料缺口。
    /// </summary>
    /// <param name="targetGroup">目标物品 Group。</param>
    /// <param name="targetNumber">目标物品 Number。</param>
    /// <returns>如果合成成功返回 IsSatisfied=true 的条目；如果合成失败返回重新分析的材料缺口。</returns>
    public MaterialKnowledgeEntry? ReevaluateAfterCraft(int targetGroup, int targetNumber)
    {
        // 1. 检查目标物品是否已经在背包中(合成成功)
        var hasTarget = this._player.Inventory?.Items.Any(i =>
            i.Definition?.Group == targetGroup &&
            i.Definition?.Number == targetNumber &&
            i.Durability > 0) ?? false;

        if (hasTarget)
        {
            this._logger.LogInformation(
                "[MaterialRequirement] 合成成功: G{Group}N{Num} 已在背包",
                targetGroup,
                targetNumber);
            return new MaterialKnowledgeEntry
            {
                TargetDescription = $"物品G{targetGroup}N{targetNumber}",
                TargetGroup = targetGroup,
                TargetNumber = targetNumber,
                IsSatisfied = true,
                Materials = new List<MaterialItemState>(),
            };
        }

        // 2. 还没在背包 → 合成失败 → 重新分析缺口
        this._logger.LogWarning(
            "[MaterialRequirement] 重评估: G{Group}N{Num} 尚未在背包，检查材料缺口",
            targetGroup,
            targetNumber);

        var entry = this.AnalyzeTarget(targetGroup, targetNumber, $"物品G{targetGroup}N{targetNumber}");
        return entry;
    }

    /// <summary>扫描所有玩家可合成的迷你游戏门票。</summary>
    private List<(int Group, int Number, string Name, int Level)> ScanTicketTargets()
    {
        var targets = new List<(int Group, int Number, string Name, int Level)>();

        foreach (var miniGameDef in this._config.MiniGameDefinitions)
        {
            if (miniGameDef?.TicketItem is null)
            {
                continue;
            }

            var ticket = miniGameDef.TicketItem;
            targets.Add((
                ticket.Group,
                ticket.Number,
                (string)ticket.Name,
                miniGameDef.TicketItemLevel));
        }

        return targets;
    }

    /// <summary>扫描翅膀合成材料缺口。</summary>
    private List<(int Group, int Number, string Name)> ScanWingTargets()
    {
        // 翅膀合成通常产出 Group=12 的物品（翅膀类）
        // 从配置中找所有 ItemSlot 对应背部/翅膀位的物品的合成配方
        // 在 MU 中翅膀槽位编号包含 7（左翅膀槽）
        var wingSlotTypes = this._config.ItemSlotTypes
            .Where(s => s?.ItemSlots is not null && s.ItemSlots.Contains(7))
            .ToList();

        if (wingSlotTypes.Count == 0)
        {
            // Fallback: 已知翅膀物品组
            return new List<(int Group, int Number, string Name)>
            {
                (12, 36, "Fairy Wing"),
                (12, 37, "Magic Wing"),
                (12, 38, "Power Wing"),
            };
        }

        var results = new List<(int Group, int Number, string Name)>();
        foreach (var itemDef in this._config.Items)
        {
            if (itemDef is null)
            {
                continue;
            }

            if (itemDef.ItemSlot is null || !wingSlotTypes.Contains(itemDef.ItemSlot))
            {
                continue;
            }

            // 检查是否有合成配方产出这个物品
            var hasRecipe = false;
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

                    if (crafting.SimpleCraftingSettings.ResultItems
                        .Any(r => r?.ItemDefinition == itemDef))
                    {
                        hasRecipe = true;
                        break;
                    }
                }

                if (hasRecipe)
                {
                    break;
                }
            }

            if (hasRecipe)
            {
                results.Add((itemDef.Group, itemDef.Number, (string)itemDef.Name));
            }
        }

        return results;
    }
}

/// <summary>
/// 合成材料项 — 描述一个物品的合成需要什么原材料。
/// </summary>
/// <param name="Group">物品组。</param>
/// <param name="Number">物品编号。</param>
/// <param name="Name">物品名称。</param>
/// <param name="RequiredLevel">所需最小等级。</param>
/// <param name="RequiredCount">所需数量。</param>
public record MaterialItem(
    int Group,
    int Number,
    string Name,
    byte RequiredLevel,
    int RequiredCount);
