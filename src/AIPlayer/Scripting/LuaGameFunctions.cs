// <copyright file="LuaGameFunctions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Scripting;

using MUnique.OpenMU.AIPlayer;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlayerActions.Quests;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// 注册所有游戏函数到Lua脚本引擎。
/// 覆盖外挂级能力：移动/战斗/物品/对话/技能/加点/条件判断。
/// </summary>
public static class LuaGameFunctions
{
    public static void RegisterAll(LuaScriptEngine engine, AiPlayer player, IGameAdapter adapter)
    {
        // ═══ 移动类 ═══
        RegisterMoveFunctions(engine, adapter);
        // ═══ 战斗类 ═══
        RegisterCombatFunctions(engine, player, adapter);
        // ═══ NPC/对话类 ═══
        RegisterDialogFunctions(engine, player, adapter);
        // ═══ 物品/拾取 ═══
        RegisterItemFunctions(engine, player, adapter);
        // ═══ 状态/生存 ═══
        RegisterSurvivalFunctions(engine, player, adapter);
        // ═══ 条件判断 ═══
        RegisterConditionFunctions(engine, player, adapter);
    }

    static void RegisterMoveFunctions(LuaScriptEngine e, IGameAdapter a)
    {
        e.RegisterFunction("walk_to", async (args, _) =>
        {
            if (args.Length < 2 || !byte.TryParse(args[0], out var x) || !byte.TryParse(args[1], out var y)) return false;
            var m = a.GetCurrentMap(); if (m is null) return false;
            await a.WalkToAsync(new Point(x, y), m).ConfigureAwait(false); return true;
        });

        e.RegisterFunction("walk_to_npc", async (args, _) =>
        {
            if (args.Length < 1 || !short.TryParse(args[0], out var num)) return false;
            var m = a.GetCurrentMap(); if (m is null) return false;
            // 用 GetNpcsInRange 查询NPC（非怪物列表）
            var npc = m.GetNpcsInRange(a.GetPlayerPosition(), 200)
                .FirstOrDefault(n => n.Definition?.Number == num);
            if (npc is null) return false;
            await a.WalkToAsync(npc.Position, m).ConfigureAwait(false); return true;
        });

        e.RegisterFunction("random_walk", async (args, _) =>
        {
            var r = args.Length > 0 && int.TryParse(args[0], out var v) ? v : 10;
            var p = a.GetPlayerPosition(); var m = a.GetCurrentMap(); if (m is null) return false;
            var nx = (byte)Math.Clamp(p.X + Random.Shared.Next(-r, r + 1), 0, 255);
            var ny = (byte)Math.Clamp(p.Y + Random.Shared.Next(-r, r + 1), 0, 255);
            await a.WalkToAsync(new Point(nx, ny), m).ConfigureAwait(false); return true;
        });

        e.RegisterFunction("leave_safezone", async (_, _) =>
        {
            var p = a.GetPlayerPosition(); var m = a.GetCurrentMap(); if (m is null) return false;
            var ex = (byte)Math.Min(255, p.X + 20);
            await a.WalkToAsync(new Point(ex, p.Y), m).ConfigureAwait(false); return true;
        });

        e.RegisterFunction("warp_to_map", async (args, _) =>
        {
            if (args.Length > 0 && ushort.TryParse(args[0], out var mn)) { await a.WarpToMapAsync(mn).ConfigureAwait(false); return true; }
            return false;
        });
    }

    static void RegisterCombatFunctions(LuaScriptEngine e, AiPlayer p, IGameAdapter a)
    {
        e.RegisterFunction("attack", async (_, _) =>
        {
            var pos = a.GetPlayerPosition(); var m = a.GetCurrentMap(); if (m is null) return false;
            var t = m.GetAttackablesInRange(pos, 10).FirstOrDefault(x => x.IsAlive);
            if (t is null) return false;
            await a.HitAsync(t, 0, 0).ConfigureAwait(false); return true;
        });

        e.RegisterFunction("attack_target", async (args, _) =>
        {
            var pos = a.GetPlayerPosition(); var m = a.GetCurrentMap(); if (m is null) return false;
            var t = m.GetAttackablesInRange(pos, 10).FirstOrDefault(x => x.IsAlive); if (t is null) return false;
            var best = p.SkillList?.Skills?.OrderByDescending(s => s.Skill?.AttackDamage ?? 0).FirstOrDefault();
            if (best?.Skill is not null) { await a.HitWithSkillAsync(t, best).ConfigureAwait(false); return true; }
            await a.HitAsync(t, 0, 0).ConfigureAwait(false); return true;
        });

        e.RegisterFunction("use_best_skill", async (_, _) =>
        {
            var best = p.SkillList?.Skills?.OrderByDescending(s => s.Skill?.AttackDamage ?? 0).FirstOrDefault();
            if (best?.Skill is null) return false;
            var pos = a.GetPlayerPosition(); var m = a.GetCurrentMap(); if (m is null) return false;
            var t = m.GetAttackablesInRange(pos, 10).FirstOrDefault(x => x.IsAlive);
            if (t is null) return false;
            await a.HitWithSkillAsync(t, best).ConfigureAwait(false); return true;
        });
    }

