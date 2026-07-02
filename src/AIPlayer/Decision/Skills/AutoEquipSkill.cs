// <copyright file="AutoEquipSkill.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision.Skills;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using System.Threading;

/// <summary>
/// auto_equip SKILL — 背包有更好装备时自动换上。
/// 提取自 HeartbeatService.BeatAsync Step 5.6 (RuleEngine case "auto_equip")。
/// 不阻塞 tick，换装后继续让其他 SKILL/决策系统执行。
/// </summary>
public sealed class AutoEquipSkill : ISkill
{
    private readonly EquipmentCompareService _equipmentCompare;
    private readonly ILogger _logger;

    public AutoEquipSkill(EquipmentCompareService equipmentCompare, ILogger logger)
    {
        this._equipmentCompare = equipmentCompare;
        this._logger = logger;
    }

    public string Id => "auto_equip";
    public int Priority => 20;
    public string Category => "equip";

    public bool CanExecute(AiPlayer player, IGameAdapter adapter)
    {
        var inv = player.Inventory;
        if (inv is null) return false;

        // 遍历背包，检查是否有可装备且比身上同部位好的物品
        foreach (var item in inv.Items)
        {
            if (item.Definition is null || item.ItemSlot <= 11)
                continue;

            if (item.Definition.ItemSlot is null)
                continue;

            var targetSlot = item.Definition.ItemSlot.ItemSlots?.FirstOrDefault(s => s <= 11);
            if (targetSlot is null)
                continue;

            var equipped = inv.Items.FirstOrDefault(i => i.ItemSlot == targetSlot.Value);
            if (equipped is null)
                return true; // 空装备位，可穿

            var dir = player.BuildDirection;
            var newScore = EquipmentCompareService.ScoreEquipQuality(item, dir);
            var oldScore = EquipmentCompareService.ScoreEquipQuality(equipped, dir);
            if (newScore > oldScore)
                return true;
        }

        return false;
    }

    public async ValueTask<SkillResult> ExecuteAsync(AiPlayer player, IGameAdapter adapter, CancellationToken cancellationToken = default)
    {
        this._logger.LogDebug("[Skill:auto_equip] 执行自动换装检查");
        if (this._equipmentCompare is not null)
        {
            await this._equipmentCompare.AutoEquipIfBetterAsync().ConfigureAwait(false);
        }

        return SkillResult.Executed;
    }
}
