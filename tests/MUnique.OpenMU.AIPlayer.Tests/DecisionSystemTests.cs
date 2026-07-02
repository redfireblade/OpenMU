// <copyright file="DecisionSystemTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.AIPlayer;
using MUnique.OpenMU.AIPlayer.Decision;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Unit tests for the AI decision system: <see cref="RuleEngine"/> rule evaluation,
/// <see cref="MissionBoardService"/> initialization, and rule-driven behavior selection.
/// </summary>
[TestFixture]
public class DecisionSystemTests
{
    /// <summary>
    /// Creates a <see cref="RuleEngine"/> instance for testing with default rules
    /// and a mocked script library.
    /// </summary>
    private static RuleEngine CreateRuleEngine()
    {
        var aiPlayer = TestHelper.CreateAiPlayer();
        var scriptLib = new ScriptLibrary(
            aiPlayer,
            Mock.Of<IGameAdapter>(),
            new BoardState(),
            new BehaviorContext(aiPlayer),
            new NullLogger<ScriptLibrary>(),
            new MaterialKnowledgeService(TestHelper.CreateGameConfiguration(), new NullLogger<MaterialKnowledgeService>()));
        return new RuleEngine(scriptLib, new NullLogger<RuleEngine>());
    }

    /// <summary>
    /// Tests that the rule engine loads default rules on construction.
    /// </summary>
    [Test]
    public void RuleEngineLoadsDefaultRules()
    {
        var engine = CreateRuleEngine();
        var rules = engine.GetRules();

        Assert.That(rules, Is.Not.Empty);
        Assert.That(rules.Count, Is.EqualTo(8));
    }

    /// <summary>
    /// Tests that default rules are ordered by priority (ascending).
    /// RuleEngine evaluates rules by priority — lower numbers execute first.
    /// </summary>
    [Test]
    public void DefaultRulesAreOrderedByPriority()
    {
        var engine = CreateRuleEngine();
        var rules = engine.GetRules();

        // survival_hp=1 < inventory_cleanup=10 < auto_equip=20 < ...
        Assert.That(rules[0].Priority, Is.EqualTo(1));
        Assert.That(rules[0].RuleId, Is.EqualTo("survival_hp"));

        Assert.That(rules[1].Priority, Is.EqualTo(10));
        Assert.That(rules[1].RuleId, Is.EqualTo("inventory_cleanup"));

        Assert.That(rules[2].Priority, Is.EqualTo(20));
        Assert.That(rules[2].RuleId, Is.EqualTo("auto_equip"));

        Assert.That(rules[3].Priority, Is.EqualTo(25));
        Assert.That(rules[3].RuleId, Is.EqualTo("learn_skill"));

        // craft rules at 40
        Assert.That(rules[4].Priority, Is.EqualTo(40));
        Assert.That(rules[5].Priority, Is.EqualTo(40));

        // quest at 60
        Assert.That(rules[6].Priority, Is.EqualTo(60));

        // survival_farm at 90 (lowest priority)
        Assert.That(rules[^1].Priority, Is.EqualTo(90));
        Assert.That(rules[^1].RuleId, Is.EqualTo("survival_farm"));
    }

    /// <summary>
    /// Tests that the rule engine selects the highest-priority (lowest number) rule
    /// whose condition is met when multiple rules are registered.
    /// </summary>
    [Test]
    public void RuleEngineSelectsHighestPriorityRule()
    {
        var engine = CreateRuleEngine();
        var rules = engine.GetRules();

        // Verify the rule ordering: survival_hp (1) < inventory_cleanup (10) < auto_equip (20)
        var survivalHp = rules.First(r => r.RuleId == "survival_hp");
        var inventoryCleanup = rules.First(r => r.RuleId == "inventory_cleanup");
        var autoEquip = rules.First(r => r.RuleId == "auto_equip");

        Assert.That(survivalHp.Priority, Is.LessThan(inventoryCleanup.Priority));
        Assert.That(inventoryCleanup.Priority, Is.LessThan(autoEquip.Priority));
    }

