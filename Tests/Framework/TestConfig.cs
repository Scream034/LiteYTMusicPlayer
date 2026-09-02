using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LMP.Tests.Framework;

/// <summary>
/// Конфигурация параметров тестов, загружаемая из JSON-файла.
/// <para>
/// <b>Назначение:</b> позволяет менять тестовые данные (videoId, itag, tokens, iterations)
/// БЕЗ перекомпиляции — просто редактируй JSON и перезапускай тесты.
/// </para>
/// <para>
/// <b>Порядок загрузки:</b>
/// <list type="number">
///   <item>Путь из <see cref="G.FilePath.TestConfig"/> (%LOCALAPPDATA%/LMP/test-config.json)</item>
///   <item>Переопределение через env переменную LMP_TEST_CONFIG</item>
///   <item>Если файл не найден — создаётся с дефолтными значениями</item>
/// </list>
/// </para>
/// <para>
/// <b>Использование в тестах:</b>
/// <code>
/// var config = TestConfig.Get();
/// var videoId = config.Pipeline.DebugVideoId;
/// var iterations = config.NToken.BenchmarkIterations;
/// </code>
/// </para>
/// </summary>
/// <remarks>
/// Singleton с lazy-инициализацией. Thread-safe.
/// Для горячей перезагрузки используй <see cref="Reload"/>.
/// </remarks>
public sealed class TestConfig
{
    //
    // SINGLETON
    //

    private static TestConfig? _instance;
    private static readonly Lock _lock = new();

    /// <summary>
    /// Возвращает текущий экземпляр конфигурации.
    /// При первом обращении загружает из файла или создаёт дефолтный.
    /// </summary>
    public static TestConfig Get()
    {
        if (_instance is not null) return _instance;

        lock (_lock)
        {
            _instance ??= Load();
        }

        return _instance;
    }

    //
    // СЕКЦИИ КОНФИГУРАЦИИ
    //

    /// <summary>
    /// Метаданные конфига (не редактируются пользователем).
    /// </summary>
    [JsonPropertyName("_meta")]
    public MetaConfig Meta { get; init; } = new();

    /// <summary>
    /// Параметры для StreamPipelineTests.
    /// Управляет списком тестовых видео, debug-параметрами и itag'ами.
    /// </summary>
    [JsonPropertyName("pipeline")]
    public PipelineConfig Pipeline { get; init; } = new();

    /// <summary>
    /// Параметры для NTokenTests.
    /// Управляет тестовыми токенами и количеством итераций benchmark'а.
    /// </summary>
    [JsonPropertyName("ntoken")]
    public NTokenConfig NToken { get; init; } = new();

    /// <summary>
    /// Параметры для SigCipherTests.
    /// Управляет тестовой подписью для проверки дешифрации.
    /// </summary>
    [JsonPropertyName("sigCipher")]
    public SigCipherConfig SigCipher { get; init; } = new();

    /// <summary>
    /// Параметры для SigCipherSolverTests.
    /// Управляет количеством random-комбинаций и benchmark-итераций.
    /// </summary>
    [JsonPropertyName("solver")]
    public SolverConfig Solver { get; init; } = new();

    /// <summary>
    /// Параметры для тестов совместимости разных версий плеера.
    /// </summary>
    [JsonPropertyName("playerVersions")]
    public PlayerVersionConfig PlayerVersions { get; init; } = new();

    /// <summary>
    /// Общие параметры: таймауты, флаги поведения.
    /// </summary>
    [JsonPropertyName("general")]
    public GeneralConfig General { get; init; } = new();

    //
    // КОНФИГ-КЛАССЫ
    //

    /// <summary>
    /// Метаданные конфига (служебная информация).
    /// </summary>
    public sealed class MetaConfig
    {
        /// <summary>Описание файла.</summary>
        [JsonPropertyName("description")]
        public string Description { get; init; } = "LMP Test Configuration. Edit values and reload (no recompilation needed).";

        /// <summary>Путь к документации.</summary>
        [JsonPropertyName("docs")]
        public string Docs { get; init; } = "https://github.com/Scream034/LMP/tree/main/Tests/Framework/TestConfig.cs";

