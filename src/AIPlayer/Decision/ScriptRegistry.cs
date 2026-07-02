// <copyright file="ScriptRegistry.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.AIPlayer.Decision;

using System.Collections.ObjectModel;

/// <summary>
/// 共享脚本注册表 — 全局唯一的模块类型注册中心。
/// 存储脚本 ID → CLR Type 的映射，不持有实例。
/// 所有 AI 角色的 <see cref="ScriptLibrary"/> 实例共享此注册表，
/// 按需懒加载创建模块实例（每个角色独立实例）。
/// </summary>
public static class ScriptRegistry
{
    private static readonly Dictionary<string, Type> Registrations = new();

    private static readonly object LockObj = new();

    /// <summary>
    /// 注册一个脚本模块类型到全局注册表。
    /// </summary>
    /// <typeparam name="T">实现 <see cref="IBehaviorSubModule"/> 的模块类型。</typeparam>
    /// <param name="scriptId">唯一脚本标识，如 "quest_executor", "crafting_executor"。</param>
    public static void Register<T>(string scriptId)
        where T : IBehaviorSubModule
    {
        lock (LockObj)
        {
            Registrations[scriptId] = typeof(T);
        }
    }

    /// <summary>
    /// 注册一个脚本模块类型到全局注册表（非泛型重载）。
    /// </summary>
    /// <param name="scriptId">唯一脚本标识。</param>
    /// <param name="moduleType">模块的 CLR 类型。</param>
    /// <exception cref="ArgumentException"><paramref name="moduleType"/> 不实现 <see cref="IBehaviorSubModule"/>。</exception>
    public static void Register(string scriptId, Type moduleType)
    {
        if (!typeof(IBehaviorSubModule).IsAssignableFrom(moduleType))
        {
            throw new ArgumentException($"Type {moduleType.FullName} does not implement {nameof(IBehaviorSubModule)}.", nameof(moduleType));
        }

        lock (LockObj)
        {
            Registrations[scriptId] = moduleType;
        }
    }

    /// <summary>
    /// 获取指定脚本 ID 注册的模块类型。
    /// </summary>
    /// <param name="scriptId">脚本标识。</param>
    /// <returns>CLR 类型，未注册返回 null。</returns>
    public static Type? GetType(string scriptId)
    {
        lock (LockObj)
        {
            Registrations.TryGetValue(scriptId, out var type);
            return type;
        }
    }

    /// <summary>
    /// 获取所有已注册的脚本 ID 列表。
    /// </summary>
    /// <returns>只读脚本 ID 集合。</returns>
    public static IReadOnlyCollection<string> GetAllScriptIds()
    {
        lock (LockObj)
        {
            return new ReadOnlyCollection<string>(Registrations.Keys.ToList());
        }
    }

    /// <summary>
    /// 注册默认内置模块到全局注册表。
    /// 在 <see cref="ScriptLibrary"/> 静态构造函数中自动调用。
    /// </summary>
    public static void RegisterDefaultModules()
    {
        Register<QuestExecutor>("quest_executor");
        Register<CraftingModule>("crafting_executor");
        Register<EventExecutorModule>("event_executor");
        Register<MaterialFarmModule>("material_farm");
        Register<VaultModule>("vault_executor");
        Register<ItemFarmModule>("item_farm");
        Register<SurvivalMode>("survival");
        Register<PetHandlerModule>("pet_handler");
        Register<ItemPickupManager>("item_pickup_manager");
    }
}
