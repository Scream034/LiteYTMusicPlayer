using LMP.Core.Audio.Normalization;
using static LMP.Core.Audio.AudioConstants;

namespace LMP.Core.Audio;

/// <summary>
/// Настройки инициализации аудиоплеера.
/// </summary>
public sealed class AudioPlayerOptions
{
    /// <summary>Колбэк для обновления протухших URL на лету.</summary>
    public Func<string, CancellationToken, ValueTask<string?>>? UrlRefreshCallback { get; init; }

    /// <summary>
    /// Вызывается при обнаружении starvation (200+ underrun подряд).
    /// AudioEngine использует для форсированного rebuild HTTP-клиентов.
    /// </summary>
    public Action? OnStarvationDetected { get; init; }

    /// <summary>Частота оповещений UI об изменении позиции (мс).</summary>
    public TimeSpan PositionUpdateInterval { get; init; } = TimeSpan.FromMilliseconds(DefaultPositionUpdateIntervalMs);

    /// <summary>Количество попыток переподключения при ошибках сети.</summary>
    public int MaxRetryAttempts { get; init; } = AudioConstants.MaxRetryAttempts;

    /// <summary>Задержка между попытками переподключения.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(RetryDelayMs);

    /// <summary>Использовать заглушку вместо реального звукового драйвера.</summary>
    public bool UseNullBackend { get; init; }

    /// <summary>Конфигурация стриминга (настройки буферизации сети).</summary>
    public StreamingConfig? StreamingConfig { get; init; }

    /// <summary>
    /// Callback, определяющий, нужен ли fast-replay текущего трека
    /// при естественном завершении (RepeatMode.One).
    /// Если возвращает <c>true</c>, player выполняет rewind pipeline без пересоздания.
    /// </summary>
    public Func<bool>? ShouldFastReplay { get; init; }

    /// <summary>
    /// Callback конфигурации pipeline перед открытием gate.
    /// </summary>
    /// <remarks>
    /// <para><b>Аргументы:</b></para>
    /// <list type="bullet">
    ///   <item><c>AudioPipeline</c> — конфигурируемый pipeline.</item>
    ///   <item><c>string?</c> — trackId трека, для которого создан pipeline.
    ///     Передаётся из <see cref="AudioPlayer.HandlePlayAsync"/> через <c>cmd.Descriptor.TrackId</c>.
    ///     Гарантирует привязку gain к конкретному pipeline при rapid next/prev.</item>
    /// </list>
    /// </remarks>
    public Action<AudioPipeline, string?>? OnPipelineConfiguring { get; set; }

    /// <summary>
    /// Callback фиксации integrated loudness из EBU R128 анализа.
    /// Вызывается после pre-scan или после завершения real-time фазы (~3 сек).
    /// Аргументы: trackId, integrated LUFS, source.
    /// </summary>
    public Action<string, float, LoudnessSource>? OnIntegratedLufsResolved { get; init; }

    /// <summary>
    /// Callback для первичного получения continuation URL у partial-cache source.
    /// Используется только для initial network acquire, не для 403 refresh.
    /// </summary>
    public Func<string, CancellationToken, ValueTask<string?>>? UrlAcquireCallback { get; init; }
}