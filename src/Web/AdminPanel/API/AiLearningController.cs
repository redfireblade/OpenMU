// <copyright file="AiLearningController.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.API;

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AIPlayer;

/// <summary>
/// API controller that exposes AI learning system data for external query.
/// 学习系统数据查询接口 — 可查询地图热点、NPC位置、狩猎建议等。
/// </summary>
[Route("api/ai/learn")]
[ApiController]
public class AiLearningController : ControllerBase
{
    private readonly AiPlayerManager _manager;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiLearningController"/> class.
    /// </summary>
    public AiLearningController(IAiDebugService debugService, ILogger<AiLearningController> logger)
    {
        this._manager = (AiPlayerManager)debugService;
        this._logger = logger;
    }

    /// <summary>
    /// 获取所有玩家行为观察记录（PBO 汇总数据）。
    /// GET /api/ai/learn/behavior-records
    /// </summary>
    [HttpGet("behavior-records")]
    public Microsoft.AspNetCore.Mvc.IActionResult GetBehaviorRecords()
    {
        var result = new List<object>();

        foreach (var kvp in this._manager.AllBehaviorRecords)
        {
            var record = kvp.Value;
            result.Add(new
            {
                playerName = kvp.Key,
                level = record.LastSeenLevel,
                attackDistance = new
                {
                    average = record.AverageAttackDistance,
                    histogram = record.AttackDistanceHistogram.OrderBy(h => h.Key).ToDictionary(h => $"{h.Key}格", h => h.Value),
                },
                skillUsage = record.SkillUsage.OrderByDescending(s => s.Value).Take(10).ToDictionary(s => $"#{s.Key}", s => s.Value),
                hpPotionThreshold = record.InferredHpPotionThreshold,
                mpPotionThreshold = record.InferredMpPotionThreshold,
                attackedMonsters = record.AttackedMonsters.OrderByDescending(m => m.Value).Take(10).ToDictionary(m => $"#{m.Key}", m => m.Value),
                equipmentChanges = record.EquipmentChanges.Count,
                totalGoldPicked = record.TotalGoldPicked,
                totalGoldSpent = record.TotalGoldSpent,
                averageWalkSpeed = record.AverageWalkSpeed,
                questCompletions = record.QuestCompletions.ToDictionary(q => $"G{q.Key}", q => q.Value),
            });
        }

        return this.Ok(new { totalPlayers = result.Count, records = result });
    }

    /// <summary>
    /// 获取指定玩家的NPC交互记录。
    /// GET /api/ai/learn/npc-interactions?playerName=FREASH
    /// </summary>
    [HttpGet("npc-interactions")]
    public Microsoft.AspNetCore.Mvc.IActionResult GetNpcInteractions([FromQuery] string playerName)
    {
        var interactions = this._manager.GetNpcInteractions(playerName);
        return this.Ok(new
        {
            playerName,
            total = interactions.Count,
            interactions = interactions.Select(n => new
            {
                npcName = n.NpcName,
                npcNumber = n.NpcNumber,
                mapNumber = n.MapNumber,
                x = n.X,
                y = n.Y,
                action = n.DialogAction,
                level = n.PlayerLevel,
                time = n.Timestamp,
            }),
        });
    }

    /// <summary>
    /// 获取集体经验记忆（全地图打怪/死亡/金币统计）。
    /// GET /api/ai/learn/collective-memory
    /// </summary>
    [HttpGet("collective-memory")]
    public Microsoft.AspNetCore.Mvc.IActionResult GetCollectiveMemory()
    {
        var expMem = this._manager.CollectiveExpMem;
        if (expMem is null)
            return this.Ok(new { maps = new object[] { } });

        var maps = expMem.MapStats.Select(map => new
        {
            mapNumber = map.Key,
            totalKills = map.Value.TotalKills,
            totalExperience = map.Value.TotalExperience,
            totalDeaths = map.Value.TotalDeaths,
            totalGoldPicked = map.Value.TotalGoldPicked,
            visitCount = map.Value.VisitCount,
            monsters = map.Value.MonsterStats.Select(m => new
            {
                monsterId = m.Key,
                killCount = m.Value.KillCount,
                avgExp = m.Value.AverageExpPerKill,
                monsterLevel = m.Value.MonsterLevel,
                deaths = m.Value.PlayerDeaths,
                drops = m.Value.TotalDrops,
            }),
            dangerLevel = map.Value.TotalKills > 0
                ? (double)map.Value.TotalDeaths / map.Value.TotalKills * 100
                : 0,
        });

        return this.Ok(new
        {
            characterName = expMem.CharacterName,
            lastUpdated = expMem.LastUpdated,
            totalMaps = expMem.MapStats.Count,
            maps,
        });
    }

