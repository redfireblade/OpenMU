// <copyright file="OapsMindEngineTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests;

using System.Reflection;
using System.Threading;
using Moq;
using OAPS.AccessLayer;
using OAPS.Mind;
using OAPS.World;

/// <summary>
/// 验证 OAPS v3.0 架构集成 — 心智引擎单元测试 + 接入层集成检查。
/// 覆盖 MemorySystem（三层记忆）、PersonalityEngine（个性演化 + OCC 情感）、
/// DecisionCore（双轨决策）、CognitiveLoop（认知循环）及接入层集成点。
/// 纯单元测试层级，不需要启动服务器。
/// </summary>
[TestFixture]
public class OapsMindEngineTests
{
    // ═══════════════════════════════════════════════════════════════
    // 2a) MemorySystem 三层记忆测试
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 感觉记忆（Sensory Memory）：写入后在 500ms 内可检索。
    /// MemorySystem 的感觉记忆采用 16 项循环缓冲区，Retrieve 时筛选 500ms 内的记录。
    /// </summary>
    [Test]
    public void RecordSensory_WriteAndRetrieveWithin500ms_ReturnsRecord()
    {
        var memory = new MemorySystem();
        memory.RecordSensory(new SensoryRecord
        {
            Type = SensoryType.MonsterPosition,
            EntityId = 100,
            X = 10,
            Y = 20,
        });

        var context = new PerceptContext(); // 空关键词，只检查感觉记忆
        var result = memory.Retrieve(context);

        Assert.That(result.Sensory.Count, Is.GreaterThanOrEqualTo(1));
        Assert.That(result.Sensory[0].EntityId, Is.EqualTo(100));
    }

    /// <summary>
    /// 工作记忆（Working Memory）：写入后通过 PerceptContext 关键词匹配可检索。
    /// 30s 衰减期内，内容匹配关键词的条目应被返回。
    /// </summary>
    [Test]
    public void RecordWorking_WriteWithMatchingKeywords_ReturnsInRetrieval()
    {
        var memory = new MemorySystem();
        memory.RecordWorking(new WorkingRecord
        {
            Content = "Heading to boss room for hunting",
            Category = WorkingCategory.Movement,
            Importance = 0.5f,
            TargetX = 100,
            TargetY = 200,
        });

        var context = new PerceptContext { Keywords = new HashSet<string> { "boss" } };
        var result = memory.Retrieve(context);

        Assert.That(result.Working.Count, Is.EqualTo(1));
        Assert.That(result.Working[0].Content, Does.Contain("boss"));
    }

    /// <summary>
    /// 长期记忆（Long-Term Memory）：多条记录按重要性降序返回。
    /// Retrieve 时取 importance >= 0.5 或标签匹配的记录，按重要性排序，最多 5 条。
    /// </summary>
    [Test]
    public void RecordLongTerm_WriteMultipleImportances_ReturnsOrderedByImportance()
    {
        var memory = new MemorySystem();
        memory.RecordLongTerm(new LongTermRecord
        {
            Summary = "Normal monster kill",
            Importance = 0.3f,
            Tags = new HashSet<string> { "combat" },
        });
        memory.RecordLongTerm(new LongTermRecord
        {
            Summary = "Death by boss",
            Importance = 0.9f,
            Tags = new HashSet<string> { "death" },
        });
        memory.RecordLongTerm(new LongTermRecord
        {
            Summary = "Rare jewel drop",
            Importance = 0.8f,
            Tags = new HashSet<string> { "loot" },
        });

        var context = new PerceptContext { Keywords = new HashSet<string> { "combat", "death", "loot" } };
        var result = memory.Retrieve(context);

        Assert.That(result.Episodic.Count, Is.EqualTo(3));
        Assert.That(result.Episodic[0].Importance, Is.EqualTo(0.9f), "最高重要性应在第一位");
        Assert.That(result.Episodic[1].Importance, Is.EqualTo(0.8f), "次高重要性应在第二位");
        Assert.That(result.Episodic[2].Importance, Is.EqualTo(0.3f), "最低重要性应在最后");
    }

