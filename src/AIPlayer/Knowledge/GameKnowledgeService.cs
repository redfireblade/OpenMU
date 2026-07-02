// <copyright file="GameKnowledgeService.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;
using MUnique.OpenMU.GameLogic.Attributes;

/// <summary>
/// Global game knowledge service that pre-loads all game world knowledge from <see cref="GameConfiguration"/>
/// at construction time and provides convenient query interfaces for AI characters.
/// </summary>
public sealed class GameKnowledgeService
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GameKnowledgeService"/> class.
    /// Scans the entire <paramref name="config"/> and populates all knowledge dictionaries.
    /// </summary>
    /// <param name="config">The game configuration.</param>
    /// <param name="logger">The logger.</param>
    public GameKnowledgeService(GameConfiguration config, ILogger logger)
    {
        this._logger = logger;

        var maps = this.LoadMaps(config);
        var monsters = this.LoadMonsters(config, maps);
        var items = this.LoadItems(config);
        var npcs = this.LoadNpcs(config, maps);
        var quests = this.LoadQuests(config, monsters);
        var miniGames = this.LoadMiniGameEvents(config);
        var dropSources = this.LoadDropSources(config, monsters);
        var mapConnections = this.LoadMapConnections(config, maps);

        this.Maps = maps;
        this.Monsters = monsters;
        this.Items = items;
        this.Npcs = npcs;
        this.Quests = quests;
        this.MiniGameEvents = miniGames;
        this.DropSources = dropSources;
        this._mapConnections = mapConnections;

        this._logger.LogInformation(
            "[GameKnowledge] 加载完成: {MapCount} 地图, {MonsterCount} 怪物/NPC, {ItemCount} 物品, {NpcCount} NPC, {QuestCount} 任务, {EventCount} 事件, {DropCount} 掉落来源, {ConnCount} 地图连接",
            maps.Count,
            monsters.Count,
            items.Count,
            npcs.Count,
            quests.Count,
            miniGames.Count,
            dropSources.Count,
            mapConnections.Count);

        // Build knowledge graph from all loaded data
        var kgBuilder = new KnowledgeGraph.KnowledgeGraphBuilder(this._logger);
        this.KnowledgeGraph = kgBuilder.Build(config, this);
    }

    /// <summary>Gets all maps, indexed by map number.</summary>
    public IReadOnlyDictionary<int, MapInfo> Maps { get; }

    /// <summary>Gets all monsters, indexed by monster number.</summary>
    public IReadOnlyDictionary<int, MonsterInfo> Monsters { get; }

    /// <summary>Gets all items, indexed by (Group, Number).</summary>
    public IReadOnlyDictionary<(int Group, int Number), ItemInfo> Items { get; }

    /// <summary>Gets all NPCs, indexed by monster number.</summary>
    public IReadOnlyDictionary<int, NpcInfo> Npcs { get; }

    /// <summary>Gets all quests, indexed by (Group, Number).</summary>
    public IReadOnlyDictionary<(int Group, int Number), QuestInfo> Quests { get; }

    /// <summary>Gets all mini game events, indexed by (Type, GameLevel).</summary>
    public IReadOnlyDictionary<(MiniGameType, int), MiniGameEventInfo> MiniGameEvents { get; }

    /// <summary>Gets all drop sources.</summary>
    public IReadOnlyList<DropSourceInfo> DropSources { get; }

    /// <summary>Gets the knowledge graph, built from game configuration data.</summary>
    /// <remarks>
    /// Provides a graph-based representation of the game world including map connectivity,
    /// monster-item drop relationships, quest dependencies, and other semantic links.
    /// May be <c>null</c> if the knowledge graph builder has not been registered.
    /// </remarks>
    public KnowledgeGraph.KnowledgeGraph? KnowledgeGraph { get; private set; }

    private IReadOnlyList<MapConnectionInfo> _mapConnections;

    /// <summary>
    /// Gets all drop sources for a specific item.
    /// </summary>
    /// <param name="itemGroup">The item group.</param>
    /// <param name="itemNumber">The item number.</param>
    /// <returns>List of drop source info.</returns>
    public List<DropSourceInfo> GetDropSources(int itemGroup, int itemNumber)
    {
        return this.DropSources
            .Where(d => d.ItemGroup == itemGroup && d.ItemNumber == itemNumber)
            .OrderByDescending(d => d.DropRate)
            .ToList();
    }

    /// <summary>
    /// Gets all monsters that spawn on a specific map.
    /// </summary>
    /// <param name="mapNumber">The map number.</param>
    /// <returns>List of monster info on the map.</returns>
    public List<MonsterInfo> GetMonstersOnMap(int mapNumber)
    {
        return this.Monsters.Values
            .Where(m => m.SpawnMapNumbers.Contains(mapNumber))
            .OrderBy(m => m.Level)
            .ToList();
    }

    /// <summary>
    /// Gets the items dropped by a specific monster (via DropItemGroups).
    /// </summary>
    /// <param name="monsterNumber">The monster number.</param>
    /// <returns>List of items dropped by this monster.</returns>
    public List<ItemInfo> GetDropsOfMonster(int monsterNumber)
    {
        var itemKeys = this.DropSources
            .Where(d => d.MonsterNumber == monsterNumber)
            .Select(d => (d.ItemGroup, d.ItemNumber))
            .Distinct();

        var results = new List<ItemInfo>();
        foreach (var key in itemKeys)
        {
            if (this.Items.TryGetValue(key, out var itemInfo))
            {
                results.Add(itemInfo);
            }
        }

        return results;
    }

    /// <summary>
    /// Finds monsters within a specific level range (for recommended grinding spots).
    /// </summary>
    /// <param name="minLevel">Minimum level.</param>
    /// <param name="maxLevel">Maximum level.</param>
    /// <returns>List of (monster, map number) tuples.</returns>
    public List<(MonsterInfo Monster, int MapNumber)> FindMonstersByLevel(int minLevel, int maxLevel)
    {
        var results = new List<(MonsterInfo, int)>();
        foreach (var monster in this.Monsters.Values)
        {
            if (monster.Level >= minLevel && monster.Level <= maxLevel)
            {
                foreach (var mapNum in monster.SpawnMapNumbers)
                {
                    results.Add((monster, mapNum));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Finds maps suitable for a player level (maps whose monsters are within ±15 levels of the player).
    /// </summary>
    /// <param name="playerLevel">The player level.</param>
    /// <returns>List of recommended maps.</returns>
    public List<MapInfo> FindMapsByLevel(int playerLevel)
    {
        var minLevel = Math.Max(1, playerLevel - 20);
        var maxLevel = playerLevel + 15;

        var mapNumbers = new HashSet<int>();
        foreach (var monster in this.Monsters.Values)
        {
            if (monster.Level >= minLevel && monster.Level <= maxLevel && monster.SpawnMapNumbers.Count > 0)
            {
                foreach (var mapNum in monster.SpawnMapNumbers)
                {
                    if (this.Maps.ContainsKey(mapNum))
                    {
                        mapNumbers.Add(mapNum);
                    }
                }
            }
        }

        return mapNumbers
            .Select(n => this.Maps[n])
            .OrderBy(m => m.Number)
            .ToList();
    }

    /// <summary>
    /// Gets the ticket crafting materials for a mini game event.
    /// </summary>
    /// <param name="type">The mini game type.</param>
    /// <param name="gameLevel">The game level.</param>
    /// <returns>List of (group, number, required count) tuples.</returns>
    public List<(int Group, int Number, int RequiredCount)> GetTicketMaterials(MiniGameType type, int gameLevel)
    {
        var key = (type, gameLevel);
        if (!this.MiniGameEvents.TryGetValue(key, out var eventInfo))
        {
            return new List<(int, int, int)>();
        }

        if (eventInfo.TicketItemGroup is null || eventInfo.TicketItemNumber is null)
        {
            return new List<(int, int, int)>();
        }

        return new List<(int, int, int)>
        {
            (eventInfo.TicketItemGroup.Value, eventInfo.TicketItemNumber.Value, 1),
        };
    }

    /// <summary>
    /// Gets the map where a specific NPC is located.
    /// </summary>
    /// <param name="npcNumber">The NPC number.</param>
    /// <returns>The map number, or null if not found.</returns>
    public int? GetNpcMap(int npcNumber)
    {
        if (this.Npcs.TryGetValue(npcNumber, out var npc))
        {
            return npc.MapNumber;
        }

        return null;
    }

    /// <summary>
    /// Gets the NPC number that starts a specific quest.
    /// </summary>
    /// <param name="questGroup">The quest group.</param>
    /// <param name="questNumber">The quest number.</param>
    /// <returns>The NPC number, or null if not found.</returns>
    public int? GetQuestNpc(int questGroup, int questNumber)
    {
        var questKey = (questGroup, questNumber);
        if (this.Quests.TryGetValue(questKey, out var quest))
        {
            return quest.StartNpcNumber;
        }

        return null;
    }

    /// <summary>
    /// Gets the map connections (entrance gates) from the specified map.
    /// </summary>
    /// <param name="mapNumber">The map number.</param>
    /// <returns>List of map connections.</returns>
    public List<MapConnectionInfo> GetMapConnections(int mapNumber)
    {
        return this._mapConnections
            .Where(c => c.FromMapNumber == mapNumber)
            .ToList();
    }

    /// <summary>
    /// Gets a map's info by number.
    /// </summary>
    /// <param name="mapNumber">The map number.</param>
    /// <returns>The map info, or null if not found.</returns>
    public MapInfo? GetMap(int mapNumber)
    {
        this.Maps.TryGetValue(mapNumber, out var map);
        return map;
    }

    /// <summary>
    /// Gets a monster's info by number.
    /// </summary>
    /// <param name="monsterNumber">The monster number.</param>
    /// <returns>The monster info, or null if not found.</returns>
    public MonsterInfo? GetMonster(int monsterNumber)
    {
        this.Monsters.TryGetValue(monsterNumber, out var monster);
        return monster;
    }

    /// <summary>
    /// Gets an item's info by group and number.
    /// </summary>
    public ItemInfo? GetItem(int group, int number)
    {
        this.Items.TryGetValue((group, number), out var item);
        return item;
    }

    /// <summary>
    /// Gets an NPC's info by number.
    /// </summary>
    public NpcInfo? GetNpc(int npcNumber)
    {
        this.Npcs.TryGetValue(npcNumber, out var npc);
        return npc;
    }

    /// <summary>
    /// Gets a quest's info by group and number.
    /// </summary>
    public QuestInfo? GetQuest(int group, int number)
    {
        this.Quests.TryGetValue((group, number), out var quest);
        return quest;
    }

    /// <summary>
    /// Gets a mini game event's info by type and level.
    /// </summary>
    public MiniGameEventInfo? GetMiniGameEvent(MiniGameType type, int gameLevel)
    {
        this.MiniGameEvents.TryGetValue((type, gameLevel), out var evt);
        return evt;
    }

    private Dictionary<int, MapInfo> LoadMaps(GameConfiguration config)
    {
        var dict = new Dictionary<int, MapInfo>();
        foreach (var map in config.Maps)
        {
            if (map is null)
            {
                continue;
            }

            var monsterCount = map.MonsterSpawns
                .Where(s => s?.MonsterDefinition is not null)
                .Sum(s => (int)s!.Quantity);

            var safeZoneNumber = map.SafezoneMap?.Number ?? 0;
            var safeZoneName = map.SafezoneMap?.Name.ToString() ?? string.Empty;

            dict[map.Number] = new MapInfo(
                Number: map.Number,
                Name: map.Name.ToString() ?? string.Empty,
                ExpMultiplier: map.ExpMultiplier,
                SafeZoneMapNumber: safeZoneNumber,
                SafeZoneName: safeZoneName,
                MonsterCount: monsterCount);
        }

        return dict;
    }

    private Dictionary<int, MonsterInfo> LoadMonsters(GameConfiguration config, IReadOnlyDictionary<int, MapInfo> maps)
    {
        // Build a map of monster number -> list of map numbers it spawns on
        var monsterSpawnMaps = new Dictionary<short, List<int>>();
        foreach (var map in config.Maps)
        {
            if (map is null)
            {
                continue;
            }

            foreach (var spawn in map.MonsterSpawns)
            {
                if (spawn?.MonsterDefinition is null)
                {
                    continue;
                }

                if (!monsterSpawnMaps.TryGetValue(spawn.MonsterDefinition.Number, out var mapList))
                {
                    mapList = new List<int>();
                    monsterSpawnMaps[spawn.MonsterDefinition.Number] = mapList;
                }

                if (!mapList.Contains(map.Number))
                {
                    mapList.Add(map.Number);
                }
            }
        }

        var dict = new Dictionary<int, MonsterInfo>();
        foreach (var monster in config.Monsters)
        {
            if (monster is null)
            {
                continue;
            }

            var level = 0;
            try
            {
                level = (int)monster[Stats.Level];
            }
            catch
            {
                // No Level attribute — this is an NPC or special entity
            }

            var spawnMaps = monsterSpawnMaps.TryGetValue(monster.Number, out var mapsList)
                ? mapsList
                : new List<int>();

            var respawnSeconds = (int)monster.RespawnDelay.TotalSeconds;
            var hasCraftings = monster.ItemCraftings is { Count: > 0 };
            var hasQuests = monster.Quests is { Count: > 0 };

            dict[monster.Number] = new MonsterInfo(
                Number: monster.Number,
                Name: monster.Designation.ToString() ?? string.Empty,
                Level: level,
                ObjectKind: monster.ObjectKind.ToString(),
                NpcWindow: monster.NpcWindow.ToString(),
                AttackRange: monster.AttackRange,
                ViewRange: monster.ViewRange,
                RespawnDelaySeconds: respawnSeconds,
                HasItemCraftings: hasCraftings,
                HasQuests: hasQuests,
                SpawnMapNumbers: spawnMaps);
        }

        return dict;
    }

    private Dictionary<(int Group, int Number), ItemInfo> LoadItems(GameConfiguration config)
    {
        var dict = new Dictionary<(int, int), ItemInfo>();
        foreach (var item in config.Items)
        {
            if (item is null)
            {
                continue;
            }

            dict[(item.Group, item.Number)] = new ItemInfo(
                Group: item.Group,
                Number: item.Number,
                Name: item.Name.ToString() ?? string.Empty,
                DropLevel: item.DropLevel,
                ItemSlot: item.ItemSlot?.ToString(),
                Width: item.Width,
                Height: item.Height);
        }

        return dict;
    }

    private Dictionary<int, NpcInfo> LoadNpcs(GameConfiguration config, IReadOnlyDictionary<int, MapInfo> maps)
    {
        // Build a map of monster number -> first spawn area coordinates
        var npcSpawns = new Dictionary<short, (int MapNumber, int X, int Y, byte Radius)>();
        foreach (var map in config.Maps)
        {
            if (map is null)
            {
                continue;
            }

            foreach (var spawn in map.MonsterSpawns)
            {
                if (spawn?.MonsterDefinition is null)
                {
                    continue;
                }

                var monsterNum = spawn.MonsterDefinition.Number;
                if (!npcSpawns.ContainsKey(monsterNum))
                {
                    var spawnX = (spawn.X1 + spawn.X2) / 2;
                    var spawnY = (spawn.Y1 + spawn.Y2) / 2;
                    var radius = spawn.IsPoint() ? (byte)0 : (byte)Math.Max(1, Math.Abs(spawn.X2 - spawn.X1) / 2);
                    npcSpawns[monsterNum] = ((int)map.Number, spawnX, spawnY, radius);
                }
            }
        }

        var dict = new Dictionary<int, NpcInfo>();
        foreach (var monster in config.Monsters)
        {
            if (monster is null)
            {
                continue;
            }

            // Determine if this is an NPC (not a monster in the combat sense)
            var isNpc = monster.ObjectKind != NpcObjectKind.Monster
                        || monster.NpcWindow != NpcWindow.Undefined;

            if (!isNpc)
            {
                continue;
            }

            var number = monster.Number;
            var mapNumber = 0;
            var spawnX = 0;
            var spawnY = 0;
            byte spawnRadius = 0;

            if (npcSpawns.TryGetValue(number, out var spawnInfo))
            {
                mapNumber = spawnInfo.MapNumber;
                spawnX = spawnInfo.X;
                spawnY = spawnInfo.Y;
                spawnRadius = spawnInfo.Radius;
            }

            dict[number] = new NpcInfo(
                Number: number,
                Name: monster.Designation.ToString() ?? string.Empty,
                NpcWindow: monster.NpcWindow.ToString(),
                MapNumber: mapNumber,
                SpawnX: spawnX,
                SpawnY: spawnY,
                SpawnRadius: spawnRadius);
        }

        return dict;
    }

    private Dictionary<(int Group, int Number), QuestInfo> LoadQuests(
        GameConfiguration config,
        IReadOnlyDictionary<int, MonsterInfo> monsters)
    {
        var dict = new Dictionary<(int, int), QuestInfo>();
        foreach (var monster in config.Monsters)
        {
            if (monster?.Quests is null)
            {
                continue;
            }

            foreach (var quest in monster.Quests)
            {
                if (quest is null)
                {
                    continue;
                }

                var killsDesc = DescribeQuestKills(quest);
                var itemsDesc = DescribeQuestItems(quest);
                var rewardsDesc = DescribeQuestRewards(quest);

                var key = ((int)quest.Group, (int)quest.Number);
                if (dict.ContainsKey(key))
                {
                    continue; // deduplicate by (group, number)
                }

                dict[key] = new QuestInfo(
                    QuestGroup: quest.Group,
                    QuestNumber: quest.Number,
                    Name: quest.Name.ToString() ?? string.Empty,
                    MinLevel: quest.MinimumCharacterLevel,
                    MaxLevel: quest.MaximumCharacterLevel,
                    Repeatable: quest.Repeatable,
                    StartNpcNumber: quest.QuestGiver?.Number,
                    StartMoney: quest.RequiredStartMoney,
                    RequiresClientAction: quest.RequiresClientAction,
                    RequiredKillsDescription: killsDesc,
                    RequiredItemsDescription: itemsDesc,
                    RewardsDescription: rewardsDesc);
            }
        }

        return dict;
    }

    private static string DescribeQuestKills(QuestDefinition quest)
    {
        if (quest.RequiredMonsterKills is null || quest.RequiredMonsterKills.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("; ", quest.RequiredMonsterKills
            .Where(k => k is not null)
            .Select(k => $"{k.MinimumNumber}x {k.Monster?.Designation ?? "?"}"));
    }

    private static string DescribeQuestItems(QuestDefinition quest)
    {
        if (quest.RequiredItems is null || quest.RequiredItems.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("; ", quest.RequiredItems
            .Where(i => i is not null)
            .Select(i => $"{i.MinimumNumber}x {i.Item?.Name ?? "?"}"));
    }

    private static string DescribeQuestRewards(QuestDefinition quest)
    {
        if (quest.Rewards is null || quest.Rewards.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("; ", quest.Rewards
            .Where(r => r is not null)
            .Select(r =>
            {
                if (r.RewardType == QuestRewardType.Item && r.ItemReward is not null)
                {
                    return $"{r.Value}x {r.ItemReward.Definition?.Name ?? "?"}";
                }

                if (r.RewardType == QuestRewardType.Experience)
                {
                    return $"EXP {r.Value}";
                }

                if (r.RewardType == QuestRewardType.Money)
                {
                    return $"Zen {r.Value}";
                }

                if (r.RewardType == QuestRewardType.LevelUpPoints)
                {
                    return $"StatPoints {r.Value}";
                }

                if (r.RewardType is QuestRewardType.CharacterEvolutionFirstToSecond
                    or QuestRewardType.CharacterEvolutionSecondToThird)
                {
                    return r.RewardType.ToString();
                }

                if (r.RewardType == QuestRewardType.Attribute && r.AttributeReward is not null)
                {
                    return $"{r.AttributeReward.Designation} +{r.Value}";
                }

                if (r.RewardType == QuestRewardType.Skill && r.SkillReward is not null)
                {
                    return $"Skill: {r.SkillReward.Name}";
                }

                return r.RewardType.ToString();
            }));
    }

    private Dictionary<(MiniGameType, int), MiniGameEventInfo> LoadMiniGameEvents(GameConfiguration config)
    {
        var dict = new Dictionary<(MiniGameType, int), MiniGameEventInfo>();
        foreach (var def in config.MiniGameDefinitions)
        {
            if (def is null)
            {
                continue;
            }

            var ticketGroup = def.TicketItem?.Group;
            var ticketNumber = def.TicketItem?.Number;

            dict[(def.Type, def.GameLevel)] = new MiniGameEventInfo(
                Type: def.Type,
                GameLevel: def.GameLevel,
                Name: def.Name.ToString() ?? string.Empty,
                MinLevel: def.MinimumCharacterLevel,
                MaxLevel: def.MaximumCharacterLevel,
                TicketItemGroup: ticketGroup is not null ? (int)ticketGroup : null,
                TicketItemNumber: ticketNumber is not null ? (int)ticketNumber : null,
                TicketItemLevel: def.TicketItemLevel,
                EntranceFee: def.EntranceFee,
                GameDurationMinutes: (int)def.GameDuration.TotalMinutes,
                EnterDurationMinutes: (int)def.EnterDuration.TotalMinutes,
                MaxPlayers: def.MaximumPlayerCount);
        }

        return dict;
    }

    private List<DropSourceInfo> LoadDropSources(
        GameConfiguration config,
        IReadOnlyDictionary<int, MonsterInfo> monsters)
    {
        var results = new List<DropSourceInfo>();
        var seen = new HashSet<(short MonsterNumber, ushort MapNumber, byte ItemLevel, int ItemGroup, int ItemNumber)>();

        // Stage 1: Monster-specific DropItemGroups
        foreach (var map in config.Maps)
        {
            if (map is null)
            {
                continue;
            }

            foreach (var spawn in map.MonsterSpawns)
            {
                if (spawn?.MonsterDefinition is null)
                {
                    continue;
                }

                var monster = spawn.MonsterDefinition;
                foreach (var dropGroup in monster.DropItemGroups)
                {
                    if (dropGroup is null)
                    {
                        continue;
                    }

                    foreach (var itemDef in dropGroup.PossibleItems)
                    {
                        if (itemDef is null)
                        {
                            continue;
                        }

                        var itemLevel = dropGroup.ItemLevel ?? 1;
                        var key = (monster.Number, (ushort)map.Number, itemLevel, (int)itemDef.Group, (int)itemDef.Number);
                        if (!seen.Add(key))
                        {
                            continue;
                        }

                        results.Add(new DropSourceInfo(
                            ItemGroup: itemDef.Group,
                            ItemNumber: itemDef.Number,
                            MonsterNumber: monster.Number,
                            MonsterName: monster.Designation.ToString() ?? string.Empty,
                            MapNumber: (ushort)map.Number,
                            MapName: map.Name.ToString() ?? string.Empty,
                            ItemLevel: itemLevel,
                            DropRate: dropGroup.Chance));
                    }
                }
            }
        }

        // Stage 2: Map-specific DropItemGroups
        foreach (var map in config.Maps)
        {
            if (map is null)
            {
                continue;
            }

            foreach (var dropGroup in map.DropItemGroups)
            {
                if (dropGroup is null)
                {
                    continue;
                }

                foreach (var itemDef in dropGroup.PossibleItems)
                {
                    if (itemDef is null)
                    {
                        continue;
                    }

                    // Apply to all monsters on this map within level range
                    var minLevel = dropGroup.MinimumMonsterLevel ?? 0;
                    var maxLevel = dropGroup.MaximumMonsterLevel ?? byte.MaxValue;

                    foreach (var spawn in map.MonsterSpawns)
                    {
                        if (spawn?.MonsterDefinition is null)
                        {
                            continue;
                        }

                        var monster = spawn.MonsterDefinition;
                        if (dropGroup.Monster is not null && dropGroup.Monster != monster)
                        {
                            continue;
                        }

                        byte monsterLevel;
                        try
                        {
                            monsterLevel = (byte)monster[Stats.Level];
                        }
                        catch
                        {
                            continue;
                        }

                        if (monsterLevel < minLevel || monsterLevel > maxLevel)
                        {
                            continue;
                        }

                        var itemLevel = dropGroup.ItemLevel ?? 1;
                        var key = (monster.Number, (ushort)map.Number, itemLevel, (int)itemDef.Group, (int)itemDef.Number);
                        if (!seen.Add(key))
                        {
                            continue;
                        }

                        results.Add(new DropSourceInfo(
                            ItemGroup: itemDef.Group,
                            ItemNumber: itemDef.Number,
                            MonsterNumber: monster.Number,
                            MonsterName: monster.Designation.ToString() ?? string.Empty,
                            MapNumber: (ushort)map.Number,
                            MapName: map.Name.ToString() ?? string.Empty,
                            ItemLevel: itemLevel,
                            DropRate: dropGroup.Chance));
                    }
                }
            }
        }

        // Stage 3: Global DropItemGroups (config.DropItemGroups)
        foreach (var dropGroup in config.DropItemGroups)
        {
            if (dropGroup is null)
            {
                continue;
            }

            foreach (var itemDef in dropGroup.PossibleItems)
            {
                if (itemDef is null)
                {
                    continue;
                }

                var itemLevel = dropGroup.ItemLevel ?? 1;

                if (dropGroup.Monster is { } specificMonster)
                {
                    // Specific monster drop — scan maps for spawns
                    foreach (var map in config.Maps)
                    {
                        if (map is null)
                        {
                            continue;
                        }

                        foreach (var spawn in map.MonsterSpawns)
                        {
                            if (spawn?.MonsterDefinition != specificMonster)
                            {
                                continue;
                            }

                            var key = (specificMonster.Number, (ushort)map.Number, itemLevel, (int)itemDef.Group, (int)itemDef.Number);
                            if (!seen.Add(key))
                            {
                                continue;
                            }

                            results.Add(new DropSourceInfo(
                                ItemGroup: itemDef.Group,
                                ItemNumber: itemDef.Number,
                                MonsterNumber: specificMonster.Number,
                                MonsterName: specificMonster.Designation.ToString() ?? string.Empty,
                                MapNumber: (ushort)map.Number,
                                MapName: map.Name.ToString() ?? string.Empty,
                                ItemLevel: itemLevel,
                                DropRate: dropGroup.Chance));
                        }
                    }
                }
                else
                {
                    // Global level-range drop — scan all maps/monsters
                    var minLevel = dropGroup.MinimumMonsterLevel ?? 0;
                    var maxLevel = dropGroup.MaximumMonsterLevel ?? byte.MaxValue;

                    foreach (var map in config.Maps)
                    {
                        if (map is null)
                        {
                            continue;
                        }

                        foreach (var spawn in map.MonsterSpawns)
                        {
                            if (spawn?.MonsterDefinition is null)
                            {
                                continue;
                            }

                            var monster = spawn.MonsterDefinition;
                            byte monsterLevel;
                            try
                            {
                                monsterLevel = (byte)monster[Stats.Level];
                            }
                            catch
                            {
                                continue;
                            }

                            if (monsterLevel < minLevel || monsterLevel > maxLevel)
                            {
                                continue;
                            }

                            var key = (monster.Number, (ushort)map.Number, itemLevel, (int)itemDef.Group, (int)itemDef.Number);
                            if (!seen.Add(key))
                            {
                                continue;
                            }

                            results.Add(new DropSourceInfo(
                                ItemGroup: itemDef.Group,
                                ItemNumber: itemDef.Number,
                                MonsterNumber: monster.Number,
                                MonsterName: monster.Designation.ToString() ?? string.Empty,
                                MapNumber: (ushort)map.Number,
                                MapName: map.Name.ToString() ?? string.Empty,
                                ItemLevel: itemLevel,
                                DropRate: dropGroup.Chance));
                        }
                    }
                }
            }
        }

        this._logger.LogInformation("[GameKnowledge] DropSources 加载完成: {Count} 条掉落记录", results.Count);
        return results;
    }

    private List<MapConnectionInfo> LoadMapConnections(
        GameConfiguration config,
        IReadOnlyDictionary<int, MapInfo> maps)
    {
        var results = new List<MapConnectionInfo>();
        foreach (var map in config.Maps)
        {
            if (map is null)
            {
                continue;
            }

            foreach (var enterGate in map.EnterGates)
            {
                if (enterGate?.TargetGate?.Map is null)
                {
                    continue;
                }

                results.Add(new MapConnectionInfo(
                    FromMapNumber: map.Number,
                    ToMapNumber: enterGate.TargetGate.Map.Number,
                    GateNumber: enterGate.Number,
                    MinLevel: enterGate.LevelRequirement,
                    GateName: $"Gate {enterGate.Number}"));
            }
        }

        return results;
    }
}
