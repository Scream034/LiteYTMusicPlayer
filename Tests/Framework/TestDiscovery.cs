using System.Collections.Frozen;
using System.Reflection;

namespace LMP.Tests.Framework;

/// <summary>
/// Автоматическое обнаружение тестов через рефлексию.
/// Кэширует результаты в <see cref="FrozenDictionary{TKey, TValue}"/> для O(1) lookup.
/// </summary>
public static class TestDiscovery
{
    private static FrozenDictionary<string, TestDescriptor>? _cache;
    private static FrozenSet<string>? _groupsCache;
    private static readonly object _lock = new();

    /// <summary>Все обнаруженные тесты по ID.</summary>
    public static FrozenDictionary<string, TestDescriptor> GetAll()
    {
        if (_cache is not null) return _cache;

        lock (_lock)
        {
            if (_cache is not null) return _cache;
            _cache = Discover();
            _groupsCache = null; // сбрасываем кэш групп
        }

        return _cache;
    }

    /// <summary>Тесты, отсортированные по категории → группе → порядку.</summary>
    public static IReadOnlyList<TestDescriptor> GetAllOrdered() =>
        GetAll().Values
            .OrderBy(t => t.Category)
            .ThenBy(t => t.Group, StringComparer.Ordinal)
            .ThenBy(t => t.Order)
            .ThenBy(t => t.DisplayName, StringComparer.Ordinal)
            .ToList();

    /// <summary>Тесты, сгруппированные по категории.</summary>
    public static IReadOnlyDictionary<TestCategory, IReadOnlyList<TestDescriptor>> GetGrouped() =>
        GetAllOrdered()
            .GroupBy(t => t.Category)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TestDescriptor>)[.. g]);

    /// <summary>
    /// Возвращает все уникальные тематические группы, обнаруженные в тестах.
    /// Используется для построения UI-фильтров.
    /// </summary>
    public static FrozenSet<string> GetAllGroups()
    {
        if (_groupsCache is not null) return _groupsCache;

        var groups = GetAll().Values
            .Select(t => t.Group)
            .Distinct(StringComparer.Ordinal)
            .ToFrozenSet(StringComparer.Ordinal);

        _groupsCache = groups;
        return groups;
    }

    /// <summary>Сбрасывает кэш (для hot-reload).</summary>
    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _cache = null;
            _groupsCache = null;
        }
    }

    // PRIVATE

    private static FrozenDictionary<string, TestDescriptor> Discover()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var result = new Dictionary<string, TestDescriptor>(64);

        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || !type.IsAbstract || !type.IsSealed) continue;
            if (type.Namespace is null || !type.Namespace.StartsWith("LMP.Tests")) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = method.GetCustomAttribute<TestMethodAttribute>();
                if (attr is null) continue;

                if (method.ReturnType != typeof(Task))
                {
                    Log.Warn($"[TestDiscovery] Skipping {type.Name}.{method.Name}: " +
                             $"return type must be Task, got {method.ReturnType.Name}");
                    continue;
                }

                var parameters = method.GetParameters();
                bool requiresServices = false;

                switch (parameters.Length)
                {
                    case 0:
                        break;
                    case 1 when parameters[0].ParameterType == typeof(IServiceProvider):
                        requiresServices = true;
                        break;
                    default:
                        Log.Warn($"[TestDiscovery] Skipping {type.Name}.{method.Name}: " +
                                 $"unsupported parameters ({parameters.Length})");
                        continue;
                }

                var className = type.Name;
                var displayName = attr.DisplayName ?? FormatMethodName(method.Name);
                var id = $"{className}.{method.Name}";

                // Группа: явно заданная > производная от имени класса
                var group = attr.Group ?? InferGroupFromClassName(className);

                result[id] = new TestDescriptor
                {
                    Id = id,
                    DisplayName = displayName,
                    Category = attr.Category,
                    Group = group,
                    ClassName = className,
                    Order = attr.Order,
                    RequiresNetwork = attr.RequiresNetwork,
                    TimeoutSeconds = attr.TimeoutSeconds,
                    RequiresServices = requiresServices,
                    Method = method,
                };
            }
        }

        Log.Info($"[TestDiscovery] Found {result.Count} tests in {assembly.GetName().Name}");
        return result.ToFrozenDictionary();
    }

    /// <summary>
    /// Выводит группу из имени класса: "SigCipherTests" → "SigCipher",
    /// "StreamPipelineTests" → "Pipeline", "NTokenTests" → "NToken".
    /// </summary>
    private static string InferGroupFromClassName(string className)
    {
        var name = className.AsSpan();

        // Убираем суффикс "Tests"
        if (name.EndsWith("Tests", StringComparison.Ordinal))
            name = name[..^5];

        // Убираем суффикс "Solver" (SigCipherSolverTests → SigCipher... нет, лучше "Solver")
        // Специальные маппинги для известных классов
        var result = name.ToString();

        return result switch
        {
            "SigCipherSolver" => TestGroups.Solver,
            "SigCipher" => TestGroups.SigCipher,
            "StreamPipeline" => TestGroups.Pipeline,
            "NToken" => TestGroups.NToken,
            "Cache" => TestGroups.Cache,
            _ => result, // для неизвестных — как есть
        };
    }

    /// <summary>
    /// "TestManifestSerializationAsync" → "Manifest Serialization".
    /// </summary>
    private static string FormatMethodName(string methodName)
    {
        var name = methodName.AsSpan();

        if (name.StartsWith("Test", StringComparison.Ordinal))
            name = name[4..];

        if (name.EndsWith("Async", StringComparison.Ordinal))
            name = name[..^5];

        Span<char> buffer = stackalloc char[name.Length * 2];
        int pos = 0;

        for (int i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (i > 0 && char.IsUpper(ch) && !char.IsUpper(name[i - 1]))
                buffer[pos++] = ' ';
            buffer[pos++] = ch;
        }

        return new string(buffer[..pos]);
    }
}