// <copyright file="Program.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.ConsoleClient;

using MUnique.OpenMU.AIPlayer.Testing;

/// <summary>
/// AI Player Debug Console — interactive REPL for testing AI behavior.
/// </summary>
internal static class Program
{
    /// <summary>Entry point.</summary>
    internal static async Task Main(string[] args)
    {
        Console.Title = "MU AI Player Debug Console";
        await using var repl = new AiRepl();
        await repl.RunAsync().ConfigureAwait(false);
    }
}
