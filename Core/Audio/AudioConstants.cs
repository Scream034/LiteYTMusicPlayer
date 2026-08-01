namespace LMP.Core.Audio;

/// <summary>
/// Централизованные константы аудио системы.
/// Единственный источник истины для всех числовых параметров.
/// </summary>
public static class AudioConstants
{
    // ═══════════════════════════════════════════════════════
    // SOURCE STATE — Общие значения для источников аудио
    // ═══════════════════════════════════════════════════════

    /// <summary>Неизвестная длительность трека в миллисекундах.</summary>
    public const long UnknownDurationMs = -1;

    /// <summary>Неизвестная sample rate из контейнера.</summary>
    public const int UnknownSampleRate = 0;

    /// <summary>Неизвестное количество каналов из контейнера.</summary>
    public const int UnknownChannels = 0;

    /// <summary>Полный прогресс буферизации в процентах.</summary>
    public const double FullBufferProgressPercent = 100.0;

    /// <summary>Начало полного диапазона буферизации.</summary>
    public const double BufferedRangeStart = 0.0;

    /// <summary>Конец полного диапазона буферизации.</summary>
    public const double BufferedRangeEnd = 1.0;

    // ═══════════════════════════════════════════════════════
    // CHUNK SETTINGS — Управление сегментацией данных
    // ═══════════════════════════════════════════════════════

    /// <summary>Размер чанка для кэширования (64KB = оптимум для HTTP Range + минимум аллокаций).</summary>
    public const int ChunkSize = 64 * 1024;

    /// <summary>Максимум чанков в RAM (32 × 64KB = 2MB RAM на трек).</summary>
    public const int MaxRamChunks = 96;

    /// <summary>Расстояние от текущей позиции для вытеснения (eviction) чанков из RAM.</summary>
    public const int RamEvictionDistance = 14;

    // ═══════════════════════════════════════════════════════
    // PRELOAD SETTINGS — Стратегия упреждающей загрузки
    // ═══════════════════════════════════════════════════════

    /// <summary>Чанков загружать перед стартом воспроизведения (300ms @ 128kbps).</summary>
    public const int InitialChunksToLoad = 4;

    /// <summary>Чанков держать впереди от текущей позиции (adaptive buffering).</summary>
    public const int PreloadAheadChunks = 6;

    /// <summary>Чанков загружать при seek (instant seek UX).</summary>
    public const int SeekPreloadChunks = 6;

    /// <summary>Интервал проверки preload loop (мс).</summary>
    public const int PreloadIntervalMs = 500;

    /// <summary>Максимум параллельных загрузок чанков (баланс скорость/RAM).</summary>
    public const int MaxConcurrentDownloads = 3;

    // ═══════════════════════════════════════════════════════
    // DOWNLOAD TIMEOUTS — HTTP операции
    // ═══════════════════════════════════════════════════════

    /// <summary>Таймаут загрузки одного чанка (15s = mobile-friendly).</summary>
    public const int DownloadTimeoutMs = 15_000;

    /// <summary>Таймаут ожидания слота загрузки (мс).</summary>
    public const int DownloadSlotTimeoutMs = 300;

    // ═══════════════════════════════════════════════════════
    // BACKGROUND DOWNLOAD LIMITS — Экономия сети
    // ═══════════════════════════════════════════════════════

    /// <summary>Циклов простоя перед фоновой докачкой (5 × 1s = 5s idle).</summary>
    public const int BackgroundFillIdleCycles = 5;

    /// <summary>Пауза между фоновыми загрузками (5s = gentle network usage).</summary>
    public const int BackgroundFillIntervalMs = 5_000;

    /// <summary>Максимум чанков для фоновой докачки за сессию (0 = unlimited).</summary>
    public const int MaxBackgroundChunksPerSession = 50;

    /// <summary>Минимум буфера впереди для начала фоновой докачки.</summary>
    public const int MinBufferAheadForBackgroundFill = 6;

    // ═══════════════════════════════════════════════════════
    // DECODER SETTINGS — Параметры декодирования
    // ═══════════════════════════════════════════════════════

    /// <summary>Sample rate по умолчанию (48kHz = industry standard).</summary>
    public const int DefaultSampleRate = 48_000;

    /// <summary>Количество каналов по умолчанию (stereo).</summary>
    public const int DefaultChannels = 2;

