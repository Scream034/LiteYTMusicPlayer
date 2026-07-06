using System.Text.Json;
using System.Text.Json.Serialization;
using LMP.Core.Audio.Cache;
using LMP.Core.Audio.Http;
using LMP.Core.Data;
using LMP.Core.Youtube.Bridge.Common;
using LMP.Core.Youtube.Bridge.PoToken;

namespace LMP.Core.Models;

/// <summary>
/// JSON Source Generator для оптимизации памяти и производительности.
/// Убирает накладные расходы рефлексии при работе с моделями.
/// </summary>
[JsonSerializable(typeof(LegacyLibraryData))]
[JsonSerializable(typeof(TrackInfo))]
[JsonSerializable(typeof(List<TrackInfo>))]
[JsonSerializable(typeof(Playlist))]
[JsonSerializable(typeof(List<Playlist>))]
[JsonSerializable(typeof(PoTokenCacheEntry))]
[JsonSerializable(typeof(AudioCacheManager.AudioCacheIndexEnvelope))]
[JsonSerializable(typeof(AudioCacheEntry))]
[JsonSerializable(typeof(List<AudioCacheEntry>))]
[JsonSerializable(typeof(CachedSearchResult))]
[JsonSerializable(typeof(CdnHostStatsEnvelope))]
[JsonSerializable(typeof(CdnClusterStats))]
[JsonSerializable(typeof(List<CdnClusterStats>))]
[JsonSerializable(typeof(SessionCacheEnvelope))]
[JsonSerializable(typeof(TrackManifestEntry))]
[JsonSerializable(typeof(List<TrackManifestEntry>))]
[JsonSerializable(typeof(VariantEntry))]
[JsonSerializable(typeof(List<VariantEntry>))]
[JsonSerializable(typeof(ThemeSettings))]
[JsonSerializable(typeof(BootstrapSettings))]
[JsonSerializable(typeof(AuthState))]
[JsonSerializable(typeof(NotificationService.AttemptDto))]
[JsonSerializable(typeof(List<NotificationService.AttemptDto>))]
[JsonSerializable(typeof(DecryptorCache.DecryptorCacheData))]
[JsonSerializable(typeof(YoutubeAccountItem))]
[JsonSerializable(typeof(List<YoutubeAccountItem>))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AudioSettings))]
[JsonSerializable(typeof(ProxySettings))]
[JsonSerializable(typeof(StorageSettings))]
[JsonSerializable(typeof(NotificationSettings))]
[JsonSerializable(typeof(MemorySettings))]
[JsonSerializable(typeof(CloseAction))]
[JsonSerializable(typeof(RepeatMode))]
[JsonSerializable(typeof(AudioQualityPreference))]
[JsonSerializable(typeof(InternetProfile))]
[JsonSerializable(typeof(YoutubeClientProfile))]
[JsonSerializable(typeof(VolumeCurveType))]
[JsonSerializable(typeof(PlaybackErrorBehavior))]
[JsonSerializable(typeof(LikeSyncMode))]
[JsonSerializable(typeof(NTokenNotificationMode))]
[JsonSerializable(typeof(PlaybackFailureBehavior))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
public sealed partial class AppJsonContext : JsonSerializerContext
{
    public static AppJsonContext DefaultCompact { get; } = new(new JsonSerializerOptions
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    });
}