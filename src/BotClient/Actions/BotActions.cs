using MUnique.OpenMU.BotClient.Core;

namespace MUnique.OpenMU.BotClient.Actions;

/// <summary>动作分发器。将 ActionCommand 转换为具体的封包发送操作。</summary>
/// <remarks>
/// 每个方法对应一个具体的游戏操作：
/// - 这里只封装"要发送什么"，不包含决策逻辑
/// - 实际的封包构造通过 MuHeadlessBot 的 Connection 完成
/// - 所有方法都是 fire-and-forget 异步，不阻塞调度器
/// </remarks>
public static class BotActions
{
    /// <summary>MU 方向偏移表（索引 1-8 对应 8 方向）。</summary>
    private static readonly (sbyte Dx, sbyte Dy)[] DirectionOffsets =
    {
        (0, 0),     // 0: 未使用
        (-1, -1),   // 1: 左上
        (0, -1),    // 2: 上
        (1, -1),    // 3: 右上
        (1, 0),     // 4: 右
        (1, 1),     // 5: 右下
        (0, 1),     // 6: 下
        (-1, 1),    // 7: 左下
        (-1, 0),    // 8: 左
    };

    /// <summary>根据 ActionCommand 和当前上下文执行对应操作。</summary>
    /// <param name="cmd">动作指令。</param>
    /// <param name="ctx">BOT 上下文。</param>
    /// <param name="mirror">世界镜像。</param>
    /// <param name="sendAction">封包发送回调（由 MuHeadlessBot 提供）。</param>
    public static void Execute(
        ActionCommand cmd,
        BotContext ctx,
        WorldMirror mirror,
        BotActionDelegates sendAction)
    {
        switch (cmd)
        {
            case ActionCommand.WalkRandom:
                DoWalkRandom(ctx, mirror, sendAction);
                break;

            case ActionCommand.WalkToTarget:
                DoWalkToTarget(ctx, mirror, sendAction);
                break;

            case ActionCommand.AttackMonster:
                DoAttackMonster(ctx, mirror, sendAction);
                break;

            case ActionCommand.UseHealthPotion:
                DoUseHealthPotion(sendAction);
                break;

            case ActionCommand.PickupItem:
                DoPickupItem(ctx, mirror, sendAction);
                break;

            case ActionCommand.Respawn:
                // 复活由游戏服务器自动处理
                break;

            default:
                break;
        }
    }

    private static void DoWalkRandom(BotContext ctx, WorldMirror mirror, BotActionDelegates sendAction)
    {
        // 每 10 tick 走一次（约 2 秒）
        if (ctx.TicksSinceLastAction < 10)
            return;

        ctx.TicksSinceLastAction = 0;

        var dir = Random.Shared.Next(1, 9);     // 1..8
        var steps = Random.Shared.Next(1, 4);   // 1..3

        var offset = DirectionOffsets[dir];
        var newX = (byte)Math.Clamp(mirror.PosX + (offset.Dx * steps), 0, 255);
        var newY = (byte)Math.Clamp(mirror.PosY + (offset.Dy * steps), 0, 255);

        sendAction.SendWalk(mirror.PosX, mirror.PosY, (byte)steps, (byte)dir, newX, newY);
    }

    private static void DoWalkToTarget(BotContext ctx, WorldMirror mirror, BotActionDelegates sendAction)
    {
        if (ctx.TargetMonsterId == null)
            return;

        // 每 10 tick 走一步
        if (ctx.TicksSinceLastAction < 10)
            return;

        ctx.TicksSinceLastAction = 0;

        var dir = GetDirectionTo(mirror.PosX, mirror.PosY,
            (byte)ctx.TargetMonsterX, (byte)ctx.TargetMonsterY);
        if (dir == 0)
            return;

        var offset = DirectionOffsets[dir];
        var newX = (byte)Math.Clamp(mirror.PosX + offset.Dx, 0, 255);
        var newY = (byte)Math.Clamp(mirror.PosY + offset.Dy, 0, 255);

        sendAction.SendWalk(mirror.PosX, mirror.PosY, 1, dir, newX, newY);
    }

    private static void DoAttackMonster(BotContext ctx, WorldMirror mirror, BotActionDelegates sendAction)
    {
        if (ctx.TargetMonsterId == null)
            return;

        // 每 10 tick 攻击一次
        if (ctx.TicksSinceLastAction < 10)
            return;

        ctx.TicksSinceLastAction = 0;

        sendAction.SendAttack(ctx.TargetMonsterId.Value);
    }

    private static void DoUseHealthPotion(BotActionDelegates sendAction)
    {
        sendAction.SendUseHealthPotion();
    }

    private static void DoPickupItem(BotContext ctx, WorldMirror mirror, BotActionDelegates sendAction)
    {
        if (ctx.TargetItemId == null)
            return;

        // 每 5 tick 尝试一次
        if (ctx.TicksSinceLastAction < 5)
            return;

        ctx.TicksSinceLastAction = 0;

        sendAction.SendPickup(ctx.TargetItemId.Value);
    }

    /// <summary>计算 MU 方向（1-8）。</summary>
    private static byte GetDirectionTo(byte fromX, byte fromY, byte toX, byte toY)
    {
        var dx = toX - fromX;
        var dy = toY - fromY;

        if (dx == 0 && dy == 0) return 0;

        if (Math.Abs(dx) >= Math.Abs(dy))
            return dx > 0 ? (byte)(dy >= 0 ? 5 : 3) : (byte)(dy >= 0 ? 7 : 1);
        else
            return dy > 0 ? (byte)(dx >= 0 ? 6 : 7) : (byte)(dx >= 0 ? 4 : 2);
    }
}

/// <summary>封包发送回调委托集合。由 MuHeadlessBot 实现。</summary>
public sealed class BotActionDelegates
{
    /// <summary>发送行走请求。</summary>
    public required Action<byte, byte, byte, byte, byte, byte> SendWalk { get; init; }

    /// <summary>发送攻击请求。</summary>
    public required Action<ushort> SendAttack { get; init; }

    /// <summary>发送喝药请求。</summary>
    public required Action SendUseHealthPotion { get; init; }

    /// <summary>发送拾取请求。</summary>
    public required Action<ushort> SendPickup { get; init; }
}
