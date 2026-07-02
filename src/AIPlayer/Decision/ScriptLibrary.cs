// <copyright file="ScriptLibrary.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using System.Reflection;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.AIPlayer;
using MUnique.OpenMU.GameLogic;

/// <summary>
/// 全局脚本注册表 — AI角色行为模块的中央工厂和持有者。
/// 每个 AI 角色一个实例（因为模块依赖 per-player AiPlayer）。
/// 内部使用 <see cref="ScriptRegistry"/> 共享类型注册表，
/// 通过懒加载模式按需创建模块实例。
/// </summary>
public sealed class ScriptLibrary
{
    private readonly Dictionary<string, IBehaviorSubModule> _scripts = new();
    private readonly AiPlayer _player;
    private readonly IGameAdapter _adapter;
    private readonly BoardState _boardState;
    private readonly BehaviorContext _context;
    private readonly ILogger _logger;
    private readonly MaterialKnowledgeService _materialKnowledge;
    private readonly ValueAssessmentService? _valueAssessment;

    private bool _defaultRegistered;

    /// <summary>
    /// 静态构造函数：确保默认模块已注册到 ScriptRegistry。
    /// </summary>
    static ScriptLibrary()
    {
        ScriptRegistry.RegisterDefaultModules();
    }

    /// <summary>
    /// Gets the number of currently instantiated scripts.
    /// </summary>
    public int Count => this._scripts.Count;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptLibrary"/> class.
    /// 只存储依赖，不创建任何模块。模块通过 <see cref="Get"/> 或 <see cref="GetAll"/> 懒加载。
    /// </summary>
    /// <param name="player">The AI player this library serves.</param>
    /// <param name="adapter">The game adapter.</param>
    /// <param name="boardState">The mission board state (needed by EventExecutorModule).</param>
    /// <param name="context">The behavior context (needed by ItemPickupManager).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="materialKnowledge">Material knowledge service (needed by EventExecutorModule).</param>
    /// <param name="valueAssessment">Value assessment service (needed by ItemPickupManager).</param>
    public ScriptLibrary(
        AiPlayer player,
        IGameAdapter adapter,
        BoardState boardState,
        BehaviorContext context,
        ILogger logger,
        MaterialKnowledgeService materialKnowledge,
        ValueAssessmentService? valueAssessment = null)
    {
        this._player = player;
        this._adapter = adapter;
        this._boardState = boardState;
        this._context = context;
        this._logger = logger;
        this._materialKnowledge = materialKnowledge;
        this._valueAssessment = valueAssessment;
    }

    /// <summary>
    /// Register a behavior submodule by its script ID.
    /// 可用于注入 ScriptRegistry 之外的额外或覆盖模块。
    /// </summary>
    /// <param name="scriptId">Unique script identifier.</param>
    /// <param name="script">The module instance.</param>
    public void Register(string scriptId, IBehaviorSubModule script)
    {
        this._scripts[scriptId] = script;
    }

    /// <summary>
    /// Get a registered behavior submodule by script ID.
    /// 优先查缓存，未命中则查 ScriptRegistry 懒加载创建。
    /// </summary>
    /// <param name="scriptId">The script identifier.</param>
    /// <returns>The registered module, or null if not found and not registered.</returns>
    public IBehaviorSubModule? Get(string scriptId)
    {
        if (this._scripts.TryGetValue(scriptId, out var script))
        {
            return script;
        }

        // Lazy create from ScriptRegistry
        var type = ScriptRegistry.GetType(scriptId);
        if (type is null)
        {
            return null;
        }

        var instance = this.CreateInstance(type);
        if (instance is not null)
        {
            this._scripts[scriptId] = instance;
        }

        return instance;
    }

    /// <summary>
    /// Get all registered modules as a read-only dictionary (for DecisionSystem/AIStateMachineExecutor compatibility).
    /// 确保所有 ScriptRegistry 中注册的模块均已实例化。
    /// </summary>
    /// <returns>Read-only dictionary of script ID → module.</returns>
    public IReadOnlyDictionary<string, IBehaviorSubModule> GetAll()
    {
        // Ensure all ScriptRegistry entries are instantiated
        foreach (var scriptId in ScriptRegistry.GetAllScriptIds())
        {
            if (!this._scripts.ContainsKey(scriptId))
            {
                var type = ScriptRegistry.GetType(scriptId);
                if (type is not null)
                {
                    var instance = this.CreateInstance(type);
                    if (instance is not null)
                    {
                        this._scripts[scriptId] = instance;
                    }
                }
            }
        }

        return this._scripts.AsReadOnly();
    }

    /// <summary>
    /// 使用存储的依赖创建指定类型的模块实例。
    /// 通过构造函数参数类型匹配自动注入已知依赖。
    /// </summary>
    private IBehaviorSubModule? CreateInstance(Type type)
    {
        try
        {
            var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            foreach (var ctor in ctors.OrderByDescending(c => c.GetParameters().Length))
            {
                var parameters = ctor.GetParameters();
                var args = new object?[parameters.Length];
                var allMatched = true;

                for (var i = 0; i < parameters.Length; i++)
                {
                    var paramType = parameters[i].ParameterType;
                    args[i] = this.ResolveDependency(paramType);

                    if (args[i] is null && !parameters[i].IsOptional && !paramType.IsValueType)
                    {
                        allMatched = false;
                        break;
                    }
                }

                if (allMatched && args.All(a => a is not null || parameters[Array.IndexOf(args, a)].IsOptional))
                {
                    return (IBehaviorSubModule)ctor.Invoke(args);
                }
            }
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "[ScriptLib] 创建模块实例失败: {Type}", type.Name);
        }

        return null;
    }

    /// <summary>
    /// 根据类型解析存储的依赖。
    /// </summary>
    private object? ResolveDependency(Type type)
    {
        if (type == typeof(AiPlayer)) return this._player;
        if (type == typeof(IGameAdapter)) return this._adapter;
        if (type == typeof(BoardState)) return this._boardState;
        if (type == typeof(BehaviorContext)) return this._context;
        if (type == typeof(ILogger)) return this._logger;
        if (type == typeof(MaterialKnowledgeService)) return this._materialKnowledge;
        if (type == typeof(ValueAssessmentService)) return this._valueAssessment;

        return null;
    }
}
