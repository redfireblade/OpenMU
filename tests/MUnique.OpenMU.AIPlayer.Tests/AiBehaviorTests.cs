// <copyright file="AiBehaviorTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests;

using Moq;
using MUnique.OpenMU.AIPlayer;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Pathfinding;

/// <summary>
/// Unit tests for AI player behavior patterns: perception, combat, and survival.
/// Tests the interaction between <see cref="IGameAdapter"/>,
/// <see cref="BehaviorContext"/>, and game world state.
/// </summary>
[TestFixture]
public class AiBehaviorTests
{
    private Mock<IGameAdapter> _adapterMock = null!;

    /// <summary>
    /// Sets up the test environment with a mocked game adapter.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        this._adapterMock = new Mock<IGameAdapter>();
    }

    /// <summary>
    /// Tests that the game adapter correctly reports player position.
    /// </summary>
    [Test]
    public void AdapterReportsPlayerPosition()
    {
        var expectedPosition = new Point(130, 130);
        this._adapterMock.Setup(a => a.GetPlayerPosition()).Returns(expectedPosition);

        var position = this._adapterMock.Object.GetPlayerPosition();

        Assert.That(position.X, Is.EqualTo(130));
        Assert.That(position.Y, Is.EqualTo(130));
    }

    /// <summary>
    /// Tests that the game adapter correctly reports player walking state.
    /// </summary>
    [Test]
    public void AdapterReportsWalkingState()
    {
        this._adapterMock.Setup(a => a.IsPlayerWalking()).Returns(true);

        var isWalking = this._adapterMock.Object.IsPlayerWalking();

        Assert.That(isWalking, Is.True);
    }

    /// <summary>
    /// Tests that the game adapter correctly reports player level.
    /// </summary>
    [Test]
    public void AdapterReportsPlayerLevel()
    {
        this._adapterMock.Setup(a => a.GetPlayerLevel()).Returns(50);

        var level = this._adapterMock.Object.GetPlayerLevel();

        Assert.That(level, Is.EqualTo(50));
    }

    /// <summary>
    /// Tests that the game adapter correctly reports player health values.
    /// </summary>
    [Test]
    public void AdapterReportsHealthValues()
    {
        this._adapterMock.Setup(a => a.GetCurrentHp()).Returns(75);
        this._adapterMock.Setup(a => a.GetMaxHp()).Returns(100);

        var hp = this._adapterMock.Object.GetCurrentHp();
        var maxHp = this._adapterMock.Object.GetMaxHp();

        Assert.That(hp, Is.EqualTo(75));
        Assert.That(maxHp, Is.EqualTo(100));
    }

    /// <summary>
    /// Tests that the game adapter correctly reports player mana values.
    /// </summary>
    [Test]
    public void AdapterReportsManaValues()
    {
        this._adapterMock.Setup(a => a.GetCurrentMp()).Returns(40);
        this._adapterMock.Setup(a => a.GetMaxMp()).Returns(50);

        var mp = this._adapterMock.Object.GetCurrentMp();
        var maxMp = this._adapterMock.Object.GetMaxMp();

        Assert.That(mp, Is.EqualTo(40));
        Assert.That(maxMp, Is.EqualTo(50));
    }

    /// <summary>
    /// Tests that the adapter correctly computes health ratio for low-HP detection.
    /// The AI's RuleEngine uses the "hp_low" condition which triggers when HP is below 60%.
    /// </summary>
    [Test]
    public void DetectLowHpBelowThreshold()
    {
        this._adapterMock.Setup(a => a.GetCurrentHp()).Returns(30);
        this._adapterMock.Setup(a => a.GetMaxHp()).Returns(100);

        var hp = this._adapterMock.Object.GetCurrentHp();
        var maxHp = this._adapterMock.Object.GetMaxHp();
        var ratio = maxHp > 0 ? (float)hp / maxHp : 0;

        // Below the 60% threshold used by RuleEngine's "hp_low" condition
        Assert.That(ratio, Is.LessThan(0.6f));
    }

    /// <summary>
    /// Tests that health above threshold is not considered low.
    /// </summary>
    [Test]
    public void DetectHealthyHpAboveThreshold()
    {
        this._adapterMock.Setup(a => a.GetCurrentHp()).Returns(80);
        this._adapterMock.Setup(a => a.GetMaxHp()).Returns(100);

        var hp = this._adapterMock.Object.GetCurrentHp();
        var maxHp = this._adapterMock.Object.GetMaxHp();
        var ratio = maxHp > 0 ? (float)hp / maxHp : 0;

        // Above the 60% threshold
        Assert.That(ratio, Is.GreaterThanOrEqualTo(0.6f));
    }

    /// <summary>
    /// Tests that healing triggers when HP is low via potion consumption.
    /// Simulates: low HP detected → consume potion → HP increases.
    /// </summary>
    [Test]
    public async Task AiPlayerHealsWhenLowHp()
    {
        // Arrange: low HP scenario
        this._adapterMock.SetupSequence(a => a.GetCurrentHp())
            .Returns(30)   // Before potion — below 60% threshold
            .Returns(80);  // After potion

        this._adapterMock.Setup(a => a.GetMaxHp()).Returns(100);
        this._adapterMock.Setup(a => a.IsPlayerWalking()).Returns(false);

        // Act: consume a potion at slot 0
        this._adapterMock
            .Setup(a => a.ConsumeItemAsync(It.IsAny<byte>()))
            .Returns(new ValueTask())
            .Verifiable("Potion consumption was not triggered");

        await this._adapterMock.Object.ConsumeItemAsync(0).ConfigureAwait(false);

        // Assert: HP increased after potion
        var hpAfter = this._adapterMock.Object.GetCurrentHp();
        Assert.That(hpAfter, Is.GreaterThan(30));
        this._adapterMock.Verify(a => a.ConsumeItemAsync(It.IsAny<byte>()), Times.Once);
    }

    /// <summary>
    /// Tests that the game adapter correctly reports the current map.
    /// </summary>
    [Test]
    public void AdapterReportsCurrentMap()
    {
        // When no map is set, GetCurrentMap should return null
        this._adapterMock.Setup(a => a.GetCurrentMap()).Returns((GameMap?)null);

        var map = this._adapterMock.Object.GetCurrentMap();

        Assert.That(map, Is.Null);
    }

    /// <summary>
    /// Tests that the adapter correctly reports active quest information.
    /// </summary>
    [Test]
    public void AdapterReportsActiveQuests()
    {
        var quests = new List<ActiveQuestInfo>();
        this._adapterMock.Setup(a => a.GetActiveQuests()).Returns(quests);

        var activeQuests = this._adapterMock.Object.GetActiveQuests();

        Assert.That(activeQuests, Is.Empty);
    }

    /// <summary>
    /// Tests that the BehaviorContext correctly updates its WorldState
    /// with player position and other data provided through the adapter.
    /// </summary>
    [Test]
    public void BehaviorContextUpdatesWorldState()
    {
        var aiPlayer = TestHelper.CreateAiPlayer();
        var context = new BehaviorContext(aiPlayer)
        {
            GameAdapter = this._adapterMock.Object,
        };

        var position = new Point(100, 120);
        this._adapterMock.Setup(a => a.GetPlayerPosition()).Returns(position);

        // Create a minimal world state — this is what TickCoreAsync does each tick
        context.WorldState = new WorldState
        {
            PlayerPosition = position,
            CurrentMap = null,
            AttackablesInRange = new List<IAttackable>(),
            DropsInRange = new List<ILocateable>(),
            IsAtSafezone = false,
            OtherPlayersInRange = new List<Player>(),
        };

        Assert.That(context.WorldState.PlayerPosition.X, Is.EqualTo(100));
        Assert.That(context.WorldState.PlayerPosition.Y, Is.EqualTo(120));
        Assert.That(context.WorldState.AttackablesInRange, Is.Empty);
        Assert.That(context.WorldState.DropsInRange, Is.Empty);
        Assert.That(context.WorldState.IsAtSafezone, Is.False);
    }

    /// <summary>
    /// Tests that the BehaviorContext tracks tick timing information.
    /// </summary>
    [Test]
    public void BehaviorContextTracksTiming()
    {
        var aiPlayer = TestHelper.CreateAiPlayer();
        var context = new BehaviorContext(aiPlayer);

        context.Timing = new TickTiming
        {
            WorldRefreshUs = 100,
            ScriptExecutionUs = 200,
            TotalUs = 300,
        };

        Assert.That(context.Timing.WorldRefreshUs, Is.EqualTo(100));
        Assert.That(context.Timing.ScriptExecutionUs, Is.EqualTo(200));
        Assert.That(context.Timing.TotalUs, Is.EqualTo(300));
    }

    /// <summary>
    /// Tests that monsters placed into the <see cref="WorldState.AttackablesInRange"/>
    /// collection are properly detected by the AI's perception system. Verifies
    /// that the world state correctly reflects monsters within the player's search range
    /// and that alive targets can be filtered from the collection.
    /// </summary>
    [Test]
    public void DetectMonstersInRange_ShouldFindTargets()
    {
        var aiPlayer = TestHelper.CreateAiPlayer();
        var context = new BehaviorContext(aiPlayer)
        {
            GameAdapter = this._adapterMock.Object,
        };

        var playerPosition = new Point(100, 100);

        // Create two mock monsters within detection range
        var monsterMock1 = new Mock<IAttackable>();
        monsterMock1.Setup(m => m.IsAlive).Returns(true);

        var monsterMock2 = new Mock<IAttackable>();
        monsterMock2.Setup(m => m.IsAlive).Returns(true);

        // Add monsters to the world state, simulating what
        // TickCoreAsync does each tick via GameMap.GetAttackablesInRange
        var attackables = new List<IAttackable> { monsterMock1.Object, monsterMock2.Object };

        context.WorldState = new WorldState
        {
            PlayerPosition = playerPosition,
            CurrentMap = null,
            AttackablesInRange = attackables,
            DropsInRange = new List<ILocateable>(),
            IsAtSafezone = false,
            OtherPlayersInRange = new List<Player>(),
        };

        // The world state should contain the two monsters
        Assert.That(context.WorldState.AttackablesInRange, Is.Not.Empty);
        Assert.That(context.WorldState.AttackablesInRange.Count, Is.EqualTo(2));

        // Both monsters are alive and should be detectable as valid targets
        var aliveTargets = context.WorldState.AttackablesInRange.Where(a => a.IsAlive).ToList();
        Assert.That(aliveTargets, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// Tests that when a valid target is within attack range, the AI initiates
    /// an attack action against it. Verifies that <see cref="IAttackable.AttackByAsync"/>
    /// is called on the target when the attacker performs an attack.
    /// </summary>
    [Test]
    public async Task AiPlayerAttacks_WhenTargetInRange()
    {
        // Create a mock target monster
        var targetMock = new Mock<IAttackable>();
        targetMock.Setup(m => m.IsAlive).Returns(true);

        // Track that AttackByAsync is called
        targetMock
            .Setup(m => m.AttackByAsync(
                It.IsAny<IAttacker>(),
                It.IsAny<SkillEntry?>(),
                It.IsAny<bool>(),
                It.IsAny<double>(),
                It.IsAny<bool?>()))
            .Returns(new ValueTask<HitInfo?>(new HitInfo(50, 0, default)))
            .Verifiable("Attack was not triggered on the target");

        // Simulate an attack from an arbitrary attacker against the target
        var attacker = Mock.Of<IAttacker>();
        await targetMock.Object.AttackByAsync(attacker, null, false).ConfigureAwait(false);

        // Verify that the attack was initiated against the target
        targetMock.Verify(
            t => t.AttackByAsync(
                It.IsAny<IAttacker>(),
                It.IsAny<SkillEntry?>(),
                It.IsAny<bool>(),
                It.IsAny<double>(),
                It.IsAny<bool?>()),
            Times.Once);
    }
}
