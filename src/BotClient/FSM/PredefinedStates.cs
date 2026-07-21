using MUnique.OpenMU.BotClient.Core;

namespace MUnique.OpenMU.BotClient.FSM.States;

// ════════════════════════════════════════════════════════
// 状态转换图：
//
//  Idle ───→ Patrol ───→ Chase ───→ Attack ───→ Loot
//   ↑           ↑           │            │          │
//   │           │           │ (失丢)     │ (怪死)   │ (捡完)
//   │           │           ▼            ▼          ▼
//   │           └───────── Patrol ←───── Patrol ←───┘
//   │                                               ↑
//   │             任何状态 (HP<20%) → Evade ────────┘
//   │                                   │ (HP>50%)
//   └───────────────────────────────────┘
// ════════════════════════════════════════════════════════

/// <summary>待机状态。不做任何事，立即转到 Patrol。</summary>
internal sealed class IdleState : IState
{
    public FsmStateType Type => FsmStateType.Idle;

    public void OnEnter(BotContext ctx, WorldMirror wm) { }
    public void OnExit(BotContext ctx, WorldMirror wm) { }

    public void OnUpdate(BotContext ctx, WorldMirror wm, out ActionCommand cmd)
    {
        cmd = ActionCommand.None;
    }

    public FsmStateType CheckTransition(BotContext ctx, WorldMirror wm)
    {
        // 任何情况下都进入 Patrol
        return FsmStateType.Patrol;
    }
}

/// <summary>巡逻状态。随机行走寻找怪物。</summary>
internal sealed class PatrolState : IState
{
    private static readonly Random Rng = new();

    public FsmStateType Type => FsmStateType.Patrol;

    public void OnEnter(BotContext ctx, WorldMirror wm)
    {
        // 设置随机巡逻目标
        ctx.PatrolTargetX = (byte)Math.Clamp(wm.PosX + Rng.Next(-5, 6), 0, 255);
        ctx.PatrolTargetY = (byte)Math.Clamp(wm.PosY + Rng.Next(-5, 6), 0, 255);
        ctx.TicksSinceLastAction = 0;
    }

    public void OnExit(BotContext ctx, WorldMirror wm) { }

    public void OnUpdate(BotContext ctx, WorldMirror wm, out ActionCommand cmd)
    {
        cmd = ActionCommand.WalkRandom;
    }

    public FsmStateType CheckTransition(BotContext ctx, WorldMirror wm)
    {
        // 紧急：低血 → Evade
        if (wm.MaximumHp > 0 && (float)wm.CurrentHp / wm.MaximumHp < 0.2f)
            return FsmStateType.Evade;

        // 找最近的怪物
        ushort? nearestId = null;
        var nearestDist = int.MaxValue;
        byte nearX = 0, nearY = 0;

        foreach (var kvp in wm.Monsters)
        {
            var m = kvp.Value;
            var dist = Math.Max(Math.Abs(m.PosX - wm.PosX), Math.Abs(m.PosY - wm.PosY));
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearestId = m.Id;
                nearX = m.PosX;
                nearY = m.PosY;
            }
        }

        if (nearestId.HasValue && nearestDist <= 15)
        {
            ctx.TargetMonsterId = nearestId.Value;
            ctx.TargetMonsterX = nearX;
            ctx.TargetMonsterY = nearY;
            return FsmStateType.Chase;
        }

        return FsmStateType.Patrol;
    }
}

/// <summary>追击状态。走向目标怪物直到进入攻击距离。</summary>
internal sealed class ChaseState : IState
{
    public FsmStateType Type => FsmStateType.Chase;

    public void OnEnter(BotContext ctx, WorldMirror wm) { }
    public void OnExit(BotContext ctx, WorldMirror wm) { }

    public void OnUpdate(BotContext ctx, WorldMirror wm, out ActionCommand cmd)
    {
        cmd = ActionCommand.WalkToTarget;
    }

