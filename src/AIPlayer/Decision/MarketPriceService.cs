// <copyright file="MarketPriceService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using MUnique.OpenMU.DataModel.Entities;
using Microsoft.Extensions.Logging;

/// <summary>
/// 交易类型 — 消息中识别的交易方向。
/// </summary>
public enum TradeType { Sell, Buy, ConfirmedDeal }

/// <summary>
/// 市场价格服务 — 维护物品价值表(初始基础估价 + 市场偏移校准)。
/// 市场偏移值 = 当前市场价 - 基础估价。
/// 正数 = 市场热销(比预期贵), 负数 = 市场冷门(比预期便宜)。
/// 每收到一条交易消息, 偏移值微调。
/// </summary>
public sealed class MarketPriceService
{
    /// <summary>
    /// 物品价格记录。
    /// </summary>
    private sealed record PriceRecord(int BaseValue, int MarketOffset, DateTime LastUpdate, int SampleCount);

    /// <summary>
    /// Key = $"G{group}N{number}" 或 "JewelOfBless" 等特殊名称。
    /// </summary>
    private readonly Dictionary<string, PriceRecord> _prices = new();
    private readonly ILogger _logger;

    /// <summary>
    /// 市场偏移衰减: 每5分钟无新数据偏移值衰减一半。
    /// </summary>
    private static readonly TimeSpan OffsetDecayInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 价格 EMA 平滑系数 (0~1), 越大越跟随最新数据。
    /// </summary>
    private const double EmaAlpha = 0.3;

    /// <summary>
    /// 最大衰减周期数 (防止衰减循环太久)。
    /// </summary>
    private const int MaxDecayCycles = 5;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarketPriceService"/> class.
    /// </summary>
    /// <param name="logger">日志实例。</param>
    public MarketPriceService(ILogger logger)
    {
        this._logger = logger;
        this.InitializeBasePrices();
    }

    /// <summary>
    /// 获取物品的基础估价。
    /// </summary>
    /// <param name="itemKey">物品键值。</param>
    /// <returns>基础估价; 如果不存在则返回 0。</returns>
    public int GetBasePrice(string itemKey)
    {
        return this._prices.TryGetValue(itemKey, out var record) ? record.BaseValue : 0;
    }

    /// <summary>
    /// 获取当前市场偏移值(正=热销, 负=冷门)。
    /// </summary>
    /// <param name="itemKey">物品键值。</param>
    /// <returns>市场偏移值; 如果不存在则返回 0。</returns>
    public int GetMarketOffset(string itemKey)
    {
        return this._prices.TryGetValue(itemKey, out var record) ? record.MarketOffset : 0;
    }

    /// <summary>
    /// 获取调整后的价格: baseValue + marketOffset。
    /// 用于 ValueAssessmentService 的 EstimatedPrice 校准。
    /// </summary>
    /// <param name="itemKey">物品键值。</param>
    /// <returns>有效市场价; 如果不存在则返回 0。</returns>
    public int GetEffectivePrice(string itemKey)
    {
        return this._prices.TryGetValue(itemKey, out var record) ? record.BaseValue + record.MarketOffset : 0;
    }

