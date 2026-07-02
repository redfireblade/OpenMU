// <copyright file="OapsEvolutionLayerTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests;

using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OAPS.Evolution;
using MUnique.OpenMU.AIPlayer;
using MUnique.OpenMU.AIPlayer.Scripting;
using MUnique.OpenMU.GameLogic;

/// <summary>
/// Layer 6 集成测试 — 验证 OAPS Evolution 全部组件的编译、集成点、功能正确性。
/// 覆盖 OapsObserver、BehaviorPatternAnalyzer、RuleGenerator、ScriptGenerator、SwarmEvolution。
/// </summary>
[TestFixture]
public class OapsEvolutionLayerTests
{
    /// <summary>
    /// Tests that <see cref="OapsObserver.ExportBehaviorRecords"/> returns non-empty
    /// export data when the underlying <see cref="PlayerBehaviorObserver"/> has
    /// behavior records.
    /// </summary>
    [Test]
    public void ExportBehaviorRecords_WithRecords_ReturnsExportData()
    {
        // Arrange
        var gameContextMock = TestHelper.CreateMinimalGameContextMock();
        var expMem = new ExperienceMemory { CharacterName = "_test" };
        var pbo = new PlayerBehaviorObserver(
            gameContextMock.Object, expMem, new NullLogger<PlayerBehaviorObserver>());

        // Inject behavior records into PBO via reflection (PBO is sealed, so we
        // cannot mock it; its _behaviorRecords dictionary is private readonly).
        var record = new PlayerBehaviorObserver.BehaviorRecord
        {
            CharacterName = "TestPlayer",
            LastSeenLevel = 50,
            InferredHpPotionThreshold = 0.4,
            InferredMpPotionThreshold = 0.3,
            TotalGoldPicked = 10000,
            TotalGoldSpent = 5000,
        };
        var records = new Dictionary<string, PlayerBehaviorObserver.BehaviorRecord>
        {
            ["TestPlayer"] = record,
        };
        var field = typeof(PlayerBehaviorObserver).GetField(
            "_behaviorRecords",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field!.SetValue(pbo, records);

        // Create BehaviorEventStore with a temp directory so Load() does not
        // fail when no persisted file exists.
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var eventStore = new BehaviorEventStore(tempDir, new NullLogger<BehaviorEventStore>());

        var observer = new OapsObserver(pbo, eventStore, new NullLogger<OapsObserver>());

        // Act
        var result = observer.ExportBehaviorRecords();

        // Assert
        Assert.That(result, Is.Not.Empty, "Export should contain data when PBO has records");
        Assert.That(result.ContainsKey("TestPlayer"), Is.True, "Export should include TestPlayer");
        Assert.That(result["TestPlayer"].Level, Is.EqualTo(50));

        // Cleanup
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }

    /// <summary>
    /// Tests that <see cref="OapsObserver.GetNpcInteractions"/> correctly filters
    /// NPC-related behavior events from the <see cref="BehaviorEventStore"/>.
    /// Only NpcTalk, NpcDialogChoice, PotionBought, and BuffReceived events
    /// should appear in the result; MonsterKilled events should be excluded.
    /// </summary>
    [Test]
    public void GetNpcInteractions_WithEventStore_ReturnsFiltered()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var eventStore = new BehaviorEventStore(tempDir, new NullLogger<BehaviorEventStore>());

        // Add NPC events (should be included)
        eventStore.AddEvent(new BehaviorEvent
        {
            CharacterName = "TestPlayer",
            EventType = BehaviorEventType.NpcTalk,
            NpcNumber = 100,
            NpcName = "WanderingMerchant",
            MapNumber = 0,
            X = 130,
            Y = 130,
            Level = 10,
            Timestamp = DateTime.UtcNow,
        });
        eventStore.AddEvent(new BehaviorEvent
        {
            CharacterName = "TestPlayer",
            EventType = BehaviorEventType.PotionBought,
            NpcNumber = 100,
            NpcName = "WanderingMerchant",
            MapNumber = 0,
            X = 130,
            Y = 130,
            Level = 10,
            ItemPrice = 100,
            Quantity = 5,
            Timestamp = DateTime.UtcNow,
        });

        // Add non-NPC events (should be excluded)
        eventStore.AddEvent(new BehaviorEvent
        {
            CharacterName = "TestPlayer",
            EventType = BehaviorEventType.MonsterKilled,
            MonsterNumber = 10,
            MonsterName = "TestMonster",
            MapNumber = 0,
            X = 100,
            Y = 100,
            Level = 10,
            Timestamp = DateTime.UtcNow,
        });
        eventStore.AddEvent(new BehaviorEvent
        {
            CharacterName = "TestPlayer",
            EventType = BehaviorEventType.LevelUp,
            Level = 11,
            MapNumber = 0,
            Timestamp = DateTime.UtcNow,
        });

        // Create PBO with minimal setup (needed for OapsObserver constructor)
        var gameContextMock = TestHelper.CreateMinimalGameContextMock();
        var expMem = new ExperienceMemory { CharacterName = "_test" };
        var pbo = new PlayerBehaviorObserver(
            gameContextMock.Object, expMem, new NullLogger<PlayerBehaviorObserver>());

        var observer = new OapsObserver(pbo, eventStore, new NullLogger<OapsObserver>());

        // Act
        var result = observer.GetNpcInteractions("TestPlayer");

        // Assert
        Assert.That(result, Is.Not.Empty, "NPC interactions should not be empty");
        Assert.That(result.Count, Is.EqualTo(2), "Only NPCTalk and PotionBought should be returned");
        Assert.That(result.Any(r => r.InteractionType == NpcInteractionType.Talk), Is.True);
        Assert.That(result.Any(r => r.InteractionType == NpcInteractionType.Buy), Is.True);

        // Cleanup
        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }

