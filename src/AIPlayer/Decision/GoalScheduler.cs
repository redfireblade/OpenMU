// <copyright file="GoalScheduler.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// 长周期目标规划器。
/// 定义 AI 的长周期目标体系，生成"目标任务"注入 BoardState。
///
/// 目标类型:
///   LevelGoal — 今天升到 X 级
///   ItemGoal — 获取某件装备(如"穿上一套+7龙王")
///   QuestGoal — 完成主线/转职任务链
///   GoldGoal — 存够 X 金币
///
/// 每个目标有: Id, 描述, 优先级, 依赖条件, 完成条件, 进度
/// 目标存储在 Dictionary&lt;string, AiGoal&gt;
///
/// 每日激活逻辑:
///   登录时/每60秒: 检查所有目标 → 未完成的优先级高的 → 生成 MissionItem 到 BoardState
///   目标完成逻辑: 检测触发条件(如等级达到、捡到装备、金币达标)
///
/// 持久化:
///   目标定义(含 lambda)不可序列化，只持久化进度数据：
///   - 已跳过的 GoalId (连续失败)
///   - 已完成标记的 GoalId (来自 IsCompleted 检测)
///   数据保存在 aiplayer_data/goals_{characterId}.json
/// </summary>
public sealed class GoalScheduler
{
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly ILogger _logger;
    private readonly Dictionary<string, AiGoal> _goals = new();
    private readonly HashSet<string> _skippedGoalIds = new();
    private readonly HashSet<string> _completedGoalIds = new();
    private DateTime _lastEvaluation = DateTime.MinValue;
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets the read-only view of current goals for external inspection.
    /// </summary>
    public IReadOnlyDictionary<string, AiGoal> Goals => this._goals;

    /// <summary>
    /// Initializes a new instance of the <see cref="GoalScheduler"/> class.
    /// </summary>
    public GoalScheduler(AiPlayer player, IGameAdapter adapter, ILogger logger)
    {
        this._player = player;
        this._adapter = adapter;
        this._logger = logger;
        this.Load();
    }

    // ===================== 持久化 =====================

    /// <summary>
    /// 获取持久化存储路径: aiplayer_data/goals_{characterId}.json
    /// 使用角色名（短ID）防止跨角色干扰。
    /// </summary>
    private string GetSavePath()
    {
        var characterId = this._player.SelectedCharacter?.Id.ToString("N")[..8] ?? "default";
        return Path.Combine("aiplayer_data", $"goals_{characterId}.json");
    }

