using System.Text.RegularExpressions;
using System.Threading;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Представляет иммутабельный и потокобезопасный контекст конкретной версии JS-плеера YouTube.
/// Координирует хранение оригинального кода, результатов AST-оптимизации и скомпилированного нативного байткода.
/// </summary>
/// <remarks>
/// <para>
/// <b>Архитектура памяти (Предотвращение LOH Fragmentation):</b>
/// Исходный код YouTube плеера (<c>base.js</c>) весит ~2.5 МБ. Строки такого размера аллоцируются в Large Object Heap (LOH).
/// Длительное удержание таких строк в памяти неизбежно приводит к фрагментации LOH и провоцирует дорогостоящие GC Gen 2 сборки.
/// После компиляции контекста в нативный движок, метод <see cref="ReleaseRawScripts"/> принудительно освобождает оригинальный 
/// <see cref="BaseJs"/>, минимизируя нагрузку на сборщик мусора.
/// </para>
/// <para>
/// <b>Безопасность Байткода QuickJS (Native Crash Prevention):</b>
/// Нативный байткод QuickJS-NG бинарно несовместим между разными версиями компилятора или конфигурациями моста.
/// Попытка загрузить устаревший или несовместимый байткод вызывает SegFault нативного процесса, что роняет всё .NET приложение.
/// Для защиты от таких падений кэш байткода содержит маркер ABI нативного моста в имени файла (<see cref="GetBytecodeCachePath"/>).
/// </para>
/// </remarks>
public sealed partial class PlayerContext
{
    private const int MaxCachedVersions = 10;
    private const int MaxAgeDays = 14;

    /// <summary>
    /// Легковесный объект синхронизации (.NET 9+), защищающий процесс компиляции 
    /// и оптимизации скрипта от гонок потоков (Thread Races).
    /// </summary>
    private readonly Lock _prepLock = new();

    /// <summary>
    /// Хэш-идентификатор версии плеера YouTube (например, <c>"06cb0078"</c>).
    /// Используется как ключевой маркер во всех файловых операциях и путях кэша.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Полный оригинальный исходный код плеера (<c>base.js</c>), загруженный из сети или диска.
    /// </summary>
    /// <value>
    /// Может быть сброшен в <see cref="string.Empty"/> после инициализации контекста методом <see cref="ReleaseRawScripts"/>
    /// для предотвращения удержания памяти в LOH.
    /// </value>
    public string BaseJs { get; private set; }

    /// <summary>
    /// Оптимизированный методом Tree Shaking и готовый к исполнению JS-код.
    /// </summary>
    /// <remarks>
    /// Содержит только те функции и зависимости, которые непосредственно участвуют в обходе подписи (Sig) и N-токена.
    /// </remarks>
    public string? PreprocessedJs { get; private set; }

    /// <summary>
    /// Временная метка подписи (STS / Signature Timestamp).
    /// Необходима для формирования валидных запросов к серверам раздачи видео (видео-кукам) YouTube.
    /// </summary>
    public string? Sts { get; private set; }

