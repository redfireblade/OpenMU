// <copyright file="PlayerTradeMvp.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Trade;

/// <summary>
/// 玩家交易 MVP — 处理外部玩家发起的交易请求。
///
/// 交易暗号机制：
///   玩家在世界频道发送含暗号的消息 → AI 检测暗号匹配 →
///   确认交易信息 → 执行交易流程。
///
/// 第一阶段只实现被动响应模式。
/// </summary>
public sealed class PlayerTradeMvp
{
    private readonly AiPlayer _player;
    private readonly ILogger _logger;

    /// <summary>交易暗号——发起方需在聊天中包含此暗号才会被接受。</summary>
    public string SecretCode { get; set; } = "trade4fun";

    private enum TradePhase
    {
        Idle,
        WaitingForRequest,
        Accepting,
        InProgress,
        Complete,
    }

    private TradePhase _phase = TradePhase.Idle;
    private string? _partnerName;

    public PlayerTradeMvp(AiPlayer player, ILogger logger)
    {
        this._player = player;
        this._logger = logger;
    }

    /// <summary>是否正在交易中。</summary>
    public bool IsTrading => this._phase != TradePhase.Idle;

    /// <summary>
    /// 处理聊天消息，检测交易暗号。
    /// 供 HeartbeatService 的事件处理调用。
    /// </summary>
    /// <param name="senderName">发消息的玩家名。</param>
    /// <param name="message">消息内容。</param>
    /// <returns>是否触发交易流程。</returns>
    public bool HandleChatMessage(string senderName, string message)
    {
        if (this._phase != TradePhase.Idle) return false;
        if (!message.Contains(this.SecretCode, StringComparison.OrdinalIgnoreCase)) return false;

        this._partnerName = senderName;
        this._phase = TradePhase.WaitingForRequest;
        this._logger.LogInformation("[Trade] 检测到交易暗号，发送者={Sender}", senderName);
        return true;
    }

    /// <summary>
    /// 主动发送交易请求给指定玩家。
    /// </summary>
    public async ValueTask<bool> RequestTradeAsync(string playerName)
    {
        if (this._player is null) return false;

        var partner = this.FindPlayerByName(playerName);
        if (partner is null)
        {
            this._logger.LogWarning("[Trade] 找不到玩家: {Name}", playerName);
            return false;
        }

        var tradeAction = new TradeRequestAction();
        var result = await tradeAction.RequestTradeAsync(this._player, partner).ConfigureAwait(false);
        this._logger.LogInformation("[Trade] 主动请求交易: {Target} → {Result}", playerName, result);
        return result;
    }

    /// <summary>取消交易。</summary>
    public void CancelTrade()
    {
        this._phase = TradePhase.Idle;
        this._partnerName = null;
        this._logger.LogInformation("[Trade] 交易已取消");
    }

    /// <summary>交易完成标记。</summary>
    public void CompleteTrade()
    {
        this._phase = TradePhase.Complete;
        this._logger.LogInformation("[Trade] 交易完成");
        this._phase = TradePhase.Idle;
        this._partnerName = null;
    }

    /// <summary>按名字找地图上的玩家。</summary>
    private Player? FindPlayerByName(string name)
    {
        return null; // 跨地图找玩家需通过游戏引擎 API，暂未实现
    }
}