    public FsmStateType CheckTransition(BotContext ctx, WorldMirror wm)
    {
        // 紧急：低血 → Evade
        if (wm.MaximumHp > 0 && (float)wm.CurrentHp / wm.MaximumHp < 0.2f)
            return FsmStateType.Evade;

        if (ctx.TargetMonsterId == null)
            return FsmStateType.Patrol;

        // 检查目标是否还在视野内
        if (!wm.Monsters.TryGetValue(ctx.TargetMonsterId.Value, out var monster))
            return FsmStateType.Patrol;

        // 更新目标位置
        ctx.TargetMonsterX = monster.PosX;
        ctx.TargetMonsterY = monster.PosY;

        // 进入攻击距离 → Attack
        var dist = Math.Max(Math.Abs(monster.PosX - wm.PosX), Math.Abs(monster.PosY - wm.PosY));
        if (dist <= 3)
            return FsmStateType.Attack;

        return FsmStateType.Chase;
    }
}

/// <summary>攻击状态。对目标怪物发起攻击。</summary>
internal sealed class AttackState : IState
{
    public FsmStateType Type => FsmStateType.Attack;

    public void OnEnter(BotContext ctx, WorldMirror wm) { }
    public void OnExit(BotContext ctx, WorldMirror wm) { }

    public void OnUpdate(BotContext ctx, WorldMirror wm, out ActionCommand cmd)
    {
        cmd = ActionCommand.AttackMonster;
    }

    public FsmStateType CheckTransition(BotContext ctx, WorldMirror wm)
    {
        // 紧急：低血 → Evade
        if (wm.MaximumHp > 0 && (float)wm.CurrentHp / wm.MaximumHp < 0.2f)
            return FsmStateType.Evade;

        if (ctx.TargetMonsterId == null)
        {
            // 怪已死，检查是否掉落物品
            if (wm.GroundItems.Count > 0)
            {
                var item = wm.GroundItems.First().Value;
                ctx.TargetItemId = item.Id;
                return FsmStateType.Loot;
            }
            return FsmStateType.Patrol;
        }

        // 怪物是否还在视野中
        if (!wm.Monsters.TryGetValue(ctx.TargetMonsterId.Value, out var monster))
        {
            // 怪已死或出视野 → 捡东西或巡逻
            if (wm.GroundItems.Count > 0)
            {
                var item = wm.GroundItems.First().Value;
                ctx.TargetItemId = item.Id;
                return FsmStateType.Loot;
            }
            return FsmStateType.Patrol;
        }

        // 怪物走出攻击范围 → Chase
        var dist = Math.Max(Math.Abs(monster.PosX - wm.PosX), Math.Abs(monster.PosY - wm.PosY));
        if (dist > 3)
            return FsmStateType.Chase;

        return FsmStateType.Attack;
    }
}

/// <summary>拾取状态。走向地上物品并拾取。</summary>
internal sealed class LootState : IState
{
    public FsmStateType Type => FsmStateType.Loot;

    public void OnEnter(BotContext ctx, WorldMirror wm) { }
    public void OnExit(BotContext ctx, WorldMirror wm) { }

    public void OnUpdate(BotContext ctx, WorldMirror wm, out ActionCommand cmd)
    {
        cmd = ActionCommand.PickupItem;
    }

    public FsmStateType CheckTransition(BotContext ctx, WorldMirror wm)
    {
        if (ctx.TargetItemId == null)
            return FsmStateType.Patrol;

        // 物品还在吗
        if (!wm.GroundItems.ContainsKey(ctx.TargetItemId.Value))
        {
            ctx.TotalPickups++;
            return FsmStateType.Patrol;
        }

        return FsmStateType.Loot;
    }
}

/// <summary>逃跑状态。HP 过低时触发，回城/等待恢复。</summary>
internal sealed class EvadeState : IState
{
    public FsmStateType Type => FsmStateType.Evade;

    public void OnEnter(BotContext ctx, WorldMirror wm) { }
    public void OnExit(BotContext ctx, WorldMirror wm) { }

    public void OnUpdate(BotContext ctx, WorldMirror wm, out ActionCommand cmd)
    {
        cmd = ActionCommand.WalkRandom;
    }

    public FsmStateType CheckTransition(BotContext ctx, WorldMirror wm)
    {
        // HP 恢复到安全线以上 → Patrol
        if (wm.MaximumHp > 0 && (float)wm.CurrentHp / wm.MaximumHp > 0.5f)
            return FsmStateType.Patrol;

        // 如果已经死亡，等复活后走 Patrol
        if (wm.CurrentHp <= 0)
            return FsmStateType.Evade;

        return FsmStateType.Evade;
    }
}
