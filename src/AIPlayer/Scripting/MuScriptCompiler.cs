// <copyright file="MuScriptCompiler.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Scripting;

using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// 中文 MU 脚本编译器 — DSL ↔ JSON 双向转换。
///
/// DSL 语法（仿按键精灵）：
///   @段落名              # 段落标签
///   如果 条件 → 动作      # 条件分支
///   无条件 → 动作         # 无条件分支
///   跳转 @段落名          # goto 跳转
///   // 注释               # 整行注释
///   动作名                # 简短形式（无条件执行）
///
/// 条件/动作名支持中英日对照，内部统一走英文关键字。
/// </summary>
public static class MuScriptCompiler
{
    // ========== 中文 ↔ 英文条件名映射 ==========
    private static readonly Dictionary<string, string> CnToEnCondition = new()
    {
        ["无条件"] = "always",
        ["HP低于阈值"] = "hp_below_threshold",
        ["MP低于阈值"] = "mp_below_threshold",
        ["血量低"] = "hp_below_threshold",
        ["蓝量低"] = "mp_below_threshold",
        ["死亡"] = "is_dead",
        ["背包满"] = "inventory_full",
        ["耐久低"] = "equip_durable_low",
        ["目标在攻击范围"] = "has_target_in_range",
        ["目标在远处"] = "has_target_not_in_range",
        ["目标远"] = "has_target_not_in_range",
        ["无目标"] = "no_target",
        ["在安全区"] = "in_safe_zone",
        ["刚复活"] = "just_revived",
        ["长时间无怪"] = "no_monsters_recently",
        ["有活跃任务"] = "has_active_quest",
        ["任务条件满足"] = "quest_conditions_met",
        ["无活跃任务"] = "no_active_quest",
        ["可接任务"] = "can_accept_quest",
        ["不在狩猎点"] = "not_at_hotspot",
        ["有目标"] = "has_target",
        ["变量条件"] = "variable",
        ["临时执行"] = "temp",
    };

    private static readonly Dictionary<string, string> EnToCnCondition;
    private static readonly Dictionary<string, string> CnToEnAction = new()
    {
        ["喝红药"] = "use_hp_potion",
        ["喝蓝药"] = "use_mp_potion",
        ["坐下恢复"] = "sit_regen",
        ["等待复活"] = "wait_respawn",
        ["攻击目标"] = "attack_target",
        ["接近目标"] = "approach_target",
        ["搜索怪物"] = "find_nearest_monster",
        ["随机巡逻"] = "random_patrol",
        ["换点到热点"] = "relocate_to_hotspot",
        ["返回死亡点"] = "return_to_death_spot",
        ["回城补给"] = "return_and_sell",
        ["拾取物品"] = "pickup_nearby",
        ["离开安全区"] = "leave_safezone",
        ["走向目标"] = "walk_to_target",
        ["沿路线走"] = "walk_route",
        ["加Buff"] = "use_buff",
        ["施放技能"] = "use_skill",
        ["传送地图"] = "travel_to_map",
        ["传送"] = "warp_to_map",
        ["接任务"] = "accept_quest",
        ["交任务"] = "submit_quest",
        ["完成任务"] = "complete_quest",
        ["走向任务NPC"] = "walk_to_quest_npc",
        ["客户端操作"] = "perform_client_action",
    };

    private static readonly Dictionary<string, string> EnToCnAction;

