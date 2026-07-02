// <copyright file="IActionExecutor.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.AccessLayer;

/// <summary>
/// 统一执行契约 — Layer 1 核心接口。
/// 无论底层是发封包、调 API 还是模拟鼠标，必须接受标准指令执行。
/// 心智引擎通过此接口下发动作，不关心底层通信细节。
/// 参考 OAPS v3.0 2.1 核心接口契约。
/// </summary>
public interface IActionExecutor
{
    /// <summary>移动到指定坐标。</summary>
    ValueTask<bool> MoveToAsync(byte x, byte y, CancellationToken ct = default);

    /// <summary>攻击指定目标。</summary>
    ValueTask<bool> AttackAsync(uint targetId, CancellationToken ct = default);

    /// <summary>使用技能攻击目标。</summary>
    ValueTask<bool> SkillAttackAsync(uint targetId, ushort skillNumber, CancellationToken ct = default);

    /// <summary>拾取指定物品。</summary>
    ValueTask<bool> PickupAsync(uint itemId, CancellationToken ct = default);

    /// <summary>与 NPC 交互。</summary>
    ValueTask<bool> InteractAsync(uint npcId, int dialogOption = 0, CancellationToken ct = default);

    /// <summary>使用物品。</summary>
    ValueTask<bool> UseItemAsync(uint itemId, CancellationToken ct = default);

    /// <summary>使用药水（HP/MP）。</summary>
    ValueTask<bool> DrinkPotionAsync(PotionType type, CancellationToken ct = default);

    /// <summary>是否正在行走。</summary>
    bool IsWalking { get; }
}

/// <summary>
/// 药水类型。
/// </summary>
public enum PotionType
{
    /// <summary>HP 恢复药水。</summary>
    Health,

    /// <summary>MP 恢复药水。</summary>
    Mana,
}