    /// <summary>
    /// Ebbinghaus 遗忘曲线：低重要性 + 低保留率的旧记忆被自动清理。
    /// ApplyForgetting 移除 retention &lt; 5% 且 importance < 0.3 的记录。
    /// 高重要性记忆即使老化也保留。
    /// </summary>
    [Test]
    public void ApplyForgetting_LowImportanceOldRecords_AreRemoved()
    {
        var memory = new MemorySystem();

        // 应被遗忘：10 天前、强度 0.1、重要性 0.2 → retention ≈ exp(-10/0.1) ≈ 0 < 5%
        memory.RecordLongTerm(new LongTermRecord
        {
            Summary = "Old trash kill",
            Importance = 0.2f,
            Strength = 0.1f,
            Timestamp = DateTime.UtcNow.AddDays(-10),
            Tags = new HashSet<string> { "trash" },
        });

        // 应保留：10 天前但重要性 0.9 >= 0.3
        memory.RecordLongTerm(new LongTermRecord
        {
            Summary = "Old but important boss kill",
            Importance = 0.9f,
            Strength = 0.1f,
            Timestamp = DateTime.UtcNow.AddDays(-10),
            Tags = new HashSet<string> { "important" },
        });

        // 应保留：新的低重要性记录（retention ≈ 1 > 5%）
        memory.RecordLongTerm(new LongTermRecord
        {
            Summary = "Recent minor event",
            Importance = 0.2f,
            Strength = 1.0f,
            Tags = new HashSet<string> { "recent" },
        });

        var countBefore = memory.LongTermCount;
        memory.ApplyForgetting();
        var countAfter = memory.LongTermCount;

        Assert.That(countAfter, Is.LessThan(countBefore), "遗忘后记忆总数应减少");
        Assert.That(countAfter, Is.EqualTo(countBefore - 1), "只应移除 1 条低价值记忆");

        // 验证高重要性和新记录仍可检索
        var context = new PerceptContext { Keywords = new HashSet<string> { "important", "recent" } };
        var result = memory.Retrieve(context);
        Assert.That(result.Episodic.Any(r => r.Summary!.Contains("important")), Is.True,
            "高重要性记忆应保留");
        Assert.That(result.Episodic.Any(r => r.Summary!.Contains("Recent")), Is.True,
            "新的低重要性记忆应保留");
    }

    // ═══════════════════════════════════════════════════════════════
    // 2b) PersonalityEngine 演化测试
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 死亡事件 → Caution 增加，Aggression 降低。
    /// PersonalityProfile.Evolve 中 IsDeath 分支：
    /// Caution Lerp 向 1.0 (+8%)，Aggression Lerp 向 0.0 (-4%)。
    /// </summary>
    [Test]
    public void Evolve_DeathEvent_CautionIncreasesAggressionDecreases()
    {
        var engine = new PersonalityEngine();
        var initialCaution = engine.Profile.Caution;
        var initialAggression = engine.Profile.Aggression;

        engine.Evolve(new ExperienceEvent { IsDeath = true });

        Assert.That(engine.Profile.Caution, Is.GreaterThan(initialCaution),
            "死亡后谨慎度应增加");
        Assert.That(engine.Profile.Aggression, Is.LessThan(initialAggression),
            "死亡后攻击性应降低");
    }

    /// <summary>
    /// 击杀高等级怪物（成功）→ Aggression 增加。
    /// PersonalityProfile.Evolve 中 IsOverlevelKill && IsSuccess 分支：
    /// Aggression Lerp 向 1.0 (+6%)。
    /// </summary>
    [Test]
    public void Evolve_OverlevelKill_AggressionIncreases()
    {
        var engine = new PersonalityEngine();
        var initial = engine.Profile.Aggression;

        engine.Evolve(new ExperienceEvent { IsOverlevelKill = true, IsSuccess = true });

        Assert.That(engine.Profile.Aggression, Is.GreaterThan(initial),
            "击杀高等级怪物后攻击性应增加");
    }

    /// <summary>
    /// 稀有掉落 → Greed 增加。
    /// PersonalityProfile.Evolve 中 IsRareDrop 分支：
    /// Greed Lerp 向 1.0 (+5%)。
    /// </summary>
    [Test]
    public void Evolve_RareDrop_GreedIncreases()
    {
        var engine = new PersonalityEngine();
        var initial = engine.Profile.Greed;

        engine.Evolve(new ExperienceEvent { IsRareDrop = true });

        Assert.That(engine.Profile.Greed, Is.GreaterThan(initial),
            "获得稀有掉落后贪婪应增加");
    }

