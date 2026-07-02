// <copyright file="AiPlayerBenchmarks.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Benchmarks;

using System.ComponentModel;
using MUnique.OpenMU.AIPlayer.Testing;

/// <summary>
/// Benchmarks for AI player module execution times and memory allocation.
/// </summary>
[SimpleJob(RuntimeMoniker.Net80, baseline: true)]
[SimpleJob(RuntimeMoniker.Net90)]
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
public class AiPlayerBenchmarks : IAsyncDisposable
{
    private AiPlayerTestHarness? _harness;
    private bool _disposed;

    /// <summary>
    /// Sets up the benchmark: creates an AI player test harness with a level 100 character.
    /// </summary>
    [GlobalSetup]
    public async Task GlobalSetup()
    {
        this._harness = await AiPlayerTestHarness.CreateAsync(
            name: "BenchBot",
            classNumber: 0,
            mapId: 0,
            startLevel: 100).ConfigureAwait(false);

        // Give the character a weapon in inventory so combat module has work to do
        // (weapon created from scratch with minimal definition)
        this._harness.SetPosition(100, 100);
    }

    /// <summary>
    /// Measures execution of a single AI tick (all modules).
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Tick")]
    [Description("Single AI tick with all modules")]
    public async Task SingleTick()
    {
        if (this._harness is null)
        {
            throw new InvalidOperationException("Harness not initialized.");
        }

        await this._harness.StepOnceAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Measures execution of ten consecutive AI ticks.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Tick")]
    [Description("Ten consecutive AI ticks")]
    public async Task TenTicks()
    {
        if (this._harness is null)
        {
            throw new InvalidOperationException("Harness not initialized.");
        }

        for (int i = 0; i < 10; i++)
        {
            await this._harness.StepOnceAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Measures AI tick performance under low HP (survival module stress).
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("Tick", "Combat")]
    [Description("AI tick under low HP")]
    public async Task LowHpTick()
    {
        if (this._harness is null)
        {
            throw new InvalidOperationException("Harness not initialized.");
        }

        this._harness.SetHp(30);
        await this._harness.StepOnceAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        if (this._harness is not null)
        {
            await this._harness.DisposeAsync().ConfigureAwait(false);
        }
    }
}