    // ========== 节点名 → 中文注释映射 ==========
    /// <summary>节点名 → 中文注释映射表（公开，供行号匹配使用）。</summary>
    public static readonly Dictionary<string, string> NodeNameToCn = new(StringComparer.OrdinalIgnoreCase)
    {
        // 核心检查
        ["death_check"] = "检测是否死亡",
        ["hp_check"] = "检测血量",
        ["mp_check"] = "检测蓝量",
        ["inventory_check"] = "检测背包",
        ["equip_durable_check"] = "检测装备耐久",
        ["just_revived_check"] = "检测是否刚复活",
        ["safe_zone_check"] = "检测安全区",
        ["safezone_check"] = "检测安全区",
        ["leave_safezone"] = "离开安全区",
        ["in_safe_zone"] = "检测安全区",
        ["enter_hunt"] = "进入狩猎段",
        ["enter_hunt_after_warp"] = "传送后进入狩猎",
        ["back_to_hunt"] = "回到狩猎段",
        ["return_to_hunt"] = "返回狩猎段",
        ["return_to_start"] = "返回起始段",
        ["return_to_death_spot"] = "返回死亡点",

        // 战斗相关
        ["attack_current"] = "攻击当前目标",
        ["approach_current"] = "接近当前目标",
        ["approach_target"] = "接近目标",
        ["attack_target"] = "攻击目标",
        ["attack_in_range"] = "攻击范围内目标",
        ["find_target"] = "搜索目标",
        ["find_target_and_patrol"] = "搜索目标或巡逻",
        ["chase_target"] = "追击目标",
        ["combat_priority"] = "战斗优先级",
        ["patrol"] = "随机巡逻",
        ["idle_patrol"] = "空闲巡逻",
        ["random_walk"] = "随机行走",
        ["no_monsters_relocate"] = "无怪换点",
        ["relocate_action"] = "执行换点",
        ["pickup_items"] = "拾取物品",
        ["drop_when_full"] = "背包满丢弃",

        // 生存
        ["hp_recover"] = "血量恢复",
        ["wait_respawn"] = "等待复活",
        ["post_revive_hp"] = "复活后补血",
        ["buff_reapply"] = "重新加Buff",
        ["refill_potions"] = "补充药水",

        // 补给
        ["return_sell"] = "回城卖物",
        ["return_when_full"] = "背包满回城",
        ["inventory_full"] = "背包满检测",
        ["repair_check"] = "检测修理",
        ["restock"] = "补给",  // paragraph name but used as node sometimes

        // 任务相关
        ["submit_ready"] = "检测任务可提交",
        ["accept_if_none"] = "无任务则接取",
        ["has_quest_go_hunt"] = "有任务则狩猎",
        ["try_accept"] = "尝试接任务",
        ["try_submit"] = "尝试交任务",
        ["check_accepted"] = "确认任务已接",
        ["check_done"] = "确认任务完成",
        ["quest_check"] = "检测任务状态",
        ["walk_to_npc"] = "走向NPC",
        ["fallback"] = "回退处理",
        ["go_spider_hotspot"] = "前往蜘蛛热点",

        // 寻路
        ["ensure_noria"] = "确保在诺丽亚",
        ["warp_noria"] = "传送诺丽亚",
        ["warp_to_map"] = "传送地图",
        ["travel_to_map"] = "旅行到地图",
        ["not_on_target_map"] = "不在目标地图",
        ["on_target_map_fallback"] = "已在目标地图回退",
        ["follow_leader_check"] = "检测是否跟随队长",

        // 变量/演示
        ["count_attack"] = "计数攻击",
        ["count_approach"] = "计数接近",
        ["count_find_target"] = "计数搜索",
        ["increment_count"] = "增加计数",
        ["patrol_after_5"] = "攻击5次后巡逻",
        ["patrol_walk"] = "巡逻行走",

        // v2 换点
        ["relocate_to_hotspot"] = "换点到热点",

        // 默认兜底
        ["main"] = "主段落",
        ["start"] = "起始段落",
    };

    static MuScriptCompiler()
    {
        EnToCnCondition = new Dictionary<string, string>();
        foreach (var (cn, en) in CnToEnCondition)
        {
            EnToCnCondition.TryAdd(en, cn);
        }

        EnToCnAction = new Dictionary<string, string>();
        foreach (var (cn, en) in CnToEnAction)
        {
            EnToCnAction.TryAdd(en, cn);
        }
    }

    // ========== JSON → DSL 编译 ==========

