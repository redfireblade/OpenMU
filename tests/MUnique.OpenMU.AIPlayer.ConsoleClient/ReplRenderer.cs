// <copyright file="ReplRenderer.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.ConsoleClient;

using MUnique.OpenMU.AIPlayer.Testing;
using Spectre.Console;

/// <summary>
/// Spectre.Console rendering helpers for the AI player debug dashboard.
/// </summary>
internal static class ReplRenderer
{
    /// <summary>
    /// Creates a live dashboard layout showing player status, module decisions,
    /// and recent events. Designed for use with <see cref="AnsiConsole.Live{T}"/>.
    /// </summary>
    /// <param name="harness">The test harness.</param>
    /// <returns>A <see cref="Panel"/> containing the dashboard content.</returns>
    internal static Panel CreateDashboard(AiPlayerTestHarness harness)
    {
        var ctx = harness.Context;
        var snap = ctx.GetLatestSnapshot();
        var player = harness.Player;

        // Status columns
        var level = player.Level;
        var tick = player.Logic?.TickCounter ?? 0;
        var pos = snap?.Position ?? player.Position;
        var mapId = player.CurrentMap?.Definition.Number ?? 0;
        var hp = snap?.Hp ?? 0;
        var maxHp = snap?.MaxHp ?? 1;
        var mp = snap?.Mp ?? 0;
        var maxMp = snap?.MaxMp ?? 1;
        var survival = snap?.SurvivalLevel ?? SurvivalManager.SurvivalLevel.Normal;
        var hasTarget = snap?.HasTarget ?? false;
        var retreat = snap?.EmergencyRetreat ?? false;

        var hpColor = hp < maxHp * 0.3 ? "red" : hp < maxHp * 0.6 ? "yellow" : "green";
        var mpColor = mp < maxMp * 0.3 ? "red" : mp < maxMp * 0.6 ? "yellow" : "blue";

        var status = new Markup(
            $"[bold]{player.SelectedCharacter?.Name ?? "Unknown"}[/]  " +
            $"[bold]Lv.[/]{level}  " +
            $"[bold]Map:[/]{mapId}  " +
            $"[bold]Pos:[/]({pos.X}, {pos.Y})  " +
            $"[bold]Tick:[/]#{tick}\n" +
            $"HP: [{hpColor}]{hp}[/]/{maxHp}  " +
            $"MP: [{mpColor}]{mp}[/]/{maxMp}  " +
            $"Survival: [{(survival == SurvivalManager.SurvivalLevel.Emergency ? "red" : survival == SurvivalManager.SurvivalLevel.Normal ? "green" : "yellow")}]{survival}[/]  " +
            $"Target: [{(hasTarget ? "green" : "gray")}]{(hasTarget ? "yes" : "no")}[/]  " +
            $"Retreat: [{(retreat ? "red" : "gray")}]{(retreat ? "RETREAT" : "no")}[/]");

        // Module decisions table
        var decisionsTable = new Table { Border = TableBorder.Ascii, Width = 60 };
        decisionsTable.AddColumn(new TableColumn("Module").NoWrap());
        decisionsTable.AddColumn(new TableColumn("Time"));
        decisionsTable.AddColumn(new TableColumn("Tick"));

        if (snap?.Decisions is { Count: > 0 } decisions)
        {
            foreach (var d in decisions.Take(8))
            {
                var tc = d.Duration.TotalMilliseconds < 5 ? "green" : d.Duration.TotalMilliseconds < 20 ? "yellow" : "red";
                decisionsTable.AddRow(
                    d.ModuleName,
                    $"[{tc}]{d.Duration.TotalMilliseconds,6:F1}ms[/]",
                    snap.TickNumber.ToString());
            }
        }
        else
        {
            decisionsTable.AddRow("[gray](no data)[/]", "", "");
        }

        // Event log
        var eventsLines = new List<string>
        {
            $"[gray]Tick #{tick}: HP={hp}/{maxHp} MP={mp}/{maxMp} Survival={survival}[/]",
            $"[gray]Position=({pos.X},{pos.Y}) Target={(hasTarget ? "set" : "none")} Emergency={(retreat ? "yes" : "no")}[/]",
        };

        var eventsMarkup = new Markup(string.Join("\n", eventsLines));

        // Combine into layout
        var layout = new Rows(
            status,
            new Rule("[yellow]Module Decisions[/]") { Style = Style.Parse("dim") },
            decisionsTable,
            new Rule("[yellow]Events[/]") { Style = Style.Parse("dim") },
            eventsMarkup);

        return new Panel(layout)
        {
            Header = new PanelHeader($"[bold]AI Debug Dashboard[/]  [gray](refreshes every 400ms — Ctrl+C to stop)[/]"),
            Expand = true,
        };
    }
}