    /// <summary>
    /// 获取群体影子地图（热点区域/危险区域/任务知识/掉落统计）。
    /// GET /api/ai/learn/global-shadowmap
    /// </summary>
    [HttpGet("global-shadowmap")]
    public Microsoft.AspNetCore.Mvc.IActionResult GetGlobalShadowMap()
    {
        var gsm = this._manager.GlobalShadowMap;
        if (gsm is null)
            return this.Ok(new { hotspots = new object[] { }, dangerZones = new object[] { }, questKnowledge = new object[] { }, dropStatistics = new object[] { } });

        return this.Ok(new
        {
            lastUpdated = gsm.LastUpdated,
            hotspots = gsm.Hotspots.OrderByDescending(h => h.Score).Take(50).Select(h => new
            {
                mapNumber = h.MapNumber,
                x = h.X,
                y = h.Y,
                score = h.Score,
                description = h.Description,
            }),
            dangerZones = gsm.DangerZones.OrderByDescending(d => d.DeathCount).Take(50).Select(d => new
            {
                mapNumber = d.MapNumber,
                x = d.X,
                y = d.Y,
                deathCount = d.DeathCount,
                monsterNumber = d.MonsterNumber,
                monsterLevel = d.MonsterLevel,
            }),
            questKnowledge = gsm.QuestKnowledge.Select(q => new
            {
                questGroup = q.QuestGroup,
                questNumber = q.QuestNumber,
                totalAttempts = q.TotalAttempts,
                totalDeaths = q.TotalDeaths,
                recommendedLevel = q.RecommendedLevel,
            }),
            dropStatistics = gsm.DropStatistics.Take(50).Select(d => new
            {
                monsterNumber = d.MonsterNumber,
                mapNumber = d.MapNumber,
                itemGroup = d.ItemGroup,
                itemNumber = d.ItemNumber,
                observedCount = d.ObservedCount,
            }),
        });
    }

    /// <summary>
    /// 获取狩猎推荐（基于集体经验，适合当前等级的地图和怪物）。
    /// GET /api/ai/learn/hunting-recommendation?playerLevel=30&topN=3
    /// </summary>
    [HttpGet("hunting-recommendation")]
    public Microsoft.AspNetCore.Mvc.IActionResult GetHuntingRecommendation([FromQuery] int playerLevel = 30, [FromQuery] int topN = 3)
    {
        var expMem = this._manager.CollectiveExpMem;
        if (expMem is null)
            return this.Ok(new { maps = new object[] { } });

        // 从集体经验推荐地图
        var rankedMaps = expMem.GetRankedMaps(playerLevel, topN);
        var maps = rankedMaps.Select(m => new
        {
            mapNumber = m.MapNumber,
            score = m.Score,
        });

        return this.Ok(new
        {
            playerLevel,
            maps,
        });
    }

    /// <summary>
    /// 导出PBO行为数据摘要（简洁版）。
    /// GET /api/ai/learn/behavior-export
    /// </summary>
    [HttpGet("behavior-export")]
    public Microsoft.AspNetCore.Mvc.IActionResult GetBehaviorExport()
    {
        var exports = this._manager.ExportBehaviorRecords();
        return this.Ok(new
        {
            totalExports = exports.Count,
            exports = exports.Select(e => new
            {
                characterName = e.CharacterName,
                level = e.Level,
                hpPotionThreshold = e.InferredHpPotionThreshold,
                mpPotionThreshold = e.InferredMpPotionThreshold,
                avgAttackDistance = e.AverageAttackDistance,
                totalGoldPicked = e.TotalGoldPicked,
                questCompletionCount = e.QuestCompletions.Count,
                npcInteractionCount = e.NpcInteractions.Count,
                huntingSpotCount = e.HuntingSpots.Count,
            }),
        });
    }