    /// <summary>
    /// 记录一条交易消息, 更新市场偏移值。
    /// </summary>
    /// <param name="itemKey">物品键值。</param>
    /// <param name="observedPrice">本条喊话的报价。</param>
    /// <param name="type">交易类型。</param>
    public void RecordTrade(string itemKey, int observedPrice, TradeType type)
    {
        if (!this._prices.TryGetValue(itemKey, out var record))
        {
            this._logger.LogDebug("[MarketPrice] 未知物品 {Key}, 跳过 RecordTrade (报价={Price})", itemKey, observedPrice);
            return;
        }

        var now = DateTime.UtcNow;

        // 1. 衰减处理: 长时间无更新时偏移值回归到 0(基础价)
        var elapsed = now - record.LastUpdate;
        var currentOffset = record.MarketOffset;
        if (elapsed > OffsetDecayInterval)
        {
            var decayCycles = (int)(elapsed / OffsetDecayInterval);
            for (int i = 0; i < decayCycles && i < MaxDecayCycles; i++)
            {
                currentOffset /= 2;
            }

            this._logger.LogDebug("[MarketPrice] 衰减 {Key}: offset {Old}→{New} ({Cycles}周期)",
                itemKey, record.MarketOffset, currentOffset, Math.Min(decayCycles, MaxDecayCycles));
        }

        // 2. EMA 加权更新偏移值
        var newShift = observedPrice - record.BaseValue;
        var updatedOffset = (int)(currentOffset * (1 - EmaAlpha) + newShift * EmaAlpha);

        // 3. 写入新记录
        this._prices[itemKey] = record with
        {
            MarketOffset = updatedOffset,
            LastUpdate = now,
            SampleCount = record.SampleCount + 1,
        };

        this._logger.LogInformation(
            "[MarketPrice] 更新 {Key}: base={Base}, 喊价={Observed}, shift={Shift}, offset={OldOffset}→{NewOffset}, samples={Samples}",
            itemKey, record.BaseValue, observedPrice, newShift, record.MarketOffset, updatedOffset, record.SampleCount + 1);
    }

