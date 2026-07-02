// <copyright file="TestHelper.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Helper methods for creating AI player test instances.
/// AiPlayer is sealed, so Moq cannot mock it directly.
/// This helper provides factory methods for test scenarios.
/// </summary>
internal static class TestHelper
{
    /// <summary>
    /// Creates a minimal <see cref="AiPlayer"/> instance with a mocked game context.
    /// The player is not initialized — only the constructor is called with minimal dependencies.
    /// </summary>
    public static AiPlayer CreateAiPlayer()
    {
        var gameContextMock = CreateMinimalGameContextMock();
        return new AiPlayer(gameContextMock.Object);
    }

    /// <summary>
    /// Creates a <see cref="Mock{IGameContext}"/> with minimal required setup
    /// so the <see cref="Player"/> constructor does not throw.
    /// </summary>
    public static Mock<IGameContext> CreateMinimalGameContextMock()
    {
        var gameContextMock = new Mock<IGameContext>();

        // Player constructor needs LoggerFactory to create its logger
        var mockLogger = new Mock<ILogger<Player>>();
        var loggerFactoryMock = new Mock<ILoggerFactory>();
        loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(mockLogger.Object);
        gameContextMock.Setup(c => c.LoggerFactory).Returns(loggerFactoryMock.Object);

        // Player constructor needs PersistenceContextProvider
        gameContextMock.Setup(c => c.PersistenceContextProvider)
            .Returns(new InMemoryPersistenceContextProvider());

        // Optional but commonly needed
        gameContextMock.Setup(c => c.Configuration).Returns(CreateGameConfiguration());
        gameContextMock.Setup(c => c.PlugInManager)
            .Returns(new PlugInManager(null, new NullLoggerFactory(), null, null));

        return gameContextMock;
    }

    /// <summary>
    /// Creates a <see cref="Mock{IGameContext}"/> with a <see cref="GameConfiguration"/>
    /// that contains a basic character class and map, suitable for lifecycle tests.
    /// </summary>
    public static Mock<IGameContext> CreateLifecycleGameContextMock()
    {
        var mock = CreateMinimalGameContextMock();

        var config = mock.Object.Configuration!;
        config.CharacterClasses.Add(CreateCharacterClass(0, "Dark Wizard"));

        config.Maps.Add(new GameMapDefinition
        {
            Number = 0,
            Name = "Lorencia",
            TerrainData = Array.Empty<byte>(),
        });

        return mock;
    }

    /// <summary>
    /// Creates a <see cref="GameConfiguration"/> with all entity collections properly initialized.
    /// Direct use of <c>new GameConfiguration()</c> results in null collections because
    /// the DataModel uses <c>= null!</c> defaults without constructor initialization.
    /// This method uses reflection to initialize all <see cref="ICollection{T}"/> properties.
    /// </summary>
    public static GameConfiguration CreateGameConfiguration()
    {
        var config = new GameConfiguration();
        InitializeCollections(config);
        return config;
    }

    /// <summary>
    /// Initializes all <see cref="ICollection{T}"/> properties of an entity that have null values.
    /// The OpenMU DataModel uses <c>= null!</c> defaults for collection properties that are meant
    /// to be initialized by the persistence layer, so we need this for tests.
    /// </summary>
    private static void InitializeCollections(object entity)
    {
        var type = entity.GetType();
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        {
            if (!prop.Name.Contains("Site") && prop.PropertyType.IsGenericType && prop.PropertyType.GetGenericTypeDefinition() == typeof(ICollection<>))
            {
                var value = prop.GetValue(entity);
                if (value is null)
                {
                    var elementType = prop.PropertyType.GetGenericArguments()[0];
                    var listType = typeof(List<>).MakeGenericType(elementType);
                    prop.SetValue(entity, Activator.CreateInstance(listType));
                }
            }
        }
    }

    /// <summary>
    /// Creates a <see cref="CharacterClass"/> with an initialized <see cref="CharacterClass.StatAttributes"/> collection.
    /// Direct property setter is inaccessible (protected), so we initialize collections via reflection.
    /// </summary>
    public static CharacterClass CreateCharacterClass(byte number, string name)
    {
        var characterClass = new CharacterClass
        {
            Number = number,
            Name = name,
            CanGetCreated = true,
        };

        InitializeCollections(characterClass);
        return characterClass;
    }

    /// <summary>
    /// Creates an <see cref="AiPlayer"/> with a mock <see cref="Player.Inventory"/> set via reflection.
    /// The player can be used with <see cref="RuleEngine.Evaluate"/> which requires Inventory to be non-null.
    /// </summary>
    public static AiPlayer CreateAiPlayerWithInventory()
    {
        var player = CreateAiPlayer();
        var inventoryMock = new Mock<IInventoryStorage>();
        var inventoryProperty = typeof(Player).GetProperty("Inventory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (inventoryProperty?.SetMethod is not null)
        {
            inventoryProperty.SetValue(player, inventoryMock.Object);
        }

        return player;
    }

    /// <summary>
    /// Creates an entity of type <typeparamref name="T"/> with all <see cref="ICollection{T}"/>
    /// properties initialized. Use this instead of <c>new T()</c> for any OpenMU DataModel entity
    /// that has collection properties.
    /// </summary>
    public static T CreateEntity<T>()
        where T : new()
    {
        var entity = new T();
        InitializeCollections(entity);
        return entity;
    }
}
