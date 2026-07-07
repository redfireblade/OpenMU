// <copyright file="AiHost.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Persistence;

/// <summary>
/// AI 角色管理器 — 创建、调度、销毁所有 AI 实体。
/// 分帧处理 AI Tick：每帧处理 N 个，避免单帧阻塞。
/// </summary>
public sealed class AiHost : IDisposable
{
    private readonly List<AiEntity> _entities = new();
    private readonly IGameContext _gameContext;
    private readonly ILogger<AiHost> _logger;
    private int _frameCounter;
    private int _tickInterval = 2; // 每 2 帧处理一次 AI
    private int _maxPerFrame = 3;  // 每帧最多处理 3 个
    private bool _disposed;

    /// <summary>
    /// 获取当前管理的 AI 角色列表。
    /// </summary>
    public IReadOnlyList<AiEntity> Entities => this._entities.AsReadOnly();

    /// <summary>
    /// 获取 AI 角色数量。
    /// </summary>
    public int Count => this._entities.Count;

    public AiHost(IGameContext gameContext, ILogger<AiHost> logger)
    {
        this._gameContext = gameContext;
        this._logger = logger;
    }

    /// <summary>
    /// 由游戏主循环调用，分帧处理 AI Tick。
    /// </summary>
    public void OnGameTick()
    {
        if (this._disposed || this._entities.Count == 0) return;

        this._frameCounter++;
        if (this._frameCounter % this._tickInterval != 0) return;

        // 分帧: 每帧只处理 _maxPerFrame 个
        int totalGroups = Math.Max(1, (this._entities.Count + this._maxPerFrame - 1) / this._maxPerFrame);
        int groupIndex = (this._frameCounter / this._tickInterval) % totalGroups;
        int start = groupIndex * this._maxPerFrame;
        int end = Math.Min(start + this._maxPerFrame, this._entities.Count);

        for (int i = start; i < end; i++)
        {
            var entity = this._entities[i];
            if (entity.IsAlive && entity.CurrentMap != null)
            {
                _ = entity.Loop.TickAsync();
            }
        }
    }

    /// <summary>
    /// 创建一个 AI 角色。
    /// </summary>
    public async ValueTask<AiEntity> CreateAsync(AiCreateConfig config, GameMap map)
    {
        using var persistence = this._gameContext.PersistenceContextProvider.CreateNewPlayerContext(this._gameContext.Configuration);
        if (persistence == null)
            throw new InvalidOperationException("Cannot create persistence context");

        // 创建 MonsterDefinition
        var definition = persistence.CreateNew<MonsterDefinition>() ?? throw new InvalidOperationException("Cannot create MonsterDefinition");
        definition.Number = 2; // 使用客户端已知的怪物编号(Budge Dragon)
        definition.Designation = config.Name;
        definition.ObjectKind = NpcObjectKind.Monster;
        definition.NpcWindow = DataModel.Configuration.NpcWindow.Undefined;
        definition.AttackRange = 1;
        definition.ViewRange = 20;

        // 创建 MonsterSpawnArea
        var spawn = persistence.CreateNew<MonsterSpawnArea>();
        spawn.MonsterDefinition = definition;
        spawn.X1 = spawn.X2 = (byte)config.InitialPosition.X;
        spawn.Y1 = spawn.Y2 = (byte)config.InitialPosition.Y;
        spawn.Quantity = 1;
        spawn.SpawnTrigger = SpawnTrigger.Automatic;

        var entity = new AiEntity(
            spawn, definition, map,
            null!, null!, this._gameContext.PlugInManager,
            config.Name, config.ClassNumber, config.Level);

        // 设置基础属性
        SetupAttributes(entity, config);

        // 设置出生坐标
        entity.InitialPosition = config.InitialPosition;

        this._entities.Add(entity);
        await entity.InitializeAsync();

        this._logger.LogInformation("[AiHost] 创建 AI 角色 {Name}(class={Class}) Lv.{Level} on map {Map} at ({X},{Y})",
            config.Name, config.ClassNumber, config.Level, map.Definition.Number,
            config.InitialPosition.X, config.InitialPosition.Y);

        return entity;
    }

    private static void SetupAttributes(AiEntity entity, AiCreateConfig config)
    {
        // 设置基础战斗属性
        var attrs = entity.Attributes;
        if (attrs == null) return;

        var level = config.Level;
        attrs[Stats.MaximumHealth] = 50 + level * 10;
        attrs[Stats.CurrentHealth] = attrs[Stats.MaximumHealth];
        attrs[Stats.MaximumMana] = 30 + level * 5;
        attrs[Stats.CurrentMana] = attrs[Stats.MaximumMana];
        attrs[Stats.Level] = level;
        attrs[Stats.BaseStrength] = 20 + level * 2;
        attrs[Stats.BaseAgility] = 20 + level * 2;
        attrs[Stats.BaseVitality] = 15 + level * 1;
        attrs[Stats.BaseEnergy] = 15 + level * 1;
    }

    /// <summary>
    /// 移除一个 AI 角色。
    /// </summary>
    public async ValueTask RemoveAsync(AiEntity entity)
    {
        this._entities.Remove(entity);
        if (entity.CurrentMap is not null)
            await entity.CurrentMap.RemoveAsync(entity);
        entity.Dispose();
    }

    /// <summary>
    /// 移除所有 AI 角色。
    /// </summary>
    public async ValueTask ClearAsync()
    {
        foreach (var e in this._entities.ToList())
            await this.RemoveAsync(e);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this._disposed = true;
        foreach (var e in this._entities)
            e.Dispose();
        this._entities.Clear();
    }
}
