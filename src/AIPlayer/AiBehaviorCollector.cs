// <copyright file="AiBehaviorCollector.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer;

using Microsoft.Extensions.Logging;

/// <summary>
/// Collects behavior events from AI characters and feeds them into
/// the BehaviorEventStore for continuous OAPS learning.
/// Extends PBO to cover AI-generated behavior data when no human players are online.
/// </summary>
public sealed class AiBehaviorCollector
{
    private readonly BehaviorEventStore _eventStore;
    private readonly ILogger _logger;
    private readonly string _characterName;
    private int _prevLevel;
    private int _prevMapId;
    private long _prevExp;

    public AiBehaviorCollector(BehaviorEventStore eventStore, string characterName, ILogger logger)
    {
        _eventStore = eventStore;
        _characterName = characterName;
        _logger = logger;
    }

    /// <summary>Called after each tick to collect behavior events from AI actions.</summary>
    public void Collect(
        long currentExp, int currentLevel, int currentMapId,
        int posX, int posY)
    {
        var events = new List<BehaviorEvent>();

        bool killHappened = currentExp > _prevExp;
        bool leveledUp = currentLevel > _prevLevel;
        bool mapChanged = currentMapId != _prevMapId;

        if (killHappened)
        {
            events.Add(new BehaviorEvent
            {
                CharacterName = _characterName,
                EventType = BehaviorEventType.MonsterKilled,
                X = (byte)Math.Min(posX, 255),
                Y = (byte)Math.Min(posY, 255),
                MapNumber = currentMapId,
                Timestamp = DateTime.UtcNow,
            });
        }

        if (leveledUp)
        {
            events.Add(new BehaviorEvent
            {
                CharacterName = _characterName,
                EventType = BehaviorEventType.LevelUp,
                X = (byte)Math.Min(posX, 255),
                Y = (byte)Math.Min(posY, 255),
                MapNumber = currentMapId,
                Timestamp = DateTime.UtcNow,
            });
        }

        if (mapChanged)
        {
            events.Add(new BehaviorEvent
            {
                CharacterName = _characterName,
                EventType = BehaviorEventType.MapEntered,
                X = (byte)Math.Min(posX, 255),
                Y = (byte)Math.Min(posY, 255),
                MapNumber = currentMapId,
                Timestamp = DateTime.UtcNow,
            });
        }

        if (events.Count > 0)
        {
            try
            {
                foreach (var evt in events)
                {
                    _eventStore.AddEvent(evt);
                }

                _logger.LogInformation(
                    "[AiCollector] {Char}: {Count} events (kill={Kill}, lvl={Lvl}, map={Map})",
                    _characterName, events.Count,
                    events.Any(e => e.EventType == BehaviorEventType.MonsterKilled),
                    events.Any(e => e.EventType == BehaviorEventType.LevelUp),
                    events.Any(e => e.EventType == BehaviorEventType.MapEntered));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AiCollector] Save failed for {Char}", _characterName);
            }
        }

        _prevExp = currentExp;
        _prevLevel = currentLevel;
        _prevMapId = currentMapId;
    }

    /// <summary>Set initial state for change detection.</summary>
    public void SetInitialState(long exp, int level, int mapId)
    {
        _prevExp = exp;
        _prevLevel = level;
        _prevMapId = mapId;
    }
}
