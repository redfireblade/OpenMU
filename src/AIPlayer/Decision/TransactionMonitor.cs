// <copyright file="TransactionMonitor.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using System.Text.RegularExpressions;
using MUnique.OpenMU.GameLogic.Views;
using Microsoft.Extensions.Logging;

/// <summary>
/// 交易消息监听器 — 从世界频道的玩家喊话中提取交易信息。
/// 通过正则解析中文喊话模式:
///   出售/卖/出/甩 + {物品名} + {价格} + {货币单位(祝福/灵魂/zen)}
///   收购/收/求购 + {物品名} + {价格}
/// 将解析结果交给 MarketPriceService.RecordTrade()。
/// </summary>
public sealed class TransactionMonitor
{
    private readonly MarketPriceService _market;
    private readonly ILogger _logger;

    /// <summary>出售关键字。</summary>
    private static readonly string[] SellKeywords = { "出售", "卖", "出", "甩", "便宜出", "处理" };

    /// <summary>收购关键字。</summary>
    private static readonly string[] BuyKeywords = { "收购", "收", "求购", "收个", "无限收" };

    /// <summary>确认成交关键字。</summary>
    private static readonly string[] DealKeywords = { "已出", "已售", "已收", "成交" };

    /// <summary>
    /// 出售正则: 出售/卖/出/甩 + 物品名 + 价格 + 货币单位(可选)。
    /// </summary>
    private static readonly Regex SellPattern = new(
        @"^(出售|卖|出|甩|处理|便宜出)\s*(.+?)\s*(\d+)(?:万|k|K)?\s*(祝福|灵魂|zen|Z|游戏币)?$",
        RegexOptions.Compiled);

    /// <summary>
    /// 收购正则: 收购/收/求购 + 物品名 + 价格。
    /// </summary>
    private static readonly Regex BuyPattern = new(
        @"^(收购|收|求购|收个|无限收)\s*(.+?)\s*(\d+)(?:万|k|K)?\s*(祝福|灵魂|zen|Z|游戏币)?$",
        RegexOptions.Compiled);

    /// <summary>
    /// 物品+价格解析: 包含+符号的出售消息或用/分割的报价。
    /// </summary>
    private static readonly Regex ItemPricePattern = new(
        @"([+]\d*\s*\S+|[一-鿿]{2,10})\s*(\d+)(?:万|k|K)?\s*(祝福|灵魂|zen|Z)?",
        RegexOptions.Compiled);