    // ════════════════════════════════════════════════════════════════════
    // BehaviorPatternAnalyzer Tests
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tests that <see cref="BehaviorPatternAnalyzer.AnalyzeHuntingPatterns"/>
    /// groups 10 MonsterKilled events (same location, same monster) into a single
    /// pattern with KillCount=10 and Confidence=0.5.
    /// </summary>
    [Test]
    public void AnalyzeHuntingPatterns_WithKillEvents_ReturnsPatterns()
    {
        // Arrange
        var analyzer = new BehaviorPatternAnalyzer();
        var events = new List<BehaviorEvent>();
        for (int i = 0; i < 10; i++)
        {
            events.Add(new BehaviorEvent
            {
                EventType = BehaviorEventType.MonsterKilled,
                CharacterName = "TestPlayer",
                MapNumber = 0,
                X = 100,
                Y = 100,
                MonsterNumber = 10,
                MonsterName = "Buddy",
                MonsterLevel = 20,
                Level = 50,
                Timestamp = DateTime.UtcNow.AddSeconds(-i),
            });
        }

        // Act
        var patterns = analyzer.AnalyzeHuntingPatterns(events);

        // Assert
        Assert.That(patterns, Is.Not.Empty);
        Assert.That(patterns.Count, Is.EqualTo(1), "All 10 kills at same spot should collapse to one pattern");
        Assert.That(patterns[0].KillCount, Is.EqualTo(10), "KillCount should reflect all 10 events");
        Assert.That(patterns[0].MapNumber, Is.EqualTo(0));
        Assert.That(patterns[0].MonsterNumber, Is.EqualTo(10));
        Assert.That(patterns[0].MonsterName, Is.EqualTo("Buddy"));
        Assert.That(patterns[0].Confidence, Is.EqualTo(0.5), "Confidence = 10/20 = 0.5");
    }

