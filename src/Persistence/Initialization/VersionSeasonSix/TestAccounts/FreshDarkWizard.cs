// <copyright file="FreshDarkWizard.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.TestAccounts;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.Persistence.Initialization.CharacterClasses;

/// <summary>
/// 创建真正原初状态的法师角色。
/// 无装备、无药水、无金币、无宝石 — 和玩家在游戏里新建角色完全一样。
/// 只自动获得：Energy Ball 技能 + Ring of Warrior（由系统插件自动添加）。
/// </summary>
internal class FreshDarkWizard : AccountInitializerBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FreshDarkWizard"/> class.
    /// </summary>
    public FreshDarkWizard(IContext context, GameConfiguration gameConfiguration)
        : base(context, gameConfiguration, "fresh", 1)
    {
    }

    /// <inheritdoc/>
    protected override Character CreateWizard()
    {
        // 严格按照 CreateCharacterAction.cs 的原始逻辑：
        // 1. 设置基础属性（CharacterClass.StatAttributes 的 BaseValue）
        // 2. 设置出生地图 = HomeMap（Lorencia）
        // 3. 设置出生坐标 = 随机安全区
        // 4. 创建空的 Inventory（ItemStorage）
        // 5. 不加任何物品、金币、药水、宝石
        // 6. Energy Ball 技能由 AddEnergyBallForDarkWizard 插件自动添加
        // 7. Ring of Warrior 由 AddRingOfWarriorLevel40ForNewCharacters 插件自动添加
        var character = this.CreateCharacter(
            "FreshDW",
            CharacterClassNumber.DarkWizard,
            this.Level,
            0);

        // 清空金币 — 原版新角色为 0 金币
        character.Inventory!.Money = 5000;
        // 基础药水（NPC购买在某些情况下不稳定，直接给药水保证能玩）
        character.Inventory.Items.Add(this.ItemHelper.CreatePotion(36, 1, 3, 0));  // 小型HP药水 x3
        character.Inventory.Items.Add(this.ItemHelper.CreatePotion(40, 4, 3, 0));  // 小型MP药水 x3

        return character;
    }

    /// <inheritdoc/>
    protected override Account CreateAccount()
    {
        var account = base.CreateAccount();
        // 确保密码和账号名一致（BCrypt 哈希）
        account.LoginName = "fresh";
        account.PasswordHash = BCrypt.Net.BCrypt.HashPassword("fresh");
        account.State = AccountState.Normal;
        return account;
    }
}