        /// <summary>Версия схемы конфига.</summary>
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; } = 1;
    }

    /// <summary>
    /// Конфигурация StreamPipeline тестов.
    /// </summary>
    public sealed class PipelineConfig
    {
        /// <summary>
        /// Список videoId для multi-video тестов.
        /// Должны быть доступные, не удалённые видео.
        /// </summary>
        [JsonPropertyName("testVideoIds")]
        public string[] TestVideoIds { get; init; } =
        [
            "dQw4w9WgXcQ",
            "jNQXAC9IVRw",
            "kJQP7kiw5Fk",
        ];

        /// <summary>
        /// VideoId для детального debug теста DebugSpecificStreamAsync.
        /// </summary>
        [JsonPropertyName("debugVideoId")]
        public string DebugVideoId { get; init; } = "dQw4w9WgXcQ";

        /// <summary>
        /// Целевой itag для debug теста.
        /// null = любой первый audio-стрим.
        /// </summary>
        [JsonPropertyName("debugTargetItag")]
        public int? DebugTargetItag { get; init; } = 251;

        /// <summary>
        /// Скачивать ли тестовый chunk (первый 1KB) в debug тесте.
        /// </summary>
        [JsonPropertyName("debugTestDownload")]
        public bool DebugTestDownload { get; init; } = true;
    }

    /// <summary>
    /// Конфигурация NToken тестов.
    /// </summary>
    public sealed class NTokenConfig
    {
        /// <summary>
        /// Тестовый N-token для LiveDecryption теста.
        /// </summary>
        [JsonPropertyName("testToken")]
        public string TestToken { get; init; } = "Siib9I-K-KF0GqS-";

        /// <summary>
        /// Количество итераций в NToken benchmark'е.
        /// </summary>
        [JsonPropertyName("benchmarkIterations")]
        public int BenchmarkIterations { get; init; } = 100;
    }

    /// <summary>
    /// Конфигурация SigCipher тестов.
    /// </summary>
    public sealed class SigCipherConfig
    {
        /// <summary>
        /// Тестовая подпись для LiveDecryption теста.
        /// NOTE: YouTube-подписи уникальны для каждого запроса.
        /// Тест проверяет что результат не равен входу.
        /// </summary>
        [JsonPropertyName("testSignature")]
        public string TestSignature { get; init; } =
            "AHEqNM4wRQIgUw3FiHA8Pht_xgtH0N_C7fQwvOMGHPW9KCHzFbzj_uECIQDPrmvV4I7V_V-uKiksYsVh1xBFwp_vFpXjjLL7T4pBxg==";
    }

    /// <summary>
    /// Конфигурация SigCipherSolver тестов.
    /// </summary>
    public sealed class SolverConfig
    {
        /// <summary>
        /// Количество random-комбинаций для стресс-теста.
        /// </summary>
        [JsonPropertyName("randomCombinationsCount")]
        public int RandomCombinationsCount { get; init; } = 100;

        /// <summary>
        /// Количество итераций в Solver benchmark'е.
        /// </summary>
        [JsonPropertyName("benchmarkIterations")]
        public int BenchmarkIterations { get; init; } = 50;
    }

    /// <summary>
    /// Конфигурация тестов совместимости версий плеера.
    /// </summary>
    public sealed class PlayerVersionConfig
    {
        /// <summary>
        /// Список версий плеера для тестирования.
        /// Версии — хэши из URL: /player/XXXXXXXX/
        /// </summary>
        [JsonPropertyName("versions")]
        public string[] Versions { get; init; } = [];

        /// <summary>
        /// Пары token→expected для каждой версии.
        /// </summary>
        [JsonPropertyName("nTokenTestCases")]
        public Dictionary<string, NTokenTestCase[]> NTokenTestCases { get; init; } = [];

        /// <summary>
        /// Загружать ли base.js для старых версий из кэша.
        /// </summary>
        [JsonPropertyName("loadCachedPlayerVersions")]
        public bool LoadCachedPlayerVersions { get; init; } = true;
    }

    /// <summary>
    /// Пара encrypted→expected для тестирования NToken.
    /// </summary>
    public sealed class NTokenTestCase
    {
        [JsonPropertyName("encrypted")]
        public string Encrypted { get; init; } = "";

        [JsonPropertyName("expected")]
        public string Expected { get; init; } = "";

        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }

    /// <summary>
    /// Общие параметры тестов.
    /// </summary>
    public sealed class GeneralConfig
    {
        /// <summary>
        /// Дефолтный таймаут для тестов с сетью, секунды.
        /// </summary>
        [JsonPropertyName("defaultNetworkTimeoutSeconds")]
        public int DefaultNetworkTimeoutSeconds { get; init; } = 60;

        /// <summary>
        /// Подробное логирование в тестах.
        /// </summary>
        [JsonPropertyName("verboseLogging")]
        public bool VerboseLogging { get; init; } = false;
    }

    //
    // ЗАГРУЗКА И СОХРАНЕНИЕ
    //

    /// <summary>
    /// Загружает конфигурацию из файла.
    /// </summary>
    private static TestConfig Load()
    {
        var configPath = GetConfigPath();

        try
        {
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<TestConfig>(json, G.Json.Beautiful);

                if (config is not null)
                {
                    Log.Debug($"[TestConfig] Loaded from {configPath}");
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[TestConfig] Failed to load {configPath}: {ex.Message}");
        }

        // Создаём дефолтный конфиг
        var defaultConfig = new TestConfig();
        SaveConfig(configPath, defaultConfig);
        return defaultConfig;
    }

    /// <summary>
    /// Возвращает путь к файлу конфигурации.
    /// </summary>
    public static string GetConfigPath()
    {
        var envPath = Environment.GetEnvironmentVariable("LMP_TEST_CONFIG");
        if (!string.IsNullOrWhiteSpace(envPath) && Path.IsPathRooted(envPath))
            return envPath;

        return G.FilePath.TestConfig;
    }

    /// <summary>
    /// Сохраняет конфигурацию в валидный JSON (без комментариев).
    /// </summary>
    private static void SaveConfig(string path, TestConfig config)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Чистый JSON без комментариев — валидный для любого парсера
            var json = JsonSerializer.Serialize(config, G.Json.Beautiful);
            File.WriteAllText(path, json);

            Log.Info($"[TestConfig] Saved config to {path}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[TestConfig] Failed to save config: {ex.Message}");
        }
    }

    /// <summary>
    /// Открывает test-config.json в текстовом редакторе.
    /// </summary>
    /// <remarks>
    /// Стратегии открытия:
    /// 1. notepad.exe (всегда есть на Windows)
    /// 2. code (VS Code, если установлен)
    /// 3. Fallback на Process.Start с UseShellExecute
    /// </remarks>
    public static void OpenInEditor()
    {
        var path = GetConfigPath();

        // Создаём файл если не существует
        if (!File.Exists(path))
        {
            Log.Info($"[TestConfig] Creating default config at {path}");
            SaveConfig(path, new TestConfig());
        }

        try
        {
            // Стратегия 1: Notepad (гарантированно работает на Windows)
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = false
                });
                Log.Info($"[TestConfig] Opened in Notepad: {path}");
                return;
            }

            // Стратегия 2: xdg-open на Linux
            if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = false
                });
                Log.Info($"[TestConfig] Opened with xdg-open: {path}");
                return;
            }

            // Стратегия 3: open на macOS
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"-t \"{path}\"",
                    UseShellExecute = false
                });
                Log.Info($"[TestConfig] Opened with open: {path}");
                return;
            }

            // Fallback: ShellExecute
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            Log.Info($"[TestConfig] Opened via ShellExecute: {path}");
        }
        catch (Exception ex)
        {
            Log.Error($"[TestConfig] Failed to open editor: {ex.Message}");

            // Последний fallback — показать путь в логе
            Log.Info($"[TestConfig] Please open manually: {path}");
        }
    }

    /// <summary>
    /// Открывает папку с конфигом в проводнике.
    /// </summary>
    public static void OpenConfigFolder()
    {
        var path = GetConfigPath();
        var folder = Path.GetDirectoryName(path);

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            Log.Warn($"[TestConfig] Folder not found: {folder}");
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = false
                });
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", folder);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", folder);
            }

            Log.Info($"[TestConfig] Opened folder: {folder}");
        }
        catch (Exception ex)
        {
            Log.Error($"[TestConfig] Failed to open folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Перезагружает конфигурацию из файла.
    /// </summary>
    public static TestConfig Reload()
    {
        lock (_lock)
        {
            _instance = Load();
            Log.Info("[TestConfig] Reloaded from disk");
            return _instance;
        }
    }
}