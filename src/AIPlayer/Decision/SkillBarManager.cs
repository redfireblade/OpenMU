// <copyright file="SkillBarManager.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Character;

/// <summary>
/// 技能栏管理器 — 将学会的主动技能排序后写入快捷键栏。
///
/// KeyConfiguration 格式（服务端不解析，原样收发）：
///   客户端定义的 byte[]，每 4 个字节为一个快捷键格。
///   Slot 0 = 攻击技能, Slot 1-3 = 常用技能, Slot 4-8 = 其他技能
///   AI 只负责按优先级排序后写入，不关心客户端怎么渲染。
/// </summary>
public sealed class SkillBarManager
{
    private readonly AiPlayer _player;
    private readonly ILogger _logger;
    private readonly SaveKeyConfigurationAction _saveAction = new();
    private byte[]? _lastKnownConfig;

    public SkillBarManager(AiPlayer player, ILogger logger)
    {
        this._player = player;
        this._logger = logger;
    }

    /// <summary>
    /// 更新快捷键栏。检查技能列表变化，需要更新时重新生成 KeyConfiguration。
    /// 每 tick 调用，内部防抖（只有技能列表变化时才实际写入）。
    /// </summary>
    public void UpdateSkillBar()
    {
        var chara = this._player.SelectedCharacter;
        if (chara is null) return;

        var skillList = this._player.SkillList;
        if (skillList is null) return;

        // 收集所有主动攻击技能，按攻击力降序排列
        var attackSkills = skillList.Skills
            .Where(s => s.Skill is not null && IsActiveAttackSkill(s.Skill))
            .OrderByDescending(s => s.Skill!.AttackDamage)
            .ThenBy(s => s.Skill!.Number) // 同攻击力时按编号稳定排序
            .ToList();

        // 如果技能列表为空，不做任何事
        if (attackSkills.Count == 0) return;

        // 生成新的 KeyConfiguration
        // 标准 MU KeyConfiguration 是 90 字节，每 2 字节一个键位
        // Slot 0 放最强攻击技能，Slot 1-N 放其他技能
        var newConfig = new byte[90];
        for (int i = 0; i < newConfig.Length; i++) newConfig[i] = 0xFF; // 默认空

        // Slot 0: 最强攻击技能
        if (attackSkills.Count > 0)
            SkillBarManager.SetSkillSlot(newConfig, 0, attackSkills[0].Skill!.Number);

        // Slot 1-8: 其他技能
        for (int i = 1; i < Math.Min(attackSkills.Count, 9); i++)
        {
            SkillBarManager.SetSkillSlot(newConfig, i, attackSkills[i].Skill!.Number);
        }

        // 检查是否真的变了（防抖）
        if (this._lastKnownConfig is not null && newConfig.SequenceEqual(this._lastKnownConfig))
            return;

        // 写入
        this._saveAction.SaveKeyConfiguration(this._player, newConfig);
        this._lastKnownConfig = newConfig;

        this._logger.LogInformation(
            "[SkillBar] 更新快捷键栏: {Count} 个技能, 主攻击={MainSkill}",
            attackSkills.Count, attackSkills[0].Skill!.Name);
    }

    /// <summary>
    /// 判断是否为主动攻击技能（排除 BUFF/被动/召唤等）。
    /// </summary>
    private static bool IsActiveAttackSkill(Skill? skill)
    {
        if (skill is null) return false;

        return skill.SkillType switch
        {
            SkillType.DirectHit => true,
            SkillType.AreaSkillAutomaticHits => true,
            SkillType.AreaSkillExplicitHits => true,
            SkillType.AreaSkillExplicitTarget => true,
            SkillType.Buff => false,        // BUFF 技能不加入攻击快捷键
            SkillType.Regeneration => false, // 恢复技能自动处理
            SkillType.PassiveBoost => false, // 被动技能不需要施放
            SkillType.SummonMonster => false,
            SkillType.Other => false,
            _ => false,
        };
    }

    /// <summary>
    /// 设置快捷键栏的指定槽位。
    /// MU KeyConfiguration 每 4 字节一个槽位，前 2 字节是技能编号 LE。
    /// </summary>
    private static void SetSkillSlot(byte[] config, int slot, short skillNumber)
    {
        var offset = slot * 4;
        if (offset + 2 >= config.Length) return;
        config[offset] = (byte)(skillNumber & 0xFF);
        config[offset + 1] = (byte)((skillNumber >> 8) & 0xFF);
    }
}
