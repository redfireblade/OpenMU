// <copyright file="ValueAssessmentService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using System;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using Microsoft.Extensions.Logging;

/// <summary>
/// 道具价值评估服务 — 基于 MU 玩家经验体系。
/// 评估优先级：S > A > B > C > D > E(材料) > Junk(垃圾)
/// 用于拾取决策、贩卖决策、合成决策。
///
/// 参考: MU玩家社区通用价值体系, S/A/B/C/D五级分类法。
/// </summary>
public sealed class ValueAssessmentService
{
    private readonly AiPlayer _player;
    private readonly ILogger _logger;
    private readonly MarketPriceService? _marketPrice;

    public ValueAssessmentService(AiPlayer player, ILogger logger)
    {
        this._player = player;
        this._logger = logger;
    }

    public ValueAssessmentService(AiPlayer player, ILogger logger, MarketPriceService? marketPrice = null)
    {
        this._player = player;
        this._logger = logger;
        this._marketPrice = marketPrice;
    }

    // ========================================================================
    // 【拾取策略配置】参考 OfflinePlayer.ItemPickupHandler
    // ========================================================================

    /// <summary>
    /// 是否拾取所有物品（忽略价值评估）。默认 false。
    /// 启用后 AI 玩家会无条件拾取所有掉落的物品（相当于离线挂机捡物模式）。
    /// </summary>
    public bool PickAllItems { get; set; }

    /// <summary>
    /// 是否拾取金钱（Zen）。默认 true。
    /// 启用后 AI 玩家会拾取地上掉落的金钱。
    /// </summary>
    public bool PickZen { get; set; } = true;

    /// <summary>
    /// 额外物品名称列表：按物品名称子串匹配，匹配到的物品强制拾取（不经过价值评估）。
    /// 例如: {"血色", "恶魔", "果实"} 会匹配所有名称中包含这些关键词的物品。
    /// 参考 OfflinePlayer.ExtraItemNames 逻辑。
    /// </summary>
    public IReadOnlyList<string> ExtraItemNames { get; set; } = Array.Empty<string>();

    /// <summary>拾取金钱的最低数量阈值。</summary>
    private const int MinPickupZen = 100;

    /// <summary>评估道具价值。</summary>
    public ValueAssessment Evaluate(Item item)
    {
        if (item.Definition is null)
            return Junk("无定义物品");

        var def = item.Definition;
        var group = def.Group;
        var number = def.Number;

        // ===== S级: 硬通货, 永远保值, 必捡 =====
        if (group == 12) // 宝石类
        {
            switch (number)
            {
                case 14: return S("祝福宝石", "强化+1~+6必备, 基本货币单位, 硬通货");
                case 13: return S("灵魂宝石", "强化+7~+9核心, 需求量最大");
                case 15: return ChaosGem();
                case 16: return S("创造宝石", "合成翅膀/高阶装备唯一材料, 掉率低, 价值高");
                case 17: return S("生命宝石", "合成+11以上必备, 后期刚需");
                case 32: return A("再生原石", "400级镶嵌装备核心, 后期单价高");
                case 42: return A("High Grade Ore", "高阶锻造材料");
                case 43: return A("Low Grade Ore", "锻造材料有一定市场");
            }
        }

        // ===== S级: 符文/羽毛类 =====
        if (group == 13)
        {
            if (number == 11) return S("洛克之羽", "2级翅膀核心材料, 持续有需求");
            if (number == 16) return A("神鹰羽毛", "3级翅膀关键材料");
            if (number == 31) return S("大天使之羽", "5代翅膀材料, 极高价值");
            if (number == 19) return A("神鹰火种", "3级翅膀合成必需");
            if (number == 8) return A("黑龙波术技能书", "前期天价, 高价值技能书");
            if (number == 96) return A("星云火链卷轴", "高价值技能书");
            if (number == 49 || number == 50) return A("黄金文章", "高阶地图掉落, 单价10+元");
        }

        // ===== A级: 入场材料/重要合成材料 =====
        if (group == 12)
        {
            // 血骨系列: Number=19~26
            if (number >= 19 && number <= 26) return A($"血骨+{number - 19}", "血色城堡入场材料");
            // 天使卷轴系列: Number=27~34
            if (number >= 27 && number <= 34) return A($"天使卷轴+{number - 27}", "血色城堡入场材料");
        }

        if (group == 12 && (number == 18 || number == 19))
            return A("恶魔之眼/恶魔之钥", "恶魔广场入场材料");

        if (group == 13 && number == 33)
            return A("帝国入场券", "黄金频道副本门票");

        // ===== A级: 兽魂/宠物材料 =====
        if (group == 13 && (number == 1 || number == 2))
            return A("天鹰兽魂", "坐骑培养材料");

        if (group == 13 && (number == 3 || number == 4))
            return A("黑王马兽魂", "坐骑培养材料");

        // ===== 芬里尔相关 =====
        if (group == 13 && number == 37)
            return A("芬里尔碎片", "芬里尔坐骑合成材料");

        // ===== 基于物品选项的评估 =====
        return EvaluateByOptions(item);
    }

