// <copyright file="PetHandlerModule.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using System.Reflection;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Pet;

/// <summary>
/// 暗黑骑士（Dark Raven）宠物控制子模块。
/// 自动管理宠物行为模式，每个心跳检查宠物耐久，耐久为0时设为 Idle。
/// 首次执行时将宠物设为 AttackWithOwner（跟随主人攻击）模式。
/// </summary>
public sealed class PetHandlerModule : IBehaviorSubModule
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;
    private bool _initialized;

    /// <inheritdoc />
    public string ModuleId => "pet_handler";

    /// <summary>
    /// Initializes a new instance of the <see cref="PetHandlerModule"/> class.
    /// </summary>
    /// <param name="player">The AI player.</param>
    /// <param name="adapter">The game adapter.</param>
    /// <param name="logger">The logger.</param>
    public PetHandlerModule(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<StepResult> ExecuteStepAsync(MissionItem item)
    {
        // 每个心跳检查宠物耐久，耐久为0时设为 Idle
        await this.CheckPetDurabilityAsync().ConfigureAwait(false);

        // 首次执行时初始化宠物行为（AttackWithOwner）
        if (!this._initialized)
        {
            await this.InitializeDarkRavenAsync().ConfigureAwait(false);
            this._initialized = true;
        }

        return StepResult.Completed;
    }

    /// <summary>
    /// 初始化暗黑骑士宠物 — 设为 AttackWithOwner 模式（跟随主人攻击）。
    /// </summary>
    private async ValueTask InitializeDarkRavenAsync()
    {
        if (this._player.PetCommandManager is null)
        {
            this._logger.LogDebug("[PetHandler] 未装备宠物，跳过初始化");
            return;
        }

        await this.SetPetBehaviourAsync(2).ConfigureAwait(false); // 2 = AttackWithOwner
        this._logger.LogInformation("[PetHandler] 宠物已设为 AttackWithOwner 模式");
    }

    /// <summary>
    /// 检查宠物耐久，耐久为0时设为 Idle 模式。
    /// </summary>
    private async ValueTask CheckPetDurabilityAsync()
    {
        var inv = this._player.Inventory;
        if (inv is null)
        {
            return;
        }

        // Check the pet slot (right-hand ring slot = 11)
        var pet = inv.GetItem(11);
        if (pet is null)
        {
            return;
        }

        if (pet.Durability <= 0)
        {
            await this.SetPetBehaviourAsync(0).ConfigureAwait(false); // 0 = Idle
            this._logger.LogInformation("[PetHandler] 宠物耐久为0，已设为 Idle 模式");
        }
    }

    /// <summary>
    /// Sets the pet behaviour using reflection, since PetBehaviour enum is not accessible
    /// from this assembly.
    /// </summary>
    private async ValueTask SetPetBehaviourAsync(int behaviourValue)
    {
        var petCommandManager = this._player.PetCommandManager;
        if (petCommandManager is null)
        {
            return;
        }

        var setBehaviourMethod = petCommandManager.GetType().GetMethod("SetBehaviourAsync");
        if (setBehaviourMethod is null)
        {
            this._logger.LogWarning("[PetHandler] SetBehaviourAsync method not found on PetCommandManager");
            return;
        }

        var parameters = setBehaviourMethod.GetParameters();
        if (parameters.Length == 0 || parameters[0].ParameterType.Name != "PetBehaviour")
        {
            this._logger.LogWarning("[PetHandler] Unexpected SetBehaviourAsync signature");
            return;
        }

        var petBehaviourEnum = Enum.ToObject(parameters[0].ParameterType, behaviourValue);
        await (ValueTask)setBehaviourMethod.Invoke(petCommandManager, new[] { petBehaviourEnum, null! })!;
    }
}
