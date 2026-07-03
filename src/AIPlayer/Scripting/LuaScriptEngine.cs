// <copyright file="LuaScriptEngine.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Scripting;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// 轻量Lua风格DSL解释器 — 支持条件/循环/错误处理+游戏函数调用。
/// 用于AI角色执行学习规则对应的脚本。
///
/// 支持语法:
///   if hp < 0.5 then use_hp_potion() end
///   while has_target() do attack() wait(1) end
///   for i=1,5 do walk_forward() end
///   walk_to(140, 125)
///   talk_npc(257)
///   try attack() catch hp_low then use_hp_potion() end
///   pick_item()
///   wait(2)  -- 等待2秒
///   log("message")
/// </summary>
public sealed class LuaScriptEngine
{
    private readonly Dictionary<string, Func<string[], CancellationToken, Task<bool>>> _functions = new();
    private readonly ILogger _logger;

    // Lua-style function registry (exposed to scripts)
    public Dictionary<string, object> Globals { get; } = new();

    public LuaScriptEngine(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger<LuaScriptEngine>.Instance;
    }

    /// <summary>注册一个游戏函数供Lua脚本调用。</summary>
    public void RegisterFunction(string name, Func<string[], CancellationToken, Task<bool>> handler)
    {
        _functions[name] = handler;
        _logger.LogDebug("[LuaEngine] Registered function: {Name}", name);
    }