    /// <summary>Буфер декодирования (сэмплы на канал, не байты).</summary>
    public const int DecoderBufferFrames = 8192;

    /// <summary>Кадров пропустить после seek для Opus (pre-skip compensation).</summary>
    public const int SkipFramesAfterSeekOpus = 2;

    /// <summary>Кадров пропустить после seek для AAC (encoder delay).</summary>
    public const int SkipFramesAfterSeekAac = 5;

    /// <summary>Таймаут graceful shutdown декодера (мс).</summary>
    public const int DecoderStopTimeoutMs = 500;

    /// <summary>
    /// Таймаут остановки декодера при seek (мс).
    /// </summary>
    /// <remarks>
    /// <para>Увеличенный таймаут гарантирует, что старый декодер полностью прекратит работу 
    /// и освободит порт ДО старта нового декодера, предотвращая утечки потоков и SocketException.</para>
    /// </remarks>
    public const int DecoderStopTimeoutSeekMs = 600;

    // ═══════════════════════════════════════════════════════
    // PLAYBACK BUFFER SETTINGS — PCM циклический буфер
    // ═══════════════════════════════════════════════════════

    /// <summary>Максимально допустимое усиление громкости плеера (Gain).</summary>
    public const float MaxVolumeGain = 4.0f;

    /// <summary>Размер PCM буфера в секундах (2s = smooth playback).</summary>
    public const int BufferSizeSeconds = 2;

    /// <summary>Минимальный буфер для старта воспроизведения (мс).</summary>
    public const int MinBufferMs = 80;

    /// <summary>Минимальный буфер для возобновления после seek (мс).</summary>
    public const int MinSeekResumeBufferMs = 50;

    // ═══════════════════════════════════════════════════════
    // POSITION REPORTING — UI обновления
    // ═══════════════════════════════════════════════════════

    /// <summary>Интервал обновления позиции по умолчанию (мс).</summary>
    public const int DefaultPositionUpdateIntervalMs = 250;

    /// <summary>Интервал проверки состояния буфера (мс).</summary>
    public const int BufferStateUpdateIntervalMs = 500;

    // ═══════════════════════════════════════════════════════
    // RETRY POLICY — Обработка ошибок
    // ═══════════════════════════════════════════════════════

    /// <summary>Максимум попыток повтора операций.</summary>
    public const int MaxRetryAttempts = 3;

    /// <summary>Задержка между попытками (мс).</summary>
    public const int RetryDelayMs = 1_000;

    // ═══════════════════════════════════════════════════════
    // CACHE MANAGEMENT — Дисковый кэш
    // ═══════════════════════════════════════════════════════

    /// <summary>Имя файла метаданных кэша.</summary>
    public const string CacheMetadataFileName = "cache_index.json";

    /// <summary>Расширение файлов аудио кэша.</summary>
    public const string CacheFileExtension = ".audio";

    /// <summary>Интервал автосохранения индекса кэша (мс).</summary>
    public const int CacheAutoSaveIntervalMs = 30_000;

    /// <summary>Таймаут блокировки при сохранении индекса (мс).</summary>
    public const int CacheSaveLockTimeoutMs = 100;

    /// <summary>Размер буфера для файловых операций (64KB).</summary>
    public const int CacheFileBufferSize = 65_536;

    /// <summary>Количество байт в одном килобайте.</summary>
    public const int BytesPerKilobyte = 1024;

    /// <summary>Порог очистки кэша (80% от максимума).</summary>
    public const double CacheCleanupThreshold = 0.8;

    // ═══════════════════════════════════════════════════════
    // BITRATE NORMALIZATION THRESHOLDS
    // ═══════════════════════════════════════════════════════

    public const int BitrateThresholdLow = 50;
    public const int BitrateNormLow = 48;

    public const int BitrateThresholdMedLow = 62;
    public const int BitrateNormMedLow = 56;

    public const int BitrateThresholdMed = 80;
    public const int BitrateNormMed = 72;

    public const int BitrateThresholdStandardAac = 110;
    public const int BitrateNormStandardAac = 96;

    public const int BitrateThresholdStandardOpus = 140;
    public const int BitrateNormStandardOpus = 128;

    public const int BitrateThresholdHigh = 180;
    public const int BitrateNormHigh = 160;

    public const int BitrateThresholdVeryHigh = 260;
    public const int BitrateNormVeryHigh = 256;

