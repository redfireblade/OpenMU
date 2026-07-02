# MUnique.OpenMU.Orchestrator -- Release Notes

## v1.0.0 (Initial Release)

### Overview

`MUnique.OpenMU.Orchestrator` is a generic **Directed Acyclic Graph (DAG)** execution engine extracted from the OpenMU AI Player System (OAPS). It provides topological sorting via Kahn's algorithm, cycle-safe dependency resolution, node-isolated execution, typed shared context, a pub-sub event bus, and built-in telemetry with bounded history.

### Features

- **DAG Topological Sort (Kahn's Algorithm)**
  - O(V+E) computation, cached as `ImmutableArray`
  - Automatic cache invalidation on structural changes (node add/replace/remove)
  - Deterministic execution order across cycles

- **Cycle Detection**
  - `InvalidOperationException` on direct, indirect, or self-referencing cycles
  - Non-throwing validation via `TryValidate(out string? error)`

- **Node-Isolated Execution**
  - Per-node error isolation -- a failing node does not abort sibling or downstream nodes
  - Disabled nodes (`Enabled == false`) are silently skipped
  - Execution continues through all enabled nodes regardless of individual failures

- **Typed Shared Context (`IExecutionContext`)**
  - Type-safe state bag via `GetState<T>()` / `SetState<T>()` -- no string keys, no casting
  - Strongly-typed event bus for decoupled node-to-node communication
  - Built-in telemetry sink injected into every execution cycle

- **Event Bus (`IEventBus`)**
  - Publish-subscribe pattern for decoupled inter-node communication
  - Async handlers invoked in subscription order
  - No built-in payload -- use shared context for data transfer

- **Telemetry (`ITelemetrySink` / `TelemetrySink`)**
  - Per-node timing (via `Stopwatch`) and error recording
  - `CycleNumber` for multi-cycle correlation
  - Bounded history (1024 cycles max) to prevent unbounded memory growth
  - `GraphSnapshot` record for serialization and export

- **Configuration System**
  - `GraphConfig` for per-node enable/disable and parameter injection
  - `NodeConfig` with typed accessors (`GetInt`, `GetFloat`, `GetBool`, `GetString`)
  - JSON-serializable, suitable for hot-reload scenarios

### Target Framework

- `net10.0`

### Dependencies

- **Zero external dependencies** other than `Microsoft.Extensions.Logging.Abstractions` (for `GraphExecutor` logging).

### Package Metadata

| Field | Value |
|-------|-------|
| PackageId | `MUnique.OpenMU.Orchestrator` |
| Version | `1.0.0` |
| Authors | MUnique |
| License | MIT |
| Project URL | https://munique.net |
| Repository | https://github.com/MUnique/OpenMU |

### Availability

Published on [NuGet.org](https://www.nuget.org/packages/MUnique.OpenMU.Orchestrator/). Install via:

```bash
dotnet add package MUnique.OpenMU.Orchestrator
```