    /// <summary>
    /// Tests that ReloadFromJson replaces default rules with rules from valid JSON.
    /// </summary>
    [Test]
    public void ReloadFromJsonReplacesRules()
    {
        var engine = CreateRuleEngine();

        const string json = """
        [
            {
                "ruleId": "test_rule_1",
                "priority": 5,
                "category": "survival",
                "condition": "always",
                "scriptId": "test_script",
                "minLevel": 10,
                "maxLevel": 100,
                "description": "A test rule"
            }
        ]
        """;

        engine.ReloadFromJson(json);
        var rules = engine.GetRules();

        Assert.That(rules, Has.Count.EqualTo(1));
        Assert.That(rules[0].RuleId, Is.EqualTo("test_rule_1"));
        Assert.That(rules[0].Priority, Is.EqualTo(5));
        Assert.That(rules[0].MinLevel, Is.EqualTo(10));
        Assert.That(rules[0].MaxLevel, Is.EqualTo(100));
    }

    /// <summary>
    /// Tests that ReloadFromJson supports the wrapped format: {"rules": [...]}.
    /// </summary>
    [Test]
    public void ReloadFromJsonSupportsWrappedFormat()
    {
        var engine = CreateRuleEngine();

        const string json = """
        {
            "rules": [
                {
                    "ruleId": "wrapped_rule",
                    "priority": 1,
                    "category": "craft",
                    "condition": "always",
                    "scriptId": "crafting_executor",
                    "description": "A wrapped rule"
                }
            ]
        }
        """;

        engine.ReloadFromJson(json);
        var rules = engine.GetRules();

        Assert.That(rules, Has.Count.EqualTo(1));
        Assert.That(rules[0].RuleId, Is.EqualTo("wrapped_rule"));
    }

    /// <summary>
    /// Tests that GenerateMissionItem creates a MissionItem for craft category rules.
    /// </summary>
    [Test]
    public void GenerateMissionItemCreatesQuestForCraftCategory()
    {
        var engine = CreateRuleEngine();
        var rule = new RuleDef(
            "craft_test", 40, "craft", "always", "crafting_executor",
            0, 0, null, null, "Craft test item",
            new Dictionary<string, string> { { "targetItem", "wing2" } });

        var mission = engine.GenerateMissionItem(rule);

        Assert.That(mission, Is.Not.Null);
        Assert.That(mission!.Type, Is.EqualTo(MissionType.Quest));
        Assert.That(mission.Id, Does.Contain("craft_test"));
        Assert.That(mission.Context, Does.ContainKey("targetItem"));
    }

    /// <summary>
    /// Tests that GenerateMissionItem returns null for survival category rules.
    /// Survival rules are handled directly by HeartbeatService and don't
    /// generate standalone missions.
    /// </summary>
    [Test]
    public void GenerateMissionItemReturnsNullForSurvivalCategory()
    {
        var engine = CreateRuleEngine();
        var rule = new RuleDef(
            "survival_test", 1, "survival", "hp_low", "survival",
            0, 0, null, null, "Low HP potion");

        var mission = engine.GenerateMissionItem(rule);

        Assert.That(mission, Is.Null);
    }

    /// <summary>
    /// Tests that GenerateMissionItem creates MissionType.Survival for equip/skill/inventory categories.
    /// </summary>
    [Test]
    public void GenerateMissionItemCreatesSurvivalForEquipCategory()
    {
        var engine = CreateRuleEngine();
        var rule = new RuleDef(
            "auto_equip", 20, "equip", "has_better_equip", "equip_compare",
            0, 0, null, null, "Auto equip better gear");

        var mission = engine.GenerateMissionItem(rule);

        // equip category maps to MissionType.Survival → returns null (no standalone mission)
        Assert.That(mission, Is.Null);
    }

    /// <summary>
    /// Tests that rule definitions with parameters are correctly parsed.
    /// </summary>
    [Test]
    public void RuleWithParametersParsesCorrectly()
    {
        var engine = CreateRuleEngine();

        const string json = """
        [
            {
                "ruleId": "param_test",
                "priority": 30,
                "category": "craft",
                "condition": "always",
                "scriptId": "crafting_executor",
                "minLevel": 50,
                "maxLevel": 200,
                "requiredClass": "Dark Wizard",
                "description": "Rule with params",
                "parameters": {
                    "targetGroup": "15",
                    "itemName": "Scroll of Fire",
                    "count": "3"
                }
            }
        ]
        """;

        engine.ReloadFromJson(json);
        var rule = engine.GetRules()[0];

        Assert.That(rule.Parameters, Is.Not.Null);
        Assert.That(rule!.Parameters!["targetGroup"], Is.EqualTo("15"));
        Assert.That(rule!.Parameters!["itemName"], Is.EqualTo("Scroll of Fire"));
        Assert.That(rule!.Parameters!["count"], Is.EqualTo("3"));
        Assert.That(rule.RequiredClass, Is.EqualTo("Dark Wizard"));
    }

