// <copyright file="AiHost.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;

/// <summary>
/// AI 角色管理器 — 创建、调度、销毁所有 AI 实体。
/// </summary>
public sealed class AiHost : IDisposable
{
    private readonly List<AiEntity> _entities = new();
    private readonly IGameContext _gameContext;
    private readonly ILogger<AiHost> _logger;
    private int _frameCounter;
    private int _maxPerFrame = 3;
    private int _tickInterval = 2;
    private bool _disposed;

    public IReadOnlyList<AiEntity> Entities => this._entities.AsReadOnly();
    public int Count => this._entities.Count;

    public AiHost(IGameContext gameContext, ILogger<AiHost> logger)
    {
        this._gameContext = gameContext;
        this._logger = logger;
    }

    /// <summary>分帧调度 Tick。</summary>
    public void OnGameTick()
    {
        if (this._disposed || this._entities.Count == 0) return;
        this._frameCounter++;
        if (this._frameCounter % this._tickInterval != 0) return;

        int groups = Math.Max(1, (this._entities.Count + this._maxPerFrame - 1) / this._maxPerFrame);
        int idx = (this._frameCounter / this._tickInterval) % groups;
        int start = idx * this._maxPerFrame;
        int end = Math.Min(start + this._maxPerFrame, this._entities.Count);

        for (int i = start; i < end; i++)
        {
            var e = this._entities[i];
            if (e.CurrentMap != null) _ = e.Loop.TickAsync();
        }
    }

    /// <summary>创建 AI 角色。</summary>
    public async ValueTask<AiEntity> CreateAsync(AiCreateConfig config, GameMap map)
    {
        var entity = new AiEntity(this._gameContext, config.Name, config.ClassNumber, config.Level)
        {
            InitialPosition = config.InitialPosition,
        };

        SetupAttributes(entity, config);

        this._entities.Add(entity);
        await entity.InitializeAsync(map);

        this._logger.LogInformation("[AiHost] AI {Name}(class={Class}) Lv{Level} on map {Map} at ({X},{Y})",
            config.Name, config.ClassNumber, config.Level, map.Definition.Number,
            config.InitialPosition.X, config.InitialPosition.Y);

        return entity;
    }

    private static void SetupAttributes(AiEntity entity, AiCreateConfig config)
    {
        var a = entity.Attributes;
        if (a == null) return;
        var lv = config.Level;
        a[Stats.MaximumHealth] = 50 + lv * 10;
        a[Stats.CurrentHealth] = a[Stats.MaximumHealth];
        a[Stats.MaximumMana] = 30 + lv * 5;
        a[Stats.CurrentMana] = a[Stats.MaximumMana];
        a[Stats.Level] = lv;
    }

    public async ValueTask RemoveAsync(AiEntity entity)
    {
        this._entities.Remove(entity);
        if (entity.CurrentMap is not null)
            await entity.CurrentMap.RemoveAsync(entity);
    }

    public void Dispose()
    {
        this._disposed = true;
        foreach (var e in this._entities) e.Dispose();
        this._entities.Clear();
    }
}