    /// <summary>
    /// Tests that <see cref="BehaviorPatternAnalyzer.AnalyzePotionPatterns"/>
    /// correctly classifies PotionBought events by price into HP and MP categories.
    /// </summary>
    [Test]
    public void AnalyzePotionPatterns_WithBuyEvents_ReturnsPatterns()
    {
        // Arrange
        var analyzer = new BehaviorPatternAnalyzer();
        var events = new List<BehaviorEvent>();

        // HP potions: price < 500 or null
        for (int i = 0; i < 5; i++)
        {
            events.Add(new BehaviorEvent
            {
                EventType = BehaviorEventType.PotionBought,
                CharacterName = "TestPlayer",
                ItemPrice = 100,
                Quantity = 1,
                Level = 10,
            });
        }

        // MP potions: price >= 500
        for (int i = 0; i < 3; i++)
        {
            events.Add(new BehaviorEvent
            {
                EventType = BehaviorEventType.PotionBought,
                CharacterName = "TestPlayer",
                ItemPrice = 1000,
                Quantity = 1,
                Level = 10,
            });
        }

        // Act
        var patterns = analyzer.AnalyzePotionPatterns(events);

        // Assert
        Assert.That(patterns, Is.Not.Empty);
        Assert.That(patterns.Count, Is.EqualTo(2), "Should produce both HP and MP patterns");

        var hpPattern = patterns.FirstOrDefault(p => p.IsHpPotion);
        var mpPattern = patterns.FirstOrDefault(p => !p.IsHpPotion);

        Assert.That(hpPattern, Is.Not.Null, "HP potion pattern should exist");
        Assert.That(mpPattern, Is.Not.Null, "MP potion pattern should exist");
        Assert.That(hpPattern!.SampleCount, Is.EqualTo(5), "HP potions count should be 5");
        Assert.That(mpPattern!.SampleCount, Is.EqualTo(3), "MP potions count should be 3");
    }

    /// <summary>
    /// Tests that <see cref="BehaviorPatternAnalyzer.AnalyzeSkillPatterns"/>
    /// groups MonsterKilled events by SkillNumber and orders them by usage count
    /// descending.
    /// </summary>
    [Test]
    public void AnalyzeSkillPatterns_WithKillEvents_SkillGrouped()
    {
        // Arrange
        var analyzer = new BehaviorPatternAnalyzer();
        var events = new List<BehaviorEvent>();

        // Skill 18: 5 kills
        for (int i = 0; i < 5; i++)
        {
            events.Add(new BehaviorEvent
            {
                EventType = BehaviorEventType.MonsterKilled,
                CharacterName = "TestPlayer",
                SkillNumber = 18,
                SkillName = "Fireball",
                MonsterNumber = 10,
                MapNumber = 0,
                X = 100,
                Y = 100,
                Level = 50,
            });
        }

        // Skill 26: 3 kills
        for (int i = 0; i < 3; i++)
        {
            events.Add(new BehaviorEvent
            {
                EventType = BehaviorEventType.MonsterKilled,
                CharacterName = "TestPlayer",
                SkillNumber = 26,
                SkillName = "Flame",
                MonsterNumber = 10,
                MapNumber = 0,
                X = 100,
                Y = 100,
                Level = 50,
            });
        }

        // Act
        var patterns = analyzer.AnalyzeSkillPatterns(events);

        // Assert
        Assert.That(patterns, Is.Not.Empty);
        Assert.That(patterns.Count, Is.EqualTo(2), "Should produce two skill patterns");

        // Ordered by UseCount descending: Skill 18 first (5 uses), then Skill 26 (3 uses)
        Assert.That(patterns[0].SkillNumber, Is.EqualTo(18), "Most used skill should be first");
        Assert.That(patterns[0].UseCount, Is.EqualTo(5));
        Assert.That(patterns[0].SkillName, Is.EqualTo("Fireball"));

        Assert.That(patterns[1].SkillNumber, Is.EqualTo(26), "Second most used skill should be second");
        Assert.That(patterns[1].UseCount, Is.EqualTo(3));
        Assert.That(patterns[1].SkillName, Is.EqualTo("Flame"));
    }

    // ════════════════════════════════════════════════════════════════════
    // RuleGenerator Tests
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tests that <see cref="RuleGenerator.GeneratePotionRules"/> produces
    /// exactly 1 HP potion rule when provided with a PotionPattern list
    /// containing an HP pattern.
    /// </summary>
    [Test]
    public void GeneratePotionRules_WithPatterns_CreatesRules()
    {
        // Arrange
        var generator = new RuleGenerator();
        var potions = new List<PotionPattern>
        {
            new()
            {
                IsHpPotion = true,
                ThresholdPercent = 0.4,
                SampleCount = 10,
                Confidence = 0.8,
            },
        };

        // Act
        var rules = generator.GeneratePotionRules(potions);

        // Assert
        Assert.That(rules, Is.Not.Empty);
        Assert.That(rules.Count, Is.EqualTo(1), "Should generate exactly 1 HP potion rule");
        Assert.That(rules[0].ScriptId, Is.EqualTo("use_hp_potion"));
        Assert.That(rules[0].Priority, Is.EqualTo(2));
        Assert.That(rules[0].Condition, Does.Contain("0.40"), "Condition should contain the threshold value");
    }

