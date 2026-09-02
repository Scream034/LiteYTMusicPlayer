using Avalonia.Platform;
using System.ComponentModel;
using System.Text.Json;

namespace LMP.Core.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    public static readonly LocalizationService Instance = new();

    private Dictionary<string, string> _resources = [];
    private bool _isInitialized;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string>? LanguageChanged;

    public List<LanguageItem> AvailableLanguages { get; } =
    [
        new() { Code = "en", Name = "English" },
        new() { Code = "ru", Name = "Русский" }
    ];

    public string CurrentLanguageCode { get; private set; } = "en";

    public string CurrentLanguage
    {
        get => CurrentLanguageCode;
        set
        {
            if (CurrentLanguageCode != value && AvailableLanguages.Any(l => l.Code == value))
            {
                Log.Info($"Language change: {CurrentLanguageCode} → {value}");
                CurrentLanguageCode = value;
                LoadLanguage(value);

                // Обновить bootstrap для быстрого старта
                BootstrapSettings.Current.LanguageCode = value;
                BootstrapSettings.Current.Save();

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
                LanguageChanged?.Invoke(this, value);
            }
        }
    }

    private LocalizationService()
    {
        Log.Info("LocalizationService created (deferred)");
    }

    public void Initialize(string? langCode)
    {
        if (_isInitialized)
        {
            Log.Warn("LocalizationService already initialized");
            return;
        }

        var langToUse = langCode ?? "en";

        if (!AvailableLanguages.Any(l => l.Code == langToUse))
        {
            Log.Warn($"Unknown language '{langToUse}', falling back to 'en'");
            langToUse = "en";
        }

        CurrentLanguageCode = langToUse;
        LoadLanguage(langToUse);
        _isInitialized = true;

        Log.Info($"LocalizationService initialized: {langToUse}");
    }

    private void LoadLanguage(string langCode)
    {
        try
        {
            var uri = new Uri($"avares://LMP/Assets/Localization/{langCode}.json");

            if (!AssetLoader.Exists(uri))
            {
                Log.Error($"Localization file not found: {uri}");
                if (langCode != "en") LoadLanguage("en");
                return;
            }

            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            // JsonSerializer бросит JsonException при невалидной структуре
            // (вложенные объекты вместо строк, массивы и т.д.)
            var resources = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringString) ?? throw new InvalidOperationException("Deserialization returned null");
            _resources = resources;

            Log.Info($"✓ Loaded {langCode}.json ({_resources.Count} keys)");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load '{langCode}': {ex.Message}");

            if (langCode == "en")
            {
                _resources = [];
                Log.Warn("Using empty dictionary");
            }
            else
            {
                LoadLanguage("en");
            }
        }
    }

    public string this[string key]
    {
        get
        {
            if (!_isInitialized)
                return $"[{key}]";

            return _resources.TryGetValue(key, out var value) ? value : $"[{key}]";
        }
    }

    public string Get(string key, string? fallback = null)
    {
        if (!_isInitialized)
            return fallback ?? $"[{key}]";

        return _resources.TryGetValue(key, out var value) ? value : fallback ?? $"[{key}]";
    }

    public string RawGet(string key) => _resources.TryGetValue(key, out var v) ? v : key;

    public string GetPlural(string key, int count)
    {
        if (!_isInitialized) return $"{count}";

        var absCount = Math.Abs(count);
        var lastTwo = absCount % 100;
        var lastOne = absCount % 10;

        string suffix;
        if (count == 0) suffix = "_0";
        else if (lastTwo >= 11 && lastTwo <= 19) suffix = "_5";
        else if (lastOne == 1) suffix = "_1";
        else if (lastOne >= 2 && lastOne <= 4) suffix = "_2";
        else suffix = "_5";

        if (_resources.TryGetValue(key + suffix, out var specific))
            return string.Format(specific, count);

        if (_resources.TryGetValue(key + "_other", out var other))
            return string.Format(other, count);

        return $"{count}";
    }
}

public sealed class LanguageItem
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public override string ToString() => Name;
}