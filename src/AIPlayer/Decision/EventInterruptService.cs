// <copyright file="EventInterruptService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.MiniGames;

/// <summary>
/// 事件中断服务 — 收到 EventOpenEvent 后的决策层。
///
/// 职责：
///   1. 检查当前任务是否可中断（优先级对比）
///   2. 检查事件入场条件（门票/等级/金币/材料）
///   3. 条件不足时判断是否有后备方案（仓库取物/合成）
///   4. 返回决策结果 <see cref="EventReadiness"/>
/// </summary>
public sealed class EventInterruptService
{
    /// <summary>事件任务的优先级（与 HeartbeatService 注入的事件任务 Priority=15 保持一致）。</summary>
    private const int EventPriority = 15;

    private readonly AiPlayer _player;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventInterruptService"/> class.
    /// </summary>
    /// <param name="player">The AI player.</param>
    /// <param name="logger">The logger.</param>
    public EventInterruptService(AiPlayer player, ILogger logger)
    {
        this._player = player;
        this._logger = logger;
    }

    /// <summary>
    /// 判断当前任务是否应该被事件中断。
    /// 如果当前任务优先级高于事件（值越小优先级越高），则不中断。
    /// 如果当前任务已经 Suspended 或是生存兜底（Priority=999），总是可以中断。
    /// </summary>
    /// <param name="currentTask">当前正在执行的任务，可能为 null。</param>
    /// <param name="evt">事件开放事件。</param>
    /// <returns>是否应该中断当前任务去参加事件。</returns>
    public bool ShouldInterruptForEvent(MissionItem? currentTask, EventOpenEvent evt)
    {
        // 没有当前任务 → 直接可以
        if (currentTask is null)
        {
            return true;
        }

        // 已挂起 → 不重复中断
        if (currentTask.Status == MissionStatus.Suspended)
        {
            return false;
        }

        // 当前任务优先级 > 事件优先级（值越小优先级越高）→ 不中断
        // 事件 Priority=15，生存兜底 Priority=999 所以生存总是可中断
        if (currentTask.Priority < EventPriority)
        {
            this._logger.LogDebug(
                "[EventInterrupt] 当前任务 {Title} (P={Priority}) 优先级高于事件 (P={EventP})，不中断",
                currentTask.Title,
                currentTask.Priority,
                EventPriority);
            return false;
        }

        this._logger.LogInformation(
            "[EventInterrupt] ✅ 决定中断当前任务 {Title} (P={Priority}) 去参加事件 {EventName}",
            currentTask.Title,
            currentTask.Priority,
            evt.Name);
        return true;
    }

    /// <summary>
    /// 检查角色对指定迷你游戏的入场准备状态。
    /// 检查顺序：等级 → 金币 → 背包门票 → 背包材料 → 仓库门票 → 仓库材料。
    /// </summary>
    /// <param name="miniGameDef">迷你游戏定义。</param>
    /// <returns>入场准备状态枚举。</returns>
    public EventReadiness GetEventReadiness(MiniGameDefinition miniGameDef)
    {
        var level = this._player.Level;
        var money = this._player.Money;

        // 1. 等级检查
        if (level < miniGameDef.MinimumCharacterLevel)
        {
            this._logger.LogInformation(
                "[EventInterrupt] ⏭ {Name} 等级不足 ({Level}/{Need})",
                miniGameDef.Name,
                level,
                miniGameDef.MinimumCharacterLevel);
            return EventReadiness.LevelTooLow;
        }

        if (miniGameDef.MaximumCharacterLevel > 0 && level > miniGameDef.MaximumCharacterLevel)
        {
            this._logger.LogInformation(
                "[EventInterrupt] ⏭ {Name} 等级超出 ({Level}/{Max})",
                miniGameDef.Name,
                level,
                miniGameDef.MaximumCharacterLevel);
            return EventReadiness.LevelTooLow;
        }

        // 2. 金币检查
        if (miniGameDef.EntranceFee > 0 && money < miniGameDef.EntranceFee)
        {
            this._logger.LogInformation(
                "[EventInterrupt] ⏭ {Name} 金币不足 ({Money}/{Fee})",
                miniGameDef.Name,
                money,
                miniGameDef.EntranceFee);
            return EventReadiness.NotEnoughMoney;
        }

        // 3. 门票检查
        var ticketDef = miniGameDef.TicketItem;
        if (ticketDef is null)
        {
            // 不需要门票 → Ready
            return EventReadiness.Ready;
        }

        // 3a. 背包中有门票 → Ready
        if (this.HasTicketInInventory(ticketDef, miniGameDef))
        {
            return EventReadiness.Ready;
        }

        // 3b. 背包中有合成材料 → NeedTicket（已有合成链路）
        if (this.HasCraftingMaterialsInInventory(miniGameDef))
        {
            this._logger.LogInformation(
                "[EventInterrupt] {Name} 背包有合成材料但无门票 → 需要合成",
                miniGameDef.Name);
            return EventReadiness.NeedTicket;
        }

        // 3c. 仓库中有门票 → NeedVault
        if (this.HasTicketInVault(ticketDef, miniGameDef))
        {
            this._logger.LogInformation(
                "[EventInterrupt] {Name} 仓库有门票 → 需要去仓库取",
                miniGameDef.Name);
            return EventReadiness.NeedVault;
        }

        // 3d. 仓库中有合成材料 → NeedVault（取材料到背包，然后走合成链路）
        if (this.HasCraftingMaterialsInVault(miniGameDef))
        {
            this._logger.LogInformation(
                "[EventInterrupt] {Name} 仓库有合成材料 → 需要去仓库取材料",
                miniGameDef.Name);
            return EventReadiness.NeedVault;
        }

        // 4. 无门票无材料 → NeedFarm（打材料任务）
        this._logger.LogInformation(
            "[EventInterrupt] {Name} 无门票无材料 → 需要打材料",
            miniGameDef.Name);
        return EventReadiness.NeedFarm;
    }

