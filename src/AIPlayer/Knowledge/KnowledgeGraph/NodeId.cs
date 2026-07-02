// <copyright file="NodeId.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// A compact, unique identifier for a node in the knowledge graph.
/// Packs the <see cref="NodeType"/> (upper 8 bits) and a domain-specific identifier (lower 56 bits)
/// into a single <see cref="long"/> value for efficient storage and lookup.
/// </summary>
public readonly struct NodeId : IEquatable<NodeId>, IComparable<NodeId>
{
    /// <summary>
    /// Number of bits to shift to store the <see cref="NodeType"/> in the upper byte.
    /// </summary>
    private const int TypeShift = 56;

    /// <summary>
    /// Bit mask for the lower 56 bits used for the domain-specific identifier.
    /// </summary>
    private const long DomainMask = 0x00FFFFFFFFFFFFFFL;

    private readonly long _value;

    private NodeId(NodeType type, long domainId)
    {
        _value = ((long)type << TypeShift) | (domainId & DomainMask);
    }

    /// <summary>
    /// Gets the node type (decoded from the upper 8 bits of the packed value).
    /// </summary>
    public NodeType Type => (NodeType)(_value >> TypeShift);

    /// <summary>
    /// Gets the full packed value of this identifier.
    /// </summary>
    public long Value => _value;

    /// <summary>
    /// Gets the domain-specific identifier (decoded from the lower 56 bits of the packed value).
    /// </summary>
    public long DomainId => _value & DomainMask;

    /// <summary>
    /// Creates a <see cref="NodeId"/> for a map node.
    /// </summary>
    /// <param name="mapNumber">The map number.</param>
    /// <returns>A new <see cref="NodeId"/> for the specified map.</returns>
    public static NodeId ForMap(int mapNumber) => new(NodeType.Map, mapNumber);

    /// <summary>
    /// Creates a <see cref="NodeId"/> for a monster node.
    /// </summary>
    /// <param name="monsterNumber">The monster number.</param>
    /// <returns>A new <see cref="NodeId"/> for the specified monster.</returns>
    public static NodeId ForMonster(short monsterNumber) => new(NodeType.Monster, monsterNumber);

    /// <summary>
    /// Creates a <see cref="NodeId"/> for an item node.
    /// The domain ID packs the group in the upper 32 bits and the number in the lower 32 bits.
    /// </summary>
    /// <param name="group">The item group.</param>
    /// <param name="number">The item number within the group.</param>
    /// <returns>A new <see cref="NodeId"/> for the specified item.</returns>
    public static NodeId ForItem(int group, int number) => new(NodeType.Item, ((long)group << 32) | (uint)number);

    /// <summary>
    /// Creates a <see cref="NodeId"/> for a quest node.
    /// The domain ID packs the quest group in the upper 32 bits and the quest number in the lower 32 bits.
    /// </summary>
    /// <param name="questGroup">The quest group.</param>
    /// <param name="questNumber">The quest number within the group.</param>
    /// <returns>A new <see cref="NodeId"/> for the specified quest.</returns>
    public static NodeId ForQuest(int questGroup, int questNumber) => new(NodeType.Quest, ((long)questGroup << 32) | (uint)questNumber);

    /// <summary>
    /// Creates a <see cref="NodeId"/> for an NPC node.
    /// </summary>
    /// <param name="npcNumber">The NPC number.</param>
    /// <returns>A new <see cref="NodeId"/> for the specified NPC.</returns>
    public static NodeId ForNpc(short npcNumber) => new(NodeType.Npc, npcNumber);

    /// <summary>
    /// Creates a <see cref="NodeId"/> for a skill node.
    /// </summary>
    /// <param name="skillNumber">The skill number.</param>
    /// <returns>A new <see cref="NodeId"/> for the specified skill.</returns>
    public static NodeId ForSkill(short skillNumber) => new(NodeType.Skill, skillNumber);

    /// <summary>
    /// Creates a <see cref="NodeId"/> for a player class node.
    /// </summary>
    /// <param name="classNumber">The player class number.</param>
    /// <returns>A new <see cref="NodeId"/> for the specified player class.</returns>
    public static NodeId ForPlayerClass(byte classNumber) => new(NodeType.PlayerClass, classNumber);

    /// <summary>
    /// Creates a <see cref="NodeId"/> for a mini-game event node.
    /// </summary>
    /// <param name="eventId">The mini-game event identifier.</param>
    /// <returns>A new <see cref="NodeId"/> for the specified event.</returns>
    public static NodeId ForMiniGameEvent(short eventId) => new(NodeType.MiniGameEvent, eventId);

    /// <summary>
    /// Creates a <see cref="NodeId"/> for a crafting recipe node.
    /// </summary>
    /// <param name="recipeId">The crafting recipe identifier.</param>
    /// <returns>A new <see cref="NodeId"/> for the specified recipe.</returns>
    public static NodeId ForCraftingRecipe(short recipeId) => new(NodeType.CraftingRecipe, recipeId);

    /// <summary>F13: Creates a NodeId for an AI worker profile.</summary>
    public static NodeId ForWorkerProfile(string workerId) => new(NodeType.WorkerProfile, (long)workerId.GetHashCode() & DomainMask);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is NodeId other && Equals(other);

    /// <inheritdoc />
    public bool Equals(NodeId other) => _value == other._value;

    /// <inheritdoc />
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>
    /// Returns a string representation of this node identifier.
    /// Format: "NodeType(DomainId)" e.g., "Map(3)" or "Monster(25)".
    /// </summary>
    /// <returns>A string representation of this node identifier.</returns>
    public override string ToString()
    {
        if (this.Type == NodeType.Item || this.Type == NodeType.Quest)
        {
            // Unpack compound domain ID: high 32 bits = group, low 32 bits = number
            var domain = this.DomainId;
            var high = (int)(domain >> 32);
            var low = (int)(uint)(domain & 0xFFFFFFFF);
            return $"{Type}({high},{low})";
        }

        return $"{Type}({DomainId})";
    }

    /// <summary>
    /// Tries to parse a <see cref="NodeId"/> from its string representation (e.g. "Map(3)" or "Monster(25)").
    /// </summary>
    /// <param name="s">The string to parse.</param>
    /// <param name="result">When this method returns, contains the parsed <see cref="NodeId"/> if successful.</param>
    /// <returns><c>true</c> if the string was successfully parsed; otherwise, <c>false</c>.</returns>
    public static bool TryParse(string? s, out NodeId result)
    {
        result = default;
        if (string.IsNullOrEmpty(s)) return false;

        var parenOpen = s.IndexOf('(');
        var parenClose = s.IndexOf(')', parenOpen + 1);
        if (parenOpen < 0 || parenClose < 0 || parenClose <= parenOpen + 1) return false;

        var typeStr = s[..parenOpen];
        if (!Enum.TryParse<NodeType>(typeStr, out var nodeType)) return false;

        var domainStr = s[(parenOpen + 1)..parenClose];

        // Handle compound domain IDs (Item and Quest use "group,number" format)
        var commaIdx = domainStr.IndexOf(',');
        if (commaIdx >= 0 && (nodeType == NodeType.Item || nodeType == NodeType.Quest))
        {
            if (!int.TryParse(domainStr[..commaIdx], out var highPart)) return false;
            if (!int.TryParse(domainStr[(commaIdx + 1)..], out var lowPart)) return false;
            // Pack: high in upper 32 bits, low in lower 32 bits (unsigned)
            result = new NodeId(nodeType, ((long)highPart << 32) | (uint)lowPart);
            return true;
        }

        // Simple numeric domain ID for all other types (Map, Monster, Npc, Skill, etc.)
        if (!long.TryParse(domainStr, out var domainId)) return false;

        result = new NodeId(nodeType, domainId);
        return true;
    }

    /// <summary>
    /// Determines whether two specified <see cref="NodeId"/> instances are equal.
    /// </summary>
    /// <param name="left">The first <see cref="NodeId"/>.</param>
    /// <param name="right">The second <see cref="NodeId"/>.</param>
    /// <returns><c>true</c> if the values are equal; otherwise, <c>false</c>.</returns>
    public static bool operator ==(NodeId left, NodeId right) => left.Equals(right);

    /// <summary>
    /// Determines whether two specified <see cref="NodeId"/> instances are not equal.
    /// </summary>
    /// <param name="left">The first <see cref="NodeId"/>.</param>
    /// <param name="right">The second <see cref="NodeId"/>.</param>
    /// <returns><c>true</c> if the values are not equal; otherwise, <c>false</c>.</returns>
    public static bool operator !=(NodeId left, NodeId right) => !left.Equals(right);

    /// <inheritdoc />
    public int CompareTo(NodeId other) => _value.CompareTo(other._value);

    /// <summary>
    /// Determines whether one <see cref="NodeId"/> is less than another.
    /// </summary>
    public static bool operator <(NodeId left, NodeId right) => left.CompareTo(right) < 0;

    /// <summary>
    /// Determines whether one <see cref="NodeId"/> is greater than another.
    /// </summary>
    public static bool operator >(NodeId left, NodeId right) => left.CompareTo(right) > 0;
}