    /// <summary>
    /// 从磁盘加载之前保存的进度数据（跳过/完成标记）。
    /// 不覆盖 _goals Dictionary（lambda 条件由代码定义）。
    /// </summary>
    private void Load()
    {
        try
        {
            var path = this.GetSavePath();
            if (!File.Exists(path))
            {
                return;
            }

            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<GoalProgressData>(json);
            if (data is null)
            {
                return;
            }

            this._skippedGoalIds.Clear();
            foreach (var id in data.SkippedGoalIds)
            {
                this._skippedGoalIds.Add(id);
            }

            this._completedGoalIds.Clear();
            foreach (var id in data.CompletedGoalIds)
            {
                this._completedGoalIds.Add(id);
            }

            this._logger.LogInformation(
                "[GoalScheduler] 已加载持久化数据: skipped={Skipped}, completed={Completed}",
                this._skippedGoalIds.Count,
                this._completedGoalIds.Count);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning("[GoalScheduler] 读取持久化数据失败: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// 将跳过的/完成的 GoalId 保存到磁盘。
    /// 每次标记变更后自动调用。
    /// </summary>
    private void Save()
    {
        try
        {
            var path = this.GetSavePath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var data = new GoalProgressData
            {
                SkippedGoalIds = this._skippedGoalIds.ToList(),
                CompletedGoalIds = this._completedGoalIds.ToList(),
                LastSavedAt = DateTime.UtcNow,
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning("[GoalScheduler] 写入持久化数据失败: {Msg}", ex.Message);
        }
    }

    /// <summary>
    /// 登录时或等级变化时初始化默认目标链。
    /// 按角色当前等级决定"当前阶段"和"下一阶段"，
    /// 每个阶段生成3-5个目标，已不可达的目标自动标记完成。
    /// </summary>
    public void InitializeDefaultGoals()
    {
        this._goals.Clear();
        var level = this._adapter.GetPlayerLevel();

        // 低等级目标 — 阶梯升级（看板驱动的狩猎目标）
        if (level < 10) this._goals["level_10"] = new AiGoal { Id = "level_10", Title = "升到10级", Category = GoalCategory.Level, Priority = 5, IsCompleted = p => p.Level >= 10, IsUnlocked = _ => true, IsActive = true };
        if (level < 20) this._goals["level_20"] = new AiGoal { Id = "level_20", Title = "升到20级", Category = GoalCategory.Level, Priority = 6, IsCompleted = p => p.Level >= 20, IsUnlocked = _ => level >= 8, IsActive = level >= 8 };
        if (level >= 10 && level < 30) this._goals["level_30"] = new AiGoal { Id = "level_30", Title = "升到30级", Category = GoalCategory.Level, Priority = 7, IsCompleted = p => p.Level >= 30, IsUnlocked = _ => level >= 10, IsActive = level >= 10 };
        if (level >= 20) this._goals["level_50"] = new AiGoal { Id = "level_50", Title = "升到50级", Category = GoalCategory.Level, Priority = 8, IsCompleted = p => p.Level >= 50, IsUnlocked = _ => level >= 20, IsActive = level >= 20 };

        // 阶段 1: 1-50 → 转职1 + 基础装备
        this.AddGoalIfRelevant(level, 1, 50, new AiGoal
        {
            Id = "quest_class_1",
            Title = "完成一转",
            Category = GoalCategory.Quest,
            Priority = 10,
            IsCompleted = p => p.Level >= 50 || (p.SelectedCharacter?.CharacterClass?.Number > 0),
            IsUnlocked = _ => true,
        });

        this.AddGoalIfRelevant(level, 1, 80, new AiGoal
        {
            Id = "item_basic_set",
            Title = "换一套+4装备",
            Category = GoalCategory.Item,
            Priority = 20,
            IsCompleted = _ => level >= 80,
            IsUnlocked = _ => true,
        });

        // 阶段 2: 50-150 → 转职2 + 武器+7
        this.AddGoalIfRelevant(level, 50, 150, new AiGoal
        {
            Id = "level_150",
            Title = "升到150级",
            Category = GoalCategory.Level,
            Priority = 10,
            IsCompleted = p => p.Level >= 150,
            IsUnlocked = _ => true,
        });

        this.AddGoalIfRelevant(level, 50, 150, new AiGoal
        {
            Id = "quest_class_2",
            Title = "完成二转",
            Category = GoalCategory.Quest,
            Priority = 10,
            IsCompleted = p => p.Level >= 150,
            IsUnlocked = p => p.Level >= 50,
        });

        this.AddGoalIfRelevant(level, 80, 200, new AiGoal
        {
            Id = "item_weapon_7",
            Title = "武器+7",
            Category = GoalCategory.Craft,
            Priority = 20,
            IsCompleted = _ => false,
            IsUnlocked = p => p.Level >= 80 && level >= 80,
        });

        // 阶段 3: 150-220 → 卓越套 + 1级翅膀
        this.AddGoalIfRelevant(level, 150, 300, new AiGoal
        {
            Id = "item_excellent_set",
            Title = "凑一套卓越装备",
            Category = GoalCategory.Item,
            Priority = 20,
            IsCompleted = _ => false,
            IsUnlocked = p => p.Level >= 150,
        });

        this.AddGoalIfRelevant(level, 150, 300, new AiGoal
        {
            Id = "item_wing_1",
            Title = "凑一套1级翅膀",
            Category = GoalCategory.Item,
            Priority = 25,
            IsCompleted = _ => false,
            IsUnlocked = p => p.Level >= 150,
        });

        // 阶段 4: 220-300 → 转职3 + 武器+9
        this.AddGoalIfRelevant(level, 220, 400, new AiGoal
        {
            Id = "quest_class_3",
            Title = "完成三转",
            Category = GoalCategory.Quest,
            Priority = 10,
            IsCompleted = p => p.Level >= 220,
            IsUnlocked = p => p.Level >= 220,
        });

        this.AddGoalIfRelevant(level, 220, 400, new AiGoal
        {
            Id = "item_weapon_9_armor_7",
            Title = "武器+9 防具+7",
            Category = GoalCategory.Craft,
            Priority = 20,
            IsCompleted = _ => false,
            IsUnlocked = p => p.Level >= 220,
        });

        // 阶段 5: 300-400 → 2级翅膀
        this.AddGoalIfRelevant(level, 300, 600, new AiGoal
        {
            Id = "item_wing_2",
            Title = "凑一套2级翅膀",
            Category = GoalCategory.Item,
            Priority = 25,
            IsCompleted = _ => false,
            IsUnlocked = p => p.Level >= 300,
        });

        // 阶段 6: 300+ → 攒钱
        this.AddGoalIfRelevant(level, 300, int.MaxValue, new AiGoal
        {
            Id = "gold_10m",
            Title = "攒1000万金币",
            Category = GoalCategory.Gold,
            Priority = 30,
            IsCompleted = p => p.Money >= 10_000_000,
            IsUnlocked = p => p.Level >= 300,
        });

        // 标记已可达但已完成的 Goal
        this.RefreshCompletionStatus();

        this._logger.LogInformation(
            "[GoalScheduler] 初始化完成: level={Level}, totalGoals={Count}, active={Active}",
            level,
            this._goals.Count,
            this._goals.Values.Count(g => g.IsActive && !g.IsCompleted(this._player)));
    }

    /// <summary>
    /// 评估所有目标，生成新的任务注入给 DynamicMissionGenerator。
    /// 每 EvaluationInterval (60秒) 评估一次。
    /// 只返回未完成的活跃目标的最新任务。
    /// </summary>
    public List<MissionItem> EvaluateGoals(int currentLevel)
    {
        var now = DateTime.UtcNow;
        if (now - this._lastEvaluation < EvaluationInterval)
        {
            return new List<MissionItem>(0);
        }

        this._lastEvaluation = now;
        this.RefreshCompletionStatus();

        var result = new List<MissionItem>();
        var activeGoals = this._goals.Values
            .Where(g => g.IsActive
                && !this._skippedGoalIds.Contains(g.Id)
                && !g.IsCompleted(this._player))
            .OrderBy(g => g.Priority)
            .ToList();

        foreach (var goal in activeGoals)
        {
            var mission = this.GoalToMissionItem(goal);
            if (mission is not null)
            {
                result.Add(mission);
            }
        }

        if (result.Count > 0)
        {
            this._logger.LogDebug(
                "[GoalScheduler] EvaluateGoals: 生成了 {Count} 个目标任务 (当前等级 {Level})",
                result.Count,
                currentLevel);
        }

        return result;
    }

    /// <summary>
    /// 添加自定义目标（供 AIODS/外部系统用）。
    /// </summary>
    public void AddGoal(AiGoal goal)
    {
        if (string.IsNullOrEmpty(goal.Id))
        {
            this._logger.LogWarning("[GoalScheduler] 添加目标失败: Id 为空");
            return;
        }

        this._goals[goal.Id] = goal;
        this._logger.LogInformation("[GoalScheduler] + 添加自定义目标: {Id}「{Title}」优先级={P}", goal.Id, goal.Title, goal.Priority);
    }

    /// <summary>
    /// 标记指定目标为跳过（连续失败后不再重新生成）。
    /// </summary>
    public void MarkGoalFailed(string goalId)
    {
        if (string.IsNullOrEmpty(goalId))
            return;

        // 提取原始 goalId: "goal_quest_class_2" → "quest_class_2"
        var rawId = goalId.StartsWith("goal_") ? goalId[5..] : goalId;
        this._skippedGoalIds.Add(rawId);
        this.Save();
        this._logger.LogWarning("[GoalScheduler] 目标 {Id} 已标记为跳过（连续失败）", rawId);
    }

    /// <summary>
    /// 移除指定目标。
    /// </summary>
    public bool RemoveGoal(string goalId)
    {
        if (this._goals.Remove(goalId))
        {
            this._logger.LogInformation("[GoalScheduler] - 移除目标: {Id}", goalId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 强制重评估（供外部事件触发，如升级）。
    /// </summary>
    public void ForceReevaluation()
    {
        this._lastEvaluation = DateTime.MinValue;
    }

    private void AddGoalIfRelevant(int currentLevel, int minLevel, int maxLevel, AiGoal goal)
    {
        // 已超过目标区间的上限 → 忽略（不添加到 goals 字典）
        if (currentLevel > maxLevel && maxLevel < int.MaxValue)
        {
            return;
        }

        // 尚未进入目标区间 → 暂不激活
        if (currentLevel < minLevel)
        {
            goal.IsActive = false;
            this._goals[goal.Id] = goal;
            return;
        }

        goal.IsActive = true;
        this._goals[goal.Id] = goal;
    }

    private void RefreshCompletionStatus()
    {
        foreach (var goal in this._goals.Values)
        {
            if (!goal.IsActive || this._completedGoalIds.Contains(goal.Id))
            {
                continue;
            }

            if (goal.IsCompleted(this._player))
            {
                this._completedGoalIds.Add(goal.Id);
                this.Save();
                this._logger.LogInformation("[GoalScheduler] ✅ 目标已完成: {Id}「{Title}」", goal.Id, goal.Title);
                continue;
            }

            goal.ProgressPercent = this.CalculateProgress(goal);
        }
    }

    private int CalculateProgress(AiGoal goal)
    {
        var currentLevel = this._adapter.GetPlayerLevel();
        return goal.Category switch
        {
            GoalCategory.Level => goal.Id switch
            {
                "level_150" => Math.Clamp(currentLevel * 100 / 150, 0, 100),
                _ => currentLevel >= 150 ? 100 : Math.Clamp(currentLevel * 100 / 150, 0, 100),
            },
            GoalCategory.Gold => Math.Clamp((int)(this._player.Money * 100L / 10_000_000), 0, 100),
            _ => 50, // 默认显示一半进度（无法精确判断时）
        };
    }

    private MissionItem? GoalToMissionItem(AiGoal goal)
    {
        // 只从活跃且未完成的 Goal 生成任务
        if (!goal.IsActive || goal.IsCompleted(this._player))
        {
            return null;
        }

        var missionType = goal.Category switch
        {
            GoalCategory.Level => MissionType.Survival,
            GoalCategory.Item => MissionType.ItemFarm,
            GoalCategory.Quest => MissionType.Survival,
            GoalCategory.Gold => MissionType.ItemFarm,
            GoalCategory.Craft => MissionType.ItemFarm,
            _ => MissionType.Survival,
        };

        var module = goal.Category switch
        {
            GoalCategory.Quest => "survival",
            GoalCategory.Level => "survival",
            GoalCategory.Item => "item_farm",
            GoalCategory.Gold => "item_farm",
            GoalCategory.Craft => "item_farm",
            _ => "survival",
        };

        var category = goal.Category switch
        {
            GoalCategory.Quest => QuestCategory.MainStory,
            GoalCategory.Level => QuestCategory.Survival,
            GoalCategory.Item => QuestCategory.AiCustom,
            GoalCategory.Gold => QuestCategory.AiCustom,
            GoalCategory.Craft => QuestCategory.AiCustom,
            _ => QuestCategory.AiCustom,
        };

        return new MissionItem
        {
            Id = $"goal_{goal.Id}",
            Title = $"[目标] {goal.Title}",
            Priority = goal.Priority,
            Type = missionType,
            Module = module,
            Category = category,
            Source = QuestSource.AiCustom,
            Status = MissionStatus.Pending,
            FailureRetryable = true,
            MaxRepeatCount = -1,
        };
    }

    /// <summary>
    /// 等级断点配置 — 每阶段的目标区间。
    /// </summary>
    private static readonly List<LevelRange> GoalLevelRanges = new()
    {
        new LevelRange { Min = 1, Max = 50 },
        new LevelRange { Min = 50, Max = 150 },
        new LevelRange { Min = 150, Max = 220 },
        new LevelRange { Min = 220, Max = 300 },
        new LevelRange { Min = 300, Max = 400 },
        new LevelRange { Min = 400, Max = int.MaxValue },
    };

    /// <summary>
    /// 等级范围定义。
    /// </summary>
    private sealed class LevelRange
    {
        public int Min { get; set; }
        public int Max { get; set; }
    }
}

/// <summary>
/// AI 长周期目标定义。
/// </summary>
public sealed class AiGoal
{
    /// <summary>目标唯一标识 (如 "level_150", "item_dragon_set")。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>目标显示标题 (如 "升到150级")。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>目标类别。</summary>
    public GoalCategory Category { get; init; }

    /// <summary>
    /// 优先级（数值越小越优先）。
    /// 对应 MissionItem.Priority 直接映射。
    /// </summary>
    public int Priority { get; init; }

    /// <summary>完成条件委托 — 返回 true 表示目标已完成。</summary>
    public Func<AiPlayer, bool> IsCompleted { get; init; } = _ => false;

    /// <summary>解锁条件委托 — 返回 true 表示目标已解锁可用。</summary>
    public Func<AiPlayer, bool> IsUnlocked { get; init; } = _ => true;

    /// <summary>进度百分比 (0~100)，由 GoalScheduler 每轮评估更新。</summary>
    public int ProgressPercent { get; set; }

    /// <summary>目标是否激活（在等级区间内且未完成）。</summary>
    public bool IsActive { get; set; }
}

/// <summary>
/// 目标类别。
/// </summary>
public enum GoalCategory
{
    /// <summary>等级目标 — 如"升到150级"。</summary>
    Level,

    /// <summary>装备目标 — 如"穿上一套+7龙王"。</summary>
    Item,

    /// <summary>任务目标 — 如"完成一转/二转"。</summary>
    Quest,

    /// <summary>金币目标 — 如"存够1000万"。</summary>
    Gold,

    /// <summary>合成/强化目标 — 如"武器+7"。</summary>
    Craft,
}

/// <summary>
/// GoalScheduler 的持久化数据结构。
/// 只序列化可持久化的进度标记。
/// </summary>
public sealed class GoalProgressData
{
    /// <summary>连续失败被跳过的 GoalId。</summary>
    public List<string> SkippedGoalIds { get; init; } = new();

    /// <summary>已完成的目标 Id。</summary>
    public List<string> CompletedGoalIds { get; init; } = new();

    /// <summary>上次保存时间。</summary>
    public DateTime LastSavedAt { get; init; }
}
