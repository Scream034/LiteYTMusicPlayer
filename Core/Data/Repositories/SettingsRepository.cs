using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LMP.Core.Data.Entities;
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
}

public sealed class SettingsRepository(IDbContextFactory<LibraryDbContext> factory) : ISettingsRepository
{
    private readonly IDbContextFactory<LibraryDbContext> _factory = factory;

    public async Task<T?> GetAsync<T>(
        string key,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default) where T : class
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var entity = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);

        if (entity is null) return null;

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
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var json = JsonSerializer.Serialize(value, typeInfo);
        var existing = await ctx.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);

        Log.Trace(json);

        if (existing != null)
        {
            existing.Value = json;
            ctx.Settings.Update(existing);
        }
        else
        {
            ctx.Settings.Add(new SettingEntity { Key = key, Value = json });
        }

        await ctx.SaveChangesAsync(ct);
    }
}