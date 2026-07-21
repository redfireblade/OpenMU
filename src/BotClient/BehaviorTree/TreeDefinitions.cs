using MUnique.OpenMU.BotClient.Core;

namespace MUnique.OpenMU.BotClient.BehaviorTree;

/// <summary>预定义行为树工厂方法。</summary>
public static class TreeDefinitions
{
    /// <summary>创建标准狩猎行为树。</summary>
    /// <remarks>
    /// 树结构：
    /// Selector "MainDecision"
    /// ├── Sequence "EmergencyHeal"
    /// │   ├── Condition "HP &lt; 30%"
    /// │   └── Action "UseHealthPotion"
    /// ├── Sequence "PickupItem"
    /// │   ├── Condition "Item On Ground"
    /// │   └── Action "PickupItem"
    /// ├── Sequence "Combat"
    /// │   ├── Condition "Monster In Vision"
    /// │   ├── Action "WalkToTarget"  (超出攻击距离时)
    /// │   └── Action "AttackMonster" (在攻击距离内)
    /// └── Action "RandomWalk" (兜底)
    /// </remarks>
    public static BehaviorTreeEngine CreateHuntingTree()
    {
        var root = new SelectorNode
        {
            Name = "MainDecision",
            NodeId = 0,
        };

        // 1. 紧急喝药
        root.Add(new SequenceNode
        {
            Name = "EmergencyHeal",
            NodeId = 1,
        }.Add(new ConditionNode((ctx, wm) =>
        {
            if (wm.MaximumHp <= 0) return false;
            return (float)wm.CurrentHp / wm.MaximumHp < 0.3f;
        }, "HP < 30%")
        {
            NodeId = 2,
        }).Add(new ActionNode(ActionCommand.UseHealthPotion, "UseHealthPotion")
        {
            NodeId = 3,
        }));

        // 2. 捡物品
        root.Add(new SequenceNode
        {
            Name = "PickupItem",
            NodeId = 4,
        }.Add(new ConditionNode((ctx, wm) =>
        {
            return wm.GroundItems.Count > 0;
        }, "Item On Ground")
        {
            NodeId = 5,
        }).Add(new ActionNode(ActionCommand.PickupItem, "PickupItem")
        {
            NodeId = 6,
        }));

        // 3. 战斗
        var combatSeq = new SequenceNode
        {
            Name = "Combat",
            NodeId = 7,
        };

        combatSeq.Add(new ConditionNode((ctx, wm) =>
        {
            // 找最近的怪物
            ushort? nearestId = null;
            var nearestDist = int.MaxValue;

            foreach (var kvp in wm.Monsters)
            {
                var m = kvp.Value;
                var dist = Math.Max(Math.Abs(m.PosX - wm.PosX), Math.Abs(m.PosY - wm.PosY));
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestId = m.Id;
                }
            }

            if (nearestId.HasValue)
            {
                ctx.TargetMonsterId = nearestId.Value;
                return true;
            }
            return false;
        }, "Monster In Vision")
        {
            NodeId = 8,
        });

        // 战斗子节点：根据距离决定走路还是攻击
        var combatSelector = new SelectorNode
        {
            Name = "CombatAction",
            NodeId = 9,
        };

        combatSelector.Add(new SequenceNode
        {
            Name = "AttackIfInRange",
            NodeId = 10,
        }.Add(new ConditionNode((ctx, wm) =>
        {
            if (ctx.TargetMonsterId == null) return false;
            if (!wm.Monsters.TryGetValue(ctx.TargetMonsterId.Value, out var m)) return false;
            var dist = Math.Max(Math.Abs(m.PosX - wm.PosX), Math.Abs(m.PosY - wm.PosY));
            ctx.TargetMonsterX = m.PosX;
            ctx.TargetMonsterY = m.PosY;
            return dist <= 3;
        }, "In Attack Range")
        {
            NodeId = 11,
        }).Add(new ActionNode(ActionCommand.AttackMonster, "AttackMonster")
        {
            NodeId = 12,
        }));

        combatSelector.Add(new ActionNode(ActionCommand.WalkToTarget, "WalkToTarget")
        {
            NodeId = 13,
        });

        combatSeq.Add(combatSelector);
        root.Add(combatSeq);

        // 4. 兜底随机走
        root.Add(new ActionNode(ActionCommand.WalkRandom, "RandomWalk")
        {
            NodeId = 14,
        });

        return new BehaviorTreeEngine(root);
    }
}
