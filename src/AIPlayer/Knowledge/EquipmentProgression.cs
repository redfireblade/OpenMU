// <copyright file="EquipmentProgression.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

using System.Collections.Generic;

/// <summary>
/// AI 装备进阶知识 — 每个职业在每个等级段应该用什么装备、去哪里买、需要什么条件。
/// 数据来源：真实玩家经验 + 游戏配置数据。
/// 供 AI 决策系统查询，实现按等级自动换装。
/// </summary>
public static class EquipmentProgression
{
    /// <summary>
    /// 获取指定职业在指定等级的装备推荐。
    /// </summary>
    public static IReadOnlyList<EquipmentTier> GetProgression(int classNumber)
    {
        if (classNumber >= 0 && classNumber < 4) return MageProgression;   // Dark Wizard
        if (classNumber >= 4 && classNumber < 8) return KnightProgression; // Dark Knight
        if (classNumber >= 8 && classNumber < 12) return ElfProgression;   // Fairy Elf
        if (classNumber >= 16 && classNumber < 18) return LordProgression; // Dark Lord
        return DefaultProgression;
    }

    /// <summary>
    /// 获取 Noria (Elbeland) 地图中出售法师装备的 NPC 信息。
    /// </summary>
    public static readonly ShopNpcInfo IzabelTheWizard = new()
    {
        MapNumber = 3,
        SpawnX = 178,
        SpawnY = 44,
        NpcNumber = 239, // Izabel the Wizard
        Name = "Izabel the Wizard",
        Description = "安全区教堂内，出售法师武器/装备/技能书",
    };

    /// <summary>
    /// 获取 Elf Soldier 的位置信息。
    /// </summary>
    public static readonly ShopNpcInfo ElfSoldier = new()
    {
        MapNumber = 3,
        SpawnX = 240,
        SpawnY = 30,
        NpcNumber = 257,
        Name = "Elf Soldier",
        Description = "出安全区门口带翅膀的弓手，选1加攻防Buff(60分钟)",
    };

