# MUnique.OpenMU.Orchestrator -- DAG Execution Engine

A generic **Directed Acyclic Graph (DAG)** execution engine extracted from the [OpenMU AI Player System (OAPS)](https://github.com/MUnique/OpenMU). This library provides topological sorting via Kahn's algorithm, cycle-safe dependency resolution, node-isolated execution, typed shared context, a pub-sub event bus, and built-in telemetry with bounded history.

**Zero game dependencies.** The only external package is `Microsoft.Extensions.Logging.Abstractions`. Targets `net10.0`.

---

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [Quick Start](#quick-start)
- [Core Concepts](#core-concepts)
- [API Reference](#api-reference)
  - [Abstractions](#abstractions)
  - [Execution Engine](#execution-engine)
  - [Configuration](#configuration)
  - [Telemetry](#telemetry)
- [Node Configuration](#node-configuration)
- [Event Bus](#event-bus)
- [Telemetry and Debugging](#telemetry-and-debugging)
- [NuGet Publishing](#nuget-publishing)
- [Test Coverage](#test-coverage)

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────┐
│                    ExecutionGraph                     │
│  ┌──────────┐   ┌──────────┐   ┌──────────┐        │
│  │  Node A  │──>│  Node B  │──>│  Node C  │        │
│  │ (survival)│   │ (combat) │   │ (loot)   │        │
│  └──────────┘   └──────────┘   └──────────┘        │
│       │                                              │
│  Kahn's Algorithm ──> Topological Order              │
│  Cycle Detection ──> InvalidOperationException       │
│  Cached Order ──> Invalidated on structural change   │
└───────────────────────┬─────────────────────────────┘
                        │
┌───────────────────────▼─────────────────────────────┐
│                    GraphExecutor                      │
│  Sequential execution in topological order           │
│  Per-node error isolation (failures don't propagate) │
│  Telemetry injection (per-node timing)               │
│  Cycle numbering for multi-cycle correlation         │
└───────────────────────┬─────────────────────────────┘
                        │
┌───────────────────────▼─────────────────────────────┐
│                  IExecutionContext                    │
│  ┌──────────┐  ┌──────────┐  ┌───────────────────┐ │
│  │ GetState │  │ SetState │  │  IEventBus         │ │
│  │  <T>()   │  │  <T>()   │  │  (pub-sub)         │ │
│  └──────────┘  └──────────┘  └───────────────────┘ │
│                       ┌───────────────────┐         │
│                       │  ITelemetrySink    │         │
│                       │  (per-node timing) │         │
│                       └───────────────────┘         │
└─────────────────────────────────────────────────────┘
```

### Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Sequential (not parallel) execution** | Simplifies state sharing; nodes communicate via typed context, not shared memory |
| **Error isolation** | A failing node does not abort sibling or downstream nodes; failures are recorded in telemetry |
| **Cached topological order** | Execution order is computed once and cached until the graph structure changes (O(V+E) amortized) |
| **Typed state bag** | Nodes share state via `GetState<T>()` / `SetState<T>()` -- type-safe, no string keys |
| **Bounded telemetry history** | History is capped at 1024 cycles to prevent unbounded memory growth |

---

## Quick Start

```csharp
using MUnique.OpenMU.Orchestrator;

// 1. Define a node
sealed class MyNode(string name, string[] dependsOn) : IExecutionNode
{
    public string Name => name;
    public bool Enabled { get; set; } = true;
    public IReadOnlySet<string> DependsOn { get; } = new HashSet<string>(dependsOn);

    public async ValueTask ExecuteAsync(IExecutionContext context)
    {
        // Simulate work; read/write shared state via context
        var counter = context.GetState<int>() ?? 0;
        context.SetState(counter + 1);
        await Task.Delay(10);
    }
}

// 2. Build the graph
var graph = new ExecutionGraph();
graph.AddNode(new MyNode("validate", []));
graph.AddNode(new MyNode("process", ["validate"]));
graph.AddNode(new MyNode("save", ["process"]));

// 3. Create execution context
var context = new ExecutionContext();

// 4. Execute (auto-ordered: validate -> process -> save)
var executor = new GraphExecutor();
await executor.ExecuteAsync(graph, context);

// 5. Read telemetry
foreach (var exec in context.Telemetry.GetCurrentCycle())
    Console.WriteLine($"{exec.NodeName}: {exec.Duration.TotalMilliseconds}ms");
```

**Output:**

```
validate: 10.12ms
process: 10.08ms
save: 10.21ms
```

---

## Core Concepts

### Directed Acyclic Graph (DAG)

Each node in the graph represents an executable unit. Edges represent dependencies: if B depends on A, A must execute before B. The graph **must** be acyclic -- circular dependencies cause `InvalidOperationException` when `GetExecutionOrder()` is called.

### Topological Sort (Kahn's Algorithm)

The engine computes the execution order using **Kahn's algorithm** in **O(V + E)** time. The result is cached as an `ImmutableArray` and automatically invalidated when nodes are added or removed. This means the sorting overhead is paid only once per structural change.

```
Algorithm: Kahn's Algorithm
1. Compute in-degree for each node
2. Enqueue all nodes with in-degree 0
3. While queue is not empty:
   a. Dequeue node, add to sorted order
   b. Decrement in-degree of its dependents
   c. If any dependent's in-degree reaches 0, enqueue it
4. If sorted count < total nodes: cycle detected -> throw
```

### Execution Cycle

`GraphExecutor.ExecuteAsync()` runs nodes sequentially in topological order:

1. Skip disabled nodes (`node.Enabled == false`)
2. Call `telemetry.OnNodeStarted(node.Name)`
3. Start `Stopwatch`
4. Execute `node.ExecuteAsync(context)`
5. Catch exceptions (do not propagate -- subsequent nodes continue)
6. Stop timer, call `telemetry.OnNodeCompleted(node.Name, elapsed, error)`
7. After all nodes, finalize the cycle in the telemetry sink

### Error Isolation

Node failures are isolated by design:

```csharp
// If "process" throws, "save" still executes
// The exception is logged and recorded in telemetry
// but does not abort the cycle
```

### Dependency Declaration

Nodes declare dependencies via the `DependsOn` property:

```csharp
// Simple: string-based dependency names
public IReadOnlySet<string> DependsOn => new HashSet<string> { "survival", "buff" };

// The engine resolves these names against registered nodes
// and produces the correct execution order automatically
```

---

## API Reference

### Abstractions

#### `IExecutionNode`

The fundamental building block. Every executable unit in the graph must implement this interface.

```csharp
public interface IExecutionNode
{
    string Name { get; }
    bool Enabled { get; }
    IReadOnlySet<string> DependsOn { get; }
    ValueTask ExecuteAsync(IExecutionContext context);
}
```

| Member | Description |
|--------|-------------|
| `Name` | Unique identifier used for dependency resolution, config lookup, and telemetry |
| `Enabled` | When `false`, the executor skips this node entirely |
| `DependsOn` | Set of node names that must execute before this one; empty set for root nodes |
| `ExecuteAsync(ctx)` | Called once per execution cycle; receives shared context |

#### `IExecutionContext`

Shared context passed to every node during an execution cycle.

```csharp
public interface IExecutionContext
{
    T? GetState<T>() where T : class;
    void SetState<T>(T value) where T : class;
    IEventBus Events { get; }
    ITelemetrySink Telemetry { get; }
}
```

| Member | Description |
|--------|-------------|
| `GetState<T>()` | Retrieves shared state by type; returns `null` if not set |
| `SetState<T>(value)` | Stores shared state by type; overwrites existing state of the same type |
| `Events` | Pub-sub event bus for decoupled node-to-node communication |
| `Telemetry` | Telemetry sink for recording per-node execution metrics |

#### `IEventBus`

Decoupled publish-subscribe mechanism between nodes.

```csharp
public interface IEventBus
{
    void Subscribe(string eventType, Func<ValueTask> handler);
    ValueTask PublishAsync(string eventType);
}
```

| Member | Description |
|--------|-------------|
| `Subscribe(eventType, handler)` | Registers an async handler for the given event type |
| `PublishAsync(eventType)` | Invokes all registered handlers for the given event type |

#### `ITelemetrySink`

Records per-node execution metrics across cycles.

```csharp
public interface ITelemetrySink
{
    void OnNodeStarted(string nodeName);
    void OnNodeCompleted(string nodeName, TimeSpan duration, Exception? error = null);
    IReadOnlyList<INodeExecution> GetCurrentCycle();
    IReadOnlyList<ICycleExecution> GetHistory();
}
```

| Member | Description |
|--------|-------------|
| `OnNodeStarted(name)` | Called by executor before `ExecuteAsync` |
| `OnNodeCompleted(name, duration, error)` | Called by executor after `ExecuteAsync` completes |
| `GetCurrentCycle()` | Returns snapshot of node executions in the current cycle |
| `GetHistory()` | Returns execution history across all completed cycles |

#### `INodeExecution`

Record of a single node execution.

```csharp
public interface INodeExecution
{
    string NodeName { get; }
    TimeSpan Duration { get; }
    Exception? Error { get; }
}
```

#### `ICycleExecution`

Record of a full execution cycle.

```csharp
public interface ICycleExecution
{
    long CycleNumber { get; }
    DateTime StartedAt { get; }
    TimeSpan TotalDuration { get; }
    IReadOnlyList<INodeExecution> NodeExecutions { get; }
}
```

### Execution Engine

#### `ExecutionGraph`

The DAG container. Manages nodes, computes topological order, and detects cycles.

```csharp
public sealed class ExecutionGraph
{
    // Properties
    public IReadOnlyList<IExecutionNode> Nodes { get; }

    // Mutation
    public void AddNode(IExecutionNode node);
    public void AddRange(IEnumerable<IExecutionNode> nodes);

    // Ordering
    public IReadOnlyList<IExecutionNode> GetExecutionOrder();

    // Validation
    public bool TryValidate(out string? error);

    // Cache control
    public void InvalidateCache();
}
```

| Method | Description |
|--------|-------------|
| `AddNode(node)` | Registers a node; if a node with the same name already exists, it is replaced |
| `AddRange(nodes)` | Bulk registers multiple nodes |
| `GetExecutionOrder()` | Returns nodes in topological order; throws `InvalidOperationException` on cycle detection; result is cached |
| `TryValidate(out error)` | Validates the graph without throwing; returns `false` + error message on cycle |
| `InvalidateCache()` | Forces re-computation on next `GetExecutionOrder()` call |

**Caching behavior:**

```csharp
var graph = new ExecutionGraph();
graph.AddNode(nodeA);
graph.AddNode(nodeB);

// First call computes and caches
var order1 = graph.GetExecutionOrder();

// Second call returns cached result (O(1))
var order2 = graph.GetExecutionOrder();

// Mutation invalidates the cache
graph.AddNode(nodeC);

// Third call re-computes
var order3 = graph.GetExecutionOrder();
```

**Cycle detection example:**

```csharp
graph.AddNode(new MyNode("A", ["B"]));
graph.AddNode(new MyNode("B", ["A"]));

// Throws InvalidOperationException:
// "Cycle detected in execution graph. Nodes involved: A, B"
var order = graph.GetExecutionOrder();

// Safe alternative:
if (!graph.TryValidate(out var error))
    Console.WriteLine($"Graph invalid: {error}");
```

#### `GraphExecutor`

Sequential executor that runs all enabled nodes in topological order.

```csharp
public sealed class GraphExecutor
{
    public GraphExecutor(ILogger<GraphExecutor>? logger = null);

    public async ValueTask ExecuteAsync(ExecutionGraph graph, IExecutionContext context);
    public long CycleNumber { get; }
}
```

| Method | Description |
|--------|-------------|
| `ExecuteAsync(graph, context)` | Runs enabled nodes in topological order; errors are isolated; telemetry recorded |
| `CycleNumber` | Monotonically increasing counter, incremented on each `ExecuteAsync` call |

**Execution rules:**

1. Nodes are executed **sequentially** in the order returned by `GetExecutionOrder()`
2. Disabled nodes (`Enabled == false`) are **skipped** silently
3. If a node throws, the exception is **caught** and recorded in telemetry; the next node still runs
4. The executor calls `telemetry.OnNodeStarted` before execution and `telemetry.OnNodeCompleted` after (including on failure)
5. After all nodes, if the telemetry sink is a `TelemetrySink`, `CompleteCycle` is called to finalize the cycle

### Configuration

#### `GraphConfig`

Serializable graph-level configuration for per-node enable/disable and parameters.

```csharp
public sealed class GraphConfig
{
    public Dictionary<string, bool> Enabled { get; init; }
    public Dictionary<string, Dictionary<string, object>> NodeConfigs { get; init; }

    public bool IsEnabled(string nodeName, bool defaultEnabled);
    public NodeConfig GetNodeConfig(string nodeName);
}
```

| Member | Description |
|--------|-------------|
| `Enabled` | Per-node override map; key = node name, value = enabled |
| `NodeConfigs` | Per-node parameter dictionaries; key = node name, value = parameter map |
| `IsEnabled(name, default)` | Returns enabled status with fallback to `defaultEnabled` |
| `GetNodeConfig(name)` | Returns typed config accessor; returns `NodeConfig.Empty` if not found |

#### `NodeConfig`

Typed accessor for a single node's configuration parameters.

```csharp
public sealed class NodeConfig
{
    public static NodeConfig Empty { get; }

    public int GetInt(string key, int defaultValue = 0);
    public float GetFloat(string key, float defaultValue = 0f);
    public bool GetBool(string key, bool defaultValue = false);
    public string? GetString(string key, string? defaultValue = null);
}
```

All getters return `defaultValue` when the key is not present or the value is not the expected type.

**Usage example:**

```csharp
var config = new GraphConfig
{
    Enabled = new() { ["combat"] = false },
    NodeConfigs = new()
    {
        ["navigation"] = new() { ["patrol_radius"] = 30, ["enable_logging"] = true }
    }
};

// Apply configuration to graph
foreach (var node in graph.Nodes)
{
    node.Enabled = config.IsEnabled(node.Name, defaultEnabled: true);
    var nodeConfig = config.GetNodeConfig(node.Name);
    var radius = nodeConfig.GetInt("patrol_radius", 20);
    var logging = nodeConfig.GetBool("enable_logging", false);
}
```

### Telemetry

#### `TelemetrySink`

Default `ITelemetrySink` implementation with bounded history.

```csharp
public sealed class TelemetrySink : ITelemetrySink
{
    // ITelemetrySink implementation
    public void OnNodeStarted(string nodeName);
    public void OnNodeCompleted(string nodeName, TimeSpan duration, Exception? error = null);
    public IReadOnlyList<INodeExecution> GetCurrentCycle();
    public IReadOnlyList<ICycleExecution> GetHistory();

    // Extended API
    public IReadOnlyList<INodeExecution> DrainCurrentCycle();
    internal void CompleteCycle(long cycleNumber, DateTime startedAt);
}
```

| Member | Description |
|--------|-------------|
| `GetCurrentCycle()` | Read-only snapshot of the current cycle's node executions |
| `DrainCurrentCycle()` | Returns and clears the current cycle's records (for custom recording systems) |
| `GetHistory()` | Returns all completed cycles; bounded at 1024 entries (oldest dropped first) |
| `CompleteCycle(cycleNumber, startedAt)` | Finalizes the current cycle and moves it into history; called automatically by `GraphExecutor` |

#### `GraphSnapshot`

Immutable snapshot of a single execution cycle.

```csharp
public sealed record GraphSnapshot
{
    public required long CycleNumber { get; init; }
    public required DateTime Timestamp { get; init; }
    public required TimeSpan TotalDuration { get; init; }
    public required IReadOnlyList<INodeExecution> NodeExecutions { get; init; }
    public required IReadOnlyList<string> ExecutionOrder { get; init; }
}
```

Designed for serialization and export. The `ExecutionOrder` field captures the exact topological order used during that cycle.

---

## Node Configuration

The configuration system provides runtime control over node behavior without code changes. It works in two layers:

1. **Enable/disable** -- turn individual nodes on or off per cycle
2. **Parameters** -- inject typed parameters into nodes at runtime

```csharp
// JSON-serializable configuration object
var config = new GraphConfig
{
    Enabled = new()
    {
        ["combat"] = false,
        ["navigation"] = true
    },
    NodeConfigs = new()
    {
        ["navigation"] = new()
        {
            ["patrol_radius"] = 50,
            ["patrol_speed"] = 1.5f,
            ["enable_debug"] = true
        }
    }
};

// Apply to graph before execution
foreach (var node in graph.Nodes)
{
    node.Enabled = config.IsEnabled(node.Name, defaultEnabled: true);
}
```

---

## Event Bus

The event bus enables decoupled communication between nodes. One node can publish an event, and any number of other nodes can subscribe to react.

```csharp
// Subscriber
context.Events.Subscribe("item_collected", async () =>
{
    Console.WriteLine("Item collected event received!");
    await Task.CompletedTask;
});

// Publisher (in another node)
await context.Events.PublishAsync("item_collected");
```

**Characteristics:**
- Thread-safe for the current sequential execution model
- Handlers are invoked in subscription order
- Async handlers are awaited before returning
- No built-in event payload; use `IExecutionContext.GetState/SetState` for data transfer

---

## Telemetry and Debugging

The `TelemetrySink` provides three ways to access execution data:

```csharp
// Method 1: Read current cycle (non-destructive)
var current = sink.GetCurrentCycle();
foreach (var exec in current)
    Console.WriteLine($"{exec.NodeName}: {exec.Duration.TotalMilliseconds}ms");

// Method 2: Drain current cycle (consumes and clears -- for custom recording)
var drained = sink.DrainCurrentCycle();

// Method 3: Query history (bounded at 1024 cycles)
var history = sink.GetHistory();
foreach (var cycle in history)
    Console.WriteLine($"Cycle {cycle.CycleNumber}: {cycle.TotalDuration.TotalMilliseconds}ms total");
```

For serialization and analysis, use `GraphSnapshot`:

```csharp
var current = sink.GetCurrentCycle();
var snapshot = new GraphSnapshot
{
    CycleNumber = executor.CycleNumber,
    Timestamp = DateTime.UtcNow,
    TotalDuration = TimeSpan.FromTicks(current.Sum(e => e.Duration.Ticks)),
    NodeExecutions = current,
    ExecutionOrder = graph.Nodes.Select(n => n.Name).ToArray()
};

// Serialize to JSON, export, etc.
var json = JsonSerializer.Serialize(snapshot);
```

---

## NuGet Publishing

```bash
# Build Release configuration
dotnet build src/Orchestrator -c Release

# Create NuGet package
dotnet pack src/Orchestrator -c Release -o ./nupkg

# Push to NuGet.org (replace with your API key)
dotnet nuget push ./nupkg/MUnique.OpenMU.Orchestrator.*.nupkg \
    --source https://api.nuget.org/v3/index.json \
    --api-key YOUR_API_KEY
```

### Package Metadata

The project file (`MUnique.OpenMU.Orchestrator.csproj`) includes the following NuGet metadata:

| Field | Value |
|-------|-------|
| `PackageId` | `MUnique.OpenMU.Orchestrator` |
| `Version` | `1.0.0` |
| `Authors` | MUnique |
| `License` | MIT |
| `ProjectUrl` | <https://munique.net> |
| `RepositoryUrl` | <https://github.com/MUnique/OpenMU/tree/master/src/Orchestrator> |
| `Tags` | `MUnique`, `OpenMU`, `Orchestrator`, `DAG`, `dependency-graph`, `execution-engine` |
| `Readme` | `README.md` (included in package) |

The README is bundled into the NuGet package automatically via:

```xml
<None Include="README.md" Pack="true" PackagePath="\" />
```

---

## Test Coverage

The library is covered by **34 unit tests** spanning:

- **Topological Sort**: Correct ordering for linear, diamond, and complex DAGs
- **Cycle Detection**: Immediate throw on direct, indirect, and self-referencing cycles
- **Execution Order Caching**: Cache hits, invalidation on node add/replace, selective invalidation
- **Graph Validation**: `TryValidate` returns correct success/failure, error messages
- **Node Execution**: Sequential order, disabled nodes skipped, error isolation
- **Telemetry**: Per-node timing, cycle history, drain semantics, bounded capacity
- **Event Bus**: Subscribe, publish, handler invocation order, no handler on unknown event
- **Configuration**: `GraphConfig.IsEnabled` fallback, `NodeConfig` typed accessors, missing keys

All tests target `net10.0` and use `xUnit` (or the project's chosen test framework).

---

## Project Structure

```
src/Orchestrator/
  MUnique.OpenMU.Orchestrator.csproj
  README.md
  Abstractions/
    IExecutionNode.cs          # Node interface
    IExecutionContext.cs       # Context interface
    IEventBus.cs               # Event bus interface
    ITelemetrySink.cs          # Telemetry interface + INodeExecution, ICycleExecution
  Config/
    GraphConfig.cs             # Serializable graph-level configuration
    NodeConfig.cs              # Typed per-node configuration accessor
  Core/
    ExecutionGraph.cs          # DAG container with Kahn's algorithm
    GraphExecutor.cs           # Sequential executor with error isolation
  Telemetry/
    TelemetrySink.cs           # Default telemetry implementation
    GraphSnapshot.cs           # Immutable cycle snapshot record
```
