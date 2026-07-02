// <copyright file="ScriptTestBase.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests;

using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Moq;
using MUnique.OpenMU.AIPlayer.Scripting;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.Persistence;
/// <summary>
/// A minimal attribute system for testing that stores values in a dictionary.
/// </summary>
public sealed class TestAttributeSystem : AttributeSystem
{
    private readonly Dictionary<AttributeDefinition, float> _overrides = new();

    public TestAttributeSystem()
        : base(Enumerable.Empty<IAttribute>(), Enumerable.Empty<IAttribute>(), Enumerable.Empty<AttributeRelationship>())
    {
    }

    /// <summary>
    /// Sets a stat attribute value directly.
    /// </summary>
    public void Set(AttributeDefinition definition, float value)
    {
        this[definition] = value;
    }

    /// <inheritdoc />
    public new float this[AttributeDefinition? attributeDefinition]
    {
        get => base[attributeDefinition];
        set => base[attributeDefinition] = value;
    }
}

/// <summary>
/// Base class for ScriptExecutor unit tests.
/// Provides mock infrastructure and helper methods.
/// </summary>
public abstract class ScriptTestBase
{
    /// <summary>
    /// Gets the test player instance.
    /// </summary>
    protected AiPlayer Player { get; private set; }

    /// <summary>
    /// Gets the behavior context.
    /// </summary>
    protected BehaviorContext Context { get; private set; }

    /// <summary>
    /// Gets the test attribute system.
    /// </summary>
    protected TestAttributeSystem Attributes { get; private set; }

    /// <summary>
    /// Gets the mock game context.
    /// </summary>
    protected Mock<IGameContext> MockGameContext { get; private set; }

    protected ScriptTestBase()
    {
        var mockLogger = new Mock<ILogger<Player>>();
        this.MockGameContext = new Mock<IGameContext>();

        // Setup LoggerFactory on game context so Player constructor can create its logger
        var loggerFactoryMock = new Mock<ILoggerFactory>();
        loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(mockLogger.Object);
        this.MockGameContext.Setup(g => g.LoggerFactory).Returns(loggerFactoryMock.Object);

        // Setup PersistenceContextProvider so Player constructor doesn't NPE
        var persistenceProviderMock = new Mock<IPersistenceContextProvider>();
        var playerContextMock = new Mock<IPlayerContext>();
        persistenceProviderMock.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>())).Returns(playerContextMock.Object);
        this.MockGameContext.Setup(g => g.PersistenceContextProvider).Returns(persistenceProviderMock.Object);

        // Setup Configuration so Player constructor doesn't NPE on InfoRange
        var config = TestHelper.CreateGameConfiguration();
        config.InfoRange = 20;
        this.MockGameContext.Setup(g => g.Configuration).Returns(config);

        // Create test attribute system
        this.Attributes = new TestAttributeSystem();

        // Create real AiPlayer (calls Player constructor which needs IGameContext with LoggerFactory)
        this.Player = new AiPlayer(this.MockGameContext.Object);

        // Set up attributes on the player via reflection since the setter is private.
        // Player.Attributes is ItemAwareAttributeSystem (sealed), so TestAttributeSystem
        // won't match. Skip if the type is incompatible — Attributes can be null safely.
        var attrProperty = typeof(Player).GetProperty("Attributes", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (attrProperty?.SetMethod is not null && attrProperty.PropertyType.IsInstanceOfType(this.Attributes))
        {
            attrProperty.SetValue(this.Player, this.Attributes);
        }

        // Default HP/MaxHP setup
        this.SetupHp(1000, 1000);

        var gameAdapterMock = new Mock<IGameAdapter>();
        gameAdapterMock.Setup(a => a.GetPlayerPosition()).Returns(new Point(100, 100));
        this.Context = new BehaviorContext(this.Player)
        {
            GameAdapter = gameAdapterMock.Object,
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
    /// Sets the player's current and maximum HP in the attribute system.
    /// </summary>
    protected void SetupHp(int currentHp, int maxHp)
    {
        this.Attributes.Set(Stats.CurrentHealth, currentHp);
        this.Attributes.Set(Stats.MaximumHealth, maxHp);
    }

    /// <summary>
    /// Sets the player's current and maximum MP in the attribute system.
    /// </summary>
    protected void SetupMp(int currentMp, int maxMp)
    {
        this.Attributes.Set(Stats.CurrentMana, currentMp);
        this.Attributes.Set(Stats.MaximumMana, maxMp);
    }

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