    /// <summary>
    /// 法师装备进阶表（DW 职业 0-3）。
    /// </summary>
    private static readonly List<EquipmentTier> MageProgression = new()
    {
        new EquipmentTier
        {
            MinLevel = 1,
            MaxLevel = 4,
            Priority = "攒钱买红药和火球术",
            ShoppingList = new List<ShopItem>
            {
                new() { ItemName = "Apple (红)", Group = 14, Number = 0, Quantity = 20, Npc = "Potion Girl", Map = 3 },
                new() { ItemName = "Small Mana Potion (蓝)", Group = 14, Number = 4, Quantity = 5, Npc = "Potion Girl", Map = 3 },
            },
            BuffBeforeHunt = null, // 5级才有火球，暂时近战
        },
        new EquipmentTier
        {
            MinLevel = 5,
            MaxLevel = 9,
            Priority = "学火球术，出安全区加Buff",
            ShoppingList = new List<ShopItem>
            {
                new() { ItemName = "Fireball Scroll", Group = 10, Number = 3, Quantity = 1, Npc = "Izabel the Wizard", Map = 3,
                        Note = "技能书，只能学一次，学后出现在技能栏", MinLevel = 5 },
                new() { ItemName = "Skull Staff (骷髅杖)", Group = 5, Number = 0, Quantity = 1, Npc = "Izabel the Wizard", Map = 3,
                        Note = "法师初始武器" },
                new() { ItemName = "Pad Set (藤装)", Group = 2, Number = 0, Quantity = 1, Npc = "Izabel the Wizard", Map = 3,
                        Note = "法师初始防具，注意体力要求", ItemSlot = EquipSlot.Helm },
                new() { ItemName = "Pad Armor (藤铠)", Group = 2, Number = 0, Quantity = 1, Npc = "Izabel the Wizard", Map = 3,
                        ItemSlot = EquipSlot.Armor },
                new() { ItemName = "Pad Pants (藤腿)", Group = 2, Number = 0, Quantity = 1, Npc = "Izabel the Wizard", Map = 3,
                        ItemSlot = EquipSlot.Pants },
                new() { ItemName = "Pad Gloves (藤手)", Group = 2, Number = 0, Quantity = 1, Npc = "Izabel the Wizard", Map = 3,
                        ItemSlot = EquipSlot.Gloves },
                new() { ItemName = "Pad Boots (藤靴)", Group = 2, Number = 0, Quantity = 1, Npc = "Izabel the Wizard", Map = 3,
                        ItemSlot = EquipSlot.Boots },
                new() { ItemName = "Small Healing Potion", Group = 14, Number = 1, Quantity = 20, Npc = "Potion Girl", Map = 3 },
                new() { ItemName = "Small Mana Potion", Group = 14, Number = 4, Quantity = 10, Npc = "Potion Girl", Map = 3 },
            },
            BuffBeforeHunt = ElfSoldier,
        },
        new EquipmentTier
        {
            MinLevel = 10,
            MaxLevel = 19,
            Priority = "换骨装，攒钱买更好的法杖",
            ShoppingList = new List<ShopItem>
            {
                new() { ItemName = "Bone Set (骨装)", Group = 4, Quantity = 1, Npc = "Izabel the Wizard", Map = 3,
                        Note = "比藤装更好的防具" },
                new() { ItemName = "Angelic Staff (天使杖)", Group = 5, Number = 1, Quantity = 1, Npc = "Izabel the Wizard", Map = 3,
                        Note = "比骷髅杖攻击更高", MinLevel = 10 },
                new() { ItemName = "Scroll of Teleport", Group = 10, Number = 5, Quantity = 1, Npc = "Izabel the Wizard", Map = 3,
                        Note = "瞬移技能，逃跑保命用", MinLevel = 10 },
            },
            BuffBeforeHunt = ElfSoldier,
        },
        new EquipmentTier
        {
            MinLevel = 20,
            MaxLevel = 29,
            Priority = "去冰风谷，换更好的法杖和防具",
            ShoppingList = new List<ShopItem>
            {
                new() { ItemName = "Serpent Staff (蛇杖)", Group = 5, Number = 2, Quantity = 1, Npc = "Izabel the Wizard", Map = 3,
                        Note = "冰风谷必备武器", MinLevel = 20 },
                new() { ItemName = "Sphinx Set (斯芬克斯装)", Group = 7, Quantity = 1, Npc = "Izabel the Wizard", Map = 3,
                        Note = "冰风谷适用的防具", MinLevel = 20 },
                new() { ItemName = "Large Healing Potion", Group = 14, Number = 3, Quantity = 20, Npc = "Potion Girl", Map = 3 },
                new() { ItemName = "Large Mana Potion", Group = 14, Number = 6, Quantity = 10, Npc = "Potion Girl", Map = 3 },
            },
            BuffBeforeHunt = ElfSoldier,
        },
    };

    /// <summary>
    /// 战士装备进阶表（DK 职业 4-7）。
    /// </summary>
    private static readonly List<EquipmentTier> KnightProgression = new()
    {
        new EquipmentTier
        {
            MinLevel = 1,
            MaxLevel = 9,
            Priority = "买红药，买斧子，攒钱换装",
            ShoppingList = new List<ShopItem>
            {
                new() { ItemName = "Apple", Group = 14, Number = 0, Quantity = 20, Npc = "Potion Girl", Map = 3 },
                new() { ItemName = "Small Axe", Group = 1, Number = 0, Quantity = 1, Npc = "Hanzo the Blacksmith", Map = 0 },
            },
        },
        new EquipmentTier
        {
            MinLevel = 10,
            MaxLevel = 19,
            Priority = "换青铜装",
            ShoppingList = new List<ShopItem>
            {
                new() { ItemName = "Bronze Set", Group = 0, Quantity = 1, Npc = "Hanzo the Blacksmith", Map = 0 },
                new() { ItemName = "Double Axe", Group = 1, Number = 2, Quantity = 1, Npc = "Hanzo the Blacksmith", Map = 0 },
            },
        },
    };

