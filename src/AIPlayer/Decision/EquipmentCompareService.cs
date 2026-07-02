// <copyright file="EquipmentCompareService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.AIPlayer.Knowledge;

/// <summary>
/// 装备自动对比换装服务 — 背包有更好装备时自动换上。
///
/// 执行时机：
///   每次拾取物品后，由 HeartbeatService.BeatAsync 检测。
///   遍历背包储物格中所有装备类物品，与身上装备对比，择优换装。
///
/// 换装流程：
///   1. 确定物品对应装备栏位（从 ItemDefinition.ItemSlot.ItemSlots 推导）
///   2. 目标栏位为空 → 直接穿上
///   3. 目标栏位已有物品 → 评分对比，新装备更好才换
///   4. 换装时先卸下旧装备→背包空格，再穿上新装备
/// </summary>
public sealed class EquipmentCompareService
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;
    private readonly MoveItemAction _moveItemAction = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="EquipmentCompareService"/> class.
    /// </summary>
    /// <param name="player">AI 玩家实例。</param>
    /// <param name="adapter">游戏操作适配器。</param>
    /// <param name="logger">日志。</param>
    public EquipmentCompareService(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
    }

    /// <summary>
    /// 自动换装检测：遍历背包，发现更好装备就换上。
    /// 每 tick 只处理一件装备，避免单次操作过多。
    /// </summary>
    public async ValueTask AutoEquipIfBetterAsync()
    {
        var inv = this._player.Inventory;
        if (inv is null)
        {
            return;
        }

        // 快照背包物品（只取非装备格，排除已装备的 0-11），
        // 避免遍历过程中 MoveItemAction 修改 Items 集合导致枚举异常。
        var bagItems = inv.Items
            .Where(i => i.ItemSlot > InventoryConstants.LastEquippableItemSlotIndex)
            .ToList();

        foreach (var item in bagItems)
        {
            // 跳过非装备类物品
            var equipSlot = GetEquipmentSlot(item);
            if (equipSlot is null)
            {
                continue;
            }

            // 跳过不满足穿戴条件的物品（等级/职业，引擎自行判断）
            if (!this.CanWearItem(item))
            {
                continue;
            }

            // 重新获取 inv.Items 查找装备位当前情况（每次重新查询保证最新）
            var currentEquipped = inv.Items.FirstOrDefault(i =>
                i.ItemSlot == equipSlot.Value && i.ItemSlot <= InventoryConstants.LastEquippableItemSlotIndex);

            if (currentEquipped is null)
            {
                // 装备位为空 → 直接穿上
                this._logger.LogInformation(
                    "[EquipCompare] 🆕 装备位 {Slot} 为空，穿上 {Name} (Slot={FromSlot})",
                    equipSlot.Value,
                    item.Definition?.Name ?? "?",
                    item.ItemSlot);

                await this._moveItemAction.MoveItemAsync(
                    this._player,
                    item.ItemSlot,
                    Storages.Inventory,
                    equipSlot.Value,
                    Storages.Inventory).ConfigureAwait(false);
                return;
            }

            // 如果装备位上的是药水等非装备物品（无 ItemSlot），
            // 需要先把药水移到背包空格，再穿装备
            if (currentEquipped.Definition?.ItemSlot is null)
            {
                // 找个背包空格用来放药水
                var freeSlot = FindFreeInventorySlot(inv);
                if (freeSlot is null)
                {
                    this._logger.LogWarning("[EquipCompare] ⚠️ 背包无空格，无法腾出装备位穿 {Name}",
                        item.Definition?.Name ?? "?");
                    continue;
                }

                this._logger.LogInformation(
                    "[EquipCompare] 🆕 装备位 {Slot} 有非装备物({OldName})，移至背包(Slot={FreeSlot})再穿 {NewName} (Slot={FromSlot})",
                    equipSlot.Value,
                    currentEquipped.Definition?.Name ?? "?",
                    freeSlot.Value,
                    item.Definition?.Name ?? "?",
                    item.ItemSlot);

                // 1. 先把药水移到背包空格
                await this._moveItemAction.MoveItemAsync(
                    this._player,
                    equipSlot.Value,  // 装备位（药水所在）
                    Storages.Inventory,
                    freeSlot.Value,   // 背包空格
                    Storages.Inventory).ConfigureAwait(false);

                // 2. 再穿装备到装备位
                await this._moveItemAction.MoveItemAsync(
                    this._player,
                    item.ItemSlot,
                    Storages.Inventory,
                    equipSlot.Value,
                    Storages.Inventory).ConfigureAwait(false);
                return;
            }

            // 已有装备 → 评分对比（使用玩家 BuildDirection 调整权重）
            var playerDirection = this._player.BuildDirection;
            var newScore = ScoreEquipQuality(item, playerDirection);
            var oldScore = ScoreEquipQuality(currentEquipped, playerDirection);

            if (newScore <= oldScore)
            {
                // 新装备不如旧的 → 跳过
                continue;
            }

            this._logger.LogInformation(
                "[EquipCompare] 🔄 换装: {NewName} (Score={NewScore}) > {OldName} (Score={OldScore}) in Slot {Slot}",
                item.Definition?.Name ?? "?",
                newScore,
                currentEquipped.Definition?.Name ?? "?",
                oldScore,
                equipSlot.Value);

            // 换装：先卸下旧装备到背包空格，再穿上新装备
            await this.SwapEquipmentAsync(item, equipSlot.Value, currentEquipped).ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// 交换装备：旧装备卸到背包空格，新装备穿到装备位。
    /// </summary>
    private async ValueTask SwapEquipmentAsync(Item newItem, byte equipSlot, Item oldItem)
    {
        var inv = this._player.Inventory;
        if (inv is null)
        {
            return;
        }

        // 保护：如果装备位上的物品不是装备（如药水），不要尝试移动它
        // 游戏引擎不允许把药水从装备位移到背包格
        if (oldItem.Definition?.ItemSlot is null)
        {
            this._logger.LogWarning("[EquipCompare] ⏭ 装备位 {Slot} 上的 {Name} 不是装备(无ItemSlot)，跳过换装",
                equipSlot, oldItem.Definition?.Name ?? "?");
            return;
        }

        // 找一个背包空格（只在储物格 12+ 中找）
        var freeSlot = FindFreeInventorySlot(inv);
        if (freeSlot is null)
        {
            this._logger.LogWarning("[EquipCompare] ⚠️ 背包无空格，无法换装");
            return;
        }

        // 1. 卸下旧装备到背包空格
        await this._moveItemAction.MoveItemAsync(
            this._player,
            equipSlot,
            Storages.Inventory,
            freeSlot.Value,
            Storages.Inventory).ConfigureAwait(false);

        // 2. 穿上新装备
        await this._moveItemAction.MoveItemAsync(
            this._player,
            newItem.ItemSlot,
            Storages.Inventory,
            equipSlot,
            Storages.Inventory).ConfigureAwait(false);

        this._logger.LogInformation(
            "[EquipCompare] ✅ 换装完成: 已穿 {NewName} (Slot={EquipSlot}), 旧装 {OldName} → 背包 (Slot={FreeSlot})",
            newItem.Definition?.Name ?? "?",
            equipSlot,
            oldItem.Definition?.Name ?? "?",
            freeSlot.Value);
    }

    /// <summary>
    /// 在背包储物格（12+）中找一个空格。
    /// </summary>
    private static byte? FindFreeInventorySlot(IInventoryStorage inv)
    {
        // FreeSlots 返回所有可用空格（包括装备位 0-11）
        // 我们只从装备位之后找
        foreach (var slot in inv.FreeSlots)
        {
            if (slot > InventoryConstants.LastEquippableItemSlotIndex)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>
    /// 判断物品是否可以穿戴（等级/职业要求）。
    /// 委托给游戏引擎检查。
    /// </summary>
    private bool CanWearItem(Item item)
    {
        if (item.Definition is null)
        {
            return false;
        }

        // 游戏引擎的 Player.CompliesRequirements(Item) 检查
        return this._player.CompliesRequirements(item);
    }

    /// <summary>
    /// 确定物品对应的装备栏位。
    /// 从 ItemDefinition.ItemSlot.ItemSlots 取第一个可用装备位。
    /// </summary>
    /// <param name="item">要检查的物品。</param>
    /// <returns>装备栏位索引（0-11），如果物品不是可穿戴装备则返回 null。</returns>
    private static byte? GetEquipmentSlot(Item item)
    {
        var def = item.Definition;
        if (def?.ItemSlot is null)
        {
            return null;
        }

        var slots = def.ItemSlot.ItemSlots;
        if (slots is null || slots.Count == 0)
        {
            return null;
        }

        // 取第一个有效装备位（在 0-11 范围内）
        var firstSlot = slots.First();
        if (firstSlot >= 0 && firstSlot <= InventoryConstants.LastEquippableItemSlotIndex)
        {
            return (byte)firstSlot;
        }

        return null;
    }

    /// <summary>
    /// 评分装备品质，用于对比两件装备好坏。
    /// 考虑因素：基础属性（攻击/防御）、强化等级、卓越/幸运/技能/古代等选项。
    /// </summary>
    internal static float ScoreEquipQuality(Item item)
    {
        return ScoreEquipQuality(item, null);
    }

    /// <summary>
    /// 带 BuildDirection 的装备评分 — 根据 build 方向调整攻击/防御属性权重。
    /// 物理系（战士/力魔/敏弓等）→ 物理攻击权重更高
    /// 法系（法师/法魔/智弓等）→ 魔攻权重更高
    /// 辅助系（智弓等）→ 防御/生命权重更高
    /// </summary>
    internal static float ScoreEquipQuality(Item item, Knowledge.BuildDirection? direction)
    {
        var def = item.Definition;
        if (def is null)
        {
            return 0;
        }

        var isWeapon = def.ItemSlot?.ItemSlots.Contains(InventoryConstants.LeftHandSlot) == true
                       || def.ItemSlot?.ItemSlots.Contains(InventoryConstants.RightHandSlot) == true;

        var isArmor = def.ItemSlot?.ItemSlots.Contains(InventoryConstants.HelmSlot) == true
                      || def.ItemSlot?.ItemSlots.Contains(InventoryConstants.ArmorSlot) == true
                      || def.ItemSlot?.ItemSlots.Contains(InventoryConstants.PantsSlot) == true
                      || def.ItemSlot?.ItemSlots.Contains(InventoryConstants.GlovesSlot) == true
                      || def.ItemSlot?.ItemSlots.Contains(InventoryConstants.BootsSlot) == true;

        // Determine build category for weight adjustment
        float physAttackWeight = 1.5f;
        float magicAttackWeight = 1.5f;
        float defenseWeight = 1.0f;
        if (direction.HasValue)
        {
            switch (direction.Value)
            {
                // 物理系：物理攻击 × 2.0，魔攻 × 1.0
                case Knowledge.BuildDirection.ForceKnight:
                case Knowledge.BuildDirection.BalancedKnight:
                case Knowledge.BuildDirection.SpeedKnight:
                case Knowledge.BuildDirection.BloodKnight:
                case Knowledge.BuildDirection.SmartKnight:
                case Knowledge.BuildDirection.ForceMG:
                case Knowledge.BuildDirection.AgilityElf:
                case Knowledge.BuildDirection.AgilityFighter:
                case Knowledge.BuildDirection.ForceDL:
                    physAttackWeight = 2.0f;
                    magicAttackWeight = 1.0f;
                    defenseWeight = 0.8f;
                    break;

                // 法系：魔攻 × 2.0，物理攻击 × 1.0
                case Knowledge.BuildDirection.IntWizard:
                case Knowledge.BuildDirection.ManaWizard:
                case Knowledge.BuildDirection.SpeedWizard:
                case Knowledge.BuildDirection.IntMG:
                case Knowledge.BuildDirection.IntSummoner:
                case Knowledge.BuildDirection.IntDL:
                    physAttackWeight = 1.0f;
                    magicAttackWeight = 2.0f;
                    defenseWeight = 0.8f;
                    break;

                // 辅助系：防御权重 × 1.5
                case Knowledge.BuildDirection.IntElf:
                case Knowledge.BuildDirection.HybridElf:
                case Knowledge.BuildDirection.SupportSummoner:
                case Knowledge.BuildDirection.AgilityDL:
                    physAttackWeight = 1.2f;
                    magicAttackWeight = 1.2f;
                    defenseWeight = 1.5f;
                    break;

                // 默认（AgilityElf 等已有分类，兜底保持原权重）
                default:
                    break;
            }
        }

        float score = 0;

        // 1) 基础属性评分：扫描 BasePowerUpAttributes，将攻击力/防御力纳入评分
        if (def.BasePowerUpAttributes is not null)
        {
            foreach (var p in def.BasePowerUpAttributes)
            {
                if (p.TargetAttribute is null) continue;

                // 基础值（武器的最小/最大攻击，防具的防御）
                var val = p.BaseValue;

                // 等级加成
                if (item.Level > 0 && p.BonusPerLevelTable?.BonusPerLevel is not null)
                {
                    var levelBonus = p.BonusPerLevelTable.BonusPerLevel
                        .FirstOrDefault(b => b.Level == item.Level);
                    if (levelBonus is not null)
                    {
                        val += levelBonus.AdditionalValue;
                    }
                }

                // 物理攻击类属性：使用 build 感知权重
                if (p.TargetAttribute == Stats.MinimumPhysBaseDmg
                    || p.TargetAttribute == Stats.MaximumPhysBaseDmg
                    || p.TargetAttribute == Stats.MinimumPhysBaseDmgByWeapon
                    || p.TargetAttribute == Stats.MaximumPhysBaseDmgByWeapon)
                {
                    score += val * physAttackWeight;
                }
                // 魔法攻击类属性：使用 build 感知权重
                else if (p.TargetAttribute == Stats.WizardryBaseDmg
                    || p.TargetAttribute == Stats.CurseBaseDmg
                    || p.TargetAttribute == Stats.MinimumCurseBaseDmg
                    || p.TargetAttribute == Stats.MaximumCurseBaseDmg)
                {
                    score += val * magicAttackWeight;
                }
                // 防御类属性：使用 build 感知权重
                else if (p.TargetAttribute == Stats.DefenseBase)
                {
                    score += val * defenseWeight;
                }
                // 其他属性（攻击速度等）：轻微加分
                else
                {
                    score += val * 0.3f;
                }
            }
        }

        // 2) 强化等级分
        score += item.Level * 10;

        // 3) 选项加分
        var hasExcellent = item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Excellent);
        var hasLuck = item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Luck);
        var hasSkill = item.HasSkill;
        var optionCount = item.ItemOptions.Count;
        var isAncient = item.ItemSetGroups.Any(s => s.AncientSetDiscriminator != 0);

        if (hasExcellent)
        {
            score += 50;
        }

        if (hasLuck)
        {
            score += 20;
        }

        if (hasSkill && isWeapon)
        {
            score += 15;
        }

        score += optionCount * 10;

        if (isAncient)
        {
            score += 30;
        }

        if (isArmor)
        {
            // 防具：DropLevel 越高防御越高（保留作为兜底）
            score += def.DropLevel * 5;
        }

        return score;
    }
}