    public const int BitrateNormMax = 320;

    /// <summary>
    /// Нормализация битрейта для кэш-ключей.
    /// Группирует близкие битрейты в стандартные bucket'ы.
    /// </summary>
    public static int NormalizeBitrate(int bitrate) => bitrate switch
    {
        <= 0 => 0,
        < BitrateThresholdLow => BitrateNormLow,
        < BitrateThresholdMedLow => BitrateNormMedLow,
        < BitrateThresholdMed => BitrateNormMed,
        < BitrateThresholdStandardAac => BitrateNormStandardAac,
        < BitrateThresholdStandardOpus => BitrateNormStandardOpus,
        < BitrateThresholdHigh => BitrateNormHigh,
        < BitrateThresholdVeryHigh => BitrateNormVeryHigh,
        _ => BitrateNormMax
    };

    // ═══════════════════════════════════════════════════════
    // FORMAT DETECTION — Magic bytes для форматов
    // ═══════════════════════════════════════════════════════

    /// <summary>Размер заголовка для определения формата (в байтах).</summary>
    public const int FormatDetectionHeaderSize = 12;

    /// <summary>WebM: EBML header magic bytes.</summary>
    public static ReadOnlySpan<byte> WebMMagic => [0x1A, 0x45, 0xDF, 0xA3];

    /// <summary>MP4: 'ftyp' box identifier at offset 4.</summary>
    public static ReadOnlySpan<byte> Mp4FtypMagic => "ftyp"u8;

    /// <summary>Ogg: 'OggS' page header magic.</summary>
    public static ReadOnlySpan<byte> OggMagic => "OggS"u8;

    // ═══════════════════════════════════════════════════════
    // AAC DECODER TABLES — Единственный источник истины
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Полная таблица sample rates по индексу из AAC Audio Specific Config (ISO 14496-3).
    /// 16 элементов: индексы 0–11 = стандартные частоты, 12 = 7350 Hz (редкая),
    /// 13–15 = зарезервированы (0).
    /// </summary>
    /// <remarks>
    /// <para><b>Единственный источник истины.</b> Используется в Mp4ContainerParser,
    /// AacDecoder и любом коде, работающем с AAC Audio Specific Config.</para>
    /// <para>Предыдущая версия содержала 12 элементов (без 7350) — это приводило
    /// к IndexOutOfRange при парсинге редких AAC-LC профилей.</para>
    /// </remarks>
    private static readonly int[] AacSampleRates =
    [
        96000, 88200, 64000, 48000, 44100, 32000,
        24000, 22050, 16000, 12000, 11025, 8000,
        7350, 0, 0, 0
    ];

    /// <summary>
    /// Получить sample rate по индексу из AAC Audio Specific Config.
    /// </summary>
    /// <param name="index">Индекс частоты (0–15) из ASC.</param>
    /// <returns>Sample rate в Hz; <see cref="DefaultSampleRate"/> для невалидных/зарезервированных индексов.</returns>
    public static int GetAacSampleRate(int index)
    {
        if ((uint)index >= (uint)AacSampleRates.Length)
            return DefaultSampleRate;

        int rate = AacSampleRates[index];
        return rate > 0 ? rate : DefaultSampleRate;
    }

    /// <summary>
    /// Полная таблица channel count по channel configuration из AAC ASC (ISO 14496-3 §1.6.5.1).
    /// </summary>
    /// <remarks>
    /// <para>Индекс 0 = определяется отдельно (program_config_element), здесь → <see cref="DefaultChannels"/>.</para>
    /// <para>Индекс 7 = 7.1 surround (8 каналов), индексы 8–15 = зарезервированы.</para>
    /// </remarks>
    private static readonly int[] AacChannelCounts = [0, 1, 2, 3, 4, 5, 6, 8];

    /// <summary>
    /// Получить количество каналов по channel configuration из AAC Audio Specific Config.
    /// </summary>
    /// <param name="channelConfig">Channel configuration (0–7) из ASC.</param>
    /// <returns>Количество каналов; <see cref="DefaultChannels"/> для невалидных значений.</returns>
    public static int GetAacChannels(int channelConfig)
    {
        if ((uint)channelConfig >= (uint)AacChannelCounts.Length)
            return DefaultChannels;

        int ch = AacChannelCounts[channelConfig];
        return ch > 0 ? ch : DefaultChannels;
    }
}