    /// <summary>
    /// 定期衰减: 对所有超过衰减周期的物品偏移值减半。
    /// 由 HeartbeatService 定期调用(如每60秒)。
    /// </summary>
    public void DecayOffsets()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in this._prices)
        {
            var record = kvp.Value;
            var elapsed = now - record.LastUpdate;
            if (elapsed <= OffsetDecayInterval)
            {
                continue;
            }

            var decayCycles = (int)(elapsed / OffsetDecayInterval);
            var newOffset = record.MarketOffset;
            for (int i = 0; i < decayCycles && i < MaxDecayCycles; i++)
            {
                newOffset /= 2;
            }

            if (newOffset != record.MarketOffset)
            {
                this._prices[kvp.Key] = record with { MarketOffset = newOffset, LastUpdate = now };
                this._logger.LogDebug("[MarketPrice] 定期衰减 {Key}: offset {Old}→{New}", kvp.Key, record.MarketOffset, newOffset);
            }
        }
    }

    /// <summary>
    /// 获取已知物品数量(调试/监控用)。
    /// </summary>
    public int KnownItemCount => this._prices.Count;

    // ======================== 基础估价初始化 ========================

    private void SetBase(string key, int baseValue)
    {
        this._prices[key] = new PriceRecord(baseValue, 0, DateTime.MinValue, 0);
    }

    private void SetBaseByGroupNumber(short group, short number, int baseValue)
    {
        this._prices[KeyForDefinition(group, number)] = new PriceRecord(baseValue, 0, DateTime.MinValue, 0);
    }

    private void InitializeBasePrices()
    {
        // S级: 硬通货
        this.SetBase("JewelOfBless", 10000);       // 祝福宝石
        this.SetBase("JewelOfSoul", 8000);         // 灵魂宝石
        this.SetBase("JewelOfChaos", 5000);        // 玛雅宝石(混沌)
        this.SetBase("JewelOfCreation", 50000);    // 创造宝石
        this.SetBase("JewelOfLife", 30000);        // 生命宝石
        this.SetBase("FeatherOfLoch", 20000);      // 洛克之羽
        this.SetBase("FeatherOfAngel", 100000);    // 大天使之羽
        this.SetBase("JewelOfRefine", 40000);      // 再生原石

        // A级: 入场材料
        // 血骨系列: Number=19~26, 每级+3000
        this.SetBaseByGroupNumber(12, 19, 5000);   // 血骨+0
        this.SetBaseByGroupNumber(12, 20, 8000);   // 血骨+1
        this.SetBaseByGroupNumber(12, 21, 11000);  // 血骨+2
        this.SetBaseByGroupNumber(12, 22, 14000);  // 血骨+3
        this.SetBaseByGroupNumber(12, 23, 17000);  // 血骨+4
        this.SetBaseByGroupNumber(12, 24, 20000);  // 血骨+5
        this.SetBaseByGroupNumber(12, 25, 23000);  // 血骨+6
        this.SetBaseByGroupNumber(12, 26, 26000);  // 血骨+7

        // 天使卷轴系列: Number=27~34, 每级+3000
        this.SetBaseByGroupNumber(12, 27, 5000);   // 天使卷轴+0
        this.SetBaseByGroupNumber(12, 28, 8000);   // 天使卷轴+1
        this.SetBaseByGroupNumber(12, 29, 11000);  // 天使卷轴+2
        this.SetBaseByGroupNumber(12, 30, 14000);  // 天使卷轴+3
        this.SetBaseByGroupNumber(12, 31, 17000);  // 天使卷轴+4
        this.SetBaseByGroupNumber(12, 32, 20000);  // 天使卷轴+5
        this.SetBaseByGroupNumber(12, 33, 23000);  // 天使卷轴+6
        this.SetBaseByGroupNumber(12, 34, 26000);  // 天使卷轴+7

        this.SetBase("GoldenText", 40000);         // 黄金文章
        this.SetBase("FireHawkFeather", 30000);    // 神鹰羽毛
        this.SetBase("FireHawkSeed", 25000);       // 神鹰火种

        // B级: 职业套装/翅膀
        this.SetBase("WingLevel1", 30000);         // 1级翅膀
        this.SetBase("WingLevel2", 150000);        // 2级翅膀
        this.SetBase("WingLevel3", 500000);        // 3级翅膀

        // 卓越装备基础: 按等级估算
        this.SetBase("ExcellentWeapon", 50000);    // 卓越武器
        this.SetBase("ExcellentArmor", 30000);     // 卓越防具
        this.SetBase("ExcellentSetItem", 80000);   // 卓越套装

        this._logger.LogInformation("[MarketPrice] 基础估价初始化完成: {Count} 个物品项", this._prices.Count);
    }

    // ======================== 静态工具方法 ========================

    /// <summary>
    /// 根据 Item 实例生成键值。
    /// </summary>
    /// <param name="item">道具实例。</param>
    /// <returns>物品键值, 如 "G12N14"。</returns>
    public static string KeyForItem(Item item)
    {
        if (item.Definition is null)
        {
            return string.Empty;
        }

        return KeyForDefinition(item.Definition.Group, item.Definition.Number);
    }

    /// <summary>
    /// 根据 Group/Number 生成键值。
    /// </summary>
    /// <param name="group">物品组编号。</param>
    /// <param name="number">物品编号。</param>
    /// <returns>物品键值, 如 "G12N14"。</returns>
    public static string KeyForDefinition(short group, short number)
    {
        return $"G{group}N{number}";
    }

    /// <summary>
    /// 尝试通过物品 Group/Number 匹配特殊名称键值。
    /// 优先匹配特定位, 匹配不上则返回通用 KeyForDefinition 格式。
    /// </summary>
    /// <param name="group">物品组编号。</param>
    /// <param name="number">物品编号。</param>
    /// <returns>匹配到的键值。</returns>
    public static string KeyForGroupNumber(short group, short number)
    {
        // 宝石类 (Group 12)
        if (group == 12)
        {
            switch (number)
            {
                case 14: return "JewelOfBless";
                case 13: return "JewelOfSoul";
                case 15: return "JewelOfChaos";
                case 16: return "JewelOfCreation";
                case 17: return "JewelOfLife";
                case 32: return "JewelOfRefine";
            }

            // 血骨系列 19~26
            if (number >= 19 && number <= 26) return "JewelOfBless"; // 暂不细分血骨
            // 天使卷轴 27~34
            if (number >= 27 && number <= 34) return "JewelOfBless"; // 暂不细分卷轴
        }

        // 羽毛类 (Group 13)
        if (group == 13)
        {
            switch (number)
            {
                case 11: return "FeatherOfLoch";
                case 16: return "FireHawkFeather";
                case 31: return "FeatherOfAngel";
                case 19: return "FireHawkSeed";
                case 49:
                case 50: return "GoldenText";
            }
        }

        return KeyForDefinition(group, number);
    }
}