    /// <summary>通过装备词条和属性综合评估。</summary>
    private ValueAssessment EvaluateByOptions(Item item)
    {
        var def = item.Definition;
        var group = def!.Group;

        // 药水/箭矢 → 垃圾
        if (group == 14) return Junk("药水, 批量价值极低");
        if (group == 15) return Junk("箭矢/弩箭, 低价值消耗品");

        var hasExcellent = item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Excellent);
        var hasLuck = item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Luck);
        var hasSkill = item.HasSkill;
        var isAncient = item.ItemSetGroups.Any(s => s.AncientSetDiscriminator != 0);
        var hasSocket = item.SocketCount > 0;
        var hasGuardian = item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.GuardianOption);
        var hasHarmony = item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.HarmonyOption);
        var optionCount = item.ItemOptions.Count;
        var level = item.Level;

        // 翅膀类 (Group 13 特定范围是翅膀)
        if (group == 12 && (def.Number == 35 || def.Number == 36 || def.Number == 40))
        {
            if (hasExcellent && level >= 9) return S("卓越翅膀+" + level, "极品翅膀, 极高价值");
            if (level >= 7) return A("翅膀+" + level, "高等级翅膀有价值");
            return B("翅膀+" + level, "普通翅膀有基础价值");
        }

        // 戒指/项链 (特殊首饰)
        if (group == 10 || group == 11)
        {
            if (hasExcellent && hasLuck && optionCount >= 3)
                return A("极品卓越首饰", "3条以上属性卓越首饰价值高");
            if (hasExcellent && optionCount >= 2)
                return B("卓越首饰", "双属性卓越首饰");
            if (hasExcellent)
                return C("单属性卓越首饰", "1属性卓越首饰, 价值有限");
            if (isAncient)
                return B("古代首饰", "套装首饰有市场");
        }

        // 普通装备
        var isWeapon = def.ItemSlot?.ItemSlots.Contains(0) == true
                       || def.ItemSlot?.ItemSlots.Contains(1) == true;

        // S级条件: 卓越+幸运+高等级 或 卓越3条以上属性
        if (hasExcellent && hasLuck && level >= 9 && optionCount >= 3)
            return S($"卓越幸运+{level}装备", "卓越+幸运+高等级, 极品装备");

        // S级条件: 古代套装
        if (isAncient && hasLuck && level >= 7)
            return S("古代幸运+" + level, "古代套装+幸运, 顶级装备");

        // A级条件: 卓越+多属性
        if (hasExcellent && optionCount >= 3)
            return A($"卓越装备({optionCount}属性)", "多属性卓越装备, 高价值");

        // A级条件: 古代套装
        if (isAncient)
        {
            if (level >= 7) return A($"古代套装+{level}", "高等级古代套装");
            return B("古代套装", "古代套装, 稳定价值");
        }

        // A级条件: 洞装(镶嵌)
        if (hasSocket && hasExcellent)
            return A("卓越洞装", "卓越镶嵌装备, 后期顶级");

        if (hasSocket && hasLuck && level >= 4)
            return A($"洞装+{level}", "镶嵌装备有基础价值");

        // B级条件: 卓越+低级
        if (hasExcellent && optionCount >= 2)
            return B($"卓越装备({optionCount}属性)", "双属性卓越, 可卖可分解");

        if (hasExcellent)
            return C($"卓越装备(1属性)", "单属性卓越, 价值有限");

        // B级条件: 幸运+高等级
        if (hasLuck && level >= 9)
            return B($"幸运+9+" + (isWeapon ? "武器" : "防具"), "高等级幸运装备, 合成材料");

        if (hasLuck && level >= 6)
            return C($"幸运+{level}装备", "中等幸运装备, 可卖");

        // C级条件: 有技能武器
        if (isWeapon && hasSkill && level >= 6)
            return C($"技能+{level}武器", "有技能的武器有一定价值");

        // D级条件: 高等级白装
        if (level >= 9)
            return D($"白装+{level}", "高等级白装, 有分解价值");

        if (level >= 6)
            return E($"白装+{level}", "低级白装, 可卖给NPC");

        // E级: 有选项的普通装备
        if (optionCount > 0 || hasSkill)
            return E("有选项普通装备", "基础选项装备, 价值有限");

        // Junk: 白装+0~+5
        if (level <= 5)
            return Junk("白装低等级, 无价值");

        return E("未知道具", "无法分类的道具");
    }

    /// <summary>获取道具拾取优先级 (0-100, 越高越优先拾取)。</summary>
    public int GetPickupPriority(ValueAssessment assessment)
    {
        return assessment.Tier switch
        {
            ValueTier.S => 100,
            ValueTier.A => 80,
            ValueTier.B => 60,
            ValueTier.C => 40,
            ValueTier.D => 20,
            ValueTier.E => 5,
            ValueTier.Junk => 0,
            _ => 0,
        };
    }

    /// <summary>
    /// 该物品是否应该被捡起。
    /// 先检查 PickAllItems / ExtraItemNames 等外部配置，再走价值评估。
    /// S/A/B 级拾取，D/E/Junk 不捡（绿色卓越=S/A/B，任务道具=C级）。
    /// </summary>
    public bool ShouldPickup(Item item)
    {
        // 【1】PickAllItems 模式：无条件拾取所有物品
        if (PickAllItems)
            return true;

        // 【2】ExtraItemNames 按名称匹配：匹配到的物品强制拾取
        if (ExtraItemNames.Count > 0 && item.Definition is not null)
        {
            var itemNameStr = item.Definition.Name.ToString();
            if (!string.IsNullOrEmpty(itemNameStr))
            {
                foreach (var name in ExtraItemNames)
                {
                    if (itemNameStr.Contains(name, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        // 【3】走价值评估：S/A/B 级拾取
        return Evaluate(item).Tier <= ValueTier.B;
    }

    /// <summary>
    /// 判断指定数量的金钱（Zen）是否应该被拾取。
    /// 参考 OfflinePlayer.PickZen 逻辑。
    /// </summary>
    public bool ShouldPickupMoney(int amount)
    {
        if (!PickZen)
            return false;

        return amount >= MinPickupZen;
    }

    /// <summary>
    /// 该物品是否应该卖给NPC商店。
    /// 如果市场价明显高于NPC回收价(>2倍), 则保留去玩家市场。
    /// </summary>
    public bool ShouldSellToNpc(Item item)
    {
        var assessment = this.Evaluate(item);
        if (assessment.Tier > ValueTier.E)
            return false;
        if (assessment.Tier != ValueTier.Junk && assessment.Tier != ValueTier.E)
            return false;

        // 市场偏移判断: 如果市场有效价 > 2倍NPC价, 保留
        if (this._marketPrice is not null && item.Definition is not null)
        {
            var itemKey = MarketPriceService.KeyForItem(item);
            var effectivePrice = this._marketPrice.GetEffectivePrice(itemKey);
            // NPC回收价约为基础价的 1/3, 2倍回收价 ≈ 2/3 基础价
            // 如果市场有效价 > 2倍NPC价 → 值得去玩家市场
            var npcPrice = assessment.EstimatedPrice / 3;
            if (npcPrice > 0 && effectivePrice > npcPrice * 2)
            {
                return false; // 市场价高, 保留
            }
        }

        return assessment.Tier == ValueTier.Junk || assessment.Tier == ValueTier.E;
    }

    /// <summary>
    /// 获取含市场偏移的有效价格。
    /// </summary>
    public int GetEffectivePrice(string itemKey) => this._marketPrice?.GetEffectivePrice(itemKey) ?? 0;

    /// <summary>
    /// 获取市场偏移值。
    /// </summary>
    public int GetMarketOffset(string itemKey) => this._marketPrice?.GetMarketOffset(itemKey) ?? 0;

    /// <summary>该物品是否应该保留（存入仓库或个人商店）。</summary>
    public bool ShouldKeep(Item item) => Evaluate(item).Tier >= ValueTier.D;

    private static ValueAssessment S(string label, string reason) =>
        new(ValueTier.S, label, reason, 1000);

    private static ValueAssessment A(string label, string reason) =>
        new(ValueTier.A, label, reason, 500);

    private static ValueAssessment B(string label, string reason) =>
        new(ValueTier.B, label, reason, 200);

    private static ValueAssessment C(string label, string reason) =>
        new(ValueTier.C, label, reason, 100);

    private static ValueAssessment D(string label, string reason) =>
        new(ValueTier.D, label, reason, 30);

    private static ValueAssessment E(string label, string reason) =>
        new(ValueTier.E, label, reason, 10);

    private static ValueAssessment Junk(string reason) =>
        new(ValueTier.Junk, "垃圾道具", reason, 0);

    private ValueAssessment ChaosGem()
    {
        // 混沌宝石是S级或A级取决于是否正在做合成
        // 合成师大量需要, 普通玩家少一些
        return new ValueTier?[] { ValueTier.S, ValueTier.A }
            .Contains(ValueTier.S)
            ? new ValueAssessment(ValueTier.S, "玛雅宝石", "所有混沌合成必需, 用途最广的合成材料", 800)
            : new ValueAssessment(ValueTier.A, "玛雅宝石", "合成材料", 500);
    }
}

/// <summary>价值层级 (S > A > B > C > D > E > Junk)。</summary>
public enum ValueTier
{
    /// <summary>硬通货 — 祝福/灵魂/创造/洛克之羽/大天使之羽。永远保值, 必捡。</summary>
    S,

    /// <summary>高价值 — 生命宝石/入场材料/兽魂/极品卓越3属性+/黄金文章。</summary>
    A,

    /// <summary>稳定价值 — 普通卓越/古代套装/幸运+9/洞装。</summary>
    B,

    /// <summary>有限价值 — 单属性卓越/幸运+6/有技能+6。</summary>
    C,

    /// <summary>低价值 — 白装+9以上, 可分解材料。</summary>
    D,

    /// <summary>材料级 — 白装+6~+8, 仅卖给NPC。</summary>
    E,

    /// <summary>垃圾 — 药水/箭矢/白装+0~+5, 忽略。</summary>
    Junk,
}

/// <summary>道具价值评估结果。</summary>
public sealed record ValueAssessment(
    ValueTier Tier,
    string Label,
    string Reason,
    int EstimatedPrice,
    int MarketOffset = 0,
    string MarketInfo = "");