    // ════════════════════════════════════════════════════════════════════
    // ScriptGenerator Tests
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tests that <see cref="ScriptGenerator.GenerateHuntingScript"/> produces
    /// a script containing both emergency_heal and combat priority nodes when
    /// provided with HuntingPattern and SkillPattern data.
    /// </summary>
    [Test]
    public void GenerateHuntingScript_WithPatterns_CreatesScript()
    {
        // Arrange
        var generator = new ScriptGenerator();
        var hunting = new List<HuntingPattern>
        {
            new()
            {
                MapNumber = 0,
                X = 100,
                Y = 100,
                MonsterNumber = 10,
                MonsterName = "Buddy",
                MonsterLevel = 20,
                KillCount = 20,
                Confidence = 1.0,
            },
        };
        var skills = new List<SkillPattern>
        {
            new()
            {
                SkillNumber = 18,
                SkillName = "Fireball",
                UseCount = 20,
                Confidence = 0.8,
            },
        };

        // Act
        var result = generator.GenerateHuntingScript(hunting, skills, null, 50);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Script, Is.Not.Null);
        Assert.That(result.Script.PriorityChain, Is.Not.Empty);

        var nodeNames = result.Script.PriorityChain.Select(n => n.Name).ToList();
        Assert.That(nodeNames, Does.Contain("emergency_heal"),
            "Script should contain emergency_heal node");
        Assert.That(nodeNames, Does.Contain("combat"),
            "Script should contain combat node");
        Assert.That(nodeNames, Does.Contain("skillAttack"),
            "Script should contain skillAttack node (high confidence skill present)");