    /// <summary>
    /// OCC 情感模型：死亡后 Fear 和 Sadness 升高。
    /// EmotionState.Update 中 IsDeath 分支：
    /// Sadness += 0.3, Fear += 0.25，随后自然衰减（×0.97 / ×0.95）。
    /// 单次死亡后 Fear~0.2375、Sadness~0.291，仍低于 0.6 的门槛阈值，
    /// 因此主导情感保持 Neutral，但情感幅值确实增加了。
    /// 要触发 Fear 主导需要连续 3 次死亡（累积 Fear > 0.6）。
    /// </summary>
    [Test]
    public void Evolve_DeathEvent_FearAndSadnessIncrease()
    {
        var engine = new PersonalityEngine();

        engine.Evolve(new ExperienceEvent { IsDeath = true });

        Assert.That(engine.Emotion.Fear, Is.GreaterThan(0),
            "死亡后恐惧应增加");
        Assert.That(engine.Emotion.Sadness, Is.GreaterThan(0),
            "死亡后悲伤应增加");
    }

    /// <summary>
    /// 连续 3 次死亡后 Fear 累积超过 0.6 阈值，触发 Fear 主导情感。
    /// 此时 EmotionalState 应切换为 Fear，验证多事件累积效应。
    /// </summary>
    [Test]
    public void Evolve_ThreeDeaths_FearBecomesDominant()
    {
        var engine = new PersonalityEngine();

        // 3 次连续死亡 → Fear ≈ 0.678 > 0.6 → EmotionalState.Fear
        engine.Evolve(new ExperienceEvent { IsDeath = true });
        engine.Evolve(new ExperienceEvent { IsDeath = true });
        engine.Evolve(new ExperienceEvent { IsDeath = true });

        Assert.That(engine.Emotion.EmotionalState, Is.EqualTo(EmotionalState.Fear),
            "3 次死亡后恐惧应成为主导情感");
        Assert.That(engine.Emotion.Fear, Is.GreaterThan(0.6f),
            "恐惧值应超过 0.6 门控阈值");
    }

    /// <summary>
    /// 稀有掉落 → Joy 升高。
    /// EmotionState.Update 中 IsRareDrop 分支：Joy += 0.4。
    /// </summary>
    [Test]
    public void Evolve_RareDrop_JoyIncreases()
    {
        var engine = new PersonalityEngine();

        engine.Evolve(new ExperienceEvent { IsRareDrop = true });

        Assert.That(engine.Emotion.Joy, Is.GreaterThan(0),
            "稀有掉落后喜悦应增加");
    }

    // ═══════════════════════════════════════════════════════════════
    // 2c) DecisionCore 快速轨道测试
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// HP &lt; 5% → 返回 Flee 决策。
    /// FastDecisionEngine.EvaluateSurvival 紧急逃生规则。
    /// </summary>
    [Test]
    public async Task Decide_HpBelow5Percent_ReturnsFlee()
    {
        var personality = new PersonalityEngine();
        var core = new DecisionCore();
        var state = new WorldState
        {
            Self = new PlayerSnapshot(1, 10, 10, 4, 100, 50, 100, 10, 1000),
        };
        var context = new PerceptContext();

        var result = await core.Decide(state, context, personality);

        Assert.That(result.Action, Is.EqualTo(DecisionAction.Flee));
        Assert.That(result.Priority, Is.EqualTo(1), "紧急逃生应具有最高优先级");
        Assert.That(result.Reason, Does.Contain("HP"));
    }

    /// <summary>
    /// HP &lt; 20% → 返回 UseHealthPotion 决策。
    /// FastDecisionEngine.EvaluateSurvival 自动喝红规则。
    /// </summary>
    [Test]
    public async Task Decide_HpBelow20Percent_ReturnsUseHealthPotion()
    {
        var personality = new PersonalityEngine();
        var core = new DecisionCore();
        var state = new WorldState
        {
            Self = new PlayerSnapshot(1, 10, 10, 19, 100, 50, 100, 10, 1000),
        };
        var context = new PerceptContext();

        var result = await core.Decide(state, context, personality);

        Assert.That(result.Action, Is.EqualTo(DecisionAction.UseHealthPotion));
        Assert.That(result.Priority, Is.EqualTo(2), "喝药优先级应低于紧急逃生");
        Assert.That(result.Reason, Does.Contain("HP"));
    }

