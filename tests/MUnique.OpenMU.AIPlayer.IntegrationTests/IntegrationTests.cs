// <copyright file="IntegrationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.IntegrationTests;

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Moq;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Views.Login;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.GameServer.MessageHandler;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Network;
using MUnique.OpenMU.Network.PlugIns;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;
using MUnique.OpenMU.TcpDebugClient;

/// <summary>
/// Integration tests that connect a TCP client to a real in-process game server.
/// </summary>
[TestFixture]
public class IntegrationTests
{
    /// <summary>
    /// Tests the full login sequence over a real TCP connection:
    /// TCP connect → Login → CharacterList → SelectCharacter → EnterWorld.
    /// </summary>
    [Test]
    public async ValueTask TcpClient_LoginSequence_CompletesSuccessfully()
    {
        // Determine a free port dynamically
        using var tempSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        tempSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var freePort = ((IPEndPoint)tempSocket.LocalEndPoint!).Port;
        tempSocket.Close();

        // --- Logger setup: writes to console (captured by NUnit) ---
        using var loggerFactory = new ConsoleLoggerFactory();

        // --- 1. Setup InMemory persistence ---
        var contextProvider = new InMemoryPersistenceContextProvider();
        var persistenceContext = contextProvider.CreateNewContext();

        // --- 2. Create GameConfiguration with one map and one character class ---
        var gameConfig = persistenceContext.CreateNew<GameConfiguration>();

        var mapDef = persistenceContext.CreateNew<GameMapDefinition>();
        mapDef.Number = 0;
        mapDef.TerrainData = new byte[ushort.MaxValue + 3];
        gameConfig.Maps.Add(mapDef);
        gameConfig.MaximumPartySize = 5;
        gameConfig.RecoveryInterval = int.MaxValue;
        gameConfig.MaximumInventoryMoney = int.MaxValue;
        gameConfig.MaximumLevel = 400;
        gameConfig.MaximumMasterLevel = 200;

        var charClass = persistenceContext.CreateNew<CharacterClass>();
        charClass.Number = 0;
        charClass.Name = "TestClass";
        AddStatAttributeDefs(charClass, persistenceContext);
        AddAttributeCombinations(charClass, persistenceContext);
        AddBaseAttributeValues(charClass, persistenceContext);
        gameConfig.CharacterClasses.Add(charClass);

        // --- 3. Create GameClientDefinition ---
        var clientDef = persistenceContext.CreateNew<GameClientDefinition>();
        clientDef.Season = 6;
        clientDef.Episode = 3;
        clientDef.Language = ClientLanguage.English;
        clientDef.Description = "Season 6 Episode 3 Test Client";
        clientDef.Version = [6, 0, 3, 0, 0];
        clientDef.Serial = [];

        // --- 4. Create GameServerConfiguration with the map ---
        var serverConfig = persistenceContext.CreateNew<GameServerConfiguration>();
        serverConfig.MaximumPlayers = 100;
        serverConfig.Maps.Add(mapDef);

        // --- 5. Create GameServerEndpoint ---
        var endpoint = persistenceContext.CreateNew<GameServerEndpoint>();
        endpoint.NetworkPort = freePort;
        endpoint.Client = clientDef;

        // --- 6. Create GameServerDefinition ---
        var serverDef = persistenceContext.CreateNew<GameServerDefinition>();
        serverDef.ServerID = 1;
        serverDef.Description = "Integration Test Server";
        serverDef.GameConfiguration = gameConfig;
        serverDef.ServerConfiguration = serverConfig;
        serverDef.Endpoints.Add(endpoint);

        // --- 7. Create mocks for external dependencies ---
        var guildServerMock = new Mock<IGuildServer>();
        var eventPublisherMock = new Mock<IEventPublisher>();
        var loginServerMock = new Mock<ILoginServer>();
        var friendServerMock = new Mock<IFriendServer>();
        var stateObserverMock = new Mock<IGameServerStateObserver>();
        var addressResolverMock = new Mock<IIpAddressResolver>();

        loginServerMock.Setup(l => l.TryLoginAsync(It.IsAny<string>(), It.IsAny<byte>()))
            .ReturnsAsync(true);
        loginServerMock.Setup(l => l.LogOffAsync(It.IsAny<string>(), It.IsAny<byte>()))
            .Returns(ValueTask.CompletedTask);

        addressResolverMock.Setup(r => r.ResolveIPv4Async())
            .ReturnsAsync(IPAddress.Loopback);

        // --- 8. Create PlugInManager and discover encryption plugins ---
        // Force-load assemblies containing [PlugIn]-annotated types so they're
        // visible to DiscoverAllPlugIns() before plugin discovery runs.
        _ = typeof(DefaultTcpGameServerListener).Assembly;                   // GameServer (packet handlers)
        _ = typeof(Season6Episode3NetworkEncryptionFactoryPlugIn).Assembly;  // Network   (encryption)

        var plugInManager = new PlugInManager(null, loggerFactory, null, null);
        plugInManager.DiscoverAndRegisterPlugIns();

        // -- Plugin discovery diagnostics --
        var logger = loggerFactory.CreateLogger<IntegrationTests>();
        var packetHandlerPlugIns = plugInManager.GetKnownPlugInsOf<IPacketHandlerPlugIn>().ToList();
        logger.LogInformation("Discovered {Count} IPacketHandlerPlugIn plugins.", packetHandlerPlugIns.Count);

        var subPacketHandlerPlugIns = plugInManager.GetKnownPlugInsOf<ISubPacketHandlerPlugIn>().ToList();
        logger.LogInformation("Discovered {Count} ISubPacketHandlerPlugIn plugins.", subPacketHandlerPlugIns.Count);

        if (packetHandlerPlugIns.Count == 0)
        {
            throw new InvalidOperationException("No IPacketHandlerPlugIn plugins discovered. Login will time out.");
        }

        if (subPacketHandlerPlugIns.Count == 0)
        {
            throw new InvalidOperationException("No ISubPacketHandlerPlugIn plugins discovered. Login will time out.");
        }

        // --- 9. Register client version mapping ---
        ClientVersionResolver.Register([6, 0, 3, 0, 0], new ClientVersion(6, 3, ClientLanguage.English));

        // --- 10. Create GameServerContext ---
        var mapInitializer = new MapInitializer(
            gameConfig,
            loggerFactory.CreateLogger<MapInitializer>(),
            NullDropGenerator.Instance,
            new ConfigurationChangeMediator());

        var gameContext = new GameServerContext(
            serverDef,
            guildServerMock.Object,
            eventPublisherMock.Object,
            loginServerMock.Object,
            friendServerMock.Object,
            contextProvider,
            mapInitializer,
            loggerFactory,
            plugInManager,
            NullDropGenerator.Instance,
            new ConfigurationChangeMediator());

        mapInitializer.PlugInManager = gameContext.PlugInManager;
        mapInitializer.PathFinderPool = gameContext.PathFinderPool;

        // --- 11. Create and start the TCP listener ---
        var serverInfo = new ServerInfo(1, "Test Server", 0, 100);
        var listener = new DefaultTcpGameServerListener(
            endpoint,
            serverInfo,
            gameContext,
            stateObserverMock.Object,
            addressResolverMock.Object,
            loggerFactory);

        listener.PlayerConnected += async e =>
        {
            var player = e.ConntectedPlayer;
            await gameContext.AddPlayerAsync(player).ConfigureAwait(false);
            await player.InvokeViewPlugInAsync<IShowLoginWindowPlugIn>(
                p => p.ShowLoginWindowAsync()).ConfigureAwait(false);
            await player.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen)
                .ConfigureAwait(false);
            e.ConntectedPlayer.PlayerDisconnected += async remotePlayer =>
            {
                if (!remotePlayer.IsTemplatePlayer)
                {
                    await gameContext.RemovePlayerAsync(remotePlayer).ConfigureAwait(false);
                }
            };
        };

