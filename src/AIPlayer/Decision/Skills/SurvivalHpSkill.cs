// <copyright file="SurvivalHpSkill.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision.Skills;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using System.Threading;

/// <summary>
/// survival_hp SKILL — 血量低于 60% 时自动喝红药。
/// 提取自 HeartbeatService.BeatAsync Step 5.6 (RuleEngine case "survival_hp")。
/// 带 2 秒冷却保护，防止每 tick 都喝药。
/// </summary>
public sealed class SurvivalHpSkill : ISkill
{
    private readonly ILogger _logger;
    private DateTime _lastHpPotionTime = DateTime.MinValue;

    /// <summary>冷却时间：每 2 秒最多喝一次药。</summary>
    private static readonly TimeSpan HpCooldown = TimeSpan.FromSeconds(2);

    public SurvivalHpSkill(ILogger logger)
    {
        this._logger = logger;
    }

    public string Id => "survival_hp";
    public int Priority => 1;
    public string Category => "survival";

    public bool CanExecute(AiPlayer player, IGameAdapter adapter)
    {
        var hp = adapter.GetCurrentHp();
        var maxHp = adapter.GetMaxHp();
        if (maxHp <= 0)
            return false;

        return (float)hp / maxHp < 0.6f;
    }

    public async ValueTask<SkillResult> ExecuteAsync(AiPlayer player, IGameAdapter adapter, CancellationToken cancellationToken = default)
    {
        // 冷却保护
        if (DateTime.UtcNow - this._lastHpPotionTime < HpCooldown)
        {
            this._logger.LogDebug("[Skill:survival_hp] HP冷却中，跳过");
            return SkillResult.Skipped;
        }

        this._lastHpPotionTime = DateTime.UtcNow;
        this._logger.LogDebug("[Skill:survival_hp] triggered: HP={Hp}", adapter.GetCurrentHp());

        // 从背包找红药（优先大瓶）
        var inv = player.Inventory;
        if (inv is null)
        {
            this._logger.LogDebug("[Skill:survival_hp] 背包无效");
            return SkillResult.Skipped;
        }

        var hpPotion = inv.Items.FirstOrDefault(i =>
            i.Definition?.Group == 14 && i.Definition?.Number == 3 && i.Durability > 0);
        hpPotion ??= inv.Items.FirstOrDefault(i =>
            i.Definition?.Group == 14 && i.Definition?.Number == 2 && i.Durability > 0);
        hpPotion ??= inv.Items.FirstOrDefault(i =>
            i.Definition?.Group == 14 && i.Definition?.Number == 1 && i.Durability > 0);

        if (hpPotion is null)
        {
            this._logger.LogDebug("[Skill:survival_hp] 背包中无红药");
            return SkillResult.Skipped;
        }

        await adapter.ConsumeItemAsync(hpPotion.ItemSlot).ConfigureAwait(false);
        this._logger.LogInformation("[Skill:survival_hp] ❤️ 喝红药 (Slot={Slot}, HP={Hp}/{MaxHp})",
            hpPotion.ItemSlot, adapter.GetCurrentHp(), adapter.GetMaxHp());
        return SkillResult.ExecutedAndStop;
    }
}
