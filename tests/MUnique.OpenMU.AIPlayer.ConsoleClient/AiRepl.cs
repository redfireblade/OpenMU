// <copyright file="AiRepl.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.ConsoleClient;

using System.Diagnostics;
using Spectre.Console;

/// <summary>
/// Interactive REPL loop for the AI player debug console.
/// Dispatches user commands to <see cref="AiReplCommands"/>.
/// </summary>
internal sealed class AiRepl : IAsyncDisposable
{
    private readonly AiReplCommands _commands;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiRepl"/> class.
    /// </summary>
    internal AiRepl()
    {
        this._commands = new AiReplCommands();
    }

    /// <summary>
    /// Runs the REPL command loop until the user exits.
    /// </summary>
    internal async Task RunAsync()
    {
        AnsiConsole.Write(new Rule("[yellow]MU AI Player Debug Console[/]"));
        AnsiConsole.MarkupLine("Type [green]help[/] for commands, [red]exit[/] to quit.");
        AnsiConsole.WriteLine();

        await this._commands.EnsureHarnessAsync().ConfigureAwait(false);

        while (true)
        {
            var input = AnsiConsole.Ask<string>("[bold]ai>[/]");
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            input = input.Trim();
            var parts = ParseArgs(input);
            var command = parts[0].ToLowerInvariant();
            var args = parts.Skip(1).ToArray();

            if (command == "exit" || command == "quit")
            {
                break;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                await this.DispatchAsync(command, args).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] {ex.Message}");
            }

            sw.Stop();
            if (sw.ElapsedMilliseconds > 100)
            {
                AnsiConsole.MarkupLine($"[gray]({sw.ElapsedMilliseconds} ms)[/]");
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!this._disposed)
        {
            this._disposed = true;
            await this._commands.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(string command, string[] args)
    {
        switch (command)
        {
            case "help":
                this._commands.Help();
                break;

            case "create":
                await this._commands.CreateAsync(args).ConfigureAwait(false);
                break;

            case "status":
                await this._commands.StatusAsync().ConfigureAwait(false);
                break;

            case "step":
                await this._commands.StepAsync(args).ConfigureAwait(false);
                break;

            case "modules":
                await this._commands.ModulesAsync().ConfigureAwait(false);
                break;

            case "hp":
                await this._commands.SetHpAsync(args).ConfigureAwait(false);
                break;

            case "mp":
                await this._commands.SetMpAsync(args).ConfigureAwait(false);
                break;

            case "level":
                await this._commands.SetLevelAsync(args).ConfigureAwait(false);
                break;

            case "pos":
                await this._commands.SetPositionAsync(args).ConfigureAwait(false);
                break;

            case "inventory":
                await this._commands.InventoryAsync().ConfigureAwait(false);
                break;

            case "additem":
                await this._commands.AddItemAsync(args).ConfigureAwait(false);
                break;

            case "spawn":
                await this._commands.SpawnAsync(args).ConfigureAwait(false);
                break;

            case "clearmonsters":
                await this._commands.ClearMonstersAsync().ConfigureAwait(false);
                break;

            case "watch":
                await this._commands.WatchAsync().ConfigureAwait(false);
                break;

            case "cont":
                this._commands.ContinuousMode();
                break;

            case "scenario":
                await this._commands.ScenarioAsync(args).ConfigureAwait(false);
                break;

            // TCP mode commands
            case "connect":
                await this._commands.ConnectTcpAsync(args).ConfigureAwait(false);
                break;

            case "disconnect":
                await this._commands.DisconnectTcpAsync().ConfigureAwait(false);
                break;

            case "walk":
                await this._commands.TcpWalkAsync(args).ConfigureAwait(false);
                break;

            case "say":
                await this._commands.TcpSayAsync(args).ConfigureAwait(false);
                break;

            default:
                AnsiConsole.MarkupLineInterpolated($"[red]Unknown command:[/] {command}. Type [green]help[/] for available commands.");
                break;
        }
    }

    private static string[] ParseArgs(string input)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuote = false;

        foreach (var c in input)
        {
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (c == ' ' && !inQuote)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return [.. parts];
    }
}
