// <copyright file="SwarmEvolution.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace OAPS.Evolution;

using System;
using System.Collections.Generic;
using System.Linq;
using MUnique.OpenMU.AIPlayer.Scripting;

/// <summary>
/// 脚本运行统计 — 记录单个脚本在一轮评估周期中的运行表现。
/// 由外部系统（AI Player 主机）在每轮评估结束后填充并传入 <see cref="SwarmEvolution.EvolveGeneration"/>。
/// </summary>
public record BotStats
{
    /// <summary>脚本标识（对应 <see cref="BehaviorScript.Id"/>）。</summary>
    public string ScriptId { get; init; } = string.Empty;

    /// <summary>当前世代编号。</summary>
    public int Generation { get; init; }

    /// <summary>运行时长。</summary>
    public TimeSpan TimeSpent { get; init; }

    /// <summary>获得的总经验值。</summary>
    public long ExpGained { get; init; }

    /// <summary>死亡次数。</summary>
    public int Deaths { get; init; }

    /// <summary>回城卷轴使用次数。</summary>
    public int ReturnScrollUsed { get; init; }

    /// <summary>获得的金币总量。</summary>
    public int GoldEarned { get; init; }

    /// <summary>拾取物品数。</summary>
    public int ItemsPicked { get; init; }

    /// <summary>完成任务数。</summary>
    public int QuestsCompleted { get; init; }
}

/// <summary>
/// 适应度评分 — 单个脚本的量化评估结果。
/// 由 <see cref="SwarmEvolution.CalculateFitness"/> 计算产生。
/// </summary>
public record FitnessResult
{
    /// <summary>脚本标识。</summary>
    public string ScriptId { get; init; } = string.Empty;

    /// <summary>综合适应度得分。</summary>
    public float Fitness { get; init; }

    /// <summary>经验效率（每分钟获得经验值）。</summary>
    public float ExpEfficiency { get; init; }

    /// <summary>生存率（0~1，1 表示完全无死亡）。</summary>
    public float SurvivalRate { get; init; }

    /// <summary>死亡惩罚扣分（折合适应度值）。</summary>
    public int DeathPenalty { get; init; }
}

/// <summary>
/// 进化结果 — 一代进化的完整输出。
/// 由 <see cref="SwarmEvolution.EvolveGeneration"/> 返回。
/// </summary>
public record EvolutionResult
{
    /// <summary>进化后的新世代编号。</summary>
    public int Generation { get; init; }

    /// <summary>群体规模。</summary>
    public int PopulationSize { get; init; }

    /// <summary>本代最佳适应度。</summary>
    public float BestFitness { get; init; }

    /// <summary>本代平均适应度。</summary>
    public float AverageFitness { get; init; }

    /// <summary>本代最佳脚本 ID。</summary>
    public string? BestScriptId { get; init; }

    /// <summary>下一代脚本列表。</summary>
    public List<GeneratedScript> NextGeneration { get; init; } = new();
}

/// <summary>
/// 群体进化引擎 — 从 <see cref="GeneratedScript"/> 群体中通过遗传算法迭代优化脚本质量。
///
/// 每代流程：
/// <list type="number">
///   <item>评估 — 基于 <see cref="BotStats"/> 计算每个脚本的适应度</item>
///   <item>选择 — 按适应度降序选取精英脚本（eliteRate 比例）</item>
///   <item>交叉 — 从精英库中随机选取父代，合并节点生成子代</item>
///   <item>变异 — 以 mutationRate 概率对子代做随机参数调整</item>
/// </list>
///
/// 算法参数可通过构造函数调整：
/// <list type="bullet">
///   <item><paramref name="populationSize"/> — 群体规模（默认 20）</item>
///   <item><paramref name="eliteRate"/> — 精英选择比例（默认 0.2，取前 20%）</item>
///   <item><paramref name="mutationRate"/> — 变异概率（默认 0.1，10% 的子代发生变异）</item>
/// </list>
///
/// 纯算法逻辑，无 I/O 依赖，线程安全。
/// </summary>
public sealed class SwarmEvolution
{
    private readonly int _populationSize;
    private readonly float _eliteRate;
    private readonly float _mutationRate;
    private readonly Random _random;

    /// <summary>当前世代编号。</summary>
    public int CurrentGeneration { get; private set; }

    /// <summary>所有世代中最佳适应度。</summary>
    public float BestFitnessEver { get; private set; }