        // Verify skillAttack node carries the correct skill number
        var skillNode = result.Script.PriorityChain.First(n => n.Name == "skillAttack");
        Assert.That(skillNode.Parameters?.SkillNumber, Is.EqualTo((ushort)18));
    }

    // ════════════════════════════════════════════════════════════════════
    // SwarmEvolution Tests
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tests that <see cref="SwarmEvolution.EvolveGeneration"/> produces a
    /// next generation whose count equals <c>populationSize</c>, and that
    /// generation metrics (BestFitness, AverageFitness) are reported correctly.
    /// </summary>
    [Test]
    public void EvolveGeneration_WithStats_EvolvesAndImproves()
    {
        // Arrange
        const int populationSize = 3;
        var scripts = new List<GeneratedScript>();

        for (int i = 0; i < populationSize; i++)
        {
            var script = new GeneratedScript
            {
                Script = new BehaviorScript
                {
                    Id = $"script_{i}",
                    Name = $"Test Script {i}",
                    PriorityChain = new List<PriorityNode>
                    {
                        new()
                        {
                            Name = "emergency_heal",
                            Condition = "hp_below_threshold",
                            Action = "use_hp_potion",
                            Parameters = new ScriptParameters { HpThreshold = 0.2f },
                        },
                        new()
                        {
                            Name = "combat",
                            Condition = "has_target",
                            Action = "attack_target",
                        },
                    },
                },
                Confidence = 0.5 + (i * 0.1),
            };
            scripts.Add(script);
        }

        var statsMap = new Dictionary<string, BotStats>();
        for (int i = 0; i < populationSize; i++)
        {
            statsMap[$"script_{i}"] = new BotStats
            {
                ScriptId = $"script_{i}",
                Generation = 0,
                ExpGained = 1000 * (i + 1),
                Deaths = i,
                ItemsPicked = 10,
                GoldEarned = 5000,
                TimeSpent = TimeSpan.FromMinutes(10),
            };
        }

        var evolution = new SwarmEvolution(
            populationSize: populationSize,
            eliteRate: 0.5f,
            mutationRate: 0f);

        // Act
        var result = evolution.EvolveGeneration(scripts, statsMap);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.NextGeneration.Count, Is.EqualTo(populationSize),
            "Next generation count should equal populationSize");
        Assert.That(result.PopulationSize, Is.EqualTo(populationSize));
        Assert.That(result.BestFitness, Is.GreaterThan(0f), "Best fitness should be positive");
        Assert.That(result.AverageFitness, Is.GreaterThan(0f), "Average fitness should be positive");
        Assert.That(result.BestScriptId, Is.Not.Null, "BestScriptId should not be null");
        Assert.That(result.Generation, Is.EqualTo(1), "Generation should increment to 1");
    }

    // ════════════════════════════════════════════════════════════════════
    // 端到端管道测试 — 从行为事件→模式分析→脚本生成→脚本加载
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 端到端测试 1/3：BehaviorPatternAnalyzer + GenerateHuntingScript 直接测试
    /// 验证 NPC BUFF 节点正确生成。
    /// </summary>
    [Test]
    public void EndToEnd_AnalyzeNpcAndGenerateHuntingScript()
    {
        // Arrange
        var events = new List<BehaviorEvent>();
        var now = DateTime.UtcNow;
        for (int i = 0; i < 10; i++)
            events.Add(new BehaviorEvent { EventType = BehaviorEventType.MonsterKilled, CharacterName = "T", MapNumber = 0, X = 120, Y = 110, MonsterNumber = 10, MonsterName = "Buddy", MonsterLevel = 20, Level = 30, SkillNumber = 18, SkillName = "Fireball", Timestamp = now.AddSeconds(-i * 10) });
        for (int i = 0; i < 3; i++)
            events.Add(new BehaviorEvent { EventType = BehaviorEventType.NpcTalk, CharacterName = "T", MapNumber = 0, X = 140, Y = 125, NpcNumber = 257, NpcName = "Elf Soldier", Level = 30, Timestamp = now.AddSeconds(-i * 30) });
        for (int i = 0; i < 2; i++)
            events.Add(new BehaviorEvent { EventType = BehaviorEventType.BuffReceived, CharacterName = "T", MapNumber = 0, X = 140, Y = 125, NpcNumber = 257, NpcName = "Elf Soldier", Level = 30, Timestamp = now.AddSeconds(-i * 30 - 5) });

        // Act 1 — 分析
        var patterns = new BehaviorPatternAnalyzer().AnalyzeAll(events);

        // Assert 1 — NPC 交互模式
        Assert.That(patterns.NpcInteractions, Is.Not.Empty);
        Assert.That(patterns.NpcInteractions.Any(n => n.InteractionType == NpcInteractionType.Talk && n.NpcNumber == 257), Is.True);
        Assert.That(patterns.NpcInteractions.Any(n => n.InteractionType == NpcInteractionType.Buff && n.NpcNumber == 257), Is.True);

        // Act 2 — 生成狩猎脚本（直接测试 GenerateHuntingScript，避开候选选择逻辑）
        var huntingScript = new ScriptGenerator().GenerateHuntingScript(patterns.Hunting, patterns.Skills, patterns.NpcInteractions, 30);

        // Assert 2 — 狩猎脚本包含 get_npc_buff 节点
        Assert.That(huntingScript.Script.PriorityChain.Any(n => n.Name == "get_npc_buff"), Is.True,
            "Hunting script should contain get_npc_buff node");
        var buffNode = huntingScript.Script.PriorityChain.First(n => n.Name == "get_npc_buff");
        Assert.That(buffNode.Parameters?.QuestNpcNumber, Is.EqualTo((short)257),
            "get_npc_buff should target Elf Soldier (NPC 257)");
        Assert.That(buffNode.Condition, Is.EqualTo("not_buffed"));
        Assert.That(huntingScript.Script.PriorityChain.Any(n => n.Name == "skillAttack"), Is.True,
            "Hunting script should contain skillAttack");
    }

    /// <summary>
    /// 端到端测试 2/3：GenerateFromPatterns 带 NPC 交互数据
    /// 验证在 GenerateFromPatterns 中狩猎脚本被选中时包含 BUFF 节点。
    /// </summary>
    [Test]
    public void EndToEnd_GenerateFromPatternsWithNpcData()
    {
        // Arrange — 只有狩猎和 NPC 数据（无药水，确保狩猎脚本被选为最佳）
        var patterns = new AllPatternsResult();
        patterns.Hunting.Add(new HuntingPattern { MapNumber = 0, X = 100, Y = 100, MonsterNumber = 10, MonsterName = "Buddy", KillCount = 20, Confidence = 1.0 });
        patterns.Skills.Add(new SkillPattern { SkillNumber = 18, SkillName = "Fireball", UseCount = 10, Confidence = 0.8 });
        patterns.NpcInteractions.Add(new NpcInteractionPattern { NpcNumber = 257, NpcName = "Elf Soldier", InteractionType = NpcInteractionType.Talk, Frequency = 5, Confidence = 0.8 });
        patterns.NpcInteractions.Add(new NpcInteractionPattern { NpcNumber = 257, NpcName = "Elf Soldier", InteractionType = NpcInteractionType.Buff, Frequency = 3, Confidence = 0.6 });

        // Act
        var script = new ScriptGenerator().GenerateFromPatterns(patterns, "TestPlayer", 30);

        // Assert
        Assert.That(script.Script.PriorityChain.Any(n => n.Name == "get_npc_buff"), Is.True,
            "Best script should contain get_npc_buff node");
        Assert.That(script.Script.PriorityChain.Any(n => n.Name == "combat"), Is.True);
    }

    /// <summary>
    /// 端到端测试 3/3：从 BehaviorEventStore → 分析 → 生成 → 写文件 → ScriptExecutor 加载
    /// 使用 OapsKnowledgeBridge.SyncLearning 验证完整管道。
    /// </summary>
    [Test]
    public void EndToEnd_Pipeline_SyncAndLoadScript()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var eventStore = new BehaviorEventStore(tempDir, new NullLogger<BehaviorEventStore>());
        var now = DateTime.UtcNow;
        var playerName = "E2ETestPlayer";

        // 注入足够多的击杀事件确保分段器产生狩猎段
        for (int s = 0; s < 4; s++)
            for (int k = 0; k < 3; k++)
                eventStore.AddEvent(new BehaviorEvent {
                    EventType = BehaviorEventType.MonsterKilled, CharacterName = playerName,
                    MapNumber = 0, X = (byte)(120 + k), Y = 110,
                    MonsterNumber = 10, MonsterName = "Buddy",
                    Level = 25, SkillNumber = 18,
                    Timestamp = now.AddSeconds(-s * 120 - k * 10) });

        // NPC 交互
        eventStore.AddEvent(new BehaviorEvent {
            EventType = BehaviorEventType.NpcTalk, CharacterName = playerName,
            MapNumber = 0, X = 140, Y = 125,
            NpcNumber = 257, NpcName = "Elf Soldier", Level = 25,
            Timestamp = now.AddSeconds(-65) });
        eventStore.AddEvent(new BehaviorEvent {
            EventType = BehaviorEventType.BuffReceived, CharacterName = playerName,
            MapNumber = 0, X = 140, Y = 125,
            NpcNumber = 257, NpcName = "Elf Soldier", Level = 25,
            Timestamp = now.AddSeconds(-60) });

        // Act
        var bridge = new OapsKnowledgeBridge(eventStore, new NullLogger<OapsKnowledgeBridge>());
        bridge.SyncLearning(playerName);

        // Assert — 脚本文件已生成
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "learned", $"learned_{playerName}.json");
        Assert.That(File.Exists(scriptPath), Is.True, "Script file should be written by SyncLearning");

        // Act 2 — ScriptExecutor 加载
        if (File.Exists(scriptPath))
        {
            var loaded = ScriptExecutor.LoadFromFile(scriptPath);
            Assert.That(loaded, Is.Not.Null, "Generated script must be loadable by ScriptExecutor");
            Assert.That(loaded.PriorityChain, Is.Not.Empty, "Script must have priority nodes");
            Assert.That(loaded.PriorityChain.Any(n => n.Name is "combat" or "skillAttack" or "emergency_heal"),
                Is.True, "Script must contain combat nodes");

            // 清理
            File.Delete(scriptPath);
            var dir = Path.Combine(AppContext.BaseDirectory, "scripts", "learned");
            if (Directory.Exists(dir) && !Directory.GetFiles(dir).Any())
                Directory.Delete(dir, recursive: true);
        }

        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }
}