    /// <summary>
    /// Tests that invalid JSON does not replace the existing rules.
    /// </summary>
    [Test]
    public void InvalidJsonDoesNotReplaceRules()
    {
        var engine = CreateRuleEngine();

        // Should keep default rules when reload fails
        const string invalidJson = "{ this is not valid json }";

        // This should not throw
        Assert.DoesNotThrow(() => engine.ReloadFromJson(invalidJson));

        // Default rules should still be present
        Assert.That(engine.GetRules(), Is.Not.Empty);
    }

    /// <summary>
    /// Tests that the RuleEngine Evaluate method returns the highest-priority
    /// matching rule for a player that meets multiple conditions.
    /// Tests with the "always" condition which always matches.
    /// </summary>
    [Test]
    public void EvaluateReturnsHighestPriorityMatch()
    {
        var engine = CreateRuleEngine();

        // Inject two always-match rules with different priorities
        const string json = """
        [
            {
                "ruleId": "low_priority",
                "priority": 50,
                "category": "survival",
                "condition": "always",
                "scriptId": "test",
                "description": "Low priority"
            },
            {
                "ruleId": "high_priority",
                "priority": 5,
                "category": "survival",
                "condition": "always",
                "scriptId": "test",
                "description": "High priority"
            }
        ]
        """;

        engine.ReloadFromJson(json);

        // Use a real AiPlayer instance with Inventory — condition "always" skips both
        var aiPlayer = TestHelper.CreateAiPlayerWithInventory();
        var adapter = Mock.Of<IGameAdapter>();
        var result = engine.Evaluate(aiPlayer, adapter);

        // High priority (5) should win over low priority (50)
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Rule.RuleId, Is.EqualTo("high_priority"));
        Assert.That(result!.Rule.Priority, Is.EqualTo(5));
    }

    /// <summary>
    /// Tests that MissionBoardService initializes with an empty configuration
    /// without throwing. Verifies that the board state is populated and
    /// the initialized flag is set.
    /// </summary>
    [Test]
    public async Task MissionBoardInitializeWithEmptyConfigAsync()
    {
        var config = TestHelper.CreateGameConfiguration();

        var gameContextMock = new Mock<IGameContext>();
        gameContextMock.Setup(c => c.Configuration).Returns(config);
        gameContextMock.Setup(c => c.PlugInManager)
            .Returns(new PlugIns.PlugInManager(null, new NullLoggerFactory(), null, null));

        // Create a minimal mock for GameAdapter (IGameAdapter) that returns safe defaults
        var adapterMock = new Mock<IGameAdapter>();
        adapterMock.Setup(a => a.GetPlayerLevel()).Returns(1);
        adapterMock.Setup(a => a.GetCurrentHp()).Returns(100);
        adapterMock.Setup(a => a.GetMaxHp()).Returns(100);
        adapterMock.Setup(a => a.GetCurrentMp()).Returns(50);
        adapterMock.Setup(a => a.GetMaxMp()).Returns(50);
        adapterMock.Setup(a => a.GetPlayerPosition()).Returns(new Pathfinding.Point(130, 130));
        adapterMock.Setup(a => a.GetCurrentMap()).Returns((GameMap?)null);
        adapterMock.Setup(a => a.IsPlayerWalking()).Returns(false);
        adapterMock.Setup(a => a.GetActiveQuests()).Returns(new List<ActiveQuestInfo>());

        // AiPlayer needs a proper mock. Since AiPlayer inherits from Player,
        // we need to create it with a mocked IGameContext.
        var contextMock = new Mock<IGameContext>();
        contextMock.Setup(c => c.Configuration).Returns(config);
        contextMock.Setup(c => c.PlugInManager)
            .Returns(new PlugIns.PlugInManager(null, new NullLoggerFactory(), null, null));

        // Create a real AiPlayer with the mocked context
        contextMock.Setup(c => c.LoggerFactory).Returns(new NullLoggerFactory());
        contextMock.Setup(c => c.PersistenceContextProvider).Returns(new InMemoryPersistenceContextProvider());
        var player = new AiPlayer(contextMock.Object);

        var service = new MissionBoardService(player, adapterMock.Object, new NullLogger<MissionBoardService>());

        // Should initialize without throwing
        await service.InitializeAsync().ConfigureAwait(false);

        Assert.That(service.IsInitialized, Is.True);
        Assert.That(service.BoardState, Is.Not.Null);
        Assert.That(service.BoardState.Missions, Is.Not.Null);
    }

    /// <summary>
    /// Tests that MissionBoardService skips re-initialization when already initialized.
    /// </summary>
    [Test]
    public async Task MissionBoardSkipsDoubleInitializationAsync()
    {
        var player = TestHelper.CreateAiPlayer();
        var adapterMock = new Mock<IGameAdapter>();
        adapterMock.Setup(a => a.GetPlayerLevel()).Returns(1);
        adapterMock.Setup(a => a.GetActiveQuests()).Returns(new List<ActiveQuestInfo>());
        adapterMock.Setup(a => a.GetCurrentHp()).Returns(100);
        adapterMock.Setup(a => a.GetMaxHp()).Returns(100);
        adapterMock.Setup(a => a.GetCurrentMp()).Returns(50);
        adapterMock.Setup(a => a.GetMaxMp()).Returns(50);
        adapterMock.Setup(a => a.GetPlayerPosition()).Returns(new Pathfinding.Point(130, 130));
        adapterMock.Setup(a => a.IsPlayerWalking()).Returns(false);

        var service = new MissionBoardService(player, adapterMock.Object, new NullLogger<MissionBoardService>());

        await service.InitializeAsync().ConfigureAwait(false);
        Assert.That(service.IsInitialized, Is.True);

        // Second call should be no-op
        await service.InitializeAsync().ConfigureAwait(false);
        Assert.That(service.IsInitialized, Is.True);
    }

    /// <summary>
    /// Tests that the BoardState.PlayerState is synchronized correctly after SyncPlayerState.
    /// </summary>
    [Test]
    public void SyncPlayerStateUpdatesBoardState()
    {
        var player = TestHelper.CreateAiPlayer();
        var adapterMock = new Mock<IGameAdapter>();
        adapterMock.Setup(a => a.GetCurrentHp()).Returns(80);
        adapterMock.Setup(a => a.GetMaxHp()).Returns(100);
        adapterMock.Setup(a => a.GetCurrentMp()).Returns(40);
        adapterMock.Setup(a => a.GetMaxMp()).Returns(50);
        adapterMock.Setup(a => a.GetPlayerPosition()).Returns(new Pathfinding.Point(120, 130));
        adapterMock.Setup(a => a.IsPlayerWalking()).Returns(false);
        adapterMock.Setup(a => a.GetPlayerLevel()).Returns(50);
        adapterMock.Setup(a => a.GetCurrentMap()).Returns((GameMap?)null);

        var service = new MissionBoardService(player, adapterMock.Object, new NullLogger<MissionBoardService>());

        // Manually call SyncPlayerState even before initialization
        service.SyncPlayerState();

        Assert.That(service.BoardState.PlayerState.HpPercent, Is.EqualTo(80));
        Assert.That(service.BoardState.PlayerState.MpPercent, Is.EqualTo(80));
        Assert.That(service.BoardState.PlayerState.Position.X, Is.EqualTo(120));
        Assert.That(service.BoardState.PlayerState.Level, Is.EqualTo(50));
    }

    /// <summary>
    /// Tests that <see cref="RuleEngine.Evaluate"/> returns the highest-priority rule
    /// when multiple rules with always-match conditions are registered.
    /// Priority 5 should be selected over 50 and 100.
    /// </summary>
    [Test]
    public void RuleEngine_SelectsHighestPriorityRule()
    {
        var engine = CreateRuleEngine();

        const string json = """
        [
            { "ruleId": "low_prio", "priority": 100, "category": "survival", "condition": "always", "scriptId": "survival", "description": "Low priority" },
            { "ruleId": "mid_prio", "priority": 50, "category": "survival", "condition": "always", "scriptId": "survival", "description": "Mid priority" },
            { "ruleId": "high_prio", "priority": 5, "category": "survival", "condition": "always", "scriptId": "survival", "description": "High priority" }
        ]
        """;

        engine.ReloadFromJson(json);

        var result = engine.Evaluate(TestHelper.CreateAiPlayerWithInventory(), Mock.Of<IGameAdapter>());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Rule.RuleId, Is.EqualTo("high_prio"));
        Assert.That(result!.Rule.Priority, Is.EqualTo(5));
    }

    /// <summary>
    /// Tests that hot-reloading rules via <see cref="RuleEngine.ReloadFromJson"/>
    /// immediately replaces all existing rules and subsequent Evaluate calls
    /// use the updated rule set.
    /// </summary>
    [Test]
    public void RuleEngine_HotReload_UpdatesRules()
    {
        var engine = CreateRuleEngine();

        // Default rules should include survival_hp as first rule
        var defaultRules = engine.GetRules();
        Assert.That(defaultRules, Is.Not.Empty);

        // Hot reload with a single test rule
        const string json = """
        [
            { "ruleId": "hot_reload_test", "priority": 1, "category": "survival", "condition": "always", "scriptId": "test", "description": "Hot reloaded test rule" }
        ]
        """;

        engine.ReloadFromJson(json);

        // Rule list should now contain only the new rule
        var updatedRules = engine.GetRules();
        Assert.That(updatedRules, Has.Count.EqualTo(1));
        Assert.That(updatedRules[0].RuleId, Is.EqualTo("hot_reload_test"));

        // Evaluate should use the new rule
        var result = engine.Evaluate(TestHelper.CreateAiPlayerWithInventory(), Mock.Of<IGameAdapter>());
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Rule.RuleId, Is.EqualTo("hot_reload_test"));
    }

    /// <summary>
    /// Tests that <see cref="MissionBoardService.InitializeAsync"/> populates the
    /// board with quests when the <see cref="GameConfiguration"/> contains monsters
    /// with <see cref="QuestDefinition"/> entries. Verifies the quest is mapped
    /// to a <see cref="MissionItem"/> with the correct group, number, and category.
    /// </summary>
    [Test]
    public async Task MissionBoard_InitializeFromConfig_HasQuests()
    {
        var config = TestHelper.CreateGameConfiguration();

        var quest = new QuestDefinition
        {
            Group = 0,
            Number = 1,
            Name = "Test Quest",
            MinimumCharacterLevel = 1,
        };

        var monster = TestHelper.CreateEntity<MonsterDefinition>();
        monster.Number = 1;
        monster.Designation = "Quest Giver";
        monster.Quests.Add(quest);
        config.Monsters.Add(monster);

        var gameContextMock = new Mock<IGameContext>();
        gameContextMock.Setup(c => c.Configuration).Returns(config);
        gameContextMock.Setup(c => c.PlugInManager).Returns(new PlugInManager(null, new NullLoggerFactory(), null, null));
        gameContextMock.Setup(c => c.LoggerFactory).Returns(new NullLoggerFactory());
        gameContextMock.Setup(c => c.PersistenceContextProvider).Returns(new InMemoryPersistenceContextProvider());

        var aiPlayer = new AiPlayer(gameContextMock.Object);

        var adapterMock = new Mock<IGameAdapter>();
        adapterMock.Setup(a => a.GetPlayerLevel()).Returns(1);
        adapterMock.Setup(a => a.GetCurrentHp()).Returns(100);
        adapterMock.Setup(a => a.GetMaxHp()).Returns(100);
        adapterMock.Setup(a => a.GetCurrentMp()).Returns(50);
        adapterMock.Setup(a => a.GetMaxMp()).Returns(50);
        adapterMock.Setup(a => a.GetPlayerPosition()).Returns(new Point(130, 130));
        adapterMock.Setup(a => a.GetCurrentMap()).Returns((GameMap?)null);
        adapterMock.Setup(a => a.IsPlayerWalking()).Returns(false);
        adapterMock.Setup(a => a.GetActiveQuests()).Returns(new List<ActiveQuestInfo>());

        var service = new MissionBoardService(aiPlayer, adapterMock.Object, new NullLogger<MissionBoardService>());
        await service.InitializeAsync().ConfigureAwait(false);

        Assert.That(service.IsInitialized, Is.True);
        Assert.That(service.BoardState.Missions, Is.Not.Empty);

        var questMission = service.BoardState.Missions.FirstOrDefault(m => m.QuestGroup == 0 && m.QuestNumber == 1);
        Assert.That(questMission, Is.Not.Null);
        Assert.That(questMission!.Title, Does.Contain("Test Quest"));
        Assert.That(questMission.Category, Is.EqualTo(QuestCategory.MainStory));
    }
}