    /// <summary>
    /// 初始化群体进化引擎。
    /// </summary>
    /// <param name="populationSize">群体规模。必须大于 0。默认 20。</param>
    /// <param name="eliteRate">精英选择比例。取值范围 (0, 1]。默认 0.2。</param>
    /// <param name="mutationRate">变异概率。取值范围 [0, 1]。默认 0.1。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 当 <paramref name="populationSize"/> ≤ 0，
    /// 或 <paramref name="eliteRate"/> 不在 (0, 1] 范围内，
    /// 或 <paramref name="mutationRate"/> 不在 [0, 1] 范围内时抛出。
    /// </exception>
    public SwarmEvolution(int populationSize = 20, float eliteRate = 0.2f, float mutationRate = 0.1f)
    {
        if (populationSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(populationSize), "Population size must be greater than 0.");
        }

        if (eliteRate <= 0f || eliteRate > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(eliteRate), "Elite rate must be in the range (0, 1].");
        }

        if (mutationRate is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(mutationRate), "Mutation rate must be in the range [0, 1].");
        }

        _populationSize = populationSize;
        _eliteRate = eliteRate;
        _mutationRate = mutationRate;
        _random = Random.Shared;
    }

    /// <summary>
    /// 计算单个脚本的适应度。
    ///
    /// 公式：
    /// <code>
    /// expEfficiency = ExpGained / Max(TimeSpent.TotalMinutes, 1)
    /// deathPenalty = Deaths * 500
    /// survivalRate = 1.0 - Min(1.0, Deaths / Max(TimeSpent.TotalHours, 0.1))
    /// fitness = (expEfficiency * 100) - deathPenalty + (ItemsPicked * 10) + (GoldEarned / 1000)
    /// </code>
    /// </summary>
    /// <param name="stats">脚本运行统计。</param>
    /// <returns>适应度评分结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stats"/> 为 null 时抛出。</exception>
    public FitnessResult CalculateFitness(BotStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);

        var minutes = Math.Max(stats.TimeSpent.TotalMinutes, 1.0);
        var hours = Math.Max(stats.TimeSpent.TotalHours, 0.1);

        var expEfficiency = (float)(stats.ExpGained / minutes);
        var deathPenalty = stats.Deaths * 500;
        var survivalRate = 1.0f - Math.Min(1.0f, (float)(stats.Deaths / hours));
        var itemsBonus = stats.ItemsPicked * 10;
        var goldBonus = stats.GoldEarned / 1000f;

        var fitness = (expEfficiency * 100f) - deathPenalty + itemsBonus + goldBonus;

        return new FitnessResult
        {
            ScriptId = stats.ScriptId,
            Fitness = fitness,
            ExpEfficiency = expEfficiency,
            SurvivalRate = Math.Clamp(survivalRate, 0f, 1f),
            DeathPenalty = deathPenalty,
        };
    }

    /// <summary>
    /// 计算群体适应度 — 对群体中每个脚本调用 <see cref="CalculateFitness"/>。
    /// </summary>
    /// <param name="population">群体统计列表。</param>
    /// <returns>每个脚本的适应度评分列表（顺序与输入一致）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="population"/> 为 null 时抛出。</exception>
    public List<FitnessResult> EvaluatePopulation(List<BotStats> population)
    {
        ArgumentNullException.ThrowIfNull(population);

        return population
            .Select(stats => this.CalculateFitness(stats))
            .ToList();
    }

    /// <summary>
    /// 执行一代进化：评估 → 选择 → 交叉 → 变异。
    /// 输入当前世代的脚本列表和对应的运行统计，输出下一代脚本。
    /// </summary>
    /// <param name="currentGen">当前世代脚本列表。</param>
    /// <param name="statsMap">脚本 ID 到运行统计的映射。仅包含有评估数据的脚本。</param>
    /// <returns>进化结果，包含下一代脚本和统计信息。</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="currentGen"/> 或 <paramref name="statsMap"/> 为 null 时抛出。
    /// </exception>
    public EvolutionResult EvolveGeneration(List<GeneratedScript> currentGen, Dictionary<string, BotStats> statsMap)
    {
        ArgumentNullException.ThrowIfNull(currentGen);
        ArgumentNullException.ThrowIfNull(statsMap);

        // ── 1. 评估 ──
        var fitnesses = new List<FitnessResult>(currentGen.Count);
        foreach (var script in currentGen)
        {
            if (statsMap.TryGetValue(script.Script.Id, out var stats))
            {
                fitnesses.Add(this.CalculateFitness(stats));
            }
        }

        // ── 2. 精英选择 ──
        var elites = this.SelectElites(currentGen, fitnesses);

        var nextGeneration = new List<GeneratedScript>(_populationSize);
        var bestFitness = float.MinValue;
        float totalFitness = 0f;

        // 记录适应度统计（包含所有参与评估的脚本）
        foreach (var f in fitnesses)
        {
            if (f.Fitness > bestFitness)
            {
                bestFitness = f.Fitness;
            }

            totalFitness += f.Fitness;
        }

        // 如果精英数为 0，直接返回空列表
        if (elites.Count == 0)
        {
            return new EvolutionResult
            {
                Generation = CurrentGeneration,
                PopulationSize = currentGen.Count,
                BestFitness = bestFitness,
                AverageFitness = fitnesses.Count > 0 ? totalFitness / fitnesses.Count : 0f,
                BestScriptId = null,
                NextGeneration = nextGeneration,
            };
        }

        var bestScriptId = fitnesses.Count > 0
            ? fitnesses.OrderByDescending(f => f.Fitness).First().ScriptId
            : null;

        // ── 3. 用精英库填充下一代 ──
        while (nextGeneration.Count < _populationSize)
        {
            // 随机选两个不同的父代（群体很小时允许相同）
            var parent1 = elites[_random.Next(elites.Count)];
            var parent2 = elites.Count > 1
                ? elites.Where((_, i) => i != elites.FindIndex(e => e == parent1))
                        .ElementAt(_random.Next(elites.Count - 1))
                : parent1;

            // 交叉
            var child = this.Crossover(parent1, parent2);

            // 变异
            child = this.Mutate(child);

            nextGeneration.Add(child);
        }

        // ── 4. 更新状态 ──
        CurrentGeneration++;
        if (bestFitness > BestFitnessEver)
        {
            BestFitnessEver = bestFitness;
        }

        return new EvolutionResult
        {
            Generation = CurrentGeneration,
            PopulationSize = nextGeneration.Count,
            BestFitness = bestFitness,
            AverageFitness = fitnesses.Count > 0 ? totalFitness / fitnesses.Count : 0f,
            BestScriptId = bestScriptId,
            NextGeneration = nextGeneration,
        };
    }

    /// <summary>
    /// 交叉两个父代脚本，生成子代。
    ///
    /// 策略：
    /// <list type="bullet">
    ///   <item>从 parent1 取前 50% 的 PriorityChain 节点</item>
    ///   <item>从 parent2 取后 50% 的 PriorityChain 节点（跳过已在结果中的节点，按名称去重）</item>
    ///   <item>合并成子代脚本的优先链</item>
    ///   <item>子代 ID = "gen_{generation}_swarm_{Guid.NewGuid():N}"</item>
    ///   <item>子代置信度 = parent1.Confidence * 0.5 + parent2.Confidence * 0.5</item>
    /// </list>
    /// </summary>
    /// <param name="parent1">父代脚本 1。</param>
    /// <param name="parent2">父代脚本 2。</param>
    /// <returns>子代脚本。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 null 时抛出。</exception>
    public GeneratedScript Crossover(GeneratedScript parent1, GeneratedScript parent2)
    {
        ArgumentNullException.ThrowIfNull(parent1);
        ArgumentNullException.ThrowIfNull(parent2);

        var p1Nodes = parent1.Script.PriorityChain;
        var p2Nodes = parent2.Script.PriorityChain;

        var halfP1 = (int)Math.Ceiling(p1Nodes.Count * 0.5);
        var halfP2Start = p2Nodes.Count / 2;

        // 从 parent1 取前 50%
        var childNodes = new List<PriorityNode>(p1Nodes.Count);
        for (int i = 0; i < halfP1 && i < p1Nodes.Count; i++)
        {
            childNodes.Add(this.CloneNode(p1Nodes[i]));
        }

        // 已使用的节点名称集合，用于去重
        var usedNames = new HashSet<string>(childNodes.Select(n => n.Name), StringComparer.Ordinal);

        // 从 parent2 取后 50%（跳过已在 child 中的）
        for (int i = halfP2Start; i < p2Nodes.Count; i++)
        {
            var node = p2Nodes[i];
            if (!usedNames.Contains(node.Name))
            {
                childNodes.Add(this.CloneNode(node));
                usedNames.Add(node.Name);
            }
        }

        // 置信度：两个父代的平均值
        var confidence = (parent1.Confidence * 0.5) + (parent2.Confidence * 0.5);

        // 生成子代脚本
        var childScript = new BehaviorScript
        {
            Id = $"gen_{CurrentGeneration}_swarm_{Guid.NewGuid():N}",
            Name = $"Swarm Gen{CurrentGeneration}",
            Version = parent1.Script.Version,
            Status = "draft",
            PriorityChain = childNodes,
            Parameters = this.CloneParameters(parent1.Script.Parameters),
        };

        return new GeneratedScript
        {
            Script = childScript,
            SourcePlayer = string.Empty,
            Confidence = confidence,
            GeneratedAt = DateTime.UtcNow,
            TotalEvents = 0,
        };
    }

    /// <summary>
    /// 变异脚本 — 以 <see cref="_mutationRate"/> 概率随机调整节点参数。
    ///
    /// 变异操作：
    /// <list type="bullet">
    ///   <item>随机选择一个 PriorityNode</item>
    ///   <item>以 50% 概率调整 Condition 相关阈值（如 HP_PCT 的 HpThreshold ±0.05）</item>
    ///   <item>以 50% 概率调整 Action 参数（如 searchRange ±2、patrolRadius ±5）</item>
    /// </list>
    ///
    /// 如果没有触发变异，返回原脚本的深度副本。
    /// </summary>
    /// <param name="script">待变异的脚本。</param>
    /// <returns>变异后的脚本副本（原脚本不受影响）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="script"/> 为 null 时抛出。</exception>
    public GeneratedScript Mutate(GeneratedScript script)
    {
        ArgumentNullException.ThrowIfNull(script);

        // 先创建副本
        var cloned = this.CloneGeneratedScript(script);
        var clonedScript = cloned.Script;

        // 以 mutationRate 概率触发变异
        if (_random.NextDouble() >= _mutationRate)
        {
            return cloned;
        }

        // 没有节点可供变异
        if (clonedScript.PriorityChain.Count == 0)
        {
            return cloned;
        }

        // 随机选择一个节点
        var targetIndex = _random.Next(clonedScript.PriorityChain.Count);
        var targetNode = clonedScript.PriorityChain[targetIndex];

        // 确保节点有参数对象
        targetNode.Parameters ??= new ScriptParameters();

        // 随机选择变异类型
        if (_random.NextDouble() < 0.5)
        {
            // 调整 Condition 相关阈值
            this.MutateConditionParameters(targetNode);
        }
        else
        {
            // 调整 Action 参数
            this.MutateActionParameters(targetNode);
        }

        return cloned;
    }

    /// <summary>
    /// 精英选择 — 取适应度最高的 topK 脚本。
    ///
    /// 策略：
    /// <list type="bullet">
    ///   <item>将 <paramref name="fitness"/> 映射到 <paramref name="scripts"/></item>
    ///   <item>按 Fitness 降序排列</item>
    ///   <item>取前 eliteRate 比例的脚本（至少 1 个）</item>
    /// </list>
    /// </summary>
    /// <param name="scripts">脚本列表。</param>
    /// <param name="fitness">对应每个脚本的适应度结果。</param>
    /// <returns>精英脚本列表（按适应度降序）。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 null 时抛出。</exception>
    /// <exception cref="ArgumentException">脚本和适应度列表长度不匹配时抛出。</exception>
    public List<GeneratedScript> SelectElites(List<GeneratedScript> scripts, List<FitnessResult> fitness)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(fitness);

        if (scripts.Count != fitness.Count)
        {
            throw new ArgumentException(
                $"Script count ({scripts.Count}) and fitness count ({fitness.Count}) must match.",
                nameof(fitness));
        }

        if (scripts.Count == 0 || fitness.Count == 0)
        {
            return new List<GeneratedScript>(0);
        }

        // 构建 (Script, Fitness) 对，按 Fitness 降序排列
        var scored = scripts
            .Zip(fitness, (script, fit) => (Script: script, Fitness: fit.Fitness))
            .OrderByDescending(pair => pair.Fitness)
            .ToList();

        // 精英数量：至少 1 个，至多全部
        var eliteCount = Math.Max(1, (int)(scored.Count * _eliteRate));

        return scored
            .Take(eliteCount)
            .Select(pair => pair.Script)
            .ToList();
    }

    /// <summary>
    /// 深度克隆 <see cref="PriorityNode"/> 及其子节点（ELIF/ELSE）。
    /// </summary>
    /// <param name="source">源节点。</param>
    /// <returns>克隆节点。</returns>
    private PriorityNode CloneNode(PriorityNode source)
    {
        return new PriorityNode
        {
            Name = source.Name,
            Condition = source.Condition,
            Action = source.Action,
            Parameters = source.Parameters is not null ? this.CloneParameters(source.Parameters) : null,
            ElifNodes = source.ElifNodes?.Select(this.CloneNode).ToList(),
            ElseNode = source.ElseNode is not null ? this.CloneNode(source.ElseNode) : null,
        };
    }

    /// <summary>
    /// 深度克隆 <see cref="ScriptParameters"/>。
    /// </summary>
    private ScriptParameters CloneParameters(ScriptParameters source)
    {
        return new ScriptParameters
        {
            HpThreshold = source.HpThreshold,
            MpThreshold = source.MpThreshold,
            MaxLevelDiff = source.MaxLevelDiff,
            PatrolRadius = source.PatrolRadius,
            AttackRange = source.AttackRange,
            PickupFilter = source.PickupFilter,
            ReturnWhenInventoryFull = source.ReturnWhenInventoryFull,
            SearchRange = source.SearchRange,
            PotionCooldownMs = source.PotionCooldownMs,
            BlacklistExpiryTicks = source.BlacklistExpiryTicks,
            StuckDetectionTicks = source.StuckDetectionTicks,
            VariableExpression = source.VariableExpression,
            VariableOp = source.VariableOp,
            PotionHysteresisMargin = source.PotionHysteresisMargin,
            DurabilityThreshold = source.DurabilityThreshold,
            SkillNumber = source.SkillNumber,
            PatrolCenterResetTicks = source.PatrolCenterResetTicks,
            PositionStuckThreshold = source.PositionStuckThreshold,
            PickupCooldownTicks = source.PickupCooldownTicks,
            NpcBlacklist = source.NpcBlacklist?.ToList(),
            RouteId = source.RouteId,
            Jitter = source.Jitter,
            PotionThreshold = source.PotionThreshold,
            BuyPotionCount = source.BuyPotionCount,
            QuestGroup = source.QuestGroup,
            QuestNumber = source.QuestNumber,
            QuestNpcNumber = source.QuestNpcNumber,
            TargetMapNumber = source.TargetMapNumber,
            ReturnCooldownSec = source.ReturnCooldownSec,
            Hotspots = source.Hotspots?.Select(h => new HotspotDef
            {
                X = h.X,
                Y = h.Y,
                MapNumber = h.MapNumber,
                Name = h.Name,
                X1 = h.X1,
                X2 = h.X2,
                Y1 = h.Y1,
                Y2 = h.Y2,
            }).ToList(),
            SkillPriority = source.SkillPriority?.ToList(),
            NoMonsterTicksLimit = source.NoMonsterTicksLimit,
            TargetRandomizationCount = source.TargetRandomizationCount,
            HotspotOccupiedRadius = source.HotspotOccupiedRadius,
            IdleSoftLimitTicks = source.IdleSoftLimitTicks,
            IdleHardLimitTicks = source.IdleHardLimitTicks,
            IdleRecoveryTicks = source.IdleRecoveryTicks,
            CraftTargetItemGroup = source.CraftTargetItemGroup,
            CraftTargetItemNumber = source.CraftTargetItemNumber,
        };
    }

    /// <summary>
    /// 深度克隆 <see cref="GeneratedScript"/> 及其包含的 <see cref="BehaviorScript"/>。
    /// </summary>
    private GeneratedScript CloneGeneratedScript(GeneratedScript source)
    {
        var clonedScript = new BehaviorScript
        {
            Id = source.Script.Id,
            Name = source.Script.Name,
            DslSource = source.Script.DslSource,
            Version = source.Script.Version,
            Status = source.Script.Status,
            PriorityChain = source.Script.PriorityChain.Select(this.CloneNode).ToList(),
            Parameters = this.CloneParameters(source.Script.Parameters),
            Paragraphs = source.Script.Paragraphs?.Select(p => new ScriptParagraph
            {
                Label = p.Label,
                Nodes = p.Nodes.Select(this.CloneNode).ToList(),
            }).ToList(),
            BranchHitCounts = new Dictionary<string, int>(source.Script.BranchHitCounts),
            OrchestratorConfig = source.Script.OrchestratorConfig is not null
                ? new OrchestratorConfig
                {
                    Mode = source.Script.OrchestratorConfig.Mode,
                    MaxConcurrency = source.Script.OrchestratorConfig.MaxConcurrency,
                    ErrorIsolation = source.Script.OrchestratorConfig.ErrorIsolation,
                }
                : null,
        };

        return new GeneratedScript
        {
            Script = clonedScript,
            SourcePlayer = source.SourcePlayer,
            Confidence = source.Confidence,
            GeneratedAt = source.GeneratedAt,
            TotalEvents = source.TotalEvents,
        };
    }

    /// <summary>
    /// 变异节点的 Condition 相关参数。
    /// 根据条件类型调整对应的阈值参数。
    /// </summary>
    private void MutateConditionParameters(PriorityNode node)
    {
        switch (node.Condition)
        {
            case "hp_below_threshold":
                node.Parameters!.HpThreshold = Math.Clamp(
                    node.Parameters.HpThreshold + (float)((_random.NextDouble() * 0.1) - 0.05),
                    0.05f,
                    0.95f);
                break;

            case "mp_below_threshold":
                node.Parameters!.MpThreshold = Math.Clamp(
                    node.Parameters.MpThreshold + (float)((_random.NextDouble() * 0.1) - 0.05),
                    0.05f,
                    0.95f);
                break;

            case "has_target_in_range":
            case "has_target_not_in_range":
                // 调整攻击范围 ±2
                node.Parameters!.AttackRange = Math.Max(
                    1.0f,
                    node.Parameters.AttackRange + (float)((_random.NextDouble() * 4.0) - 2.0));
                break;

            default:
                // 对于其他条件类型，随机翻转一个布尔参数或调整通用阈值
                if (_random.NextDouble() < 0.3)
                {
                    node.Parameters!.ReturnWhenInventoryFull = !node.Parameters.ReturnWhenInventoryFull;
                }

                break;
        }
    }

    /// <summary>
    /// 变异节点的 Action 相关参数。
    /// 根据动作类型调整对应的执行参数。
    /// </summary>
    private void MutateActionParameters(PriorityNode node)
    {
        switch (node.Action)
        {
            case "find_nearest_monster":
            case "attack_target":
            case "walk_to_target":
                // 调整搜索范围 ±2（至少 5）
                node.Parameters!.SearchRange = Math.Max(
                    5,
                    node.Parameters.SearchRange + _random.Next(-2, 3));
                break;

            case "random_patrol":
                // 调整巡逻半径 ±5（至少 5）
                node.Parameters!.PatrolRadius = Math.Max(
                    5,
                    node.Parameters.PatrolRadius + _random.Next(-5, 6));
                break;

            case "use_hp_potion":
                // 调整 HP 阈值 ±0.05
                node.Parameters!.HpThreshold = Math.Clamp(
                    node.Parameters.HpThreshold + (float)((_random.NextDouble() * 0.1) - 0.05),
                    0.05f,
                    0.95f);
                break;

            case "use_mp_potion":
                // 调整 MP 阈值 ±0.05
                node.Parameters!.MpThreshold = Math.Clamp(
                    node.Parameters.MpThreshold + (float)((_random.NextDouble() * 0.1) - 0.05),
                    0.05f,
                    0.95f);
                break;

            case "return_and_sell":
                // 调整回城冷却 ±10 秒（至少 10 秒）
                node.Parameters!.ReturnCooldownSec = Math.Max(
                    10,
                    node.Parameters.ReturnCooldownSec + _random.Next(-10, 11));
                break;

            default:
                // 对其他动作类型，随机调整 SearchRange 或 PatrolRadius
                if (_random.NextDouble() < 0.5)
                {
                    node.Parameters!.SearchRange = Math.Max(
                        5,
                        node.Parameters.SearchRange + _random.Next(-2, 3));
                }
                else
                {
                    node.Parameters!.PatrolRadius = Math.Max(
                        5,
                        node.Parameters.PatrolRadius + _random.Next(-5, 6));
                }

                break;
        }
    }
}