        await listener.StartAsync().ConfigureAwait(false);

        // --- 12. Create account + character in persistence ---
        var account = persistenceContext.CreateNew<Account>();
        account.LoginName = "test1";
        account.PasswordHash = BCrypt.Net.BCrypt.HashPassword("test1");
        account.State = AccountState.Normal;
        account.Vault = persistenceContext.CreateNew<ItemStorage>();

        var character = persistenceContext.CreateNew<Character>();
        character.Name = "TestBot1";
        character.CharacterClass = charClass;
        character.Experience = 0;
        character.LevelUpPoints = 5;
        character.CurrentMap = mapDef;
        character.PositionX = 100;
        character.PositionY = 100;
        character.Inventory = persistenceContext.CreateNew<ItemStorage>();

        foreach (var attrDef in charClass.StatAttributes)
        {
            character.Attributes.Add(
                persistenceContext.CreateNew<StatAttribute>(attrDef.Attribute!, attrDef.BaseValue));
        }

        account.Characters.Add(character);
        await persistenceContext.SaveChangesAsync().ConfigureAwait(false);

        try
        {
            // --- 13. Connect with TcpDebugClient and run login sequence ---
            var tcpConfig = new TcpDebugClientConfig
            {
                Server = "127.0.0.1",
                Port = freePort,
                Username = "test1",
                Password = "test1",
                CharacterName = "TestBot1",
            };

            await using var tcpClient = new TcpDebugClient(tcpConfig, loggerFactory);
            await tcpClient.ConnectAsync().ConfigureAwait(false);
            var success = await tcpClient.RunLoginSequenceAsync().ConfigureAwait(false);

            Assert.That(success, Is.True);
            Assert.That(tcpClient.LoginFlow?.SelectedCharacterName, Is.EqualTo("TestBot1"));
        }
        finally
        {
            // --- 14. Cleanup ---
            listener.Stop();
        }
    }

    private static void AddStatAttributeDefs(CharacterClass charClass, IContext ctx)
    {
        var defs = new (AttributeDefinition Attr, float BaseValue, bool Increasable)[]
        {
            (Stats.Level, 1, false),
            (Stats.BaseStrength, 28, true),
            (Stats.BaseAgility, 20, true),
            (Stats.BaseVitality, 25, true),
            (Stats.BaseEnergy, 10, true),
            (Stats.CurrentHealth, 0, false),
            (Stats.CurrentMana, 0, false),
            (Stats.CurrentShield, 0, false),
            (Stats.Resets, 0, false),
            (Stats.PointsPerLevelUp, 5, false),
            (Stats.PointsPerReset, 0, false),
        };

        foreach (var (attr, baseVal, inc) in defs)
        {
            charClass.StatAttributes.Add(ctx.CreateNew<StatAttributeDefinition>(attr, baseVal, inc));
        }
    }

    private static void AddAttributeCombinations(CharacterClass charClass, IContext ctx)
    {
        var combos = new (AttributeDefinition Target, float Operand, AttributeDefinition Input)[]
        {
            (Stats.TotalStrength, 1, Stats.BaseStrength),
            (Stats.TotalAgility, 1, Stats.BaseAgility),
            (Stats.TotalVitality, 1, Stats.BaseVitality),
            (Stats.TotalEnergy, 1, Stats.BaseEnergy),
            (Stats.MaximumAbility, 1, Stats.TotalEnergy),
            (Stats.MaximumAbility, 0.3f, Stats.TotalVitality),
            (Stats.MaximumAbility, 0.2f, Stats.TotalAgility),
            (Stats.MaximumAbility, 0.15f, Stats.TotalStrength),
            (Stats.MaximumShield, 1.2f, Stats.TotalEnergy),
            (Stats.MaximumShield, 1.2f, Stats.TotalVitality),
            (Stats.MaximumShield, 1.2f, Stats.TotalAgility),
            (Stats.MaximumShield, 1.2f, Stats.TotalStrength),
            (Stats.MaximumShield, 0.5f, Stats.DefenseBase),
            (Stats.MaximumMana, 1, Stats.TotalEnergy),
            (Stats.MaximumMana, 0.5f, Stats.Level),
            (Stats.MaximumHealth, 2, Stats.Level),
            (Stats.MaximumHealth, 3, Stats.TotalVitality),
        };

        foreach (var (target, operand, input) in combos)
        {
            charClass.AttributeCombinations.Add(
                ctx.CreateNew<AttributeRelationship>(target, operand, input, AggregateType.AddRaw));
        }
    }

    private static void AddBaseAttributeValues(CharacterClass charClass, IContext ctx)
    {
        var values = new (float Value, AttributeDefinition Target)[]
        {
            (10, Stats.MaximumMana),
            (35, Stats.MaximumHealth),
            (2, Stats.SkillMultiplier),
            (2, Stats.AbilityRecoveryMultiplier),
            (1, Stats.DamageReceiveDecrement),
            (1, Stats.AttackDamageIncrease),
        };

        foreach (var (val, target) in values)
        {
            charClass.BaseAttributeValues.Add(ctx.CreateNew<ConstValueAttribute>(val, target));
        }
    }
}

/// <summary>
/// A simple <see cref="ILoggerFactory"/> that writes all log output to <see cref="System.Console"/>.
/// </summary>
internal sealed class ConsoleLoggerFactory : ILoggerFactory
{
    private readonly object _lock = new();

    public void AddProvider(ILoggerProvider provider) { }

    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName, this._lock);

    public void Dispose() { }
}

internal sealed class ConsoleLogger : ILogger
{
    private readonly string _category;
    private readonly object _lock;

    public ConsoleLogger(string category, object @lock)
    {
        this._category = category;
        this._lock = @lock;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var msg = formatter(state, exception);
        if (string.IsNullOrEmpty(msg) && exception is null) return;

        lock (this._lock)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var level = logLevel.ToString()[..4];
            Console.WriteLine($"[{timestamp} {level}] {this._category}: {msg}");
            if (exception is not null)
            {
                Console.WriteLine($"  Exception: {exception}");
            }
        }
    }
}
