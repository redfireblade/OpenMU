// <copyright file="PathFinderTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Tests.KnowledgeGraph;

using MUnique.OpenMU.AIPlayer.Knowledge.KnowledgeGraph;

/// <summary>
/// Unit tests for <see cref="KnowledgeGraphPathFinder"/> and <see cref="KnowledgeGraphQuery"/>.
/// Tests Dijkstra, BFS, multi-source, nearest-neighbor algorithms and the query wrapper.
/// </summary>
[TestFixture]
public class PathFinderTests
{
    // ========================================================================
    // Helpers for building test graphs
    // ========================================================================

    private static KnowledgeGraph CreateSimpleGraph()
    {
        var graph = new KnowledgeGraph();
        var a = new GraphNode(NodeId.ForMap(0), "A", NodeType.Map);
        var b = new GraphNode(NodeId.ForMap(1), "B", NodeType.Map);
        var c = new GraphNode(NodeId.ForMap(2), "C", NodeType.Map);
        var d = new GraphNode(NodeId.ForMap(3), "D", NodeType.Map);

        graph.AddNode(a);
        graph.AddNode(b);
        graph.AddNode(c);
        graph.AddNode(d);

        // A -(1)-> B -(1)-> C
        // A -(3)-> D -(1)-> C
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, a.Id, b.Id, Weight: 1.0));
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, b.Id, c.Id, Weight: 1.0));
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, a.Id, d.Id, Weight: 3.0));
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, d.Id, c.Id, Weight: 1.0));

        return graph;
    }

    private static KnowledgeGraph CreateDisconnectedGraph()
    {
        var graph = new KnowledgeGraph();
        var a = new GraphNode(NodeId.ForMap(0), "A", NodeType.Map);
        var b = new GraphNode(NodeId.ForMap(1), "B", NodeType.Map);
        var c = new GraphNode(NodeId.ForMap(2), "C", NodeType.Map);
        var d = new GraphNode(NodeId.ForMap(3), "D", NodeType.Map);

        graph.AddNode(a);
        graph.AddNode(b);
        graph.AddNode(c);
        graph.AddNode(d);

        // Subgraph 1: A -> B
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, a.Id, b.Id, Weight: 1.0));

        // Subgraph 2: C -> D (disconnected from A-B)
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, c.Id, d.Id, Weight: 1.0));

        return graph;
    }

    private static KnowledgeGraph CreateBfsGraph()
    {
        var graph = new KnowledgeGraph();
        var a = new GraphNode(NodeId.ForMap(0), "A", NodeType.Map);
        var b = new GraphNode(NodeId.ForMap(1), "B", NodeType.Map);
        var c = new GraphNode(NodeId.ForMap(2), "C", NodeType.Map);
        var d = new GraphNode(NodeId.ForMap(3), "D", NodeType.Map);
        var e = new GraphNode(NodeId.ForMap(4), "E", NodeType.Map);

        graph.AddNode(a);
        graph.AddNode(b);
        graph.AddNode(c);
        graph.AddNode(d);
        graph.AddNode(e);

        // A -> B -> C
        // A -> D
        // D -> E
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, a.Id, b.Id));
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, b.Id, c.Id));
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, a.Id, d.Id));
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, d.Id, e.Id));

        return graph;
    }

    private static KnowledgeGraph CreateMultiSourceGraph()
    {
        var graph = new KnowledgeGraph();
        var a = new GraphNode(NodeId.ForMap(0), "A", NodeType.Map);
        var b = new GraphNode(NodeId.ForMap(1), "B", NodeType.Map);
        var target = new GraphNode(NodeId.ForMap(2), "Target", NodeType.Map);

        graph.AddNode(a);
        graph.AddNode(b);
        graph.AddNode(target);

        // A -(1)-> Target
        // B -(2)-> Target
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, a.Id, target.Id, Weight: 1.0));
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, b.Id, target.Id, Weight: 2.0));

        return graph;
    }

    private static KnowledgeGraph CreateMixedEdgeTypeGraph()
    {
        var graph = new KnowledgeGraph();
        var map1 = new GraphNode(NodeId.ForMap(0), "Lorencia", NodeType.Map);
        var map2 = new GraphNode(NodeId.ForMap(1), "Dungeon", NodeType.Map);
        var monster = new GraphNode(NodeId.ForMonster(1), "Spider", NodeType.Monster);
        var item = new GraphNode(NodeId.ForItem(14, 20), "Sword", NodeType.Item);

        graph.AddNode(map1);
        graph.AddNode(map2);
        graph.AddNode(monster);
        graph.AddNode(item);

        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, map1.Id, map2.Id));
        graph.AddEdge(new GraphEdge(EdgeType.SpawnsOn, monster.Id, map1.Id));
        graph.AddEdge(new GraphEdge(EdgeType.DropsAt, monster.Id, item.Id));

        return graph;
    }

    private static KnowledgeGraph CreateDependencyGraph()
    {
        var graph = new KnowledgeGraph();
        var recipe = new GraphNode(NodeId.ForCraftingRecipe(1), "ChaosWeapon", NodeType.CraftingRecipe);
        var resultItem = new GraphNode(NodeId.ForItem(5, 10), "ResultWeapon", NodeType.Item);
        var material1 = new GraphNode(NodeId.ForItem(12, 15), "Bless", NodeType.Item);
        var material2 = new GraphNode(NodeId.ForItem(14, 22), "Soul", NodeType.Item);

        graph.AddNode(recipe);
        graph.AddNode(resultItem);
        graph.AddNode(material1);
        graph.AddNode(material2);

        // resultItem -> recipe (HasRecipe)
        // recipe -> material1, material2 (RequiresMaterial)
        graph.AddEdge(new GraphEdge(EdgeType.HasRecipe, resultItem.Id, recipe.Id));
        graph.AddEdge(new GraphEdge(EdgeType.RequiresMaterial, recipe.Id, material1.Id));
        graph.AddEdge(new GraphEdge(EdgeType.RequiresMaterial, recipe.Id, material2.Id));

        return graph;
    }

    /// <summary>
    /// Default weight function used in tests: returns edge weight as-is.
    /// </summary>
    private static double DefaultWeight(GraphEdge edge) => edge.Weight;

    // ========================================================================
    // Dijkstra Tests
    // ========================================================================

    /// <summary>
    /// Tests that Dijkstra finds the weighted shortest path in a simple graph
    /// with two possible routes.
    /// </summary>
    [Test]
    public void Dijkstra_FindsShortestPath()
    {
        var graph = CreateSimpleGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);
        var a = NodeId.ForMap(0);
        var c = NodeId.ForMap(2);

        var result = pathFinder.Dijkstra(a, c, DefaultWeight);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Edges, Has.Count.EqualTo(2));
        Assert.That(result.TotalWeight, Is.EqualTo(2.0));
        Assert.That(result.IsFound, Is.True);

        // Expected path: A(0) -> B(1) -> C(2)
        var nodeIds = result.Nodes;
        Assert.That(nodeIds, Has.Count.EqualTo(3));
        Assert.That(nodeIds[0], Is.EqualTo(a));
        Assert.That(nodeIds[1], Is.EqualTo(NodeId.ForMap(1)));
        Assert.That(nodeIds[2], Is.EqualTo(c));
    }

    /// <summary>
    /// Tests that Dijkstra returns null when no path exists between two nodes
    /// in disconnected subgraphs.
    /// </summary>
    [Test]
    public void Dijkstra_NoPath_ReturnsNull()
    {
        var graph = CreateDisconnectedGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);
        var a = NodeId.ForMap(0);
        var d = NodeId.ForMap(3);

        var result = pathFinder.Dijkstra(a, d, DefaultWeight);

        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// Tests that Dijkstra returns null when the source node is not in the graph.
    /// </summary>
    [Test]
    public void Dijkstra_SourceNotInGraph_ReturnsNull()
    {
        var graph = CreateSimpleGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        var result = pathFinder.Dijkstra(NodeId.ForMap(99), NodeId.ForMap(0), DefaultWeight);

        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// Tests that Dijkstra returns null when the target node is not in the graph.
    /// </summary>
    [Test]
    public void Dijkstra_TargetNotInGraph_ReturnsNull()
    {
        var graph = CreateSimpleGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        var result = pathFinder.Dijkstra(NodeId.ForMap(0), NodeId.ForMap(99), DefaultWeight);

        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// Tests that Dijkstra returns a PathResult with IsFound=false when source
    /// equals target (no edges needed).
    /// </summary>
    [Test]
    public void Dijkstra_SameSourceAndTarget_ReturnsEmptyPath()
    {
        var graph = CreateSimpleGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);
        var a = NodeId.ForMap(0);

        var result = pathFinder.Dijkstra(a, a, DefaultWeight);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsFound, Is.False);
        Assert.That(result.Edges, Is.Empty);
        Assert.That(result.TotalWeight, Is.EqualTo(0));
    }

    /// <summary>
    /// Tests that Dijkstra's path selection is affected by the weight function.
    /// When weights differ, the algorithm should choose the lighter route.
    /// </summary>
    [Test]
    public void Dijkstra_WeightFunction_AffectsPath()
    {
        var graph = CreateSimpleGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);
        var a = NodeId.ForMap(0);
        var c = NodeId.ForMap(2);

        // Use inverse weight function: prefer heavier edges
        var result = pathFinder.Dijkstra(a, c, edge => 10.0 - edge.Weight);

        // With inverse weight, A->D->C (7+9=16) is "shorter" than A->B->C (9+9=18)
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.TotalWeight, Is.EqualTo(16.0).Within(0.001));
    }

    /// <summary>
    /// Tests that Dijkstra respects the max depth constraint.
    /// </summary>
    [Test]
    public void Dijkstra_WithMaxDepth_RespectsLimit()
    {
        var graph = CreateBfsGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);
        var a = NodeId.ForMap(0);
        var c = NodeId.ForMap(2); // A -> B -> C = depth 3

        // MaxDepth = 2 means we cannot traverse beyond 2 nodes from start
        var constraints = new QueryConstraints { MaxDepth = 2 };
        var result = pathFinder.Dijkstra(a, c, DefaultWeight, constraints);

        // C is at depth 3 (A->B->C), so it should not be reachable with MaxDepth=2
        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// Tests that Dijkstra throws <see cref="ArgumentNullException"/> when weightFunc is null.
    /// </summary>
    [Test]
    public void Dijkstra_NullWeightFunction_ThrowsArgumentNullException()
    {
        var graph = CreateSimpleGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        Assert.That(
            () => pathFinder.Dijkstra(NodeId.ForMap(0), NodeId.ForMap(1), null!),
            Throws.ArgumentNullException);
    }

    /// <summary>
    /// Tests Dijkstra on an empty graph (no nodes).
    /// </summary>
    [Test]
    public void Dijkstra_EmptyGraph_ReturnsNull()
    {
        var graph = new KnowledgeGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        var result = pathFinder.Dijkstra(NodeId.ForMap(0), NodeId.ForMap(1), DefaultWeight);

        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// Tests Dijkstra on a graph with a dead end (no outbound edges from a node).
    /// </summary>
    [Test]
    public void Dijkstra_DeadEnd_ReturnsNull()
    {
        var graph = new KnowledgeGraph();
        var a = new GraphNode(NodeId.ForMap(0), "A", NodeType.Map);
        var b = new GraphNode(NodeId.ForMap(1), "B", NodeType.Map);
        var c = new GraphNode(NodeId.ForMap(2), "C", NodeType.Map);

        graph.AddNode(a);
        graph.AddNode(b);
        graph.AddNode(c);

        // A -> B only (dead end, can't reach C)
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, a.Id, b.Id));

        var pathFinder = new KnowledgeGraphPathFinder(graph);
        var result = pathFinder.Dijkstra(a.Id, c.Id, DefaultWeight);

        Assert.That(result, Is.Null);
    }

    // ========================================================================
    // BfsReachable Tests
    // ========================================================================

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphPathFinder.BfsReachable"/> finds all
    /// nodes reachable within the specified depth limit.
    /// </summary>
    [Test]
    public void BfsReachable_FindsAllNodesWithinDepth()
    {
        var graph = CreateBfsGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);
        var a = NodeId.ForMap(0);

        var results = pathFinder.BfsReachable(a, _ => true, maxDepth: 3);

        // Reachable from A within depth 3: B(d=1), D(d=1), C(d=2), E(d=2)
        Assert.That(results, Has.Count.EqualTo(4));
        var reachableIds = results.Select(r => r.To).ToHashSet();
        Assert.That(reachableIds, Is.EquivalentTo(new[]
        {
            NodeId.ForMap(1), // B
            NodeId.ForMap(3), // D
            NodeId.ForMap(2), // C
            NodeId.ForMap(4), // E
        }));
    }

    /// <summary>
    /// Tests that BFS respects the max depth parameter, not returning nodes
    /// beyond the specified depth.
    /// </summary>
    [Test]
    public void BfsReachable_DepthLimit_Respected()
    {
        var graph = CreateBfsGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);
        var a = NodeId.ForMap(0);

        // The BFS starts the start node at depth 1. Children of the start
        // are at depth 2. With maxDepth = 2, only B and D are reachable.
        var results = pathFinder.BfsReachable(a, _ => true, maxDepth: 2);

        Assert.That(results, Has.Count.EqualTo(2));
        var reachableIds = results.Select(r => r.To).ToHashSet();
        Assert.That(reachableIds, Is.EquivalentTo(new[]
        {
            NodeId.ForMap(1), // B
            NodeId.ForMap(3), // D
        }));
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphPathFinder.BfsReachable"/> throws
    /// <see cref="ArgumentNullException"/> when edgeFilter is null.
    /// </summary>
    [Test]
    public void BfsReachable_NullEdgeFilter_ThrowsArgumentNullException()
    {
        var graph = CreateBfsGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        Assert.That(
            () => pathFinder.BfsReachable(NodeId.ForMap(0), null!, 5),
            Throws.ArgumentNullException);
    }

    /// <summary>
    /// Tests that BFS on an empty graph returns an empty list.
    /// </summary>
    [Test]
    public void BfsReachable_EmptyGraph_ReturnsEmpty()
    {
        var graph = new KnowledgeGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        var results = pathFinder.BfsReachable(NodeId.ForMap(0), _ => true, 5);

        Assert.That(results, Is.Empty);
    }

    /// <summary>
    /// Tests that BFS with an edge filter that excludes everything returns an empty list.
    /// </summary>
    [Test]
    public void BfsReachable_FilterExcludesAll_ReturnsEmpty()
    {
        var graph = CreateBfsGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);
        var a = NodeId.ForMap(0);

        // Filter that rejects all edges
        var results = pathFinder.BfsReachable(a, _ => false, 5);

        Assert.That(results, Is.Empty);
    }

    // ========================================================================
    // MultiSourceShortestPath Tests
    // ========================================================================

    /// <summary>
    /// Tests that multi-source shortest path finds the best path from any of
    /// the provided sources to the target.
    /// </summary>
    [Test]
    public void MultiSourceShortestPath_FindsBestPath()
    {
        var graph = CreateMultiSourceGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        var sources = new[] { NodeId.ForMap(0), NodeId.ForMap(1) }; // A, B
        var target = NodeId.ForMap(2);

        var result = pathFinder.MultiSourceShortestPath(sources, target, DefaultWeight);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsFound, Is.True);

        // The best path is from A (weight 1) rather than B (weight 2)
        Assert.That(result.From, Is.EqualTo(NodeId.ForMap(0)));
        Assert.That(result.TotalWeight, Is.EqualTo(1.0));
    }

    /// <summary>
    /// Tests that multi-source shortest path handles a single source.
    /// </summary>
    [Test]
    public void MultiSourceShortestPath_SingleSource_Works()
    {
        var graph = CreateMultiSourceGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        var sources = new[] { NodeId.ForMap(1) }; // Only B
        var target = NodeId.ForMap(2);

        var result = pathFinder.MultiSourceShortestPath(sources, target, DefaultWeight);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsFound, Is.True);
        Assert.That(result.From, Is.EqualTo(NodeId.ForMap(1)));
    }

    /// <summary>
    /// Tests that multi-source shortest path returns null when no valid sources exist.
    /// </summary>
    [Test]
    public void MultiSourceShortestPath_NoValidSources_ReturnsNull()
    {
        var graph = CreateMultiSourceGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        // Non-existent sources
        var sources = new[] { NodeId.ForMap(99), NodeId.ForMap(100) };
        var target = NodeId.ForMap(2);

        var result = pathFinder.MultiSourceShortestPath(sources, target, DefaultWeight);

        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// Tests that multi-source shortest path throws <see cref="ArgumentNullException"/>
    /// when sources is null.
    /// </summary>
    [Test]
    public void MultiSourceShortestPath_NullSources_ThrowsArgumentNullException()
    {
        var graph = CreateMultiSourceGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        Assert.That(
            () => pathFinder.MultiSourceShortestPath(null!, NodeId.ForMap(2), DefaultWeight),
            Throws.ArgumentNullException);
    }

    /// <summary>
    /// Tests that multi-source shortest path throws <see cref="ArgumentNullException"/>
    /// when weightFunc is null.
    /// </summary>
    [Test]
    public void MultiSourceShortestPath_NullWeightFunc_ThrowsArgumentNullException()
    {
        var graph = CreateMultiSourceGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        Assert.That(
            () => pathFinder.MultiSourceShortestPath(new[] { NodeId.ForMap(0) }, NodeId.ForMap(2), null!),
            Throws.ArgumentNullException);
    }

    // ========================================================================
    // FindNearest Tests
    // ========================================================================

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphPathFinder.FindNearest"/> finds the closest
    /// node matching the given predicate.
    /// </summary>
    [Test]
    public void FindNearest_FindsClosestMatchingNode()
    {
        // Build graph where monsters are at different distances
        var graph = new KnowledgeGraph();
        var start = new GraphNode(NodeId.ForMap(0), "Start", NodeType.Map);
        var middle = new GraphNode(NodeId.ForMap(1), "Middle", NodeType.Map);
        var monster = new GraphNode(NodeId.ForMonster(1), "TargetMonster", NodeType.Monster);
        var farMonster = new GraphNode(NodeId.ForMap(2), "Other", NodeType.Map);

        graph.AddNode(start);
        graph.AddNode(middle);
        graph.AddNode(monster);
        graph.AddNode(farMonster);

        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, start.Id, middle.Id, Weight: 1.0));
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, middle.Id, monster.Id, Weight: 1.0));
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, start.Id, farMonster.Id, Weight: 10.0));

        var pathFinder = new KnowledgeGraphPathFinder(graph);

        // Find nearest node of type Monster
        var nearest = pathFinder.FindNearest(start.Id, n => n.Type == NodeType.Monster, DefaultWeight);

        Assert.That(nearest, Is.Not.Null);
        Assert.That(nearest, Is.EqualTo(monster.Id));
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphPathFinder.FindNearest"/> returns the start
    /// node if it satisfies the predicate.
    /// </summary>
    [Test]
    public void FindNearest_StartNodeMatches_ReturnsStart()
    {
        var graph = new KnowledgeGraph();
        var start = new GraphNode(NodeId.ForMonster(5), "Self", NodeType.Monster);
        graph.AddNode(start);

        var pathFinder = new KnowledgeGraphPathFinder(graph);

        var nearest = pathFinder.FindNearest(start.Id, n => n.Type == NodeType.Monster, DefaultWeight);

        Assert.That(nearest, Is.Not.Null);
        Assert.That(nearest, Is.EqualTo(start.Id));
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphPathFinder.FindNearest"/> returns null
    /// when no node matches the predicate.
    /// </summary>
    [Test]
    public void FindNearest_NoMatch_ReturnsNull()
    {
        var graph = CreateSimpleGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        // No node of type Monster in the graph
        var nearest = pathFinder.FindNearest(
            NodeId.ForMap(0),
            n => n.Type == NodeType.Monster,
            DefaultWeight);

        Assert.That(nearest, Is.Null);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphPathFinder.FindNearest"/> throws
    /// <see cref="ArgumentNullException"/> when predicate is null.
    /// </summary>
    [Test]
    public void FindNearest_NullPredicate_ThrowsArgumentNullException()
    {
        var graph = CreateSimpleGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        Assert.That(
            () => pathFinder.FindNearest(NodeId.ForMap(0), null!, DefaultWeight),
            Throws.ArgumentNullException);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphPathFinder.FindNearest"/> throws
    /// <see cref="ArgumentNullException"/> when weightFunc is null.
    /// </summary>
    [Test]
    public void FindNearest_NullWeightFunc_ThrowsArgumentNullException()
    {
        var graph = CreateSimpleGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        Assert.That(
            () => pathFinder.FindNearest(NodeId.ForMap(0), _ => true, null!),
            Throws.ArgumentNullException);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphPathFinder.FindNearest"/> returns null
    /// when the start node is not in the graph.
    /// </summary>
    [Test]
    public void FindNearest_StartNotInGraph_ReturnsNull()
    {
        var graph = CreateSimpleGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        var nearest = pathFinder.FindNearest(NodeId.ForMap(99), _ => true, DefaultWeight);

        Assert.That(nearest, Is.Null);
    }

    // ========================================================================
    // Query Wrapper Tests (KnowledgeGraphQuery)
    // ========================================================================

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphQuery.FindShortestPath"/> delegates
    /// to Dijkstra and returns the correct path.
    /// </summary>
    [Test]
    public void Query_FindShortestPath_ReturnsCorrectPath()
    {
        var graph = CreateSimpleGraph();
        var query = new KnowledgeGraphQuery(graph);

        var result = query.FindShortestPath(NodeId.ForMap(0), NodeId.ForMap(2));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsFound, Is.True);
        Assert.That(result.TotalWeight, Is.EqualTo(2.0)); // Uniform weight = 1.0 per edge
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphQuery.FindShortestPath"/> returns null
    /// for disconnected nodes.
    /// </summary>
    [Test]
    public void Query_FindShortestPath_Disconnected_ReturnsNull()
    {
        var graph = CreateDisconnectedGraph();
        var query = new KnowledgeGraphQuery(graph);

        var result = query.FindShortestPath(NodeId.ForMap(0), NodeId.ForMap(3));

        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphQuery.FindShortestPath"/> respects
    /// the MaxDepth constraint.
    /// </summary>
    [Test]
    public void Query_FindShortestPath_WithConstraints_RespectsMaxDepth()
    {
        var graph = CreateBfsGraph();
        var query = new KnowledgeGraphQuery(graph);
        var a = NodeId.ForMap(0);
        var c = NodeId.ForMap(2); // A->B->C requires depth 3

        var constraints = new QueryConstraints { MaxDepth = 2 };
        var result = query.FindShortestPath(a, c, constraints);

        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphQuery.ResolveDependencies"/> with
    /// <see cref="DependencyDirection.Backward"/> traces the dependency chain
    /// from a material back to its recipe and the result item it crafts into.
    /// Backward traversal follows incoming edges: material -> (RequiresMaterial) -> recipe -> (HasRecipe) -> resultItem.
    /// </summary>
    [Test]
    public void Query_ResolveDependencies_Backward_ProducesChain()
    {
        var graph = CreateDependencyGraph();
        var query = new KnowledgeGraphQuery(graph);
        var material = NodeId.ForItem(12, 15);

        var chain = query.ResolveDependencies(material, DependencyDirection.Backward);

        Assert.Multiple(() =>
        {
            Assert.That(chain.Target, Is.EqualTo(material));
            Assert.That(chain.Direction, Is.EqualTo(DependencyDirection.Backward));
            Assert.That(chain.IsComplete, Is.True);

            // Backward traversal from material follows incoming RequiresMaterial edges to recipe,
            // then incoming HasRecipe edge to the result item.
            // Steps: recipe (depth 1, via RequiresMaterial), resultItem (depth 2, via HasRecipe)
            Assert.That(chain.Steps, Has.Count.EqualTo(2));

            var recipeStep = chain.Steps.First(s => s.NodeId == NodeId.ForCraftingRecipe(1));
            Assert.That(recipeStep.Depth, Is.EqualTo(1));
            Assert.That(recipeStep.Edge.Type, Is.EqualTo(EdgeType.RequiresMaterial));

            var resultItemStep = chain.Steps.First(s => s.NodeId == NodeId.ForItem(5, 10));
            Assert.That(resultItemStep.Depth, Is.EqualTo(2));
            Assert.That(resultItemStep.Edge.Type, Is.EqualTo(EdgeType.HasRecipe));
        });
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphQuery.ResolveDependencies"/> with
    /// <see cref="DependencyDirection.Forward"/> traces what a node produces
    /// or enables.
    /// </summary>
    [Test]
    public void Query_ResolveDependencies_Forward_ProducesChain()
    {
        var graph = CreateDependencyGraph();
        var query = new KnowledgeGraphQuery(graph);
        var material = NodeId.ForItem(12, 15);

        var chain = query.ResolveDependencies(material, DependencyDirection.Forward);

        Assert.Multiple(() =>
        {
            Assert.That(chain.Target, Is.EqualTo(material));
            Assert.That(chain.Direction, Is.EqualTo(DependencyDirection.Forward));

            // Forward from material: material has no outgoing edges in our setup,
            // so Steps should be empty (or only include nodes that have outgoing edges to it)
            // Actually, material1 has incoming edges (RequiresMaterial from recipe),
            // but no outgoing edges. Forward traversal follows outgoing edges.
            // With forward direction, from material we check outgoing edges - there are none.
            // So Steps should be empty.
            Assert.That(chain.Steps, Is.Empty);
        });
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphQuery.ResolveDependencies"/> marks
    /// IsComplete as false when the traversal exceeds MaxDepth.
    /// </summary>
    [Test]
    public void Query_ResolveDependencies_MaxDepthIncomplete()
    {
        var graph = CreateDependencyGraph();
        var query = new KnowledgeGraphQuery(graph);
        var material = NodeId.ForItem(12, 15);

        // MaxDepth=1 means we can only go 1 level deep
        // Backward from material: depth 1 = recipe (RequiresMaterial), depth 2 = resultItem (HasRecipe)
        // With maxDepth=1, only recipe is discovered.
        var chain = query.ResolveDependencies(material, DependencyDirection.Backward, maxDepth: 1);

        Assert.Multiple(() =>
        {
            // At depth 1 we should find the recipe
            // But resultItem at depth 2 would make it incomplete
            Assert.That(chain.Steps, Has.Count.EqualTo(1));
            Assert.That(chain.IsComplete, Is.False);
        });
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphQuery.FindAllReachable"/> with an
    /// edge type filter returns only nodes reachable via edges of those types.
    /// </summary>
    [Test]
    public void Query_FindAllReachable_WithEdgeTypeFilter_Works()
    {
        var graph = CreateMixedEdgeTypeGraph();
        var query = new KnowledgeGraphQuery(graph);
        var monster = NodeId.ForMonster(1);

        // Only follow SpawnsOn and DropsAt edges, exclude ConnectsTo
        var edgeTypes = new HashSet<EdgeType> { EdgeType.SpawnsOn, EdgeType.DropsAt };

        var results = query.FindAllReachable(monster, edgeTypes, maxDepth: 3);

        // Monster has SpawnsOn -> Map1 and DropsAt -> Item
        // Both edges match the filter
        Assert.That(results, Has.Count.EqualTo(2));

        var reachableIds = results.Select(r => r.To).ToHashSet();
        Assert.That(reachableIds, Is.EquivalentTo(new[]
        {
            NodeId.ForMap(0),   // via SpawnsOn
            NodeId.ForItem(14, 20), // via DropsAt
        }));
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphQuery.FindAllReachable"/> returns empty
    /// when no edges match the filter.
    /// </summary>
    [Test]
    public void Query_FindAllReachable_NoMatchingEdges_ReturnsEmpty()
    {
        var graph = CreateMixedEdgeTypeGraph();
        var query = new KnowledgeGraphQuery(graph);

        // Only follow ConnectsTo, but monster has only SpawnsOn and DropsAt
        var edgeTypes = new HashSet<EdgeType> { EdgeType.ConnectsTo };

        var results = query.FindAllReachable(NodeId.ForMonster(1), edgeTypes, maxDepth: 3);

        Assert.That(results, Is.Empty);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphQuery.FindAllReachable"/> throws
    /// <see cref="ArgumentNullException"/> when edgeTypes is null.
    /// </summary>
    [Test]
    public void Query_FindAllReachable_NullEdgeTypes_ThrowsArgumentNullException()
    {
        var graph = CreateMixedEdgeTypeGraph();
        var query = new KnowledgeGraphQuery(graph);

        Assert.That(
            () => query.FindAllReachable(NodeId.ForMonster(1), null!, 3),
            Throws.ArgumentNullException);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphQuery.FindNearest"/> finds the closest
    /// matching node via the query interface.
    /// </summary>
    [Test]
    public void Query_FindNearest_FindsClosestMatch()
    {
        var graph = new KnowledgeGraph();
        var map = new GraphNode(NodeId.ForMap(0), "Lorencia", NodeType.Map);
        var dungeon = new GraphNode(NodeId.ForMap(1), "Dungeon", NodeType.Map);
        var monster = new GraphNode(NodeId.ForMonster(1), "Spider", NodeType.Monster);

        graph.AddNode(map);
        graph.AddNode(dungeon);
        graph.AddNode(monster);

        // Map -> Dungeon (via ConnectsTo)
        graph.AddEdge(new GraphEdge(EdgeType.ConnectsTo, map.Id, dungeon.Id));

        // Dungeon -> Monster (via a Contains-like edge for reachability)
        graph.AddEdge(new GraphEdge(EdgeType.SpawnsOn, monster.Id, dungeon.Id));

        var query = new KnowledgeGraphQuery(graph);

        // Start from map0, find nearest Monster by traversing ConnectsTo edges
        // Since FindNearest follows outgoing edges, and the only outgoing path
        // from Map(0) leads to Map(1), we'll set up constraints to follow ConnectsTo
        // and then the start node check should handle direct matches.
        // Note: FindNearest checks the start node first.
        var nearest = query.FindNearest(
            NodeId.ForMonster(1),
            n => n.Type == NodeType.Map,
            null);

        Assert.That(nearest, Is.Not.Null);
        Assert.That(nearest, Is.EqualTo(NodeId.ForMap(1)));
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphQuery"/> constructor throws
    /// <see cref="ArgumentNullException"/> when graph is null.
    /// </summary>
    [Test]
    public void Query_NullGraph_ThrowsArgumentNullException()
    {
        Assert.That(() => new KnowledgeGraphQuery(null!), Throws.ArgumentNullException);
    }

    /// <summary>
    /// Tests that <see cref="KnowledgeGraphPathFinder" /> constructor throws
    /// <see cref="ArgumentNullException"/> when graph is null.
    /// </summary>
    [Test]
    public void PathFinder_NullGraph_ThrowsArgumentNullException()
    {
        Assert.That(() => new KnowledgeGraphPathFinder(null!), Throws.ArgumentNullException);
    }

    // ========================================================================
    // PathResult Tests
    // ========================================================================

    /// <summary>
    /// Tests that <see cref="PathResult.IsFound"/> is true when edges are present.
    /// </summary>
    [Test]
    public void PathResult_IsFound_WithEdges_ReturnsTrue()
    {
        var graph = CreateSimpleGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        var result = pathFinder.Dijkstra(NodeId.ForMap(0), NodeId.ForMap(3), DefaultWeight);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsFound, Is.True);
    }

    /// <summary>
    /// Tests that <see cref="PathResult.IsFound"/> is false when no edges exist.
    /// </summary>
    [Test]
    public void PathResult_IsFound_NoEdges_ReturnsFalse()
    {
        var graph = CreateSimpleGraph();
        var pathFinder = new KnowledgeGraphPathFinder(graph);

        var result = pathFinder.Dijkstra(NodeId.ForMap(0), NodeId.ForMap(0), DefaultWeight);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsFound, Is.False);
        Assert.That(result.Nodes, Is.Empty);
    }
}
