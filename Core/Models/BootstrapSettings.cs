using System.Text.Json;
using System.Text.Json.Serialization;

namespace LMP.Core.Models;

public sealed class BootstrapSettings
{
    public string LanguageCode { get; set; } = "en";
    public bool IsFirstRun { get; set; } = true;
    public string? ThemeJson { get; set; }

    [JsonIgnore]
    private static readonly string FilePath = G.FilePath.Bootstrap;

    /// <summary>
    /// Лимит GPU texture cache Skia в байтах.
    /// Применяется при старте через SkiaOptions.MaxGpuResourceSizeBytes.
    /// Изменение вступает в силу после перезапуска.
    /// Дефолт: 64MB — покрывает ~762 обложки 120px без лишнего потребления RAM.
    /// </summary>
    public long GpuTextureCacheMb { get; set; } = 64;

    public string? LastRunVersion { get; set; }

    /// <summary>
    /// Флаг, указывающий, что в текущем запуске произошло обновление версии приложения.
    /// Используется для условного сброса критических конфигураций.
    /// </summary>
    [JsonIgnore]
    public bool AppUpdatedThisRun { get; private set; }

    public static BootstrapSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize(json, AppJsonContext.Default.BootstrapSettings);
                if (settings != null)
                {
                    // ОЧИСТКА КЭША ПРИ ОБНОВЛЕНИИ ВЕРСИИ ПЛЕЕРА
                    if (settings.LastRunVersion != G.Build.Version)
                    {
                        settings.AppUpdatedThisRun = true;
                        Log.Info($"[Bootstrap] App updated: {settings.LastRunVersion} -> {G.Build.Version}. Purging obsolete bypass caches...");
                        PurgeBypassCaches();
                        settings.LastRunVersion = G.Build.Version;
                        settings.Save();
                    }

                    // ПРОВЕРКА ПЕРВОГО ЗАПУСКА
                    if (settings.IsFirstRun)
                    {
                        settings.LanguageCode = G.SystemInfo.DetectSystemLanguage();
                        settings.IsFirstRun = false;
                        settings.Save();
                        Log.Info($"[Bootstrap] First run, detected language: {settings.LanguageCode}");
                    }
                    else
                    {
                        Log.Info($"[Bootstrap] Loaded: lang={settings.LanguageCode}");
                    }
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Bootstrap] Failed to load: {ex.Message}");
        }

        var defaults = new BootstrapSettings
        {
            IsFirstRun = false,
            LanguageCode = G.SystemInfo.DetectSystemLanguage(),
            LastRunVersion = G.Build.Version
        };

        Log.Info($"[Bootstrap] First run (no file), detected language: {defaults.LanguageCode}");
        defaults.Save();

        return defaults;
    }

    /// <summary>
    /// Принудительно удаляет кэшированные файлы обхода блокировок на диске.
    /// </summary>
    private static void PurgeBypassCaches()
    {
        try
        {
            if (Directory.Exists(G.Folder.NTokenCache))
                Directory.Delete(G.Folder.NTokenCache, true);
            if (Directory.Exists(G.Folder.SigCipherCache))
                Directory.Delete(G.Folder.SigCipherCache, true);

            G.Folder.Create(); // Пересоздаем пустые директории структуры папок
        }
        catch (Exception ex)
        {
            Log.Warn($"[Bootstrap] Failed to purge bypass caches: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, AppJsonContext.Default.BootstrapSettings);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Log.Error($"[Bootstrap] Failed to save: {ex.Message}");
        }
    }

    public static BootstrapSettings Current { get; private set; } = new();

    public static void Initialize()
    {
        Current = Load();
    }
}