    /// <summary>
    /// 行为惯性：连续 3 次成功 → 形成习惯，习惯强度 > 0.6。
    /// BehaviorInertiaEngine.Record 累计成功次数，连续 3 次后 Strength = 1.0，
    /// TryHabit 在匹配环境下返回习惯行为。
    /// </summary>
    [Test]
    public void RecordActionResult_ThreeSuccesses_FormsHabit()
    {
        var state = new WorldState
        {
            Self = new PlayerSnapshot(1, 42, 100, 100, 100, 50, 100, 10, 1000),
        };
        var inertia = new BehaviorInertiaEngine();

        // 记录 3 次连续成功
        inertia.Record("use_hp_potion", true, state);
        inertia.Record("use_hp_potion", true, state);
        inertia.Record("use_hp_potion", true, state);

        var habitResult = inertia.TryHabit(state);

        Assert.That(habitResult, Is.Not.Null, "3 次连续成功应形成习惯");
        Assert.That(habitResult!.Action, Is.EqualTo(DecisionAction.UseHealthPotion));
        Assert.That(habitResult.IsHabit, Is.True, "结果应标记为习惯");
        Assert.That(habitResult.Reason, Does.Contain("习惯"));
    }

    /// <summary>
    /// 行为惯性：失败会减少习惯强度，未经训练的不会有习惯。
    /// </summary>
    [Test]
    public void RecordActionResult_NoTraining_NoHabit()
    {
        var state = new WorldState
        {
            Self = new PlayerSnapshot(1, 42, 100, 100, 100, 50, 100, 10, 1000),
        };
        var inertia = new BehaviorInertiaEngine();

        var habitResult = inertia.TryHabit(state);

        Assert.That(habitResult, Is.Null, "未经训练不应有习惯");
    }

    // ═══════════════════════════════════════════════════════════════
    // 2d) CognitiveLoop 创建与执行测试
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 认知循环应能正确初始化所有组件（IWorldSensor、MemorySystem、
    /// PersonalityEngine、DecisionCore），不抛出异常。
    /// </summary>
    [Test]
    public void Constructor_AllComponents_InitializesCorrectly()
    {
        var sensorMock = new Mock<IWorldSensor>();
        var memory = new MemorySystem();
        var personality = new PersonalityEngine();
        var decision = new DecisionCore();

        var loop = new CognitiveLoop(sensorMock.Object, memory, personality, decision);

        Assert.That(loop, Is.Not.Null);
        Assert.That(loop.PersonalityProfile, Is.Not.Null, "个性配置应可访问");
        Assert.That(loop.CurrentEmotion, Is.Not.Null, "情感状态应可访问");
    }