    /// <summary>
    /// 获取指定玩家的离散行为事件列表。
    /// GET /api/ai/learn/behavior-events?playerName=FREASH&minLevel=1&maxLevel=999
    /// </summary>
    [HttpGet("behavior-events")]
    public Microsoft.AspNetCore.Mvc.IActionResult GetBehaviorEvents(
        [FromQuery] string playerName,
        [FromQuery] int minLevel = 1,
        [FromQuery] int maxLevel = 999)
    {
        var events = this._manager.GetBehaviorEvents(playerName);
        if (events.Count == 0)
            return this.Ok(new { playerName, total = 0, events = new object[] { } });

        var filtered = events
            .Where(e => e.Level >= minLevel && e.Level <= maxLevel)
            .OrderBy(e => e.Timestamp)
            .Select(e => new
            {
                type = e.EventType.ToString(),
                level = e.Level,
                mapNumber = e.MapNumber,
                mapName = e.MapName,
                x = e.X,
                y = e.Y,
                npcName = e.NpcName,
                npcNumber = e.NpcNumber,
                dialogChoice = e.DialogChoice,
                itemName = e.ItemName,
                itemGroup = e.ItemGroup,
                itemNumber = e.ItemNumber,
                skillName = e.SkillName,
                skillNumber = e.SkillNumber,
                monsterName = e.MonsterName,
                monsterNumber = e.MonsterNumber,
                monsterLevel = e.MonsterLevel,
                strengthAfter = e.StrengthAfter,
                agilityAfter = e.AgilityAfter,
                vitalityAfter = e.VitalityAfter,
                energyAfter = e.EnergyAfter,
                quantity = e.Quantity,
                summary = e.ToString(),
                time = e.Timestamp,
            })
            .ToList();

        return this.Ok(new
        {
            playerName,
            total = filtered.Count,
            maxLevel = filtered.Count > 0 ? filtered.Max(e => e.level) : 0,
            events = filtered,
        });
    }

    /// <summary>
    /// 获取所有角色的行为事件统计摘要。
    /// GET /api/ai/learn/behavior-stats
    /// </summary>
    [HttpGet("behavior-stats")]
    public Microsoft.AspNetCore.Mvc.IActionResult GetBehaviorStats()
    {
        var stats = this._manager.GetAllBehaviorStats();
        var levels = this._manager.GetBehaviorEventLevels();
        return this.Ok(new
        {
            totalCharacters = stats.Count,
            characters = stats.Select(s => new
            {
                characterName = s.Key,
                summary = s.Value,
                maxLevel = levels.TryGetValue(s.Key, out var lv) ? lv : 0,
            }),
        });
    }

    /// <summary>
    /// 获取指定玩家的结构化升级计划。
    /// GET /api/ai/learn/behavior-plan?playerName=FREASH
    /// </summary>
    [HttpGet("behavior-plan")]
    public Microsoft.AspNetCore.Mvc.IActionResult GetBehaviorPlan([FromQuery] string playerName)
    {
        var plan = this._manager.GetBehaviorPlan(playerName);
        return this.Ok(new
        {
            characterName = plan.CharacterName,
            className = plan.ClassName,
            classNumber = plan.CharacterClass,
            maxObservedLevel = plan.MaxObservedLevel,
            statAllocationStrategy = plan.StatAllocationStrategy,
            statPriorities = plan.StatPriorities.Select(s => new
            {
                statName = s.StatName,
                priority = s.Priority,
                targetValue = s.TargetValue,
                reason = s.Reason,
            }),
            generatedAt = plan.GeneratedAt,
            totalPhases = plan.Phases.Count,
            phases = plan.Phases.Select(p => new
            {
                name = p.Name,
                minLevel = p.MinLevel,
                maxLevel = p.MaxLevel,
                description = p.Description,
                huntingMapNumber = p.HuntingMapNumber,
                huntingMapName = p.HuntingMapName,
                steps = p.Steps.Select(s => new
                {
                    type = s.Type.ToString(),
                    description = s.Description,
                    npcName = s.NpcName,
                    npcNumber = s.NpcNumber,
                    dialogChoice = s.DialogChoice,
                    dialogChoiceDescription = s.DialogChoiceDescription,
                    itemName = s.ItemName,
                    quantity = s.Quantity,
                    skillName = s.SkillName,
                    mapName = s.MapName,
                    mapNumber = s.MapNumber,
                    targetX = s.TargetX,
                    targetY = s.TargetY,
                }),
                targetMonsters = p.TargetMonsters.Select(m => new
                {
                    monsterNumber = m.MonsterNumber,
                    monsterName = m.MonsterName,
                    monsterLevel = m.MonsterLevel,
                    killCount = m.KillCount,
                }),
            }),
        });
    }