    /// <summary>
    /// Временная метка создания объекта в памяти приложения.
    /// Используется для контроля ротации и инвалидации устаревших контекстов плеера в рамках 12-часового цикла YouTube.
    /// </summary>
    public DateTimeOffset CachedAt { get; }

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="PlayerContext"/>.
    /// </summary>
    /// <param name="version">Уникальная строковая версия плеера.</param>
    /// <param name="baseJs">Оригинальный JS-код плеера плеера.</param>
    /// <param name="preprocessedJs">Необязательный уже оптимизированный JS-код.</param>
    /// <param name="sts">Необязательная метка подписи. Если передана как <c>null</c>, будет извлечена автоматически.</param>
    public PlayerContext(string version, string baseJs, string? preprocessedJs = null, string? sts = null)
    {
        Version = version;
        BaseJs = baseJs;
        PreprocessedJs = preprocessedJs;
        Sts = sts ?? (string.IsNullOrEmpty(baseJs) ? null : YoutubeAstSolver.ExtractSts(baseJs));
        CachedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Возвращает препроцессированный JS-код или компилирует его «на лету» (Lazy Initialization)
    /// с использованием предоставленного делегата оптимизации.
    /// </summary>
    /// <param name="preprocessor">Делегат, выполняющий синтаксический разбор AST и Tree Shaking плеера.</param>
    /// <returns>Строка с оптимизированным JavaScript-кодом.</returns>
    /// <remarks>
    /// Метод потокобезопасен. После успешной компиляции результат автоматически записывается на диск asynchronously.
    /// </remarks>
    public string GetOrPrepareScript(Func<string> preprocessor)
    {
        if (!string.IsNullOrEmpty(PreprocessedJs)) return PreprocessedJs;

        lock (_prepLock)
        {
            if (!string.IsNullOrEmpty(PreprocessedJs)) return PreprocessedJs;

            string js = preprocessor();
            PreprocessedJs = js;

            // Запись кэша в фоновом режиме, чтобы не блокировать основной поток выполнения
            _ = SavePreprocessedCacheAsync();

            return js;
        }
    }

    /// <summary>
    /// Освобождает гигантскую строку оригинального скрипта плеера (<see cref="BaseJs"/>) из оперативной памяти.
    /// </summary>
    /// <remarks>
    /// <b>Важно:</b> Вызывать строго ПОСЛЕ того, как контекст нативного моста QuickJS был успешно инициализирован.
    /// Оптимизированный код (<see cref="PreprocessedJs"/>) сохраняется, так как он может потребоваться для отладки или реинициализации.
    /// </remarks>
    public void ReleaseRawScripts()
    {
        lock (_prepLock)
        {
            BaseJs = string.Empty;
            Log.Debug($"[PlayerContext] Discarded raw base.js from memory for player version: {Version}. Preprocessed script retained.");
        }
    }

    /// <summary>
    /// Проверяет, актуален ли данный контекст плеера в оперативной памяти.
    /// </summary>
    /// <returns><c>true</c>, если с момента кэширования прошло менее 12 часов (соответствует циклу ротации YouTube).</returns>
    public bool IsValid() => (DateTimeOffset.UtcNow - CachedAt).TotalHours < 12;

    /// <summary>
    /// Асинхронно записывает оптимизированную JS-версию и временную метку STS на диск.
    /// </summary>
    public async Task SavePreprocessedCacheAsync()
    {
        try
        {
            if (string.IsNullOrEmpty(PreprocessedJs)) return;

            var prepPath = GetPreprocessedCachePath(Version);
            var stsPath = GetStsCachePath(Version);

            var dir = Path.GetDirectoryName(prepPath);
            if (dir is not null) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(prepPath, PreprocessedJs).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(Sts))
            {
                await File.WriteAllTextAsync(stsPath, Sts).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Preprocessed cache save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Выполняет полное асинхронное кэширование: оригинального кода, оптимизированного JS и метаданных на диск.
    /// Запускает процедуру автоматической очистки устаревших файлов.
    /// </summary>
    public async Task SaveCacheAsync()
    {
        try
        {
            var path = GetCachePath(Version);
            var dir = Path.GetDirectoryName(path);
            if (dir is not null) Directory.CreateDirectory(dir);

            if (!string.IsNullOrEmpty(BaseJs))
            {
                await File.WriteAllTextAsync(path, BaseJs).ConfigureAwait(false);
            }

            await SavePreprocessedCacheAsync().ConfigureAwait(false);
            CleanupOldVersions();
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Cache save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Загружает метаданные из кэша. Избегает чтения тяжелых JS-файлов, если доступен готовый байткод.
    /// </summary>
    public static PlayerContext? LoadFromCache(string version)
    {
        try
        {
            var prepPath = GetPreprocessedCachePath(version);
            var stsPath = GetStsCachePath(version);
            var baseJsPath = GetCachePath(version);
            var bytecodePath = GetBytecodeCachePath(version);

            // Улучшенная оптимизация:
            // Если на диске уже есть готовый байткод и файл STS — нам вообще не нужны тяжелые JS-файлы.
            // Мы считываем только 5 байт STS (необходим для внешних HTTP-запросов к CDN Google)
            // и мгновенно возвращаем контекст. Экономия 2.5 МБ дискового I/O на каждом старте!
            if (File.Exists(bytecodePath) && File.Exists(stsPath))
            {
                var sts = File.ReadAllText(stsPath).Trim();
                Log.Debug($"[PlayerContext] Bytecode and STS caches exist for {version}. Skipping base.js/preprocessed.js disk I/O.");
                return new PlayerContext(version, string.Empty, preprocessedJs: null, sts);
            }

            // Быстрый путь (байткода нет, но есть оптимизированный JS):
            // Возвращаем объект. preprocessed.js будет считан лениво только при реальном обращении к свойству.
            if (File.Exists(prepPath) && File.Exists(stsPath))
            {
                var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(prepPath);
                if (age.TotalDays <= MaxAgeDays)
                {
                    var sts = File.ReadAllText(stsPath).Trim();
                    Log.Debug($"[PlayerContext] Loaded metadata cache for {version} (sts={sts})");
                    return new PlayerContext(version, string.Empty, preprocessedJs: null, sts);
                }
            }

            // Медленный путь (полный фоллбек на оригинальный base.js):
            if (!File.Exists(baseJsPath)) return null;

            var baseAge = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(baseJsPath);
            if (baseAge.TotalDays > MaxAgeDays)
            {
                Log.Debug($"[PlayerContext] Cache expired for {version} ({baseAge.TotalDays:F0}d old)");
                return null;
            }

            var baseJs = File.ReadAllText(baseJsPath);
            return new PlayerContext(version, baseJs);
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Cache load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Загружает кэшированный контекст плеера с диска БЕЗ валидации даты последнего изменения.
    /// </summary>
    /// <remarks>
    /// Используется преимущественно в Unit-тестах для работы со стабильными локальными дампами.
    /// </remarks>
    public static PlayerContext? LoadFromCacheNoExpiry(string version)
    {
        try
        {
            var prepPath = GetPreprocessedCachePath(version);
            var stsPath = GetStsCachePath(version);
            var baseJsPath = GetCachePath(version);

            if (File.Exists(prepPath) && File.Exists(stsPath))
            {
                var preprocessedJs = File.ReadAllText(prepPath);
                var sts = File.ReadAllText(stsPath).Trim();

                if (!string.IsNullOrWhiteSpace(preprocessedJs))
                {
                    return new PlayerContext(version, string.Empty, preprocessedJs, sts);
                }
            }

            if (!File.Exists(baseJsPath)) return null;

            var baseJs = File.ReadAllText(baseJsPath);
            if (string.IsNullOrWhiteSpace(baseJs)) return null;

            return new PlayerContext(version, baseJs);
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Cache load failed for {version}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Возвращает физический путь к файлу оригинального <c>base.js</c> на диске.</summary>
    private static string GetCachePath(string version) =>
        Path.Combine(G.Folder.NTokenCache, $"player_{version}_basejs.txt");

    /// <summary>Возвращает физический путь к файлу оптимизированного JS-кода.</summary>
    private static string GetPreprocessedCachePath(string version) =>
        Path.Combine(G.Folder.NTokenCache, $"player_{version}_preprocessed.js");

    /// <summary>Возвращает физический путь к текстовому файлу с временной меткой STS плеера.</summary>
    private static string GetStsCachePath(string version) =>
        Path.Combine(G.Folder.NTokenCache, $"player_{version}_sts.txt");

    /// <summary>Возвращает физический путь к файлу кэша бинарного байткода QuickJS.</summary>
    /// <remarks>
    /// Имя файла содержит маркер <c>QuickJsNative.BridgeAbi</c>, что гарантирует 
    /// инвалидацию старого байткода при любом обновлении нативного моста (защита от аппаратного Access Violation).
    /// </remarks>
    public static string GetBytecodeCachePath(string version) =>
        Path.Combine(G.Folder.NTokenCache, $"player_{version}_abi{QuickJsNative.BridgeAbi}_bytecode.bin");

    /// <summary>
    /// Автоматически сканирует директорию кэша и удаляет файлы плееров, которые старше <see cref="MaxAgeDays"/> дней,
    /// если общее количество сохраненных версий превышает <see cref="MaxCachedVersions"/>.
    /// </summary>
    /// <remarks>
    /// Метод осуществляет ротацию всех сопутствующих файлов версии: оригинального JS, препроцессированного JS, STS-файла и бинарного байткода.
    /// </remarks>
    private static void CleanupOldVersions()
    {
        try
        {
            var dir = G.Folder.NTokenCache;
            if (!Directory.Exists(dir)) return;

            var files = Directory.GetFiles(dir, "player_*_basejs.txt");
            if (files.Length <= MaxCachedVersions) return;

            var now = DateTime.UtcNow;

            // Фильтруем кандидатов на удаление: сортируем по дате изменения и берем только устаревшие
            var candidates = files
                .Select(static f => new FileInfo(f))
                .OrderByDescending(static f => f.LastWriteTimeUtc)
                .Skip(MaxCachedVersions)
                .Where(f => (now - f.LastWriteTimeUtc).TotalDays > MaxAgeDays)
                .ToArray();

            foreach (var file in candidates)
            {
                try
                {
                    var name = file.Name;
                    var versionMatch = PlayerVersionRegex().Match(name);
                    if (versionMatch.Success)
                    {
                        var version = versionMatch.Groups[1].Value;
                        var prepPath = GetPreprocessedCachePath(version);
                        var stsPath = GetStsCachePath(version);
                        var bytecodePath = GetBytecodeCachePath(version);

                        if (File.Exists(prepPath)) File.Delete(prepPath);
                        if (File.Exists(stsPath)) File.Delete(stsPath);
                        if (File.Exists(bytecodePath)) File.Delete(bytecodePath);
                    }

                    file.Delete();
                    Log.Debug($"[PlayerContext] Cleaned up old cache: {file.Name}");
                }
                catch
                {
                    // Игнорируем заблокированные или используемые другими процессами файлы
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[PlayerContext] Scheduled cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Определяет актуальную версию плеера на основе API-манифеста YouTube (iframe_api) 
    /// и возвращает её вместе со списком URL-кандидатов для скачивания.
    /// </summary>
    public static async Task<(string Version, string[] Urls)?> DetectVersionAsync(
        HttpClient http,
        CancellationToken ct = default)
    {
        try
        {
            var iframeApi = await http.GetStringAsync("https://www.youtube.com/iframe_api", ct).ConfigureAwait(false);
            var match = PlayerVersionIframeRegex().Match(iframeApi);
            if (!match.Success) return null;

            var version = match.Groups[1].Value;
            string[] urls = [$"https://www.youtube.com/s/player/{version}/player_es6.vflset/ru_RU/base.js"];

            return (version, urls);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"[PlayerContext] Version detection failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Физически удаляет файлы кэша конкретной версии плеера (включая бинарный байткод) с диска.
    /// </summary>
    /// <param name="version">Версия плеера, файлы которой необходимо удалить.</param>
    public static void ClearDiskCache(string version)
    {
        try
        {
            var prepPath = GetPreprocessedCachePath(version);
            var stsPath = GetStsCachePath(version);
            var baseJsPath = GetCachePath(version);
            var bytecodePath = GetBytecodeCachePath(version);

            if (File.Exists(prepPath)) File.Delete(prepPath);
            if (File.Exists(stsPath)) File.Delete(stsPath);
            if (File.Exists(baseJsPath)) File.Delete(baseJsPath);
            if (File.Exists(bytecodePath)) File.Delete(bytecodePath);

            Log.Info($"[PlayerContext] Cleared disk cache files for version {version}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[PlayerContext] Failed to clear disk cache for {version}: {ex.Message}");
        }
    }

    [GeneratedRegex(@"player_([a-fA-F0-9]+)_basejs\.txt")]
    private static partial Regex PlayerVersionRegex();

    [GeneratedRegex(@"player\\?/([0-9a-fA-F]{8})\\?/")]
    private static partial Regex PlayerVersionIframeRegex();
}