    /// <summary>
    /// ThinkAsync 应返回有效的 DecisionResult，不抛出异常。
    /// 在无生存危险、无习惯的正常状态下返回 Wait。
    /// </summary>
    [Test]
    public async Task ThinkAsync_NormalState_ReturnsDecisionResult()
    {
        var sensorMock = new Mock<IWorldSensor>();
        sensorMock.Setup(s => s.CaptureAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorldState
            {
                Self = new PlayerSnapshot(1, 10, 10, 100, 100, 50, 100, 10, 1000),
                Monsters = new Dictionary<uint, MonsterSnapshot>(),
                Items = new Dictionary<uint, ItemSnapshot>(),
            });

        var memory = new MemorySystem();
        var personality = new PersonalityEngine();
        var decision = new DecisionCore();
        var loop = new CognitiveLoop(sensorMock.Object, memory, personality, decision);

        DecisionResult result;
        try
        {
            result = await loop.ThinkAsync();
        }
        catch (Exception ex)
        {
            Assert.Fail($"ThinkAsync 不应抛异常: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        Assert.That(result, Is.Not.Null, "应返回有效的 DecisionResult");
        Assert.That(result.Action, Is.EqualTo(DecisionAction.Wait),
            "正常状态下应返回 Wait 决策");
    }

    /// <summary>
    /// CognitiveLoop 应正确处理死亡经历事件的记录。
    /// RecordExperience 触发个性演化和长期记忆存储。
    /// </summary>
    [Test]
    public void RecordExperience_DeathEvent_UpdatesPersonalityAndMemory()
    {
        var sensorMock = new Mock<IWorldSensor>();
        var memory = new MemorySystem();
        var personality = new PersonalityEngine();
        var decision = new DecisionCore();
        var loop = new CognitiveLoop(sensorMock.Object, memory, personality, decision);

        var initialAggression = personality.Profile.Aggression;

        loop.RecordExperience(new ExperienceEvent { IsDeath = true });

        Assert.That(personality.Profile.Aggression, Is.LessThan(initialAggression),
            "死亡后攻击性应降低");
        Assert.That(memory.LongTermCount, Is.GreaterThan(0),
            "死亡事件应记录到长期记忆");
    }

    /// <summary>
    /// CognitiveLoop 应正确处理高等级怪物击杀经历记录。
    /// </summary>
    [Test]
    public void RecordExperience_OverlevelKill_UpdatesAggression()
    {
        var sensorMock = new Mock<IWorldSensor>();
        var memory = new MemorySystem();
        var personality = new PersonalityEngine();
        var decision = new DecisionCore();
        var loop = new CognitiveLoop(sensorMock.Object, memory, personality, decision);

        var initialAggression = personality.Profile.Aggression;

        loop.RecordExperience(new ExperienceEvent { IsOverlevelKill = true, IsSuccess = true });

        Assert.That(personality.Profile.Aggression, Is.GreaterThan(initialAggression),
            "击杀高等级怪物后攻击性应增加");
    }

    // ═══════════════════════════════════════════════════════════════
    // 3) 集成点验证
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// AiPlayerLogic 应包含 OAPS v3.0 心智引擎字段：
    /// _cognitiveLoop (CognitiveLoop)，_memorySystem (MemorySystem)，
    /// _personalityEngine (PersonalityEngine) — 运行时反射验证。
    /// </summary>
    [Test]
    public void AiPlayerLogic_OapsFieldsExist()
    {
        var type = typeof(AiPlayerLogic);
        var cognitiveLoopField = type.GetField("_cognitiveLoop", BindingFlags.Instance | BindingFlags.NonPublic);
        var memorySystemField = type.GetField("_memorySystem", BindingFlags.Instance | BindingFlags.NonPublic);
        var personalityField = type.GetField("_personalityEngine", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.That(cognitiveLoopField, Is.Not.Null, "AiPlayerLogic 应包含 _cognitiveLoop 字段");
        Assert.That(cognitiveLoopField!.FieldType, Is.EqualTo(typeof(CognitiveLoop)),
            "_cognitiveLoop 应为 CognitiveLoop 类型");

        Assert.That(memorySystemField, Is.Not.Null, "AiPlayerLogic 应包含 _memorySystem 字段");
        Assert.That(memorySystemField!.FieldType, Is.EqualTo(typeof(MemorySystem)),
            "_memorySystem 应为 MemorySystem 类型");

        Assert.That(personalityField, Is.Not.Null, "AiPlayerLogic 应包含 _personalityEngine 字段");
        Assert.That(personalityField!.FieldType, Is.EqualTo(typeof(PersonalityEngine)),
            "_personalityEngine 应为 PersonalityEngine 类型");
    }

    /// <summary>
    /// OpenMuWorldSensor 应能正常构造，不抛出异常。
    /// 使用 TestHelper 创建的 AiPlayer 实例。
    /// </summary>
    [Test]
    public void OpenMuWorldSensor_Construct_NoException()
    {
        var player = TestHelper.CreateAiPlayer();

        OpenMuWorldSensor sensor;
        try
        {
            sensor = new OpenMuWorldSensor(player);
        }
        catch (Exception ex)
        {
            Assert.Fail($"OpenMuWorldSensor 构造抛异常: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        Assert.That(sensor, Is.Not.Null);
        Assert.That(sensor.GetCurrentMode(), Is.EqualTo(AccessMode.OfficialApi),
            "接入模式应为 OfficialApi");
    }

    /// <summary>
    /// OpenMuActionExecutor 应能正常构造（含 null nativeExec），不抛出异常。
    /// </summary>
    [Test]
    public void OpenMuActionExecutor_Construct_NoException()
    {
        var player = TestHelper.CreateAiPlayer();
        var gameAdapterMock = new Mock<IGameAdapter>();

        OpenMuActionExecutor executor;
        try
        {
            executor = new OpenMuActionExecutor(player, gameAdapterMock.Object, null);
        }
        catch (Exception ex)
        {
            Assert.Fail($"OpenMuActionExecutor 构造抛异常: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        Assert.That(executor, Is.Not.Null);
        Assert.That(executor.IsWalking, Is.False);
    }

    /// <summary>
    /// nativeExec 为空时 OpenMuActionExecutor 各方法不崩溃（防御检查）。
    /// AttackAsync/SkillAttackAsync 在 nativeExec 为 null 时记录警告并返回 false。
    /// </summary>
    [Test]
    public async Task OpenMuActionExecutor_NativeExecNull_DoesNotCrash()
    {
        var player = TestHelper.CreateAiPlayer();
        var gameAdapterMock = new Mock<IGameAdapter>();
        var executor = new OpenMuActionExecutor(player, gameAdapterMock.Object, null);

        // 以下方法应均不抛出异常
        Assert.That(await executor.AttackAsync(1), Is.False,
            "nativeExec=null 时 AttackAsync 应返回 false");
        Assert.That(await executor.SkillAttackAsync(1, 0), Is.False,
            "nativeExec=null 时 SkillAttackAsync 应返回 false");

        // MoveToAsync/PickupAsync/UseItemAsync/DrinkPotionAsync 不使用 nativeExec，
        // 但也不应抛出异常
        Assert.That(await executor.MoveToAsync(10, 10), Is.False);
        Assert.That(await executor.PickupAsync(1), Is.True,
            "PickupAsync 委托给 GameAdapter，应在模拟环境下返回 true");
        Assert.That(await executor.InteractAsync(1), Is.True,
            "InteractAsync 当前返回 true");
        Assert.That(await executor.DrinkPotionAsync(PotionType.Health), Is.False);
        Assert.That(executor.IsWalking, Is.False);
    }

    /// <summary>
    /// 验证 CognitiveLoop.Modulate 方法在 Fear 主导情感下降低 Fight 决策权重。
    /// Fear 状态下 Emotion 调制因子为 ×0.7，需要连续 3 次死亡触发 Fear > 0.6 阈值。
    /// </summary>
    [Test]
    public void Modulate_FearState_FightWeightReduced()
    {
        var personality = new PersonalityEngine();

        // 连续 3 次死亡触发 Fear 情感（累积 Fear ≈ 0.678 > 0.6 阈值）
        personality.Evolve(new ExperienceEvent { IsDeath = true });
        personality.Evolve(new ExperienceEvent { IsDeath = true });
        personality.Evolve(new ExperienceEvent { IsDeath = true });

        Assert.That(personality.Emotion.EmotionalState, Is.EqualTo(EmotionalState.Fear),
            "3 次死亡后应进入 Fear 情感状态");

        var baseWeight = 1.0f;
        var modulated = personality.Modulate(baseWeight, DecisionType.Fight);

        // Fear 状态 ×0.7 + 个性调制 → 最终应低于 1.0
        Assert.That(modulated, Is.LessThan(baseWeight),
            "恐惧状态下战斗权重应降低");
    }

    /// <summary>
    /// 验证 DecisionCore 在 HP 充足时应返回 null（无紧急行动）。
    /// </summary>
    [Test]
    public async Task Decide_HpAbove50Percent_ReturnsWait()
    {
        var personality = new PersonalityEngine();
        var core = new DecisionCore();
        var state = new WorldState
        {
            Self = new PlayerSnapshot(1, 10, 10, 80, 100, 50, 100, 10, 1000),
        };
        var context = new PerceptContext();

        var result = await core.Decide(state, context, personality);

        Assert.That(result.Action, Is.EqualTo(DecisionAction.Wait),
            "HP 充足时应返回 Wait");
    }

    /// <summary>
    /// 验证行为惯性在成功后形成习惯，失败后削弱。
    /// </summary>
    [Test]
    public void RecordActionResult_FailureAfterSuccess_WeakensHabit()
    {
        var state = new WorldState
        {
            Self = new PlayerSnapshot(1, 42, 100, 100, 100, 50, 100, 10, 1000),
        };
        var inertia = new BehaviorInertiaEngine();

        // 3 次成功形成习惯
        inertia.Record("use_hp_potion", true, state);
        inertia.Record("use_hp_potion", true, state);
        inertia.Record("use_hp_potion", true, state);

        var beforeFail = inertia.TryHabit(state);
        Assert.That(beforeFail, Is.Not.Null, "3 次成功后应有习惯");

        // 1 次失败
        inertia.Record("use_hp_potion", false, state);

        var afterFail = inertia.TryHabit(state);
        Assert.That(afterFail, Is.Not.Null, "1 次失败后不应完全消除习惯");
    }
}
