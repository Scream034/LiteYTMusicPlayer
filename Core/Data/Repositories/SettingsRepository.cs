using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;

namespace LMP.Core.Data.Repositories;

public interface ISettingsRepository
{
    Task<T?> GetAsync<T>(
        string key,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default) where T : class;

    Task<T> GetOrDefaultAsync<T>(
        string key,
        T defaultValue,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default) where T : class;

    Task SetAsync<T>(
        string key,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default);

    /// <summary>
    /// Синхронно сохраняет настройку в базу данных.
    /// Используется при завершении работы приложения (shutdown path) во избежание deadlock.
    /// </summary>
    void Set<T>(
        string key,
        T value,
        JsonTypeInfo<T> typeInfo);
}

public sealed class SettingsRepository(IDbContextFactory<LibraryDbContext> factory) : ISettingsRepository
{
    private readonly IDbContextFactory<LibraryDbContext> _factory = factory;

    public async Task<T?> GetAsync<T>(
        string key,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default) where T : class
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);

        if (entity is null) return null;

        Log.Info($"[SettingsRepository] Loaded '{key}' from DB: {entity.Value}");
        return JsonSerializer.Deserialize(entity.Value, typeInfo);
    }

    public async Task<T> GetOrDefaultAsync<T>(
        string key,
        T defaultValue,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default) where T : class
    {
        return await GetAsync(key, typeInfo, ct) ?? defaultValue;
    }

    public async Task SetAsync<T>(
         string key,
         T value,
         JsonTypeInfo<T> typeInfo,
         CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(value, typeInfo);

        // Прямой SQL Upsert в обход ChangeTracker EF Core (гарантирует реальное обновление строки в SQLite)
        await ctx.Database.ExecuteSqlRawAsync(
            "INSERT INTO Settings (Key, Value) VALUES ({0}, {1}) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;",
            [key, json],
            ct).ConfigureAwait(false);

        // Сбрасываем страницы WAL в основной файл на диске
        try
        {
            await ctx.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(PASSIVE);", ct).ConfigureAwait(false);
        }
        catch { }

        Log.Info($"[SettingsRepository] Successfully committed '{key}' to database ({json.Length} bytes)");
    }

    public void Set<T>(
        string key,
        T value,
        JsonTypeInfo<T> typeInfo)
    {
        using var ctx = _factory.CreateDbContext();

        var json = JsonSerializer.Serialize(value, typeInfo);

        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO Settings (Key, Value) VALUES ({0}, {1}) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;",
            key, json);

        try
        {
            ctx.Database.ExecuteSqlRaw("PRAGMA wal_checkpoint(PASSIVE);");
        }
        catch { }

        Log.Info($"[SettingsRepository] Successfully committed '{key}' (sync) to database ({json.Length} bytes)");
    }
}