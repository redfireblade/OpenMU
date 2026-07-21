namespace MUnique.OpenMU.BotClient.Core;

/// <summary>动作指令：AI 决策引擎的输出。</summary>
public enum ActionCommand
{
    /// <summary>无操作。</summary>
    None,

    /// <summary>随机行走巡逻。</summary>
    WalkRandom,

    /// <summary>走向锁定目标（怪物）。</summary>
    WalkToTarget,

    /// <summary>走到物品位置拾取。</summary>
    WalkToItem,

    /// <summary>走到 NPC 位置。</summary>
    WalkToNpc,

    /// <summary>攻击锁定的怪物。</summary>
    AttackMonster,

    /// <summary>使用 HP 药水。</summary>
    UseHealthPotion,

    /// <summary>使用 MP 药水。</summary>
    UseManaPotion,

    /// <summary>拾取地上物品。</summary>
    PickupItem,

    /// <summary>与 NPC 对话。</summary>
    TalkToNpc,

    /// <summary>从 NPC 商店购买。</summary>
    BuyFromNpc,

    /// <summary>复活/回城。</summary>
    Respawn,

    /// <summary>使用技能。</summary>
    UseSkill,

    /// <summary>原地待机。</summary>
    Idle,
}

/// <summary>AI 决策模式。</summary>
public enum BotDecisionMode
{
    /// <summary>有限状态机。</summary>
    FiniteStateMachine,

    /// <summary>行为树。</summary>
    BehaviorTree,
}

/// <summary>FSM 状态类型。</summary>
public enum FsmStateType
{
    /// <summary>待机。</summary>
    Idle,

    /// <summary>巡逻寻怪。</summary>
    Patrol,

    /// <summary>追击目标。</summary>
    Chase,

    /// <summary>攻击。</summary>
    Attack,

    /// <summary>拾取。</summary>
    Loot,

    /// <summary>逃跑/回城。</summary>
    Evade,
}
