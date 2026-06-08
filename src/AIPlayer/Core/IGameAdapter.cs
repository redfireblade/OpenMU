// <copyright file="IGameAdapter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 游戏操作抽象接口。
/// 将 AI 模块与具体 Player/Monster/GameContext 类型解耦，
/// 使模块可测试且不直接依赖游戏内部 API。
///
/// Wave 1 覆盖 5 个最频繁的操作调用。
/// 后续 Wave 将逐步扩展覆盖所有 15+ 直接调用。
/// </summary>
public interface IGameAdapter
{
    /// <summary>异步寻路行走。</summary>
    ValueTask<WalkResult> WalkToAsync(Point target, GameMap map);

    /// <summary>攻击指定目标。</summary>
    ValueTask HitAsync(IAttackable target, byte skillNumber, Direction direction);

    /// <summary>
    /// 使用技能直接攻击目标（绕过技能插件的安全区/observer 检查）。
    /// 与 HitAsync 的区别：传入 SkillEntry 以使用正确的技能伤害公式计算。
    /// </summary>
    ValueTask HitWithSkillAsync(IAttackable target, SkillEntry skillEntry);

    /// <summary>施放技能。</summary>
    ValueTask PerformSkillAsync(IAttackable? target, ushort skillNumber);

    /// <summary>拾取掉落物品。</summary>
    ValueTask PickupItemAsync(ushort itemId);

    /// <summary>直接行走（跳过 A* 算法选择，已有构建好的 steps）。</summary>
    ValueTask WalkDirectAsync(Point target, WalkingStep[] steps);

    /// <summary>使用药水。</summary>
    ValueTask ConsumeItemAsync(byte inventorySlot);

    // --- Wave 2: P0 查询方法 (8 方法) ---

    /// <summary>获取玩家当前坐标。</summary>
    Point GetPlayerPosition();

    /// <summary>玩家是否正在行走。</summary>
    bool IsPlayerWalking();

    /// <summary>获取玩家当前 HP。</summary>
    int GetCurrentHp();

    /// <summary>获取玩家最大 HP。</summary>
    int GetMaxHp();

    /// <summary>获取玩家当前 MP。</summary>
    int GetCurrentMp();

    /// <summary>获取玩家最大 MP。</summary>
    int GetMaxMp();

    /// <summary>获取玩家等级。</summary>
    int GetPlayerLevel();

    /// <summary>获取玩家当前地图。</summary>
    GameMap? GetCurrentMap();

    /// <summary>
    /// 将玩家传送到指定地图的随机安全区入口。
    /// 如果玩家已在该地图上，则不执行操作。
    /// </summary>
    /// <param name="mapNumber">目标地图编号。</param>
    /// <returns>传送结果，包含状态码和位置上下文。</returns>
    ValueTask<WarpResult> WarpToMapAsync(ushort mapNumber);

    // --- Wave 3: 组队操作 (3 方法) ---

    /// <summary>
    /// 加入指定玩家的队伍。
    /// 发送组队请求给目标玩家。如果目标是 AI 玩家，请求会被自动接受。
    /// </summary>
    /// <param name="targetPlayerName">目标玩家名称。</param>
    /// <returns>组队结果，包含状态码和原因。</returns>
    ValueTask<PartyJoinResult> PartyJoinAsync(string targetPlayerName);

    /// <summary>
    /// 离开当前队伍。
    /// 如果不在任何队伍中，则不执行操作。
    /// </summary>
    ValueTask PartyLeaveAsync();

    /// <summary>
    /// 跟随队长（PartyMaster）。
    /// 获取队长位置并走向队长。如果队长不在同一地图或无队伍，则不执行操作。
    /// </summary>
    /// <param name="followDistance">保持与队长的距离（默认 3 格）。</param>
    ValueTask PartyFollowAsync(float followDistance = 3f);

    // --- Wave Quest: 任务接口 (4 方法) ---

    /// <summary>获取当前角色所有活跃任务。</summary>
    IReadOnlyList<ActiveQuestInfo> GetActiveQuests();

    /// <summary>获取当前对话NPC可接的任务列表。</summary>
    IReadOnlyList<QuestDefinitionInfo> GetAvailableQuests();

    /// <summary>接受指定任务。</summary>
    ValueTask<QuestActionResult> StartQuestAsync(short group, short number);

    /// <summary>提交完成任务。</summary>
    ValueTask<QuestActionResult> CompleteQuestAsync(short group, short number);

    /// <summary>执行任务客户端操作（标记 ClientActionPerformed）。</summary>
    ValueTask PerformQuestClientActionAsync(short group, short number);
}

#region Quest Data Classes

/// <summary>
/// 活跃任务信息，用于 AI 查询任务进度。
/// </summary>
public sealed class ActiveQuestInfo
{
    /// <summary>任务组编号。</summary>
    public short Group { get; init; }

    /// <summary>任务编号。</summary>
    public short Number { get; init; }

    /// <summary>任务名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>是否需要客户端动作。</summary>
    public bool RequiresClientAction { get; init; }

    /// <summary>需要的怪物击杀进度。</summary>
    public IReadOnlyList<QuestMonsterKillInfo> RequiredKills { get; init; } = Array.Empty<QuestMonsterKillInfo>();

    /// <summary>需要的物品进度。</summary>
    public IReadOnlyList<QuestItemRequirementInfo> RequiredItems { get; init; } = Array.Empty<QuestItemRequirementInfo>();

    /// <summary>发布/提交该任务的 NPC 编号。</summary>
    public short? QuestGiverNumber { get; init; }
}

/// <summary>
/// 怪物击杀需求信息。
/// </summary>
public sealed class QuestMonsterKillInfo
{
    /// <summary>怪物名称。</summary>
    public string MonsterName { get; init; } = string.Empty;

    /// <summary>怪物编号 (MonsterDefinition.Number)。</summary>
    public short MonsterNumber { get; init; }

    /// <summary>当前击杀数。</summary>
    public int Current { get; init; }

    /// <summary>需求击杀数。</summary>
    public int Required { get; init; }
}

/// <summary>
/// 物品需求信息。
/// </summary>
public sealed class QuestItemRequirementInfo
{
    /// <summary>物品名称。</summary>
    public string ItemName { get; init; } = string.Empty;

    /// <summary>需求数量。</summary>
    public int Required { get; init; }
}

/// <summary>
/// 任务定义信息（可接任务列表用）。
/// 不暴露 QuestDefinition 游戏类型，通过数据类隔离。
/// </summary>
public sealed class QuestDefinitionInfo
{
    /// <summary>任务组编号。</summary>
    public short Group { get; init; }

    /// <summary>任务编号。</summary>
    public short Number { get; init; }

    /// <summary>任务名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>最低等级。</summary>
    public int MinimumCharacterLevel { get; init; }

    /// <summary>是否可重复完成。</summary>
    public bool Repeatable { get; init; }

    /// <summary>是否需要客户端动作。</summary>
    public bool RequiresClientAction { get; init; }

    /// <summary>发布/提交该任务的 NPC 编号。</summary>
    public short? QuestGiverNumber { get; init; }
}

#endregion
