// <copyright file="InventoryCleanupSkill.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision.Skills;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using System.Threading;

/// <summary>
/// inventory_cleanup SKILL — 背包空格不足 4 格时回城清理。
/// 提取自 HeartbeatService.BeatAsync Step 5.5 和 RuleEngine case "inventory_cleanup"。
/// </summary>
public sealed class InventoryCleanupSkill : ISkill
{
    private readonly InventoryManagerService _inventoryManager;
    private readonly ILogger _logger;

    public InventoryCleanupSkill(InventoryManagerService inventoryManager, ILogger logger)
    {
        this._inventoryManager = inventoryManager;
        this._logger = logger;
    }

    public string Id => "inventory_cleanup";
    public int Priority => 10;
    public string Category => "inventory";

    public bool CanExecute(AiPlayer player, IGameAdapter adapter)
    {
        var freeSlots = this._inventoryManager.GetFreeSlotCount();
        return this._inventoryManager.NeedsInventoryCleanup() || freeSlots < 4;
    }

    public async ValueTask<SkillResult> ExecuteAsync(AiPlayer player, IGameAdapter adapter, CancellationToken cancellationToken = default)
    {
        var freeSlots = this._inventoryManager.GetFreeSlotCount();
        this._logger.LogInformation(
            "[Skill:inventory_cleanup] 🎒 背包空间不足({FreeSlots}格空闲, {Fullness:F1}%), 启动回城清理",
            freeSlots, this._inventoryManager.Fullness * 100);

        await this._inventoryManager.ForceCleanupAsync().ConfigureAwait(false);
        return SkillResult.ExecutedAndStop;
    }
}