    /// <summary>
    /// 在仓库中查找是否有门票或合成材料（非背包）。
    /// 注意：此方法需要 vault 对话已打开（player.Vault 非 null），
    /// 否则需要先通过 VaultService 的同步方法查询。
    /// 这里为方便上层调用，使用 VaultService 提供的静态查询方式。
    /// </summary>
    private bool HasTicketInVault(ItemDefinition ticketDef, MiniGameDefinition miniGameDef)
    {
        var vaultItems = this._player.Account?.Vault?.Items;
        if (vaultItems is null)
        {
            return false;
        }

        return vaultItems.Any(item =>
            item.Definition == ticketDef
            && item.Durability > 0
            && item.Level == miniGameDef.TicketItemLevel);
    }

    /// <summary>
    /// 检查背包中是否有门票。
    /// </summary>
    private bool HasTicketInInventory(ItemDefinition ticketDef, MiniGameDefinition miniGameDef)
    {
        var inventory = this._player.Inventory;
        if (inventory is null)
        {
            return false;
        }

        return inventory.Items.Any(item =>
            item.Definition == ticketDef
            && item.Durability > 0
            && item.Level == miniGameDef.TicketItemLevel);
    }

    /// <summary>
    /// 检查背包中是否有门票合成材料。
    /// </summary>
    private bool HasCraftingMaterialsInInventory(MiniGameDefinition miniGameDef)
    {
        // 混沌宝石（Jewel of Chaos）是所有合成必需的
        if (!this.HasItemInInventoryByGroupNumber(12, 15, 0))
        {
            return false;
        }

        var ticketLevel = miniGameDef.TicketItemLevel;
        return miniGameDef.Type switch
        {
            MiniGameType.BloodCastle =>
                this.HasItemInInventoryByGroupNumber(13, 16, ticketLevel)  // Scroll of Archangel
                && this.HasItemInInventoryByGroupNumber(13, 17, ticketLevel), // Blood Bone
            MiniGameType.DevilSquare =>
                this.HasItemInInventoryByGroupNumber(14, 17, ticketLevel)  // Devil's Eye
                && this.HasItemInInventoryByGroupNumber(14, 18, ticketLevel), // Devil's Key
            _ => false,
        };
    }

    /// <summary>
    /// 检查仓库中是否有门票合成材料。
    /// </summary>
    private bool HasCraftingMaterialsInVault(MiniGameDefinition miniGameDef)
    {
        var vaultItems = this._player.Account?.Vault?.Items;
        if (vaultItems is null)
        {
            return false;
        }

        // 混沌宝石
        if (!vaultItems.Any(i => i.Definition?.Group == 12 && i.Definition?.Number == 15))
        {
            return false;
        }

        var ticketLevel = miniGameDef.TicketItemLevel;
        return miniGameDef.Type switch
        {
            MiniGameType.BloodCastle =>
                vaultItems.Any(i => i.Definition?.Group == 13 && i.Definition?.Number == 16 && i.Level == ticketLevel)
                && vaultItems.Any(i => i.Definition?.Group == 13 && i.Definition?.Number == 17 && i.Level == ticketLevel),
            MiniGameType.DevilSquare =>
                vaultItems.Any(i => i.Definition?.Group == 14 && i.Definition?.Number == 17 && i.Level == ticketLevel)
                && vaultItems.Any(i => i.Definition?.Group == 14 && i.Definition?.Number == 18 && i.Level == ticketLevel),
            _ => false,
        };
    }

    /// <summary>
    /// 检查背包中是否有指定 Group/Number/Level 的物品。
    /// </summary>
    private bool HasItemInInventoryByGroupNumber(int group, int number, int level)
    {
        var inv = this._player.Inventory;
        if (inv is null)
        {
            return false;
        }

        return inv.Items.Any(i =>
            i.Definition?.Group == group
            && i.Definition?.Number == number
            && i.Level == level);
    }
}

/// <summary>
/// 事件入场准备状态。
/// </summary>
public enum EventReadiness
{
    /// <summary>有门票、有金币、等级够 → 直接入场。</summary>
    Ready,

    /// <summary>无门票但有材料 → 需要合成。</summary>
    NeedTicket,

    /// <summary>无门票无材料 → 跳过，需要打材料。</summary>
    NeedFarm,

    /// <summary>仓库有门票/材料 → 需要去仓库取。</summary>
    NeedVault,

    /// <summary>等级不够。</summary>
    LevelTooLow,

    /// <summary>金币不够。</summary>
    NotEnoughMoney,
}
