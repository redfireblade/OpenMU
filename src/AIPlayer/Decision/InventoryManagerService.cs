// <copyright file="InventoryManagerService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 背包管理服务 — 自动检测背包满仓，回城贩卖垃圾道具。
///
/// 职责：
///   1. 计算背包占用率 (Fullness)
///   2. 判断是否需要回城清理 (NeedsInventoryCleanup)
///   3. 执行回城贩卖流程 (CleanupInventoryAsync)
///      a) 在当前地图找到贩卖 NPC
///      b) 走过去 → 打开对话 → 依次贩卖垃圾道具 → 关闭对话
/// </summary>
public sealed class InventoryManagerService
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;
    private readonly ValueAssessmentService? _valueAssessment;

    private const int RegularInventorySlots = 64;

    /// <summary>
    /// Initializes a new instance of the <see cref="InventoryManagerService"/> class.
    /// </summary>
    /// <param name="player">AI 玩家实例。</param>
    /// <param name="adapter">游戏操作适配器。</param>
    /// <param name="logger">日志。</param>
    public InventoryManagerService(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
        this._valueAssessment = null;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="InventoryManagerService"/> class with value assessment.
    /// </summary>
    public InventoryManagerService(AiPlayer player, IGameAdapter adapter, ILogger logger, ValueAssessmentService valueAssessment)
        : this(player, adapter, logger)
    {
        this._valueAssessment = valueAssessment;
    }

    /// <summary>
    /// 背包占用率 — 占据背包储物格（非装备格）的比例，范围 [0.0~1.0]。
    /// </summary>
    public float Fullness { get; private set; }

    /// <summary>
    /// 判断背包是否已满，需要清理。
    /// </summary>
    /// <param name="threshold">阈值，默认 0.8（80%）。</param>
    /// <returns>如果占用率 ≥ 阈值则返回 true。</returns>
    public bool NeedsInventoryCleanup(float threshold = 0.8f)
    {
        this.CalculateFullness();
        return this.Fullness >= threshold;
    }

    /// <summary>
    /// 执行背包清理流程：找到贩卖 NPC → 走过去 → 打开对话 → 贩卖垃圾 → 关闭对话。
    /// </summary>
    /// <returns>执行结果。</returns>
    public async ValueTask<StepResult> CleanupInventoryAsync()
    {
        var map = this._adapter.GetCurrentMap();
        if (map is null)
        {
            this._logger.LogWarning("[InvMgr] 当前地图为 null，无法执行背包清理");
            return StepResult.Failed;
        }

        // 1) 在当前地图查找贩卖 NPC（MerchantStore 不为 null）
        var npc = map.GetNpcsInRange(this._player.Position, 150)
            .FirstOrDefault(n => n.Definition?.MerchantStore is not null);

        if (npc is null)
        {
            this._logger.LogInformation("[InvMgr] 当前地图未发现贩卖NPC，巡逻搜索");
            var pos = this._adapter.GetPlayerPosition();
            var walkTarget = new Point(
                (byte)Math.Clamp(pos.X + Random.Shared.Next(-15, 15), 0, 255),
                (byte)Math.Clamp(pos.Y + Random.Shared.Next(-15, 15), 0, 255));
            await this._adapter.WalkToAsync(walkTarget, map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // 2) 不在 NPC 旁边 → 走过去
        var dist = this._player.Position.EuclideanDistanceTo(npc.Position);
        if (dist > 3f)
        {
            this._logger.LogDebug("[InvMgr] 走向贩卖NPC ({X},{Y})", npc.Position.X, npc.Position.Y);
            await this._adapter.WalkToAsync(
                new Point((byte)npc.Position.X, (byte)npc.Position.Y), map).ConfigureAwait(false);
            return StepResult.InProgress;
        }

        // 3) 在 NPC 旁边但未打开对话 → 打开 NPC 对话
        if (this._player.OpenedNpc != npc)
        {
            this._logger.LogInformation("[InvMgr] 打开贩卖NPC对话");
            var talkAction = new TalkNpcAction();
            await talkAction.TalkToNpcAsync(this._player, npc).ConfigureAwait(false);

            if (this._player.OpenedNpc != npc)
            {
                this._logger.LogWarning("[InvMgr] 打开NPC对话失败");
                return StepResult.Failed;
            }

            return StepResult.InProgress;
        }

        // 4) 对话已打开 → 依次贩卖垃圾道具
        var inv = this._player.Inventory;
        if (inv is null)
        {
            this._logger.LogWarning("[InvMgr] 背包为 null");
            return StepResult.Failed;
        }

        var junkItems = inv.Items
            .Where(i => this.IsJunkItem(i))
            .ToList();

        // 过滤并排序：先卖低层级垃圾（Junk→E），ShouldKeep 的强化保护
        if (this._valueAssessment is not null)
        {
            junkItems = junkItems
                .Where(i => !this._valueAssessment.ShouldKeep(i)) // 二次强化保护
                .OrderBy(i => this._valueAssessment.Evaluate(i).Tier) // Junk(最低)先卖, E次之
                .ThenBy(i => i.ItemSlot)
                .ToList();
        }
        else
        {
            junkItems = junkItems
                .OrderBy(i => i.ItemSlot)
                .ToList();
        }

        if (junkItems.Count == 0)
        {
            this._logger.LogInformation("[InvMgr] 无垃圾道具可卖，关闭对话");
            await this.CloseNpcDialogAsync().ConfigureAwait(false);
            return StepResult.Completed;
        }

        // 每 tick 只卖一个，避免单次操作过多
        var itemToSell = junkItems[0];
        this._logger.LogInformation(
            "[InvMgr] 🏪 贩卖垃圾道具 slot={Slot} '{Name}' (Group={Group}, 还剩 {Remaining} 件)",
            itemToSell.ItemSlot,
            itemToSell.Definition?.Name ?? "?",
            itemToSell.Definition?.Group,
            junkItems.Count - 1);

        await this._adapter.SellItemToNpcAsync(itemToSell.ItemSlot).ConfigureAwait(false);

        // 如果这是最后一件垃圾，贩卖完关闭对话
        if (junkItems.Count == 1)
        {
            this._logger.LogInformation("[InvMgr] 最后一件垃圾已卖，关闭对话");
            await this.CloseNpcDialogAsync().ConfigureAwait(false);
            return StepResult.Completed;
        }

        return StepResult.InProgress;
    }

    /// <summary>
    /// 判断道具是否为可贩卖的垃圾。
    /// 委托给 ValueAssessmentService.ShouldSellToNpc 进行评估；若未注入则回退到旧逻辑。
    /// </summary>
    /// <param name="item">道具实例。</param>
    /// <returns>是否为垃圾道具。</returns>
    private bool IsJunkItem(Item item)
    {
        // 非装备格（在背包储物格中）
        if (item.ItemSlot <= InventoryConstants.LastEquippableItemSlotIndex)
        {
            return false;
        }

        if (this._valueAssessment is not null)
        {
            // 强化保护：ShouldKeep 的道具绝不放进出售列表
            if (this._valueAssessment.ShouldKeep(item))
            {
                return false;
            }

            return this._valueAssessment.ShouldSellToNpc(item);
        }

        // Legacy fallback: old hardcoded logic
        return LegacyIsJunkItem(item);
    }

    /// <summary>
    /// Legacy hardcoded junk detection used when ValueAssessmentService is not available.
    /// </summary>
    private static bool LegacyIsJunkItem(Item item)
    {

        // 绑定道具不可卖
        if (item.Definition?.IsBoundToCharacter == true)
        {
            return false;
        }

        // Excellent 物品保留
        if (item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Excellent))
        {
            return false;
        }

        // Luck 物品保留
        if (item.ItemOptions.Any(o => o.ItemOption?.OptionType == ItemOptionTypes.Luck))
        {
            return false;
        }

        // Socket 物品保留（有 SocketCount 表示可镶嵌）
        if (item.SocketCount > 0)
        {
            return false;
        }

        if (item.Definition is null)
        {
            return true; // 无定义 → 可卖
        }

        // 保留贵重物品类别
        var group = item.Definition.Group;
        switch (group)
        {
            case 12: // 宝石 (Bless=14, Soul=13, Chaos=15, Creation=16, Life=17)
                return false;
            case 14: // 药水
                return false;
            case 15: // 卷轴
                return false;
            default:
                return true; // 普通白装/绿装/武器 → 可卖
        }
    }

    /// <summary>
    /// 计算背包占用率。
    /// 只计储物格（非装备格），装备格 0~11 不计入。
    /// </summary>
    private void CalculateFullness()
    {
        var inv = this._player.Inventory;
        if (inv is null)
        {
            this.Fullness = 0f;
            return;
        }

        var occupied = 0;
        foreach (var item in inv.Items)
        {
            if (item.ItemSlot > InventoryConstants.LastEquippableItemSlotIndex)
            {
                occupied++;
            }
        }

        this.Fullness = (float)occupied / RegularInventorySlots;
    }

    /// <summary>
    /// 关闭当前 NPC 对话。
    /// </summary>
    private async ValueTask CloseNpcDialogAsync()
    {
        var closeAction = new CloseNpcDialogAction();
        await closeAction.CloseNpcDialogAsync(this._player).ConfigureAwait(false);
        this._player.OpenedNpc = null;
    }
}