    /// <summary>
    /// 中物品名 → MarketPriceService key 映射表。
    /// </summary>
    private static readonly Dictionary<string, string> ChineseNameToKey = new()
    {
        // 宝石类
        { "祝福", "JewelOfBless" },
        { "祝福宝石", "JewelOfBless" },
        { "灵魂", "JewelOfSoul" },
        { "灵魂宝石", "JewelOfSoul" },
        { "玛雅", "JewelOfChaos" },
        { "玛雅宝石", "JewelOfChaos" },
        { "混沌", "JewelOfChaos" },
        { "创造", "JewelOfCreation" },
        { "创造宝石", "JewelOfCreation" },
        { "生命", "JewelOfLife" },
        { "生命宝石", "JewelOfLife" },

        // 羽毛类
        { "洛克之羽", "FeatherOfLoch" },
        { "羽毛", "FeatherOfLoch" },
        { "神鹰羽毛", "FireHawkFeather" },
        { "大天使之羽", "FeatherOfAngel" },
        { "神鹰火种", "FireHawkSeed" },

        // 入场材料
        { "血骨", "G12N19" },
        { "血骨+0", "G12N19" },
        { "血骨+1", "G12N20" },
        { "天使卷轴", "G12N27" },
        { "天使", "G12N27" },
        { "黄金文章", "GoldenText" },

        // 翅膀
        { "1级翅膀", "WingLevel1" },
        { "2级翅膀", "WingLevel2" },
        { "3级翅膀", "WingLevel3" },

        // 再生
        { "再生", "JewelOfRefine" },
        { "再生原石", "JewelOfRefine" },
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="TransactionMonitor"/> class.
    /// </summary>
    /// <param name="market">市场价格服务实例。</param>
    /// <param name="logger">日志实例。</param>
    public TransactionMonitor(MarketPriceService market, ILogger logger)
    {
        this._market = market;
        this._logger = logger;
    }

    /// <summary>
    /// 处理一条聊天消息。由 HeartbeatService 在收到聊天事件时调用。
    /// 如果是交易消息 → 解析 → 调用 _market.RecordTrade()。
    /// </summary>
    /// <param name="senderName">发送者名称。</param>
    /// <param name="message">聊天消息内容。</param>
    public void ProcessChatMessage(string senderName, string message)
    {
        // 跳过公告/系统消息
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var result = this.ParseTradeMessage(message);
        if (result is null)
        {
            return;
        }

        var (itemName, price, tradeType) = result.Value;
        if (itemName is null || price is null || tradeType is null)
        {
            return;
        }

        var itemKey = this.ResolveItemKey(itemName);
        if (itemKey is null)
        {
            this._logger.LogDebug("[TxMonitor] 无法解析物品名 '{ItemName}' (消息: {Message})", itemName, message);
            return;
        }

        this._market.RecordTrade(itemKey, price.Value, tradeType.Value);
        this._logger.LogDebug("[TxMonitor] {Type} {ItemName}({Key})={Price} by {Sender}",
            tradeType == TradeType.Sell ? "出" : tradeType == TradeType.Buy ? "收" : "成交",
            itemName, itemKey, price, senderName);
    }

    /// <summary>
    /// 尝试从消息中提取(物品名, 价格, 交易类型)。
    /// </summary>
    /// <param name="message">聊天消息。</param>
    /// <returns>解析结果, 失败则返回 null。</returns>
    private (string? itemName, int? price, TradeType? type)? ParseTradeMessage(string message)
    {
        // 检查成交关键字
        foreach (var keyword in DealKeywords)
        {
            if (message.StartsWith(keyword, StringComparison.Ordinal))
            {
                // 成交消息格式: "已出 +7传说杖 3祝福"
                var match = ItemPricePattern.Match(message[keyword.Length..]);
                if (match.Success)
                {
                    var price = this.ParsePrice(match.Groups[2].Value);
                    return (match.Groups[1].Value.Trim(), price, TradeType.ConfirmedDeal);
                }

                return null;
            }
        }

        // 尝试出售匹配
        var sellMatch = SellPattern.Match(message);
        if (sellMatch.Success)
        {
            var price = this.ParsePrice(sellMatch.Groups[3].Value);
            return (sellMatch.Groups[2].Value.Trim(), price, TradeType.Sell);
        }

        // 尝试收购匹配
        var buyMatch = BuyPattern.Match(message);
        if (buyMatch.Success)
        {
            var price = this.ParsePrice(buyMatch.Groups[3].Value);
            return (buyMatch.Groups[2].Value.Trim(), price, TradeType.Buy);
        }

        // 物品+价格模式兜底（如 "+7传说杖 3祝福"）
        var fallbackMatch = ItemPricePattern.Match(message);
        if (fallbackMatch.Success)
        {
            var price = this.ParsePrice(fallbackMatch.Groups[2].Value);
            return (fallbackMatch.Groups[1].Value.Trim(), price, TradeType.Sell);
        }

        return null;
    }

    /// <summary>
    /// 解析价格字符串（支持纯数字, 支持"万"单位）。
    /// </summary>
    /// <param name="priceStr">价格字符串。</param>
    /// <returns>解析后的整数价格。</returns>
    private int ParsePrice(string priceStr)
    {
        if (int.TryParse(priceStr, out var price))
        {
            return price;
        }

        return 0;
    }

    /// <summary>
    /// 中文物品名 → MarketPriceService 的 itemKey。
    /// </summary>
    /// <param name="itemName">中文物品名称。</param>
    /// <returns>itemKey, 匹配不上则返回 null。</returns>
    private string? ResolveItemKey(string itemName)
    {
        // 精确匹配
        if (ChineseNameToKey.TryGetValue(itemName, out var key))
        {
            return key;
        }

        // 前缀匹配（如 "祝福" 匹配 "祝福宝石"）
        foreach (var kvp in ChineseNameToKey)
        {
            if (itemName.StartsWith(kvp.Key, StringComparison.Ordinal) ||
                kvp.Key.StartsWith(itemName, StringComparison.Ordinal))
            {
                return kvp.Value;
            }
        }

        // +N装备名匹配: 提取装备名后查找
        if (itemName.StartsWith('+'))
        {
            // "+7传说杖" → 去掉 +N 前缀
            var cleanName = Regex.Replace(itemName, @"^\+?\d+\s*", string.Empty);
            if (ChineseNameToKey.TryGetValue(cleanName, out var cleanKey))
            {
                return cleanKey;
            }
        }

        return null;
    }

    /// <summary>
    /// 检查消息是否为交易相关(快速过滤用)。
    /// </summary>
    /// <param name="message">聊天消息。</param>
    /// <returns>如果包含交易关键字则返回 true。</returns>
    public static bool IsPotentialTradeMessage(string message)
    {
        foreach (var kw in SellKeywords)
        {
            if (message.Contains(kw, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var kw in BuyKeywords)
        {
            if (message.Contains(kw, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var kw in DealKeywords)
        {
            if (message.Contains(kw, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
