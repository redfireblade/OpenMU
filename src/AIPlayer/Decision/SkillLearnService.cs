// <copyright file="SkillLearnService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;

/// <summary>
/// 技能自动学习服务 — 背包中有技能书/卷轴时自动学习。
///
/// 执行时机：
///   每次拾取物品后，由 HeartbeatService.BeatAsync 检测。
///   背包中有带 Skill 定义的可消耗物品 → 尝试学习。
///
/// 判定条件：
///   1. 物品必须在背包储物格中（非装备格）
///   2. 物品 Definition.Skill 不为 null（技能书/卷轴/技能宝石）
///   3. 技能尚未习得（SkillList.ContainsSkill 返回 false）
///   4. 背包至少保留 1 个空格（技能书消耗后会腾出位置）
///   5. 角色满足学习条件（等级/职业 — 由游戏引擎 ConsumeItemAsync 自动判断）
/// </summary>
public sealed class SkillLearnService
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillLearnService"/> class.
    /// </summary>
    /// <param name="player">AI 玩家实例。</param>
    /// <param name="adapter">游戏操作适配器。</param>
    /// <param name="logger">日志。</param>
    public SkillLearnService(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
    }

    /// <summary>
    /// 检查背包是否有技能书，尝试学习。
    /// 每次 tick 只学一个技能，避免背包错乱。
    /// </summary>
    /// <returns>是否成功学了一个技能。</returns>
    public async ValueTask<bool> TryLearnSkillsAsync()
    {
        if (this._adapter.GetCurrentMap() is null)
        {
            return false;
        }

        var inv = this._player.Inventory;
        if (inv is null)
        {
            return false;
        }

        foreach (var item in inv.Items)
        {
            // 跳过装备格（0-11）
            if (item.ItemSlot <= InventoryConstants.LastEquippableItemSlotIndex)
            {
                continue;
            }

            var def = item.Definition;
            if (def is null)
            {
                continue;
            }

            // 只有带 Skill 定义的物品才可学（技能书/卷轴/技能宝石）
            var skill = def.Skill;
            if (skill is null)
            {
                continue;
            }

            // 检查技能是否已学
            var skillId = (ushort)skill.Number;
            if (this._player.SkillList?.ContainsSkill(skillId) == true)
            {
                continue;
            }

            // 尝试学习（消耗物品）
            this._logger.LogInformation(
                "[SkillLearn] 📖 学习技能: {SkillName} (Item={ItemName}, Slot={Slot})",
                skill.Name,
                def.Name.ToString() ?? "?",
                item.ItemSlot);

            await this._adapter.ConsumeItemAsync(item.ItemSlot).ConfigureAwait(false);

            // 验证是否学成
            if (this._player.SkillList?.ContainsSkill(skillId) == true)
            {
                this._logger.LogInformation(
                    "[SkillLearn] ✅ 技能 {SkillName} 学习成功！",
                    skill.Name);
            }
            else
            {
                this._logger.LogWarning(
                    "[SkillLearn] ❌ 技能 {SkillName} 学习失败（可能不满足条件）",
                    skill.Name);
            }

            return true; // 每次只学一个
        }

        return false;
    }
}
