// <copyright file="ScriptTestBase.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests;

using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.AIPlayer.Scripting;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Base class for ScriptExecutor unit tests.
/// Provides mock infrastructure and helper methods.
/// </summary>
public abstract class ScriptTestBase
{
    /// <summary>
    /// Gets the test player instance.
    /// </summary>
    protected AiPlayer Player { get; }

    /// <summary>
    /// Gets the behavior context.
    /// </summary>
    protected BehaviorContext Context { get; }

    /// <summary>
    /// Gets the mock game context.
    /// </summary>
    protected Mock<IGameContext> MockGameContext { get; }

    protected ScriptTestBase()
    {
        var mockLogger = new Mock<ILogger<Player>>();
        this.MockGameContext = new Mock<IGameContext>();

        // Setup LoggerFactory so Player constructor can create its logger
        var loggerFactoryMock = new Mock<ILoggerFactory>();
        loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);
        this.MockGameContext.Setup(g => g.LoggerFactory).Returns(loggerFactoryMock.Object);

        // Setup PersistenceContextProvider so Player constructor doesn't NPE
        var persistenceProviderMock = new Mock<IPersistenceContextProvider>();
        var playerContextMock = new Mock<IPlayerContext>();
        persistenceProviderMock.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>())).Returns(playerContextMock.Object);
        this.MockGameContext.Setup(p => p.PersistenceContextProvider).Returns(persistenceProviderMock.Object);

        // Setup Configuration
        var gameConfig = new GameConfiguration();
        this.MockGameContext.Setup(g => g.Configuration).Returns(gameConfig);

        // Create real AiPlayer
        this.Player = new AiPlayer(this.MockGameContext.Object);

        // Player.Attributes is null at this point because SetSelectedCharacterAsync
        // was never called. We use reflection to set it to a minimal AttributeSystem
        // so that script conditions/actions like GetHpRatio, GetMpRatio, is_dead, etc.
        // work correctly in tests.
        ScriptTestBase.SetPlayerAttributes(this.Player, CreateTestAttributeSystem());

        // Initialize default attribute values so tests don't see zero values.
        // Tests can override these via SetupHp/SetupMp.
        this.Player.Attributes![Stats.CurrentHealth] = 1000;
        this.Player.Attributes![Stats.MaximumHealth] = 1000;
        this.Player.Attributes![Stats.CurrentMana] = 1000;
        this.Player.Attributes![Stats.MaximumMana] = 1000;

        this.Context = new BehaviorContext(this.Player)
        {
            WorldState = new WorldState
            {
                AttackablesInRange = new List<IAttackable>(),
                DropsInRange = new List<ILocateable>(),
                NpcsInRange = new List<NonPlayerCharacter>(),
                CurrentMap = null,
                PlayerPosition = new Point(100, 100),
                IsAtSafezone = false,
            },
            CurrentTarget = null,
            GameAdapter = new GameAdapter(this.Player),
        };
    }

    /// <summary>
    /// Creates a ScriptExecutor with the given script and default mocks.
    /// </summary>
    protected ScriptExecutor CreateExecutor(BehaviorScript script)
    {
        return new ScriptExecutor(this.Player, this.Context, script);
    }

    /// <summary>
    /// Creates a minimal <see cref="AttributeSystem"/> with the four stat attributes
    /// needed by script conditions/actions: CurrentHealth, MaximumHealth, CurrentMana, MaximumMana.
    /// </summary>
    private static AttributeSystem CreateTestAttributeSystem()
    {
        var statAttributes = new IAttribute[]
        {
            new StatAttribute(Stats.CurrentHealth, 0),
            new StatAttribute(Stats.MaximumHealth, 0),
            new StatAttribute(Stats.CurrentMana, 0),
            new StatAttribute(Stats.MaximumMana, 0),
        };

        return new AttributeSystem(statAttributes, Array.Empty<IAttribute>(), Array.Empty<AttributeRelationship>());
    }

    /// <summary>
    /// Uses reflection to set the private <c>Attributes</c> property on the Player.
    /// Creates an <see cref="ItemAwareAttributeSystem"/> instance without calling its
    /// constructor (which requires Account, Character, and GameConfiguration).
    /// </summary>
    private static void SetPlayerAttributes(Player player, AttributeSystem attributeSystem)
    {
        // ItemAwareAttributeSystem is sealed, so we can't subclass it.
        // We create one without calling the constructor (which requires Account/Character/etc.),
        // then copy the internal state from our minimal AttributeSystem.
        var itemAwareSystem = (ItemAwareAttributeSystem)System.Runtime.Serialization.FormatterServices
            .GetUninitializedObject(typeof(ItemAwareAttributeSystem));

        // Copy the _attributes dictionary from the source system into the new one.
        // The _attributes field is on the AttributeSystem base class.
        var sourceField = typeof(AttributeSystem).GetField(
            "_attributes",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var destField = typeof(AttributeSystem).GetField(
            "_attributes",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (sourceField is null || destField is null)
        {
            throw new InvalidOperationException("Could not find _attributes field on AttributeSystem.");
        }

        var sourceDict = sourceField.GetValue(attributeSystem);
        destField.SetValue(itemAwareSystem, sourceDict);

        // Set the ItemPowerUps dictionary (needed by ItemAwareAttributeSystem)
        var powerUpsField = typeof(ItemAwareAttributeSystem).GetField(
            "<ItemPowerUps>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (powerUpsField is not null)
        {
            powerUpsField.SetValue(itemAwareSystem, new Dictionary<Item, IReadOnlyList<PowerUpWrapper>>());
        }

        // Now set it on the Player via reflection on the backing field.
        // Since itemAwareSystem IS an ItemAwareAttributeSystem, the type check will pass.
        var playerField = typeof(Player).GetField(
            "<Attributes>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (playerField is null)
        {
            throw new InvalidOperationException("Could not find backing field for Player.Attributes.");
        }

        playerField.SetValue(player, itemAwareSystem);
    }

    /// <summary>
    /// Sets the player's current and maximum HP in the attribute system.
    /// </summary>
    protected void SetupHp(int currentHp, int maxHp)
    {
        var attrs = this.Player.Attributes;
        if (attrs is not null)
        {
            attrs[Stats.CurrentHealth] = currentHp;
            attrs[Stats.MaximumHealth] = maxHp;
        }
    }

    /// <summary>
    /// Sets the player's current and maximum MP in the attribute system.
    /// </summary>
    protected void SetupMp(int currentMp, int maxMp)
    {
        var attrs = this.Player.Attributes;
        if (attrs is not null)
        {
            attrs[Stats.CurrentMana] = currentMp;
            attrs[Stats.MaximumMana] = maxMp;
        }
    }

    /// <summary>
    /// Gets the player's current attribute system for direct manipulation.
    /// </summary>
    protected ItemAwareAttributeSystem? PlayerAttributes => this.Player.Attributes;

    /// <summary>
    /// Creates a minimal script with a single priority node.
    /// </summary>
    protected static BehaviorScript SingleNodeScript(string condition, string action)
    {
        return new BehaviorScript
        {
            Id = "test_script",
            Name = "Test Script",
            Version = "1.0.0",
            PriorityChain = new List<PriorityNode>
            {
                new()
                {
                    Name = "test_node",
                    Condition = condition,
                    Action = action,
                },
            },
        };
    }

    /// <summary>
    /// Creates a script with IF/ELIF/ELSE branch structure.
    /// </summary>
    protected static BehaviorScript BranchScript(
        string ifCondition, string ifAction,
        string? elifCondition = null, string? elifAction = null,
        string? elseAction = null)
    {
        var node = new PriorityNode
        {
            Name = "branch_node",
            Condition = ifCondition,
            Action = ifAction,
        };

        if (elifCondition is not null && elifAction is not null)
        {
            node.ElifNodes = new List<PriorityNode>
            {
                new()
                {
                    Name = "elif_node",
                    Condition = elifCondition,
                    Action = elifAction,
                },
            };
        }

        if (elseAction is not null)
        {
            node.ElseNode = new PriorityNode
            {
                Name = "else_node",
                Condition = "always",
                Action = elseAction,
            };
        }

        return new BehaviorScript
        {
            Id = "branch_test",
            Name = "Branch Test",
            Version = "1.0.0",
            PriorityChain = new List<PriorityNode> { node },
        };
    }

    /// <summary>
    /// Creates a paragraph-mode script with the given paragraphs.
    /// </summary>
    protected static BehaviorScript ParagraphScript(params ScriptParagraph[] paragraphs)
    {
        return new BehaviorScript
        {
            Id = "paragraph_test",
            Name = "Paragraph Test",
            Version = "1.0.0",
            Paragraphs = new List<ScriptParagraph>(paragraphs),
        };
    }
}