    static void RegisterDialogFunctions(LuaScriptEngine e, AiPlayer p, IGameAdapter a)
    {
        e.RegisterFunction("talk_npc", async (args, _) =>
        {
            if (args.Length < 1 || !short.TryParse(args[0], out var num)) return false;
            var pos = a.GetPlayerPosition(); var m = a.GetCurrentMap(); if (m is null) return false;
            var npc = m.GetNpcsInRange(pos, 200)
                .FirstOrDefault(n => n.Definition?.Number == num);
            if (npc is null) return false;
            if (pos.EuclideanDistanceTo(npc.Position) > 3) await a.WalkToAsync(npc.Position, m).ConfigureAwait(false);
            var talk = new TalkNpcAction(); await talk.TalkToNpcAsync(p, npc).ConfigureAwait(false); return true;
        });

        e.RegisterFunction("close_dialog", async (_, _) =>
        {
            try { var act = new CloseNpcDialogAction(); await act.CloseNpcDialogAsync(p).ConfigureAwait(false); return true; }
            catch { return false; }
        });

        e.RegisterFunction("get_buff", async (_, _) =>
        {
            try { await new ElfSoldierBuffRequestAction().RequestBuffAsync(p).ConfigureAwait(false); return true; }
            catch { return false; }
        });
    }

    static void RegisterItemFunctions(LuaScriptEngine e, AiPlayer p, IGameAdapter a)
    {
        e.RegisterFunction("pickup_nearby", async (_, _) =>
        {
            var pos = a.GetPlayerPosition(); var m = a.GetCurrentMap(); if (m is null) return false;
            foreach (var d in m.GetDropsInRange(pos, 8))
            {
                try { await a.PickupItemAsync(d.Id).ConfigureAwait(false); return true; } catch { }
            }
            return false;
        });

        e.RegisterFunction("use_hp_potion", async (_, _) =>
        {
            var inv = p.Inventory; if (inv is null) return false;
            foreach (var (g, n) in new[] { (14, 3), (14, 2), (14, 1) })
            {
                var po = inv.Items.FirstOrDefault(i => i.Definition?.Group == g && i.Definition?.Number == n && i.Durability > 0);
                if (po is not null) { await a.ConsumeItemAsync(po.ItemSlot).ConfigureAwait(false); return true; }
            }
            return false;
        });

        e.RegisterFunction("return_to_safezone", async (_, _) =>
        { await p.WarpToSafezoneAsync().ConfigureAwait(false); return true; });
    }

    static void RegisterSurvivalFunctions(LuaScriptEngine e, AiPlayer p, IGameAdapter a)
    {
        e.RegisterFunction("auto_potion", async (args, _) =>
        {
            var hpPct = (float)a.GetCurrentHp() / Math.Max(1, a.GetMaxHp());
            var th = args.Length > 0 && float.TryParse(args[0], out var h) ? h / 100f : 0.4f;
            if (hpPct < th) await ExecuteUseHpPotion(p, a).ConfigureAwait(false);
            return true;
        });

        e.RegisterFunction("auto_stat", async (_, _) =>
        {
            var pts = p.SelectedCharacter?.LevelUpPoints ?? 0;
            if (pts > 0)
                p.Logger.LogInformation("[Lua] auto_stat: {Pts} points available (heartbeat handles allocation)", pts);
            return true;
        });
    }

    static void RegisterConditionFunctions(LuaScriptEngine e, AiPlayer p, IGameAdapter a)
    {
        e.RegisterFunction("has_target", (_, _) =>
        {
            var pos = a.GetPlayerPosition(); var m = a.GetCurrentMap();
            return Task.FromResult(m?.GetAttackablesInRange(pos, 15).Any(x => x.IsAlive) ?? false);
        });

        e.RegisterFunction("monsters_nearby", (args, _) =>
        {
            var th = args.Length > 0 && int.TryParse(args[0], out var t) ? t : 1;
            var pos = a.GetPlayerPosition(); var m = a.GetCurrentMap();
            return Task.FromResult((m?.GetAttackablesInRange(pos, 15).Count(x => x.IsAlive) ?? 0) >= th);
        });

        e.RegisterFunction("is_dead", (_, _) => Task.FromResult(a.GetCurrentHp() <= 0));
        e.RegisterFunction("hp_below", (args, _) =>
        {
            var th = args.Length > 0 && float.TryParse(args[0], out var v) ? v / 100f : 0.5f;
            var hp = a.GetCurrentHp(); var mh = a.GetMaxHp();
            return Task.FromResult(mh > 0 && (float)hp / mh < th);
        });
        e.RegisterFunction("has_stat_points", (_, _) => Task.FromResult((p.SelectedCharacter?.LevelUpPoints ?? 0) > 0));
        e.RegisterFunction("is_walking", (_, _) => Task.FromResult(p.IsWalking));
        e.RegisterFunction("not_buffed", (_, _) =>
        {
            var effects = p.MagicEffectList?.VisibleEffects;
            if (effects is null || effects.Count == 0) return Task.FromResult(true);
            return Task.FromResult(!effects.Any(e => e.Definition.Number >= 3 && e.Definition.Number <= 6));
        });
    }

    static async Task ExecuteUseHpPotion(AiPlayer p, IGameAdapter a)
    {
        var inv = p.Inventory; if (inv is null) return;
        foreach (var (g, n) in new[] { (14, 3), (14, 2), (14, 1) })
        {
            var po = inv.Items.FirstOrDefault(i => i.Definition?.Group == g && i.Definition?.Number == n && i.Durability > 0);
            if (po is not null) { await a.ConsumeItemAsync(po.ItemSlot).ConfigureAwait(false); return; }
        }
    }
}