    /// <summary>
    /// 精灵装备进阶表（Elf 职业 8-11）。
    /// </summary>
    private static readonly List<EquipmentTier> ElfProgression = new()
    {
        new EquipmentTier
        {
            MinLevel = 1,
            MaxLevel = 9,
            Priority = "买弓和箭，红药",
            ShoppingList = new List<ShopItem>
            {
                new() { ItemName = "Short Bow", Group = 4, Number = 0, Quantity = 1, Npc = "Eo the Craftsman", Map = 3 },
                new() { ItemName = "Arrow", Group = 4, Number = 15, Quantity = 255, Npc = "Potion Girl", Map = 3,
                        Note = "箭要买够！" },
                new() { ItemName = "Apple", Group = 14, Number = 0, Quantity = 20, Npc = "Potion Girl", Map = 3 },
            },
            BuffBeforeHunt = ElfSoldier,
        },
    };

    /// <summary>
    /// 黑暗领主装备进阶表（Lord 职业 16-17）。
    /// </summary>
    private static readonly List<EquipmentTier> LordProgression = new()
    {
        new EquipmentTier
        {
            MinLevel = 1, MaxLevel = 9, Priority = "买红药和权杖",
            ShoppingList = new List<ShopItem>
            {
                new() { ItemName = "Apple", Group = 14, Number = 0, Quantity = 20, Npc = "Potion Girl", Map = 3 },
                new() { ItemName = "Small Shield", Group = 6, Number = 0, Quantity = 1, Npc = "Hanzo the Blacksmith", Map = 0 },
                new() { ItemName = "Mace", Group = 2, Number = 0, Quantity = 1, Npc = "Hanzo the Blacksmith", Map = 0 },
            },
        },
    };

    private static readonly List<EquipmentTier> DefaultProgression = new()
    {
        new EquipmentTier
        {
            MinLevel = 1, MaxLevel = 9, Priority = "打怪攒钱买药",
            ShoppingList = new List<ShopItem>
            {
                new() { ItemName = "Apple", Group = 14, Number = 0, Quantity = 20, Npc = "Potion Girl", Map = 3 },
            },
        },
    };
}

/// <summary>
/// AI 装备阶段 — 每个等级段的装备目标和购买清单。
/// </summary>
public sealed class EquipmentTier
{
    /// <summary>适用等级范围（含）。</summary>
    public int MinLevel { get; set; }
    public int MaxLevel { get; set; }

    /// <summary>该阶段的优先目标（自然语言描述，供AI决策）。</summary>
    public string Priority { get; set; } = string.Empty;

    /// <summary>需购买的物品清单。</summary>
    public List<ShopItem> ShoppingList { get; set; } = new();

    /// <summary>出猎前需要加的 Buff（null=不需要）。</summary>
    public ShopNpcInfo? BuffBeforeHunt { get; set; }
}

/// <summary>
/// 商店物品 — 描述一个物品从哪个NPC购买。
/// </summary>
public sealed class ShopItem
{
    /// <summary>物品名称（用于显示/匹配）。</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>物品组号。</summary>
    public int Group { get; set; }

    /// <summary>物品编号。</summary>
    public int Number { get; set; }

    /// <summary>购买数量。</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>出售NPC名称。</summary>
    public string Npc { get; set; } = string.Empty;

    /// <summary>NPC所在地图编号。</summary>
    public int Map { get; set; }

    /// <summary>装备栏位（如是装备）。</summary>
    public EquipSlot? ItemSlot { get; set; }

    /// <summary>最低等级要求。</summary>
    public int MinLevel { get; set; }

    /// <summary>附加说明。</summary>
    public string? Note { get; set; }
}

/// <summary>
/// 商店NPC信息 — 位置和服务。
/// </summary>
public sealed class ShopNpcInfo
{
    /// <summary>地图编号。</summary>
    public int MapNumber { get; set; }

    /// <summary>出生坐标 X。</summary>
    public int SpawnX { get; set; }

    /// <summary>出生坐标 Y。</summary>
    public int SpawnY { get; set; }

    /// <summary>NPC 编号。</summary>
    public int NpcNumber { get; set; }

    /// <summary>NPC 名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>位置描述（用于AI理解）。</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// 装备栏位枚举。
/// </summary>
public enum EquipSlot
{
    Weapon,
    Shield,
    Helm,
    Armor,
    Pants,
    Gloves,
    Boots,
    Wings,
}
