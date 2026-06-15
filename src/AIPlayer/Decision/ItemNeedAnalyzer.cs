// <copyright file="ItemNeedAnalyzer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using Microsoft.Extensions.Logging;

/// <summary>
/// 道具需求分析器 — Layer 5。
/// 检查背包/角色状态，按优先级生成任务。
/// 顺序：合成装备->打金->练级（永不完成）
///
/// Check 3: Ticket crafting detection (BloodCastle/DevilSquare)
/// Check 4: Equipment upgrade detection (+X gear + gems)
/// Check 5: Item pickup (inventory space check)
/// </summary>
public sealed class ItemNeedAnalyzer
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;
    private readonly ValueAssessmentService? _valueAssessment;

    private const int GoldThreshold = 10000;
    private const int GoldFarmingPriority = 28;
    private const int CraftingPriority = 25;
    private const int LevelingPriority = 35;

    // Check 5: Item pickup needs
    private const int PickupPriority = 30; // 介于 farming(28) 和 crafting(25) 之间，略低于 crafting
    private const int MinFreeInventorySlots = 3;

    // Check 3: Ticket crafting thresholds
    private const int TicketMinLevel = 15;
    private const float TicketMinHpRatio = 0.5f;

    // Check 4: Equipment upgrade thresholds
    private const int EquipUpgradePriority = 27;

    public ItemNeedAnalyzer(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
        this._valueAssessment = null;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemNeedAnalyzer"/> class with value assessment.
    /// </summary>
    public ItemNeedAnalyzer(AiPlayer player, IGameAdapter adapter, ILogger logger, ValueAssessmentService valueAssessment)
        : this(player, adapter, logger)
    {
        this._valueAssessment = valueAssessment;
    }

    public List<MissionItem> Scan()
    {
        var result = new List<MissionItem>();

        var craftMission = CheckCraftingNeeds();
        if (craftMission is not null) result.Add(craftMission);

        if (this._player.Money < GoldThreshold)
        {
            result.Add(CreateGoldFarmingMission());
        }

        // Check 3: Ticket crafting detection
        var ticketMission = CheckTicketCraftingNeeds();
        if (ticketMission is not null) result.Add(ticketMission);

        // Check 4: Equipment upgrade check
        var equipMission = CheckEquipmentUpgradeNeeds();
        if (equipMission is not null) result.Add(equipMission);

        // Check 5: Item pickup need
        var pickupMission = CheckItemPickupNeed();
        if (pickupMission is not null) result.Add(pickupMission);

        return result;
    }

    /// <summary>
    /// 扫描背包和仓库，找出当前可做的合成事项。
    /// 返回 MissionItem 列表，按价值排序（高优先级在前）。
    /// 同步方法 — 不涉及任何异步调用。
    /// 覆盖场景: 翅膀合成、混沌武器合成、装备升级、血瓶合成。
    /// </summary>
    public List<MissionItem> ScavengeForCrafting()
    {
        var result = new List<MissionItem>();

        // 合并背包+仓库的物品集合用于检查（仓库为 Account.Vault.Items）
        var allItems = new List<Item>();
        if (this._player.Inventory?.Items is not null)
        {
            allItems.AddRange(this._player.Inventory.Items);
        }

        if (this._player.Account?.Vault?.Items is not null)
        {
            allItems.AddRange(this._player.Account.Vault.Items);
        }

        // 1) 翅膀合成 — 检查是否有 洛克之羽(Group=13,Number=11) / 神鹰羽毛(13,16)
        var wingMission = this.CheckWingCraftNeeds(allItems);
        if (wingMission is not null) result.Add(wingMission);

        // 2) 混沌武器合成 — +4+4 装备 + 混沌宝石(12,15)
        var chaosWeaponMission = this.CheckChaosWeaponCraftNeeds(allItems);
        if (chaosWeaponMission is not null) result.Add(chaosWeaponMission);

        // 3) 装备升级 — +X 装备 + 祝福(12,14)/灵魂(12,13)/混沌(12,15)
        var vaultAwareEquip = this.CheckVaultAwareEquipUpgrade(allItems);
        if (vaultAwareEquip is not null) result.Add(vaultAwareEquip);

        // 4) 血瓶合成 — 低血且背包空位充足时，生成跳转商店任务
        var potionMission = this.CheckHpPotionCraftNeeds();
        if (potionMission is not null) result.Add(potionMission);

        // 按优先级排序（值越小越优先）
        result.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return result;
    }

    private MissionItem? CheckCraftingNeeds()
    {
        var inv = this._player.Inventory;
        if (inv is null) return null;

        var hasChaos = inv.Items.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 15);

        // 检查是否有可用装备作为合成材料（排除 A 级以上高价值装备）
        var hasEquipToUpgrade = inv.Items.Count(i =>
        {
            if (i.ItemSlot <= 11) return false; // 已装备

            // 高价值保护：A 级以上的装备不用于合成材料
            if (this._valueAssessment is not null)
            {
                var assessment = this._valueAssessment.Evaluate(i);
                if (assessment.Tier >= ValueTier.A)
                {
                    this._logger.LogDebug(
                        "[ItemNeed] 保护高价值合成材料: {Item}(Slot={Slot}) 层级={Tier}",
                        i.Definition?.Name ?? "?", i.ItemSlot, assessment.Tier);
                    return false;
                }
            }

            return true;
        }) >= 2;

        if (hasChaos && hasEquipToUpgrade)
        {
            this._logger.LogInformation("[ItemNeed] 有 Chaos 材料+装备 -> 生成合成任务");
            return new MissionItem
            {
                Id = "craft_chaos_upgrade",
                Title = "合成 -- 混沌装备升级",
                Priority = CraftingPriority,
                Type = MissionType.ItemFarm,
                Category = QuestCategory.AiCustom,
                Module = "crafting_executor",
                Source = QuestSource.AiCustom,
                FailureRetryable = true,
                MaxRepeatCount = -1,
            };
        }

        return null;
    }

    /// <summary>
    /// Check 3: 门票合成检测。
    /// 检查角色血量正常且等级达标，背包有祝福/灵魂/混沌宝石但没有对应门票时，
    /// 生成门票合成任务。
    /// </summary>
    private MissionItem? CheckTicketCraftingNeeds()
    {
        var inv = this._player.Inventory;
        if (inv is null) return null;

        // HP 检查：血量必须健康
        var maxHp = this._adapter.GetMaxHp();
        var curHp = this._adapter.GetCurrentHp();
        if (maxHp > 0 && (float)curHp / maxHp < TicketMinHpRatio)
        {
            return null;
        }

        // 等级检查：低等级不需要门票
        var level = this._adapter.GetPlayerLevel();
        if (level < TicketMinLevel)
        {
            return null;
        }

        // 检查是否有合成材料：祝福(Group=12,Num=14) + 灵魂(Group=12,Num=13) + 混沌(Group=12,Num=15)
        var hasBless = inv.Items.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 14);
        var hasSoul = inv.Items.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 13);
        var hasChaos = inv.Items.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 15);

        if (!hasBless || !hasSoul || !hasChaos)
        {
            return null;
        }

        // 检查是否已有门票（Group=13 的卷轴类物品）
        var hasTicket = inv.Items.Any(i => i.Definition?.Group == 13);
        if (hasTicket)
        {
            return null;
        }

        // 根据等级决定门票类型和等级
        // BloodCastle: 15-80级低级, 81-130中级, 131+高级
        // DevilSquare: 15-130低级, 131+高级
        string ticketType;
        int ticketLevel;

        if (level <= 80)
        {
            ticketType = "BloodCastle";
            ticketLevel = 1;
        }
        else if (level <= 130)
        {
            ticketType = "BloodCastle";
            ticketLevel = 2;
        }
        else
        {
            ticketType = "BloodCastle";
            ticketLevel = 3;
        }

        var missionId = $"craft_ticket_{ticketType}_{ticketLevel}";

        this._logger.LogInformation(
            "[ItemNeed] Check 3: 有门票材料(祝福+灵魂+混沌)且无已有门票, 等级={Level}, 生成任务={Id}",
            level, missionId);

        return new MissionItem
        {
            Id = missionId,
            Title = $"合成 -- {ticketType}门票 Lv{ticketLevel}",
            Priority = GoldFarmingPriority, // 略高于打金优先级
            Type = MissionType.ItemFarm,
            Category = QuestCategory.AiCustom,
            Module = "crafting_executor",
            Source = QuestSource.AiCustom,
            FailureRetryable = true,
            MaxRepeatCount = -1,
        };
    }

    /// <summary>
    /// Check 4: 装备强化检测。
    /// 检查背包中有已强化的装备（Item.Level>0, 非已装备）且有强化宝石
    /// （祝福/混沌/灵魂）时，生成装备强化合成任务。
    /// </summary>
    private MissionItem? CheckEquipmentUpgradeNeeds()
    {
        var inv = this._player.Inventory;
        if (inv is null) return null;

        // 检查是否有可强化的背包装备（排除 A 级以上高价值装备）
        var upgradedEquipment = inv.Items.Any(i =>
        {
            if (i.ItemSlot <= 11) return false; // 已装备
            if (i.Level <= 0) return false;
            if (i.Definition is null) return false;

            // 高价值保护：A 级以上的装备不用于强化合成材料
            if (this._valueAssessment is not null)
            {
                var assessment = this._valueAssessment.Evaluate(i);
                if (assessment.Tier >= ValueTier.A)
                {
                    return false;
                }
            }

            return true;
        });

        if (!upgradedEquipment)
        {
            return null;
        }

        // 检查是否有强化宝石：祝福(Group=12,Num=14) + 混沌(Group=12,Num=15) + 灵魂(Group=12,Num=13)
        var hasBless = inv.Items.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 14);
        var hasChaos = inv.Items.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 15);
        var hasSoul = inv.Items.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 13);

        if (!hasBless || !hasChaos || !hasSoul)
        {
            return null;
        }

        this._logger.LogInformation(
            "[ItemNeed] Check 4: 有已强化装备+宝石(祝福+混沌+灵魂), 生成装备强化任务");

        return new MissionItem
        {
            Id = "craft_equip_upgrade",
            Title = "合成 -- 装备强化升级",
            Priority = EquipUpgradePriority,
            Type = MissionType.ItemFarm,
            Category = QuestCategory.AiCustom,
            Module = "crafting_executor",
            Source = QuestSource.AiCustom,
            FailureRetryable = true,
            MaxRepeatCount = -1,
        };
    }

    /// <summary>
    /// Check 5: 物品拾取需求检测。
    /// 检查背包是否还有空位。如果有足够的空位且不处于紧急状态，
    /// 生成 item_pickup 任务让 AI 主动寻找地面掉落物品。
    /// </summary>
    private MissionItem? CheckItemPickupNeed()
    {
        var inv = this._player.Inventory;
        if (inv is null) return null;

        // 计算可用空位：背包总容量 - 已占用数（排除已装备位 0-11）
        // 简单方法：检查是否有足够空位
        var freeSlots = GetFreeInventorySlots(inv);
        if (freeSlots < MinFreeInventorySlots)
        {
            return null;
        }

        // HP 检查：如果血量过低，优先恢复而非拾取
        var maxHp = this._adapter.GetMaxHp();
        var curHp = this._adapter.GetCurrentHp();
        if (maxHp > 0 && (float)curHp / maxHp < 0.4f)
        {
            return null;
        }

        this._logger.LogInformation(
            "[ItemNeed] Check 5: 背包有空位({FreeSlots}), 生成拾取任务", freeSlots);

        return new MissionItem
        {
            Id = "item_pickup",
            Title = "拾取 -- 地面物品收集",
            Priority = PickupPriority,
            Type = MissionType.ItemFarm,
            Category = QuestCategory.AiCustom,
            Module = "item_pickup_manager",
            Source = QuestSource.AiCustom,
            FailureRetryable = true,
            MaxRepeatCount = -1,
        };
    }

    /// <summary>
    /// 翅膀合成检测 — 扫描背包+仓库。
    /// 1级翅膀: 创造宝石(12,16) + 混沌(12,15) + 卓越+4装备
    /// 2级翅膀: 洛克之羽(13,11) + 混沌(12,15) + 卓越+4装备
    /// 3级翅膀: 神鹰羽毛(13,16) + 神鹰火种(13,19) + 混沌(12,15) + 卓越+9装备
    /// </summary>
    private MissionItem? CheckWingCraftNeeds(List<Item> allItems)
    {
        // 检查背包已有翅膀（Group 12 特定翅膀物品），避免重复合成
        var hasWing = allItems.Any(i => i.Definition?.Group == 12 && i.Definition?.Number >= 30 && i.Definition?.Number <= 49);
        if (hasWing)
        {
            return null;
        }

        // 2级翅膀检测: 洛克之羽(13,11) + 混沌(12,15) + 卓越+4装备
        var hasLochFeather = allItems.Any(i => i.Definition?.Group == 13 && i.Definition?.Number == 11 && i.Durability > 0);
        var hasChaos = allItems.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 15 && i.Durability > 0);
        var hasExcellentEquip = allItems.Any(i =>
            i.ItemSlot > 11
            && i.Level >= 4
            && i.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Excellent));

        if (hasLochFeather && hasChaos && hasExcellentEquip)
        {
            this._logger.LogInformation(
                "[ItemNeed] Scavenge: 有洛克之羽+混沌+卓越+4装备 -> 生成2级翅膀合成任务");
            return new MissionItem
            {
                Id = "craft_wing_level2",
                Title = "合成 -- 2级翅膀",
                Priority = 21, // 高优先级
                Type = MissionType.ItemFarm,
                Category = QuestCategory.AiCustom,
                Module = "crafting_executor",
                Source = QuestSource.AiCustom,
                FailureRetryable = true,
                MaxRepeatCount = -1,
            };
        }

        // 3级翅膀检测: 神鹰羽毛(13,16) + 神鹰火种(13,19) + 混沌(12,15) + 卓越+9装备
        var hasFireHawkFeather = allItems.Any(i => i.Definition?.Group == 13 && i.Definition?.Number == 16 && i.Durability > 0);
        var hasFireHawkSeed = allItems.Any(i => i.Definition?.Group == 13 && i.Definition?.Number == 19 && i.Durability > 0);
        var hasExcellentEquip9 = allItems.Any(i =>
            i.ItemSlot > 11
            && i.Level >= 9
            && i.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Excellent));

        if (hasFireHawkFeather && hasFireHawkSeed && hasChaos && hasExcellentEquip9)
        {
            this._logger.LogInformation(
                "[ItemNeed] Scavenge: 有神鹰羽毛+神鹰火种+混沌+卓越+9装备 -> 生成3级翅膀合成任务");
            return new MissionItem
            {
                Id = "craft_wing_level3",
                Title = "合成 -- 3级翅膀",
                Priority = 18, // 非常高优先级
                Type = MissionType.ItemFarm,
                Category = QuestCategory.AiCustom,
                Module = "crafting_executor",
                Source = QuestSource.AiCustom,
                FailureRetryable = true,
                MaxRepeatCount = -1,
            };
        }

        // 1级翅膀检测: 创造宝石(12,16) + 混沌(12,15) + 卓越+4装备（无洛克之羽时）
        var hasCreation = allItems.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 16 && i.Durability > 0);

        if (hasCreation && hasChaos && hasExcellentEquip)
        {
            this._logger.LogInformation(
                "[ItemNeed] Scavenge: 有创造宝石+混沌+卓越+4装备 -> 生成1级翅膀合成任务");
            return new MissionItem
            {
                Id = "craft_wing_level1",
                Title = "合成 -- 1级翅膀",
                Priority = 23,
                Type = MissionType.ItemFarm,
                Category = QuestCategory.AiCustom,
                Module = "crafting_executor",
                Source = QuestSource.AiCustom,
                FailureRetryable = true,
                MaxRepeatCount = -1,
            };
        }

        return null;
    }

    /// <summary>
    /// 混沌武器合成检测 — 扫描背包+仓库。
    /// 条件: 2 件 +4 以上装备（非已装备） + 混沌宝石(12,15)。
    /// 排除 A 级以上高价值装备（由 ValueAssessmentService 保护）。
    /// MU 混沌合成公式: 混沌 + 装备+4 + 装备+4 → 随机混沌武器/道具。
    /// </summary>
    private MissionItem? CheckChaosWeaponCraftNeeds(List<Item> allItems)
    {
        var hasChaos = allItems.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 15 && i.Durability > 0);
        if (!hasChaos)
        {
            return null;
        }

        // 计数可用的 +4 以上装备（背包非已装备位，排除 A 级以上高价值）
        var availableEquipCount = allItems.Count(i =>
        {
            if (i.ItemSlot <= 11) return false; // 已装备
            if (i.Level < 4) return false;       // 等级不足
            if (i.Definition is null) return false;

            // 高价值保护：A 级以上的装备不用于合成材料
            if (this._valueAssessment is not null)
            {
                var assessment = this._valueAssessment.Evaluate(i);
                if (assessment.Tier >= ValueTier.A)
                {
                    return false;
                }
            }

            return true;
        });

        if (availableEquipCount >= 2)
        {
            this._logger.LogInformation(
                "[ItemNeed] Scavenge: 有混沌+{Count}件+4装备 -> 生成混沌武器合成任务",
                availableEquipCount);
            return new MissionItem
            {
                Id = "craft_chaos_weapon",
                Title = "合成 -- 混沌武器",
                Priority = CraftingPriority,
                Type = MissionType.ItemFarm,
                Category = QuestCategory.AiCustom,
                Module = "crafting_executor",
                Source = QuestSource.AiCustom,
                FailureRetryable = true,
                MaxRepeatCount = -1,
            };
        }

        return null;
    }

    /// <summary>
    /// 装备升级检测（仓库感知版）— 扫描背包+仓库。
    /// 检查有可强化的背包装备（Item.Level>0, 非已装备, 非A级以上）
    /// 且有强化宝石（祝福+灵魂+混沌）时，生成装备强化合成任务。
    /// 扩展了原有 CheckEquipmentUpgradeNeeds 的仓库感知。
    /// </summary>
    private MissionItem? CheckVaultAwareEquipUpgrade(List<Item> allItems)
    {
        // 检查是否有可强化的背包装备（排除 A 级以上高价值装备）
        var upgradedEquipment = allItems.Any(i =>
        {
            if (i.ItemSlot <= 11) return false; // 已装备
            if (i.Level <= 0) return false;
            if (i.Definition is null) return false;

            // 高价值保护：A 级以上的装备不用于强化合成材料
            if (this._valueAssessment is not null)
            {
                var assessment = this._valueAssessment.Evaluate(i);
                if (assessment.Tier >= ValueTier.A)
                {
                    return false;
                }
            }

            return true;
        });

        if (!upgradedEquipment)
        {
            return null;
        }

        // 检查是否有强化宝石：祝福(12,14) + 混沌(12,15) + 灵魂(12,13)
        // 从 allItems 检查（含仓库）
        var hasBless = allItems.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 14 && i.Durability > 0);
        var hasChaos = allItems.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 15 && i.Durability > 0);
        var hasSoul = allItems.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 13 && i.Durability > 0);

        if (!hasBless || !hasChaos || !hasSoul)
        {
            return null;
        }

        this._logger.LogInformation(
            "[ItemNeed] Scavenge: 有+装备+宝石(仓库感知), 生成装备强化任务");

        return new MissionItem
        {
            Id = "craft_equip_upgrade_vault",
            Title = "合成 -- 装备强化(含仓库)",
            Priority = EquipUpgradePriority,
            Type = MissionType.ItemFarm,
            Category = QuestCategory.AiCustom,
            Module = "crafting_executor",
            Source = QuestSource.AiCustom,
            FailureRetryable = true,
            MaxRepeatCount = -1,
        };
    }

    /// <summary>
    /// 血瓶合成检测 — HP 低于 40% 且背包无合成材料时，生成跳转商店买药任务。
    /// 不涉及混沌合成，而是生成 survival 模块的补给任务。
    /// </summary>
    private MissionItem? CheckHpPotionCraftNeeds()
    {
        var maxHp = this._adapter.GetMaxHp();
        var curHp = this._adapter.GetCurrentHp();
        if (maxHp <= 0 || (float)curHp / maxHp >= 0.5f)
        {
            return null;
        }

        // 检查背包是否有已有药水: Group 14 通常为药水类
        var hasPotion = this._player.Inventory?.Items.Any(i =>
            i.Definition?.Group == 14 && i.Durability > 0) ?? false;
        if (hasPotion)
        {
            return null;
        }

        this._logger.LogInformation(
            "[ItemNeed] Scavenge: HP={Hp}/{MaxHp} 低血且无药水 -> 生成补给任务",
            curHp, maxHp);

        return new MissionItem
        {
            Id = "survival_potion_refill",
            Title = "补给 -- 购买药水",
            Priority = 10,  // 高优先级
            Type = MissionType.Survival,
            Category = QuestCategory.AiCustom,
            Module = "survival",
            Source = QuestSource.AiCustom,
            FailureRetryable = true,
            MaxRepeatCount = -1,
        };
    }

    /// <summary>
    /// 计算背包可用空位数量。
    /// 基础背包：8行×8列=64格（从 ItemSlot 12 开始，0-11为已装备位）。
    /// 扩展包每格额外增加。
    /// </summary>
    private static int GetFreeInventorySlots(IInventoryStorage inv)
    {
        // 基础背包行数
        const int baseRows = 8;
        const int rowSize = 8;
        const int equipSlots = 12; // ItemSlot 0-11 为已装备位

        var totalInventorySlots = equipSlots + (baseRows * rowSize);
        var occupiedCount = inv.Items.Count(i => i.ItemSlot < totalInventorySlots && i.ItemSlot > equipSlots);
        var freeSlots = (baseRows * rowSize) - occupiedCount;

        return Math.Max(0, freeSlots);
    }

    private MissionItem CreateGoldFarmingMission()
    {
        this._logger.LogInformation("[ItemNeed] 金币不足 ({Money}/{Threshold}) -> 生成打金任务", this._player.Money, GoldThreshold);
        return new MissionItem
        {
            Id = "item_gold_farming",
            Title = "打金 -- 收集金币",
            Priority = GoldFarmingPriority,
            Type = MissionType.Survival,
            Category = QuestCategory.AiCustom,
            Module = "survival",
            Source = QuestSource.AiCustom,
            FailureRetryable = true,
            MaxRepeatCount = -1,
        };
    }
}
