// <copyright file="SkillAIService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;

public sealed class SkillAIService
{
    private readonly AiPlayer _player;

    public SkillAIService(AiPlayer player)
    {
        this._player = player;
    }

    public SkillEntry? SelectAttackSkill(IAttackable target) => null;
    public ValueTask<bool> TryHealAsync() => ValueTask.FromResult(false);
    public ValueTask<bool> TryAutoBuffAsync() => ValueTask.FromResult(false);
}
