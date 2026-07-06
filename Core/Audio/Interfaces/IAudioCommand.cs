using LMP.Core.Audio;

namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Маркерный интерфейс для всех команд аудио плеера.
/// </summary>
public interface IAudioCommand
{
    /// <summary>
    /// Уникальный ID сессии для отмены устаревших команд.
    /// </summary>
    int SessionId { get; }
}

/// <summary>
/// Команда воспроизведения.
/// </summary>
/// <param name="Descriptor">
/// Единый дескриптор resolved аудио потока, содержащий URL, trackId, bitrate,
/// format, codec, contentLength, loudnessDb и другие метаданные.
/// </param>
/// <param name="SessionId">Уникальный ID сессии.</param>
/// <param name="SeekPosition">
/// Позиция для seek ПЕРЕД стартом воспроизведения (atomic seek-before-play).
/// null = начать с начала трека.
/// 
/// <para><b>Зачем:</b> При переключении качества (SwitchQualityAsync) нужно
/// начать воспроизведение с текущей позиции, а не с начала. Без этого поля
/// между PlayAsync и SeekAsync слышен артефакт — 16-300ms звука с позиции 0.</para>
/// </param>
/// <param name="ExternalCancellationToken">
/// Токен отмены пользовательской сессии воспроизведения.
/// Позволяет мгновенно прервать запуск устаревшего трека, если пользователь уже переключился.
/// </param>
public sealed record PlayCommand(
    ResolvedStreamDescriptor Descriptor,
    int SessionId,
    TimeSpan? SeekPosition = null,
    CancellationToken ExternalCancellationToken = default) : IAudioCommand;

/// <summary>
/// Команда полной остановки воспроизведения.
/// </summary>
/// <param name="SessionId">Уникальный ID сессии.</param>
public sealed record StopCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Команда постановки воспроизведения на паузу.
/// </summary>
/// <param name="SessionId">Уникальный ID сессии.</param>
public sealed record PauseCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Команда возобновления воспроизведения после паузы.
/// </summary>
/// <param name="SessionId">Уникальный ID сессии.</param>
public sealed record ResumeCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Команда seek к новой позиции внутри текущего трека.
/// </summary>
/// <param name="Position">Целевая позиция.</param>
/// <param name="SessionId">Уникальный ID сессии.</param>
/// <param name="SeekGeneration">
/// Монотонно возрастающее поколение seek-операции.
/// Используется для отбрасывания устаревших deferred resume completion'ов.
/// </param>
/// <param name="Completion">Опциональный completion source для awaitable seek.</param>
public sealed record SeekCommand(
    TimeSpan Position,
    int SessionId,
    int SeekGeneration,
    TaskCompletionSource<bool>? Completion = null) : IAudioCommand;

/// <summary>
/// Команда deferred-resume после seek-buffering.
/// </summary>
/// <param name="SessionId">Сессия, в рамках которой был инициирован seek.</param>
/// <param name="SeekGeneration">Поколение seek-операции.</param>
/// <param name="Pipeline">Pipeline, для которого готовился resume.</param>
/// <param name="ThresholdReached">
/// <c>true</c>, если ring buffer достиг seek-threshold;
/// <c>false</c>, если ожидание завершилось по timeout и resume должен быть форсирован.
/// </param>
/// <param name="BufferedSamples">Фактическое количество сэмплов в ring buffer на момент завершения ожидания.</param>
public sealed record DeferredResumeCommand(
    int SessionId,
    int SeekGeneration,
    AudioPipeline Pipeline,
    bool ThresholdReached,
    int BufferedSamples) : IAudioCommand;

/// <summary>
/// Команда уведомления actor loop о потере аудиоустройства.
/// </summary>
/// <param name="SessionId">Сессия на момент потери устройства.</param>
/// <param name="Pipeline">Pipeline, связанный с устройством.</param>
public sealed record DeviceLostCommand(
    int SessionId,
    AudioPipeline Pipeline) : IAudioCommand;

/// <summary>
/// Команда уведомления actor loop о появлении аудиоустройства.
/// </summary>
/// <param name="SessionId">Сессия на момент появления устройства.</param>
/// <param name="Pipeline">Pipeline, ожидающий восстановления устройства.</param>
public sealed record DeviceAvailableCommand(
    int SessionId,
    AudioPipeline Pipeline) : IAudioCommand;

/// <summary>
/// Команда доставки фоновой ошибки в actor loop.
/// </summary>
/// <param name="SessionId">Сессия, в рамках которой произошла ошибка.</param>
/// <param name="Pipeline">Pipeline, из которого пришла ошибка.</param>
/// <param name="Error">Исключение-ошибка.</param>
public sealed record PlayerErrorCommand(
    int SessionId,
    AudioPipeline Pipeline,
    Exception Error) : IAudioCommand;

/// <summary>
/// Команда финального уничтожения плеера и его инфраструктуры.
/// </summary>
/// <param name="SessionId">Уникальный ID сессии.</param>
public sealed record DisposeCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Команда естественного завершения текущего трека.
/// </summary>
/// <remarks>
/// <para>Команда публикуется из decoder loop в actor channel вместо прямого вызова
/// <c>AudioPlayer.OnTrackEnded()</c> из фонового потока.</para>
/// <para>Это сохраняет single-threaded actor semantics для state machine плеера и
/// устраняет поздние stale-callback'и, которые могли сбрасывать уже новый pipeline.</para>
/// </remarks>
/// <param name="SessionId">Сессия, в рамках которой трек завершился.</param>
public sealed record TrackEndedCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Команда восстановления аудиоустройства после потери (BT disconnect и т.д.).
/// Pipeline остаётся живым, backend пересоздаётся через retry loop.
/// </summary>
/// <param name="SessionId">Сессия на момент отправки команды.</param>
public sealed record DeviceRecoveryCommand(int SessionId) : IAudioCommand;

/// <summary>
/// Команда уведомления actor loop о критическом опустошении аудиобуфера (starvation).
/// </summary>
/// <remarks>
/// <para>Публикуется из fill thread backend'а через pipeline callback.
/// Actor переводит плеер в Buffering и запускает deferred resume,
/// вместо деструктивного CancelActiveReads, который убивает единственный живой HTTP-запрос
/// на медленной сети.</para>
/// </remarks>
/// <param name="SessionId">Сессия на момент обнаружения starvation.</param>
/// <param name="Pipeline">Pipeline, в котором произошёл starvation.</param>
public sealed record StarvationCommand(
    int SessionId,
    AudioPipeline Pipeline) : IAudioCommand;