    /// <summary>
    /// 将 BehaviorScript 编译为中文 DSL 源码。
    /// </summary>
    public static string CompileToDsl(BehaviorScript script)
    {
        var sb = new StringBuilder();

        // 文件头
        sb.AppendLine($"// {script.Name} v{script.Version}");
        sb.AppendLine();

        if (script.Paragraphs is { Count: > 0 } paragraphs)
        {
            foreach (var para in paragraphs)
            {
                sb.AppendLine($"@{para.Label}");
                foreach (var node in para.Nodes)
                {
                    AppendNode(sb, node, "  ");
                }

                sb.AppendLine();
            }
        }
        else if (script.PriorityChain is { Count: > 0 } chain)
        {
            sb.AppendLine("@main");
            foreach (var node in chain)
            {
                AppendNode(sb, node, "  ");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>节点名 → 中文翻译。</summary>
    public static string TranslateNodeName(string nodeName)
    {
        if (string.IsNullOrEmpty(nodeName)) return string.Empty;
        return NodeNameToCn.TryGetValue(nodeName, out var cn) ? cn : nodeName;
    }

    private static void AppendNode(StringBuilder sb, PriorityNode node, string indent)
    {
        var act = FormatActionCn(node.Action);
        var suffix = string.IsNullOrEmpty(node.Name) ? string.Empty : $"  # {TranslateNodeName(node.Name)}";

        if (node.Condition == "always" || node.Condition == "无条件")
        {
            sb.AppendLine($"{indent}无条件 → {act}{suffix}");
        }
        else
        {
            var cond = EnToCnCondition.TryGetValue(node.Condition, out var cn) ? cn : node.Condition;
            sb.AppendLine($"{indent}如果 {cond} → {act}{suffix}");
        }

        // ELIF
        if (node.ElifNodes is { Count: > 0 } elifs)
        {
            foreach (var elif in elifs)
            {
                var ec = EnToCnCondition.TryGetValue(elif.Condition, out var ecn) ? ecn : elif.Condition;
                var ea = FormatActionCn(elif.Action);
                var esuffix = string.IsNullOrEmpty(elif.Name) ? string.Empty : $"  # {TranslateNodeName(elif.Name)}";
                sb.AppendLine($"{indent}或者如果 {ec} → {ea}{esuffix}");
            }
        }

        // ELSE
        if (node.ElseNode is { } elseNode)
        {
            var ea2 = FormatActionCn(elseNode.Action);
            var esuffix2 = string.IsNullOrEmpty(elseNode.Name) ? string.Empty : $"  # {TranslateNodeName(elseNode.Name)}";
            sb.AppendLine($"{indent}否则 → {ea2}{esuffix2}");
        }
    }

    private static string FormatActionCn(string action)
    {
        if (action.StartsWith("goto @", StringComparison.OrdinalIgnoreCase))
        {
            return $"跳转 @{action.AsSpan(6).Trim().ToString()}";
        }

        return EnToCnAction.TryGetValue(action, out var cn) ? cn : action;
    }

    // ========== DSL → JSON 编译 ==========

    /// <summary>
    /// 将中文 DSL 源码解析为 BehaviorScript。
    /// 返回 null 表示解析失败。
    /// </summary>
    public static BehaviorScript? CompileFromDsl(string dslSource, string? scriptId = null)
    {
        if (string.IsNullOrWhiteSpace(dslSource))
        {
            return null;
        }

        var lines = dslSource.Split('\n', StringSplitOptions.None);
        var script = new BehaviorScript
        {
            Id = scriptId ?? "custom",
            Name = "自定义脚本",
            Version = "1.0.0",
            Paragraphs = new List<ScriptParagraph>(),
        };

        List<PriorityNode>? currentNodes = null;

        for (var lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var raw = lines[lineIdx];
            var line = raw.TrimEnd('\r').TrimEnd();

            // 跳过空行和 // 注释
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//"))
            {
                continue;
            }

            var trimmed = line.TrimStart();

            // @段落名
            if (trimmed.StartsWith('@'))
            {
                var label = trimmed[1..].Trim();
                if (string.IsNullOrWhiteSpace(label)) continue;

                var para = new ScriptParagraph { Label = label, Nodes = new List<PriorityNode>() };
                script.Paragraphs.Add(para);
                currentNodes = para.Nodes;
                continue;
            }

            if (currentNodes is null)
            {
                // 还没遇到 @ 段落标签就先忽略
                continue;
            }

            // 解析节点行
            var node = ParseNodeLine(trimmed);
            if (node is not null)
            {
                currentNodes.Add(node);
            }
        }

        return script.Paragraphs.Count > 0 ? script : null;
    }

    private static PriorityNode? ParseNodeLine(string line)
    {
        // 提取行尾注释 # name
        string? nodeName = null;
        var hashIdx = line.LastIndexOf('#');
        if (hashIdx > 0)
        {
            nodeName = line[(hashIdx + 1)..].Trim();
            line = line[..hashIdx].TrimEnd();
        }

        // 尝试匹配: 如果 条件 → 动作
        var ifIdx = line.IndexOf("如果", StringComparison.Ordinal);
        if (ifIdx >= 0)
        {
            var rest = line[(ifIdx + 2)..].TrimStart();
            var arrowIdx = FindArrow(rest);
            if (arrowIdx >= 0)
            {
                var conditionCn = rest[..arrowIdx].Trim();
                var actionPart = rest[(arrowIdx + 1)..].Trim(); // after →

                // 跳转 @xxx 特殊处理
                var action = FormatActionEn(actionPart);
                var condition = CnToEnCondition.TryGetValue(conditionCn, out var enCond) ? enCond : conditionCn;

                return new PriorityNode
                {
                    Name = nodeName ?? condition,
                    Condition = condition,
                    Action = action,
                };
            }
        }

        // 匹配: 或者如果 条件 → 动作
        var elifIdx = line.IndexOf("或者如果", StringComparison.Ordinal);
        if (elifIdx >= 0)
        {
            var rest = line[(elifIdx + 4)..].TrimStart();
            var arrowIdx = FindArrow(rest);
            if (arrowIdx >= 0)
            {
                var conditionCn = rest[..arrowIdx].Trim();
                var actionPart = rest[(arrowIdx + 1)..].Trim();
                var action = FormatActionEn(actionPart);
                var condition = CnToEnCondition.TryGetValue(conditionCn, out var enCond) ? enCond : conditionCn;

                return new PriorityNode
                {
                    Name = nodeName ?? $"elif_{conditionCn}",
                    Condition = condition,
                    Action = action,
                };
            }
        }

        // 匹配: 否则 → 动作
        var elseIdx = line.IndexOf("否则", StringComparison.Ordinal);
        if (elseIdx >= 0)
        {
            var rest = line[(elseIdx + 2)..].TrimStart();
            var arrowIdx = FindArrow(rest);
            if (arrowIdx >= 0)
            {
                var actionPart = rest[(arrowIdx + 1)..].Trim();
                var action = FormatActionEn(actionPart);
                return new PriorityNode
                {
                    Name = nodeName ?? "else",
                    Condition = "always",
                    Action = action,
                };
            }
        }

        // 匹配: 无条件 → 动作
        var unconditionalIdx = line.IndexOf("无条件", StringComparison.Ordinal);
        if (unconditionalIdx >= 0)
        {
            var rest = line[(unconditionalIdx + 3)..].TrimStart();
            var arrowIdx = FindArrow(rest);
            if (arrowIdx >= 0)
            {
                var actionPart = rest[(arrowIdx + 1)..].Trim();
                var action = FormatActionEn(actionPart);
                return new PriorityNode
                {
                    Name = nodeName ?? "无条件",
                    Condition = "always",
                    Action = action,
                };
            }

            // 无条件直接跟动作名（无箭头）
            if (rest.Length > 0)
            {
                var action = FormatActionEn(rest);
                return new PriorityNode
                {
                    Name = nodeName ?? "无条件",
                    Condition = "always",
                    Action = action,
                };
            }
        }

        // 兜底：纯动作名行（如 "拾取物品"）
        var directAction = FormatActionEn(line);
        if (directAction != line || !string.IsNullOrWhiteSpace(line))
        {
            return new PriorityNode
            {
                Name = nodeName ?? line,
                Condition = "always",
                Action = directAction,
            };
        }

        return null;
    }

    /// <summary>找 → 或 -> 分隔符的位置。</summary>
    private static int FindArrow(string s)
    {
        var idx = s.IndexOf('→');
        if (idx >= 0) return idx;
        idx = s.IndexOf("->", StringComparison.Ordinal);
        return idx;
    }

    private static string FormatActionEn(string action)
    {
        // 跳转 @label
        if (action.StartsWith("跳转 @", StringComparison.Ordinal) || action.StartsWith("跳转@", StringComparison.Ordinal))
        {
            var label = action.Replace("跳转 @", string.Empty).Replace("跳转@", string.Empty).Trim();
            return $"goto @{label}";
        }

        return CnToEnAction.TryGetValue(action, out var en) ? en : action;
    }
}
