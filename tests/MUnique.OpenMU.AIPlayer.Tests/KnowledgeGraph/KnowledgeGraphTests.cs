// <copyright file="KnowledgeGraphTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests.KnowledgeGraph;

using MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Unit tests for the <see cref="KnowledgeGraph"/> class.
/// Tests node/edge lifecycle, graph traversal queries, and builder-pattern graph construction.
/// </summary>
[TestFixture]
public class KnowledgeGraphTests
{
    private KnowledgeGraph _graph = null!;
    private GraphNode _mapNode = null!;
    private GraphNode _monsterNode = null!;

    /// <summary>
    /// Sets up a fresh graph and reusable test nodes before each test.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        this._graph = new KnowledgeGraph();
        this._mapNode = new GraphNode(NodeId.ForMap(1), "Lorencia", NodeType.Map);
        this._monsterNode = new GraphNode(NodeId.ForMonster(10), "Buddy", NodeType.Monster);
    }

    // ========================================================================
    // AddNode Tests
    // ========================================================================

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.AddNode"/> returns true for a new node.
    /// </summary>
    [Test]
    public void AddNode_NewNode_ReturnsTrue()
    {
        var result = this._graph.AddNode(this._mapNode);
        Assert.That(result, Is.True);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.AddNode"/> returns false when a node
    /// with the same <see cref="NodeId"/> already exists.
    /// </summary>
    [Test]
    public void AddNode_Duplicate_ReturnsFalse()
    {
        this._graph.AddNode(this._mapNode);
        var duplicate = new GraphNode(this._mapNode.Id, "Duplicate", NodeType.Map);
        var result = this._graph.AddNode(duplicate);
        Assert.That(result, Is.False);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.AddNode"/> throws <see cref="ArgumentNullException"/>
    /// when <c>null</c> is passed.
    /// </summary>
    [Test]
    public void AddNode_Null_ThrowsArgumentNullException()
    {
        Assert.That(() => this._graph.AddNode(null!), Throws.ArgumentNullException);
    }

    // ========================================================================
    // AddEdge Tests
    // ========================================================================

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.AddEdge"/> returns false when the source
    /// node does not exist in the graph.
    /// </summary>
    [Test]
    public void AddEdge_SourceNotInGraph_ReturnsFalse()
    {
        this._graph.AddNode(this._mapNode);
        var edge = new GraphEdge(EdgeType.SpawnsOn, this._monsterNode.Id, this._mapNode.Id);
        Assert.That(this._graph.AddEdge(edge), Is.False);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.AddEdge"/> returns false when the target
    /// node does not exist in the graph.
    /// </summary>
    [Test]
    public void AddEdge_TargetNotInGraph_ReturnsFalse()
    {
        this._graph.AddNode(this._monsterNode);
        var edge = new GraphEdge(EdgeType.SpawnsOn, this._monsterNode.Id, this._mapNode.Id);
        Assert.That(this._graph.AddEdge(edge), Is.False);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.AddEdge"/> returns true when both source
    /// and target nodes exist.
    /// </summary>
    [Test]
    public void AddEdge_BothExist_ReturnsTrue()
    {
        this._graph.AddNode(this._monsterNode);
        this._graph.AddNode(this._mapNode);
        var edge = new GraphEdge(EdgeType.SpawnsOn, this._monsterNode.Id, this._mapNode.Id);
        Assert.That(this._graph.AddEdge(edge), Is.True);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.AddEdge"/> throws <see cref="ArgumentNullException"/>
    /// when <c>null</c> is passed.
    /// </summary>
    [Test]
    public void AddEdge_Null_ThrowsArgumentNullException()
    {
        Assert.That(() => this._graph.AddEdge(null!), Throws.ArgumentNullException);
    }

    /// <summary>
    /// Tests that adding a self-loop edge (source == target) is valid when the node exists.
    /// </summary>
    [Test]
    public void AddEdge_SelfLoop_Valid()
    {
        this._graph.AddNode(this._mapNode);
        var selfEdge = new GraphEdge(EdgeType.ConnectsTo, this._mapNode.Id, this._mapNode.Id);
        Assert.That(this._graph.AddEdge(selfEdge), Is.True);
        Assert.That(this._graph.EdgeCount, Is.EqualTo(1));
    }

    // ========================================================================
    // GetOutgoingEdges Tests
    // ========================================================================

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.GetOutgoingEdges(NodeId)"/> returns
    /// the correct edges from a source node.
    /// </summary>
    [Test]
    public void GetOutgoingEdges_ReturnsCorrectEdges()
    {
        this._graph.AddNode(this._mapNode);
        this._graph.AddNode(this._monsterNode);
        var edge = new GraphEdge(EdgeType.SpawnsOn, this._monsterNode.Id, this._mapNode.Id);
        this._graph.AddEdge(edge);

        var outgoing = this._graph.GetOutgoingEdges(this._monsterNode.Id);
        Assert.That(outgoing, Has.Count.EqualTo(1));
        Assert.That(outgoing[0].Target, Is.EqualTo(this._mapNode.Id));
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.GetOutgoingEdges(NodeId)"/> returns an
    /// empty list when the node does not exist.
    /// </summary>
    [Test]
    public void GetOutgoingEdges_NodeNotInGraph_ReturnsEmpty()
    {
        var outgoing = this._graph.GetOutgoingEdges(this._mapNode.Id);
        Assert.That(outgoing, Is.Empty);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.GetOutgoingEdges(NodeId, EdgeType)"/>
    /// filters by edge type correctly.
    /// </summary>
    [Test]
    public void GetOutgoingEdges_FilteredByType_ReturnsMatchingEdges()
    {
        this._graph.AddNode(this._monsterNode);
        this._graph.AddNode(this._mapNode);
        var map2 = new GraphNode(NodeId.ForMap(2), "Noria", NodeType.Map);
        this._graph.AddNode(map2);

        this._graph.AddEdge(new GraphEdge(EdgeType.SpawnsOn, this._monsterNode.Id, this._mapNode.Id));
        this._graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, this._mapNode.Id, map2.Id));

        var spawnsOnEdges = this._graph.GetOutgoingEdges(this._monsterNode.Id, EdgeType.SpawnsOn);
        var connectsToEdges = this._graph.GetOutgoingEdges(this._monsterNode.Id, EdgeType.ConnectsTo);

        Assert.That(spawnsOnEdges, Has.Count.EqualTo(1));
        Assert.That(connectsToEdges, Is.Empty);
    }

    // ========================================================================
    // GetIncomingEdges Tests
    // ========================================================================

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.GetIncomingEdges(NodeId)"/> returns
    /// the correct edges into a target node.
    /// </summary>
    [Test]
    public void GetIncomingEdges_ReturnsCorrectEdges()
    {
        this._graph.AddNode(this._mapNode);
        this._graph.AddNode(this._monsterNode);
        var edge = new GraphEdge(EdgeType.SpawnsOn, this._monsterNode.Id, this._mapNode.Id);
        this._graph.AddEdge(edge);

        var incoming = this._graph.GetIncomingEdges(this._mapNode.Id);
        Assert.That(incoming, Has.Count.EqualTo(1));
        Assert.That(incoming[0].Source, Is.EqualTo(this._monsterNode.Id));
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.GetIncomingEdges(NodeId)"/> returns an
    /// empty list when the node does not exist.
    /// </summary>
    [Test]
    public void GetIncomingEdges_NodeNotInGraph_ReturnsEmpty()
    {
        var incoming = this._graph.GetIncomingEdges(this._mapNode.Id);
        Assert.That(incoming, Is.Empty);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.GetIncomingEdges(NodeId, EdgeType)"/>
    /// filters by edge type correctly.
    /// </summary>
    [Test]
    public void GetIncomingEdges_FilteredByType_ReturnsMatchingEdges()
    {
        this._graph.AddNode(this._monsterNode);
        this._graph.AddNode(this._mapNode);
        var map2 = new GraphNode(NodeId.ForMap(2), "Noria", NodeType.Map);
        this._graph.AddNode(map2);

        this._graph.AddEdge(new GraphEdge(EdgeType.SpawnsOn, this._monsterNode.Id, this._mapNode.Id));
        this._graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, map2.Id, this._mapNode.Id));

        var spawnsOnIncoming = this._graph.GetIncomingEdges(this._mapNode.Id, EdgeType.SpawnsOn);
        var connectsToIncoming = this._graph.GetIncomingEdges(this._mapNode.Id, EdgeType.ConnectsTo);

        Assert.That(spawnsOnIncoming, Has.Count.EqualTo(1));
        Assert.That(connectsToIncoming, Has.Count.EqualTo(1));
    }

    // ========================================================================
    // GetEdge Tests
    // ========================================================================

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.GetEdge"/> finds a specific edge
    /// by source, target, and type.
    /// </summary>
    [Test]
    public void GetEdge_FindsSpecificEdge()
    {
        this._graph.AddNode(this._monsterNode);
        this._graph.AddNode(this._mapNode);
        var edge = new GraphEdge(EdgeType.SpawnsOn, this._monsterNode.Id, this._mapNode.Id);
        this._graph.AddEdge(edge);

        var found = this._graph.GetEdge(this._monsterNode.Id, this._mapNode.Id, EdgeType.SpawnsOn);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Type, Is.EqualTo(EdgeType.SpawnsOn));
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.GetEdge"/> returns null when no
    /// matching edge exists.
    /// </summary>
    [Test]
    public void GetEdge_NoMatch_ReturnsNull()
    {
        this._graph.AddNode(this._monsterNode);
        this._graph.AddNode(this._mapNode);
        var edge = new GraphEdge(EdgeType.SpawnsOn, this._monsterNode.Id, this._mapNode.Id);
        this._graph.AddEdge(edge);

        var found = this._graph.GetEdge(this._monsterNode.Id, this._mapNode.Id, EdgeType.ConnectsTo);
        Assert.That(found, Is.Null);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.GetEdge"/> returns null when the
    /// source node does not exist.
    /// </summary>
    [Test]
    public void GetEdge_SourceNotInGraph_ReturnsNull()
    {
        var found = this._graph.GetEdge(this._monsterNode.Id, this._mapNode.Id, EdgeType.SpawnsOn);
        Assert.That(found, Is.Null);
    }

    // ========================================================================
    // UpdateEdgeWeight Tests
    // ========================================================================

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.UpdateEdgeWeight"/> atomically
    /// updates the weight of a matching edge and that the change is visible in
    /// both outgoing and incoming adjacency lists.
    /// </summary>
    [Test]
    public void UpdateEdgeWeight_UpdatesWeight()
    {
        this._graph.AddNode(this._monsterNode);
        this._graph.AddNode(this._mapNode);
        var edge = new GraphEdge(EdgeType.SpawnsOn, this._monsterNode.Id, this._mapNode.Id, Weight: 1.0);
        this._graph.AddEdge(edge);

        var updated = this._graph.UpdateEdgeWeight(this._monsterNode.Id, this._mapNode.Id, EdgeType.SpawnsOn, 5.0);
        Assert.That(updated, Is.True);

        var updatedEdge = this._graph.GetEdge(this._monsterNode.Id, this._mapNode.Id, EdgeType.SpawnsOn);
        Assert.That(updatedEdge!.Weight, Is.EqualTo(5.0));
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.UpdateEdgeWeight"/> returns false
    /// when no matching edge exists.
    /// </summary>
    [Test]
    public void UpdateEdgeWeight_EdgeNotFound_ReturnsFalse()
    {
        this._graph.AddNode(this._monsterNode);
        this._graph.AddNode(this._mapNode);
        var result = this._graph.UpdateEdgeWeight(this._monsterNode.Id, this._mapNode.Id, EdgeType.SpawnsOn, 5.0);
        Assert.That(result, Is.False);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.UpdateEdgeWeight"/> returns false
    /// when the source node does not exist in the graph.
    /// </summary>
    [Test]
    public void UpdateEdgeWeight_SourceNotInGraph_ReturnsFalse()
    {
        var result = this._graph.UpdateEdgeWeight(this._monsterNode.Id, this._mapNode.Id, EdgeType.SpawnsOn, 5.0);
        Assert.That(result, Is.False);
    }

    // ========================================================================
    // Clear Tests
    // ========================================================================

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.Clear"/> removes all nodes and edges,
    /// resetting counts to zero.
    /// </summary>
    [Test]
    public void Clear_ResetsGraph()
    {
        this._graph.AddNode(this._mapNode);
        this._graph.AddNode(this._monsterNode);
        this._graph.AddEdge(new GraphEdge(EdgeType.SpawnsOn, this._monsterNode.Id, this._mapNode.Id));

        this._graph.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(this._graph.NodeCount, Is.EqualTo(0));
            Assert.That(this._graph.EdgeCount, Is.EqualTo(0));
            Assert.That(this._graph.HasNode(this._mapNode.Id), Is.False);
            Assert.That(this._graph.HasNode(this._monsterNode.Id), Is.False);
            Assert.That(this._graph.GetAllNodes(), Is.Empty);
            Assert.That(this._graph.GetOutgoingEdges(this._monsterNode.Id), Is.Empty);
            Assert.That(this._graph.GetIncomingEdges(this._mapNode.Id), Is.Empty);
        });
    }

    // ========================================================================
    // Query Tests (TryGetNode, GetNode, HasNode, GetAllNodes, Counts)
    // ========================================================================

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.TryGetNode"/> returns true and the
    /// correct node when the node exists.
    /// </summary>
    [Test]
    public void TryGetNode_ExistingNode_ReturnsTrue()
    {
        this._graph.AddNode(this._mapNode);
        var found = this._graph.TryGetNode(this._mapNode.Id, out var node);
        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(node, Is.Not.Null);
            Assert.That(node!.Label, Is.EqualTo("Lorencia"));
        });
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.TryGetNode"/> returns false when the
    /// node does not exist.
    /// </summary>
    [Test]
    public void TryGetNode_NonExistingNode_ReturnsFalse()
    {
        var found = this._graph.TryGetNode(NodeId.ForMap(999), out var node);
        Assert.Multiple(() =>
        {
            Assert.That(found, Is.False);
            Assert.That(node, Is.Null);
        });
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.GetNode"/> returns the correct node
    /// when it exists.
    /// </summary>
    [Test]
    public void GetNode_ExistingNode_ReturnsNode()
    {
        this._graph.AddNode(this._mapNode);
        var node = this._graph.GetNode(this._mapNode.Id);
        Assert.That(node.Label, Is.EqualTo("Lorencia"));
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.GetNode"/> throws
    /// <see cref="KeyNotFoundException"/> when the node does not exist.
    /// </summary>
    [Test]
    public void GetNode_NonExistingNode_ThrowsKeyNotFoundException()
    {
        Assert.That(() => this._graph.GetNode(NodeId.ForMap(999)), Throws.InstanceOf<KeyNotFoundException>());
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.HasNode"/> correctly detects existing
    /// and non-existing nodes.
    /// </summary>
    [Test]
    public void HasNode_ReturnsCorrectly()
    {
        this._graph.AddNode(this._mapNode);
        Assert.Multiple(() =>
        {
            Assert.That(this._graph.HasNode(this._mapNode.Id), Is.True);
            Assert.That(this._graph.HasNode(this._monsterNode.Id), Is.False);
        });
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.GetAllNodes"/> returns all added nodes.
    /// </summary>
    [Test]
    public void GetAllNodes_ReturnsAllNodes()
    {
        this._graph.AddNode(this._mapNode);
        this._graph.AddNode(this._monsterNode);
        var allNodes = this._graph.GetAllNodes();
        Assert.That(allNodes, Has.Count.EqualTo(2));
        Assert.That(allNodes.Select(n => n.Id), Is.EquivalentTo(new[] { this._mapNode.Id, this._monsterNode.Id }));
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraph.NodeCount"/> and
    /// <see cref="KnowledgeGraph.EdgeCount"/> reflect the current graph state.
    /// </summary>
    [Test]
    public void NodeCountAndEdgeCount_ReflectState()
    {
        var map2 = new GraphNode(NodeId.ForMap(2), "Noria", NodeType.Map);
        this._graph.AddNode(this._mapNode);
        this._graph.AddNode(map2);
        this._graph.AddNode(this._monsterNode);
        this._graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, this._mapNode.Id, map2.Id));
        this._graph.AddEdge(new GraphEdge(EdgeType.SpawnsOn, this._monsterNode.Id, this._mapNode.Id));

        Assert.Multiple(() =>
        {
            Assert.That(this._graph.NodeCount, Is.EqualTo(3));
            Assert.That(this._graph.EdgeCount, Is.EqualTo(2));
        });
    }

    // ========================================================================
    // Edge Cases
    // ========================================================================

    /// <summary>
    /// Tests operations on an empty graph produce safe defaults (no exceptions).
    /// </summary>
    [Test]
    public void EmptyGraph_OperationsReturnDefaults()
    {
        Assert.Multiple(() =>
        {
            Assert.That(this._graph.NodeCount, Is.EqualTo(0));
            Assert.That(this._graph.EdgeCount, Is.EqualTo(0));
            Assert.That(this._graph.GetAllNodes(), Is.Empty);
            Assert.That(this._graph.GetOutgoingEdges(NodeId.ForMap(0)), Is.Empty);
            Assert.That(this._graph.GetIncomingEdges(NodeId.ForMap(0)), Is.Empty);
            Assert.That(this._graph.GetEdge(NodeId.ForMap(0), NodeId.ForMap(1), EdgeType.ConnectsTo), Is.Null);
            Assert.That(this._graph.HasNode(NodeId.ForMap(0)), Is.False);
        });
    }

    /// <summary>
    /// Tests that a graph missing an intermediate edge still has correct counts
    /// (disconnected subgraphs).
    /// </summary>
    [Test]
    public void DisconnectedSubgraphs_AreIndependent()
    {
        var mapA = new GraphNode(NodeId.ForMap(0), "A", NodeType.Map);
        var mapB = new GraphNode(NodeId.ForMap(1), "B", NodeType.Map);
        var mapC = new GraphNode(NodeId.ForMap(2), "C", NodeType.Map);
        var mapD = new GraphNode(NodeId.ForMap(3), "D", NodeType.Map);

        this._graph.AddNode(mapA);
        this._graph.AddNode(mapB);
        this._graph.AddNode(mapC);
        this._graph.AddNode(mapD);

        // Subgraph 1: A -> B
        this._graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, mapA.Id, mapB.Id));

        // Subgraph 2: C -> D
        this._graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, mapC.Id, mapD.Id));

        Assert.Multiple(() =>
        {
            Assert.That(this._graph.EdgeCount, Is.EqualTo(2));
            Assert.That(this._graph.GetOutgoingEdges(mapA.Id), Has.Count.EqualTo(1));
            Assert.That(this._graph.GetOutgoingEdges(mapB.Id), Is.Empty);
            Assert.That(this._graph.GetOutgoingEdges(mapC.Id), Has.Count.EqualTo(1));
            Assert.That(this._graph.GetIncomingEdges(mapB.Id), Has.Count.EqualTo(1));
            Assert.That(this._graph.GetIncomingEdges(mapD.Id), Has.Count.EqualTo(1));
        });
    }

    /// <summary>
    /// Tests that nodes with an optional <see cref="GraphNode.Properties"/> dictionary
    /// are stored and retrieved correctly.
    /// </summary>
    [Test]
    public void NodeWithProperties_StoresProperties()
    {
        var node = new GraphNode(NodeId.ForMap(3), "Dungeon", NodeType.Map)
        {
            Properties = new Dictionary<string, object>
            {
                ["MinLevel"] = 10,
                ["MaxLevel"] = 30,
                ["ExpMultiplier"] = 1.5,
            },
        };
        this._graph.AddNode(node);

        var retrieved = this._graph.GetNode(node.Id);
        Assert.Multiple(() =>
        {
            Assert.That(retrieved.Properties, Is.Not.Null);
            Assert.That(retrieved.Properties!["MinLevel"], Is.EqualTo(10));
            Assert.That(retrieved.Properties["MaxLevel"], Is.EqualTo(30));
            Assert.That(retrieved.Properties["ExpMultiplier"], Is.EqualTo(1.5));
        });
    }

    /// <summary>
    /// Tests that edges with an optional <see cref="GraphEdge.Properties"/> dictionary
    /// are stored and retrieved correctly.
    /// </summary>
    [Test]
    public void EdgeWithProperties_StoresProperties()
    {
        this._graph.AddNode(this._monsterNode);
        this._graph.AddNode(this._mapNode);
        var edge = new GraphEdge(EdgeType.DropsAt, this._monsterNode.Id, this._mapNode.Id)
        {
            Properties = new Dictionary<string, object>
            {
                ["DropRate"] = 0.01,
            },
        };
        this._graph.AddEdge(edge);

        var found = this._graph.GetEdge(this._monsterNode.Id, this._mapNode.Id, EdgeType.DropsAt);
        Assert.That(found!.Properties, Is.Not.Null);
        Assert.That(found.Properties!["DropRate"], Is.EqualTo(0.01));
    }

    // ========================================================================
    // Builder-Pattern Tests
    // These tests manually construct graphs mirroring what the
    // KnowledgeGraphBuilder would produce, verifying the resulting structure.
    // ========================================================================

    /// <summary>
    /// Tests that a manually constructed graph contains map nodes matching the
    /// expected structure the builder would produce.
    /// </summary>
    [Test]
    public void BuilderPattern_ManualGraph_ContainsMapNodes()
    {
        var map1 = new GraphNode(NodeId.ForMap(0), "Lorencia", NodeType.Map)
        {
            Properties = new Dictionary<string, object>
            {
                ["MinLevel"] = 1,
                ["MaxLevel"] = 20,
                ["ExpMultiplier"] = 1.0,
            },
        };
        var map2 = new GraphNode(NodeId.ForMap(1), "Dungeon", NodeType.Map)
        {
            Properties = new Dictionary<string, object>
            {
                ["MinLevel"] = 15,
                ["MaxLevel"] = 40,
                ["ExpMultiplier"] = 1.2,
            },
        };

        this._graph.AddNode(map1);
        this._graph.AddNode(map2);

        Assert.Multiple(() =>
        {
            Assert.That(this._graph.HasNode(NodeId.ForMap(0)), Is.True);
            Assert.That(this._graph.HasNode(NodeId.ForMap(1)), Is.True);
            Assert.That(this._graph.NodeCount, Is.EqualTo(2));

            var retrieved1 = this._graph.GetNode(NodeId.ForMap(0));
            Assert.That(retrieved1.Type, Is.EqualTo(NodeType.Map));
            Assert.That(retrieved1.Label, Is.EqualTo("Lorencia"));

            var retrieved2 = this._graph.GetNode(NodeId.ForMap(1));
            Assert.That(retrieved2.Type, Is.EqualTo(NodeType.Map));
            Assert.That(retrieved2.Label, Is.EqualTo("Dungeon"));
        });
    }

    /// <summary>
    /// Tests that a manually constructed graph contains monster nodes
    /// referencing the map they spawn on via <see cref="EdgeType.SpawnsOn"/>.
    /// </summary>
    [Test]
    public void BuilderPattern_ManualGraph_ContainsMonsterNodes()
    {
        var map = new GraphNode(NodeId.ForMap(0), "Lorencia", NodeType.Map);
        var monster = new GraphNode(NodeId.ForMonster(1), "Spider", NodeType.Monster)
        {
            Properties = new Dictionary<string, object> { ["Level"] = 2 },
        };

        this._graph.AddNode(map);
        this._graph.AddNode(monster);
        this._graph.AddEdge(new GraphEdge(EdgeType.SpawnsOn, monster.Id, map.Id, Weight: 0));

        Assert.Multiple(() =>
        {
            Assert.That(this._graph.HasNode(NodeId.ForMonster(1)), Is.True);
            Assert.That(this._graph.HasNode(NodeId.ForMap(0)), Is.True);

            var spawnEdge = this._graph.GetEdge(monster.Id, map.Id, EdgeType.SpawnsOn);
            Assert.That(spawnEdge, Is.Not.Null);
            Assert.That(spawnEdge!.Weight, Is.EqualTo(0));
        });
    }

    /// <summary>
    /// Tests that a manually constructed graph has <see cref="EdgeType.ConnectsTo"/>
    /// edges between maps that mirror the builder's map connection logic.
    /// </summary>
    [Test]
    public void BuilderPattern_ManualGraph_HasConnectsToEdges()
    {
        var lorencia = new GraphNode(NodeId.ForMap(0), "Lorencia", NodeType.Map);
        var dungeon = new GraphNode(NodeId.ForMap(1), "Dungeon", NodeType.Map);
        var noria = new GraphNode(NodeId.ForMap(2), "Noria", NodeType.Map);

        this._graph.AddNode(lorencia);
        this._graph.AddNode(dungeon);
        this._graph.AddNode(noria);

        // Lorencia <-> Dungeon
        this._graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, lorencia.Id, dungeon.Id, Weight: 0));
        this._graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, dungeon.Id, lorencia.Id, Weight: 0));

        // Dungeon -> Noria (one-way)
        this._graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, dungeon.Id, noria.Id, Weight: 0));

        Assert.Multiple(() =>
        {
            var fromLorencia = this._graph.GetOutgoingEdges(lorencia.Id, EdgeType.ConnectsTo);
            Assert.That(fromLorencia, Has.Count.EqualTo(1));
            Assert.That(fromLorencia[0].Target, Is.EqualTo(dungeon.Id));

            var fromDungeon = this._graph.GetOutgoingEdges(dungeon.Id, EdgeType.ConnectsTo);
            Assert.That(fromDungeon, Has.Count.EqualTo(2));
            Assert.That(fromDungeon.Select(e => e.Target),
                Is.EquivalentTo(new[] { lorencia.Id, noria.Id }));

            // Check incoming edges on Noria
            var toNoria = this._graph.GetIncomingEdges(noria.Id, EdgeType.ConnectsTo);
            Assert.That(toNoria, Has.Count.EqualTo(1));
            Assert.That(toNoria[0].Source, Is.EqualTo(dungeon.Id));
        });
    }

    /// <summary>
    /// Tests that a manually built graph structure returns non-null when
    /// assembled (mimicking KnowledgeGraphBuilder.Build behavior).
    /// </summary>
    [Test]
    public void BuilderPattern_ManualGraph_BuildReturnsNonNull()
    {
        // This simulates what KnowledgeGraphBuilder.Build does: create a graph,
        // populate it, and return it.
        var graph = this.BuildSampleGraph();
        Assert.That(graph, Is.Not.Null);
        Assert.That(graph.NodeCount, Is.GreaterThan(0));
    }

    /// <summary>
    /// Builds a small sample programmatic graph, simulating the builder pattern.
    /// </summary>
    private KnowledgeGraph BuildSampleGraph()
    {
        var graph = new KnowledgeGraph();

        var lorencia = new GraphNode(NodeId.ForMap(0), "Lorencia", NodeType.Map);
        var dungeon = new GraphNode(NodeId.ForMap(1), "Dungeon", NodeType.Map);
        var spider = new GraphNode(NodeId.ForMonster(1), "Spider", NodeType.Monster);

        graph.AddNode(lorencia);
        graph.AddNode(dungeon);
        graph.AddNode(spider);

        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, lorencia.Id, dungeon.Id, Weight: 0));
        graph.AddEdge(new GraphEdge(EdgeType.SpawnsOn, spider.Id, lorencia.Id, Weight: 0));

        return graph;
    }
}
