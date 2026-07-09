using System.Collections.Concurrent;
using System.Text.Json;

namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Обеспечивает высокопроизводительное и потокобезопасное кэширование дешифрованных значений (string -> string)
/// с автоматическим сохранением и фоновой очисткой устаревших записей на диске.
/// </summary>
public sealed class DecryptorCache
{
    private readonly ConcurrentDictionary<string, (string Value, long Ticks)> _memory = new(StringComparer.Ordinal);
    private readonly int _maxMemory;
    private readonly int _maxDisk;
    private readonly Lock _cleanupLock = new();
    private volatile bool _cleanupInProgress;
    private int _isDirty;
    private long _lastSaveTicks;
    private string? _playerVersion;

    private const int SaveIntervalMs = 60_000;

    /// <summary>Путь к файлу кэша на диске.</summary>
    public string DiskPath { get; }

    /// <summary>Папка, в которой лежит файл кэша.</summary>
    public string CacheFolder => Path.GetDirectoryName(DiskPath)!;

    public DecryptorCache(string diskPath, int maxMemory = 2000, int maxDisk = 500)
    {
        DiskPath = diskPath;
        _maxMemory = maxMemory;
        _maxDisk = maxDisk;
    }

    public bool TryGet(string key, out string value)
    {
        if (_memory.TryGetValue(key, out var cached))
        {
            value = cached.Value;
            return true;
        }

        value = null!;
        return false;
    }

    public void Set(string key, string value)
    {
        var ticks = Environment.TickCount64;
        _memory[key] = (value, ticks);
        Volatile.Write(ref _isDirty, 1);

        if (_memory.Count > _maxMemory * 0.8 && !_cleanupInProgress)
            TriggerCleanup();

        var lastSave = Volatile.Read(ref _lastSaveTicks);
        if (ticks - lastSave > SaveIntervalMs &&
            Interlocked.CompareExchange(ref _lastSaveTicks, ticks, lastSave) == lastSave)
        {
            _ = Task.Run(SaveAsync);
        }
    }

    /// <summary>
    /// Точечно удаляет запись по ключу.
    /// </summary>
    public bool Remove(string key)
    {
        if (_memory.TryRemove(key, out _))
        {
            Volatile.Write(ref _isDirty, 1);
            _ = Task.Run(SaveAsync);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Точечно удаляет все записи с указанным расшифрованным значением.
    /// </summary>
    public void RemoveByValue(string value)
    {
        var keysToRemove = new List<string>();
        foreach (var kvp in _memory)
        {
            if (string.Equals(kvp.Value.Value, value, StringComparison.Ordinal))
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        bool removedAny = false;
        for (int i = 0; i < keysToRemove.Count; i++)
        {
            if (_memory.TryRemove(keysToRemove[i], out _))
            {
                removedAny = true;
            }
        }

        if (removedAny)
        {
            Volatile.Write(ref _isDirty, 1);
            _ = Task.Run(SaveAsync);
        }
    }

    public async Task LoadAsync(string playerVersion)
    {
        _playerVersion = playerVersion;

        try
        {
            if (!File.Exists(DiskPath)) return;

            var json = await File.ReadAllTextAsync(DiskPath);
            var data = JsonSerializer.Deserialize(json, AppJsonContext.Default.DecryptorCacheData);

            if (data is null || data.PlayerVersion != playerVersion)
            {
                File.Delete(DiskPath);
                return;
            }

            var ticks = Environment.TickCount64;
            foreach (var kvp in data.Entries)
            {
                if (kvp.Key is not null && kvp.Value is not null)
                    _memory[kvp.Key] = (kvp.Value, ticks);
            }

            Log.Debug($"[Cache] Loaded {_memory.Count} entries from {Path.GetFileName(DiskPath)}");
        }
        catch (Exception ex)
        {
            Log.Debug($"[Cache] Load failed: {ex.Message}");
        }
    }

    public async Task SaveAsync()
    {
        if (Interlocked.CompareExchange(ref _isDirty, 0, 1) == 0) return;

        _lastSaveTicks = Environment.TickCount64;

        try
        {
            Directory.CreateDirectory(CacheFolder);

            var snapshot = _memory.ToArray();

            var entries = snapshot
                .OrderByDescending(static kvp => kvp.Value.Ticks)
                .Take(_maxDisk)
                .ToDictionary(
                    static kvp => kvp.Key,
                    static kvp => kvp.Value.Value);

            var data = new DecryptorCacheData
            {
                PlayerVersion = _playerVersion ?? "unknown",
                Entries = entries
            };

            var json = JsonSerializer.Serialize(data, AppJsonContext.Default.DecryptorCacheData);
            await File.WriteAllTextAsync(DiskPath, json);
        }
        catch (Exception ex)
        {
            Log.Debug($"[Cache] Save failed: {ex.Message}");
            Volatile.Write(ref _isDirty, 1);
        }
    }

    public void Clear()
    {
        _memory.Clear();
        Volatile.Write(ref _isDirty, 0);
        try
        {
            if (File.Exists(DiskPath))
            {
                File.Delete(DiskPath);
                Log.Info($"[Cache] Deleted cache file on disk: {Path.GetFileName(DiskPath)}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Cache] Failed to delete cache file {Path.GetFileName(DiskPath)}: {ex.Message}");
        }
    }

    public int Count => _memory.Count;

    public IEnumerable<string> Keys => _memory.Keys;

    private void TriggerCleanup()
    {
        if (!_cleanupLock.TryEnter()) return;
        try
        {
            if (_memory.Count <= _maxMemory * 0.8) return;
            _cleanupInProgress = true;

            var snapshot = _memory.ToArray();
            Array.Sort(snapshot, static (a, b) => a.Value.Ticks.CompareTo(b.Value.Ticks));

            int removeCount = _maxMemory / 2;
            for (int i = 0; i < Math.Min(removeCount, snapshot.Length); i++)
            {
                _memory.TryRemove(snapshot[i].Key, out _);
            }
        }
        finally
        {
            _cleanupInProgress = false;
            _cleanupLock.Exit();
        }
    }

    public sealed class DecryptorCacheData
    {
        public string PlayerVersion { get; set; } = "";
        public Dictionary<string, string> Entries { get; set; } = [];
    }
}