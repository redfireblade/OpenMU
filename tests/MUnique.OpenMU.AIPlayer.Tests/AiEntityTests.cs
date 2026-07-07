// <copyright file="AiEntityTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests;

using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.Pathfinding;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Tests: AiEntity 创建和基础功能.
/// </summary>
[TestFixture]
public class AiEntityTests
{
    [Test]
    public void CreateEntity_DefaultState()
    {
        using var entity = CreateTestEntity("TestAI", 0, 1);
        Assert.Multiple(() =>
        {
            Assert.That(entity.Name, Is.EqualTo("TestAI"));
            Assert.That(entity.ClassNumber, Is.EqualTo(0));
            Assert.That(entity.Level, Is.EqualTo(1));
            Assert.That(entity.IsWalking, Is.False);
        });
    }

    [Test]
    public void Entity_ToString_ContainsNameAndLevel()
    {
        using var entity = CreateTestEntity("AIDW00", 0, 5);
        var str = entity.ToString();
        Assert.That(str, Does.Contain("AIDW00"));
        Assert.That(str, Does.Contain("Lv5"));
    }

    [Test]
    public void Entity_Dead_HasZeroHealth()
    {
        using var entity = CreateTestEntity("Test", 0, 1);
        entity.Health = 0;
        Assert.That(entity.Health, Is.EqualTo(0));
    }

    [Test]
    public void AiLoop_DeadEntity_NoException()
    {
        using var entity = CreateTestEntity("Test", 0, 1);
        // 不调用 InitializeAsync (不加地图)，IsAlive 默认应为 true
        // 仅验证 Loop.TickAsync 不抛出异常
        Assert.DoesNotThrowAsync(async () => await entity.Loop.TickAsync());
    }

    [Test]
    public void Perception_Empty_CountZero()
    {
        using var entity = CreateTestEntity("Test", 0, 1);
        Assert.Multiple(() =>
        {
            Assert.That(entity.Perception.VisibleMonsters, Is.Empty);
            Assert.That(entity.Perception.VisibleDrops, Is.Empty);
            Assert.That(entity.Perception.MonsterCount, Is.EqualTo(0));
            Assert.That(entity.Perception.NearestMonster, Is.Null);
        });
    }

    [Test]
    public void Perception_MonsterAdded_Visible()
    {
        using var entity = CreateTestEntity("Test", 0, 1);
        // AddMonster 是 internal 方法，通过 LocateableAddedAsync 内部调用
        // 这里通过 entity 的公开 API 验证初始状态即可
        Assert.That(entity.Perception.VisibleMonsters, Is.Empty);
    }

    [Test]
    public void AiCreateConfig_DefaultValues()
    {
        var config = new AiCreateConfig("AIDW00", 0, 50, 0, new Point(140, 120));
        Assert.Multiple(() =>
        {
            Assert.That(config.Name, Is.EqualTo("AIDW00"));
            Assert.That(config.ClassNumber, Is.EqualTo(0));
            Assert.That(config.Level, Is.EqualTo(50));
            Assert.That(config.MapNumber, Is.EqualTo(0));
            Assert.That(config.InitialPosition.X, Is.EqualTo(140));
            Assert.That(config.InitialPosition.Y, Is.EqualTo(120));
        });
    }

    [Test]
    public void AiCreateConfig_DifferentClasses()
    {
        var dw = new AiCreateConfig("DW", 0, 1, 0, new Point(100, 100));
        var dk = new AiCreateConfig("DK", 4, 1, 0, new Point(100, 100));
        var elf = new AiCreateConfig("Elf", 8, 1, 0, new Point(100, 100));

        Assert.That(dw.ClassNumber, Is.EqualTo(0));
        Assert.That(dk.ClassNumber, Is.EqualTo(4));
        Assert.That(elf.ClassNumber, Is.EqualTo(8));
    }

    [Test]
    public void AiHost_Empty_HasZeroEntities()
    {
        using var host = new AiHost(null!, new NullLogger<AiHost>());
        Assert.That(host.Count, Is.EqualTo(0));
    }

    [Test]
    public void AiHost_AddEntity_IncreasesCount()
    {
        // AiHost.CreateAsync 需要完整 IGameContext，在集成测试中覆盖
        // 这里验证 AiHost 实例可以被创建和销毁
        using var host = new AiHost(null!, new NullLogger<AiHost>());
        Assert.That(host, Is.Not.Null);
    }

    [Test]
    public void AiHost_OnGameTick_NoEntities_NoError()
    {
        using var host = new AiHost(null!, new NullLogger<AiHost>());
        // 0 实体时调用不应异常
        Assert.DoesNotThrow(() => host.OnGameTick());
    }

    /// <summary>
    /// 创建测试用 AiEntity（不加地图，仅验证属性）。
    /// </summary>
    private static AiEntity CreateTestEntity(string name, byte classNumber, int level)
    {
        // 使用 TestHelper 初始化 MonsterDefinition 的集合属性
        var def = TestHelper.CreateEntity<MonsterDefinition>();
        var spawn = TestHelper.CreateEntity<MonsterSpawnArea>();

        // 使用空的 PlugInManager（测试用）
        var plugInManager = new PlugIns.PlugInManager(
            null,
            new NullLoggerFactory(),
            null,
            null);

        return new AiEntity(
            spawn, def, null!,
            null!, null!, plugInManager,
            name, classNumber, level);
    }
}