    /// <summary>执行Lua风格脚本。</summary>
    public async Task<bool> ExecuteAsync(string script, CancellationToken ct = default)
    {
        var lines = script.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int pc = 0;
        var stack = new Stack<(string, int)>(); // (context, return_pc) for loops

        while (pc < lines.Length && !ct.IsCancellationRequested)
        {
            var line = lines[pc].Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("--"))
            {
                pc++;
                continue;
            }

            try
            {
                var (nextPc, result) = await ExecuteLineAsync(line, pc, lines, ct).ConfigureAwait(false);
                if (!result)
                {
                    _logger.LogWarning("[LuaEngine] Line {PC} failed: {Line}", pc + 1, line);
                }
                pc = nextPc;
            }
            catch (ScriptErrorException ex)
            {
                _logger.LogWarning(ex, "[LuaEngine] Script error at line {PC}: {Line}", pc + 1, line);
                pc++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LuaEngine] Fatal error at line {PC}: {Line}", pc + 1, line);
                return false;
            }
        }

        return true;
    }

    private async Task<(int nextPc, bool result)> ExecuteLineAsync(
        string line, int pc, string[] lines, CancellationToken ct)
    {
        // --- if CONDITION then STATEMENT end ---
        var ifMatch = Regex.Match(line, @"^if\s+(.+?)\s+then\s+(.+?)\s*(end)?$");
        if (ifMatch.Success)
        {
            var condition = ifMatch.Groups[1].Value.Trim();
            var statement = ifMatch.Groups[2].Value.Trim();
            if (EvaluateCondition(condition))
            {
                var (_, r) = await ExecuteLineAsync(statement, pc, lines, ct).ConfigureAwait(false);
                return (pc + 1, r);
            }
            return (pc + 1, true);
        }

        // --- if CONDITION then STATEMENT else STATEMENT end ---
        var ifElseMatch = Regex.Match(line, @"^if\s+(.+?)\s+then\s+(.+?)\s+else\s+(.+?)\s*end$");
        if (ifElseMatch.Success)
        {
            var condition = ifElseMatch.Groups[1].Value.Trim();
            var trueStmt = ifElseMatch.Groups[2].Value.Trim();
            var falseStmt = ifElseMatch.Groups[3].Value.Trim();
            if (EvaluateCondition(condition))
            {
                var (_, r) = await ExecuteLineAsync(trueStmt, pc, lines, ct).ConfigureAwait(false);
                return (pc + 1, r);
            }
            else
            {
                var (_, r) = await ExecuteLineAsync(falseStmt, pc, lines, ct).ConfigureAwait(false);
                return (pc + 1, r);
            }
        }

        // --- while CONDITION do BLOCK end ---
        var whileMatch = Regex.Match(line, @"^while\s+(.+?)\s+do$");
        if (whileMatch.Success)
        {
            var condition = whileMatch.Groups[1].Value.Trim();
            var endPc = FindMatchingEnd(pc, lines);
            if (endPc < 0) throw new ScriptErrorException($"Missing 'end' for while at line {pc + 1}");

            int attempts = 0;
            while (EvaluateCondition(condition) && attempts < 1000 && !ct.IsCancellationRequested)
            {
                attempts++;
                for (int i = pc + 1; i < endPc && !ct.IsCancellationRequested; i++)
                {
                    var inner = lines[i].Trim();
                    if (string.IsNullOrWhiteSpace(inner) || inner.StartsWith("--")) continue;
                    var (_, r) = await ExecuteLineAsync(inner, i, lines, ct).ConfigureAwait(false);
                    if (!r) break;
                }
            }
            return (endPc + 1, true);
        }

        // --- for VAR=START,END do BLOCK end ---
        var forMatch = Regex.Match(line, @"^for\s+(\w+)\s*=\s*(\d+)\s*,\s*(\d+)\s+do$");
        if (forMatch.Success)
        {
            var varName = forMatch.Groups[1].Value;
            int start = int.Parse(forMatch.Groups[2].Value);
            int end = int.Parse(forMatch.Groups[3].Value);
            var endPc = FindMatchingEnd(pc, lines);
            if (endPc < 0) throw new ScriptErrorException($"Missing 'end' for for-loop at line {pc + 1}");

            for (int i = start; i <= end && !ct.IsCancellationRequested; i++)
            {
                Globals[varName] = i;
                for (int j = pc + 1; j < endPc && !ct.IsCancellationRequested; j++)
                {
                    var inner = lines[j].Trim();
                    if (string.IsNullOrWhiteSpace(inner) || inner.StartsWith("--")) continue;
                    var (_, r) = await ExecuteLineAsync(inner, j, lines, ct).ConfigureAwait(false);
                    if (!r) break;
                }
            }
            return (endPc + 1, true);
        }

        // --- try STATEMENT catch CONDITION then STATEMENT end ---
        var tryMatch = Regex.Match(line, @"^try\s+(.+?)\s+catch\s+(.+?)\s+then\s+(.+?)\s*end$");
        if (tryMatch.Success)
        {
            var tryStmt = tryMatch.Groups[1].Value.Trim();
            var catchCond = tryMatch.Groups[2].Value.Trim();
            var catchStmt = tryMatch.Groups[3].Value.Trim();

            try
            {
                var (_, r) = await ExecuteLineAsync(tryStmt, pc, lines, ct).ConfigureAwait(false);
                if (!r && EvaluateCondition(catchCond))
                {
                    await ExecuteLineAsync(catchStmt, pc, lines, ct).ConfigureAwait(false);
                }
                return (pc + 1, true);
            }
            catch
            {
                if (EvaluateCondition(catchCond))
                {
                    await ExecuteLineAsync(catchStmt, pc, lines, ct).ConfigureAwait(false);
                }
                return (pc + 1, true);
            }
        }

        // --- end (loop terminator, skip) ---
        if (line == "end") return (pc + 1, true);

        // --- func(args) call ---
        return await ExecuteFunctionCallAsync(line, pc, ct).ConfigureAwait(false);
    }

    private async Task<(int nextPc, bool result)> ExecuteFunctionCallAsync(
        string line, int pc, CancellationToken ct)
    {
        var match = Regex.Match(line, @"^(\w+)\((.*)\)$");
        if (!match.Success)
        {
            _logger.LogWarning("[LuaEngine] Unknown statement at line {PC}: {Line}", pc + 1, line);
            return (pc + 1, false);
        }

        var funcName = match.Groups[1].Value;
        var argsStr = match.Groups[2].Value.Trim();
        var args = argsStr.Length > 0
            ? argsStr.Split(',').Select(a => a.Trim().Trim('"')).ToArray()
            : Array.Empty<string>();

        if (_functions.TryGetValue(funcName, out var handler))
        {
            var result = await handler(args, ct).ConfigureAwait(false);
            return (pc + 1, result);
        }

        // Handle built-in functions
        if (funcName == "wait")
        {
            if (args.Length > 0 && int.TryParse(args[0], out var ms))
                await Task.Delay(ms * 1000, ct).ConfigureAwait(false);
            return (pc + 1, true);
        }

        if (funcName == "log")
        {
            _logger.LogInformation("[LuaScript] {Msg}", string.Join(" ", args));
            return (pc + 1, true);
        }

        _logger.LogWarning("[LuaEngine] Unknown function: {Func}", funcName);
        return (pc + 1, false);
    }

    private bool EvaluateCondition(string condition)
    {
        if (string.IsNullOrWhiteSpace(condition)) return false;
        if (condition == "true") return true;
        if (condition == "false") return false;

        // hp < 0.5, mp > 0.3, level >= 10
        var cmpMatch = Regex.Match(condition, @"^(\w+)\s*(<|>|<=|>=|==|!=)\s*([\d.]+)$");
        if (cmpMatch.Success)
        {
            var varName = cmpMatch.Groups[1].Value;
            var op = cmpMatch.Groups[2].Value;
            if (double.TryParse(cmpMatch.Groups[3].Value, out var threshold))
            {
                var value = GetVarValue(varName);
                return op switch
                {
                    "<" => value < threshold,
                    ">" => value > threshold,
                    "<=" => value <= threshold,
                    ">=" => value >= threshold,
                    "==" => Math.Abs(value - threshold) < 0.001,
                    "!=" => Math.Abs(value - threshold) >= 0.001,
                    _ => false,
                };
            }
        }

        // Function-style condition: has_target(), is_dead(), in_safezone()
        var funcMatch = Regex.Match(condition, @"^(\w+)\(\)$");
        if (funcMatch.Success)
        {
            var funcName = funcMatch.Groups[1].Value;
            if (_functions.TryGetValue(funcName, out var handler))
            {
                var task = handler(Array.Empty<string>(), CancellationToken.None);
                task.Wait(1000);
                return task.Result;
            }

            // Built-in conditions from Globals
            if (Globals.TryGetValue(funcName, out var val) && val is bool b)
                return b;
        }

        // Variable reference: varname
        if (Globals.TryGetValue(condition, out var gVal))
        {
            if (gVal is bool b) return b;
            if (gVal is int i && i != 0) return true;
            if (gVal is double d && Math.Abs(d) > 0.001) return true;
        }

        return false;
    }

    private double GetVarValue(string varName)
    {
        if (Globals.TryGetValue(varName, out var val))
        {
            if (val is int i) return i;
            if (val is double d) return d;
            if (val is float f) return f;
        }

        // Check registered condition functions
        if (_functions.TryGetValue(varName, out var handler))
        {
            var task = handler(Array.Empty<string>(), CancellationToken.None);
            task.Wait(1000);
            return task.Result ? 1 : 0;
        }

        return 0;
    }

    private static int FindMatchingEnd(int startPc, string[] lines)
    {
        int depth = 0;
        for (int i = startPc + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line == "end")
            {
                if (depth == 0) return i;
                depth--;
            }
            else if (line.EndsWith("do") || line.EndsWith("then"))
            {
                depth++;
            }
        }
        return -1;
    }
}

/// <summary>脚本执行错误异常。</summary>
public sealed class ScriptErrorException : Exception
{
    public ScriptErrorException(string message) : base(message) { }
}