    /// <summary>
    /// 注入模拟行为事件（测试用）— 当你没有真实玩家连接时，可以使用此API注入假数据验证流程。
    /// POST /api/ai/learn/inject-events
    /// Body: { "characterName": "FREASH", "characterClass": 0, "events": [...] }
    /// </summary>
    [HttpPost("inject-events")]
    public async Task<Microsoft.AspNetCore.Mvc.IActionResult> InjectEvents([FromBody] InjectEventsRequest request)
    {
        var logger = this._logger;
        if (string.IsNullOrEmpty(request.CharacterName))
            return this.BadRequest(new { error = "characterName is required" });

        if (request.Events is null || request.Events.Count == 0)
            return this.BadRequest(new { error = "events list is required" });

        var count = 0;
        foreach (var evt in request.Events)
        {
            // 解析事件类型（兼容字符串和数字格式）
            if (!Enum.TryParse<BehaviorEventType>(evt.Type, true, out var eventType))
            {
                logger?.LogWarning("[InjectEvents] 无法解析事件类型: {Type}", evt.Type);
                continue;
            }

            var behaviorEvent = new BehaviorEvent
            {
                EventType = eventType,
                CharacterName = request.CharacterName,
                CharacterClass = request.CharacterClass,
                Level = evt.Level,
                MapNumber = evt.MapNumber,
                MapName = evt.MapName,
                X = evt.X,
                Y = evt.Y,
                NpcName = evt.NpcName,
                NpcNumber = evt.NpcNumber,
                DialogChoice = evt.DialogChoice,
                DialogChoiceDescription = evt.DialogChoiceDescription,
                ItemName = evt.ItemName,
                ItemGroup = evt.ItemGroup,
                ItemNumber = evt.ItemNumber,
                Quantity = evt.Quantity,
                SkillNumber = evt.SkillNumber,
                SkillName = evt.SkillName,
                MonsterNumber = evt.MonsterNumber,
                MonsterName = evt.MonsterName,
                MonsterLevel = evt.MonsterLevel,
                StrengthBefore = evt.StrengthBefore,
                StrengthAfter = evt.StrengthAfter,
                AgilityBefore = evt.AgilityBefore,
                AgilityAfter = evt.AgilityAfter,
                VitalityBefore = evt.VitalityBefore,
                VitalityAfter = evt.VitalityAfter,
                EnergyBefore = evt.EnergyBefore,
                EnergyAfter = evt.EnergyAfter,
                Summary = evt.Summary,
            };
            this._manager.InjectBehaviorEvent(behaviorEvent);
            count++;
        }

        // Save immediately
        this._manager.SaveBehaviorEvents();

        // Generate plan from injected events
        var plan = this._manager.GetBehaviorPlan(request.CharacterName);

        return this.Ok(new
        {
            injected = count,
            characterName = request.CharacterName,
            plan,
        });
    }
}

/// <summary>
/// 注入事件请求体。
/// </summary>
public sealed class InjectEventsRequest
{
    public string CharacterName { get; set; } = string.Empty;
    public int CharacterClass { get; set; }
    public List<InjectEventItem> Events { get; set; } = new();
}

/// <summary>
/// 单个注入事件。
/// </summary>
public sealed class InjectEventItem
{
    /// <summary>事件类型（字符串形式，如 "NpcTalk", "MonsterKilled"）。</summary>
    public string Type { get; set; } = "MonsterKilled";
    public int Level { get; set; }
    public int MapNumber { get; set; }
    public string? MapName { get; set; }
    public byte X { get; set; }
    public byte Y { get; set; }
    public string? NpcName { get; set; }
    public short? NpcNumber { get; set; }
    public int? DialogChoice { get; set; }
    public string? DialogChoiceDescription { get; set; }
    public string? ItemName { get; set; }
    public int? ItemGroup { get; set; }
    public int? ItemNumber { get; set; }
    public int? Quantity { get; set; }
    public ushort? SkillNumber { get; set; }
    public string? SkillName { get; set; }
    public short? MonsterNumber { get; set; }
    public string? MonsterName { get; set; }
    public short? MonsterLevel { get; set; }
    public short? StrengthBefore { get; set; }
    public short? StrengthAfter { get; set; }
    public short? AgilityBefore { get; set; }
    public short? AgilityAfter { get; set; }
    public short? VitalityBefore { get; set; }
    public short? VitalityAfter { get; set; }
    public short? EnergyBefore { get; set; }
    public short? EnergyAfter { get; set; }
    public string? Summary { get; set; }
}
