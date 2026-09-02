using System.Runtime.CompilerServices;

namespace LMP.Core.Logger;

/// <summary>
/// Глобальная статическая точка доступа к логгеру.
/// Thread-safe, поддерживает вызовы до Initialize (будут проигнорированы).
/// </summary>
public static class Log
{
    private static AsyncLogProcessor? _processor;
    private static volatile bool _isInitialized;

    /// <summary>
    /// Минимальный уровень логирования. Сообщения ниже этого уровня
    /// отбрасываются до создания строки — интерполяция не вычисляется.
    /// По умолчанию: Debug в Debug-сборке, Info в Release.
    /// </summary>
#if DEBUG
    public static LogLevel MinLevel { get; set; } = LogLevel.Debug;
#else
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;
#endif

    /// <summary>
    /// Проверка активности уровня. Используется InterpolatedStringHandler-ами
    /// для short-circuit вычисления интерполированных строк.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLevelEnabled(LogLevel level) =>
        _isInitialized && level >= MinLevel;

    /// <summary>
    /// Инициализация системы логгирования.
    /// Должна вызываться в начале Main, ПОСЛЕ создания папок (G.Folder.Create).
    /// </summary>
    public static void Initialize(string? logDirectory = null)
    {
        if (_isInitialized) return;

        var logsPath = logDirectory ?? GetDefaultLogDirectory();

        _processor = new AsyncLogProcessor(logsPath);
        _isInitialized = true;

        Info($"=== LOGGER INITIALIZED === Path: {logsPath}");
    }

    private static string GetDefaultLogDirectory()
    {
        try { return G.Folder.Logs; }
        catch
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LMP", "Logs");
        }
    }

    /// <summary>
    /// Корректное завершение работы. Записывает все буферизованные сообщения.
    /// Должна вызываться в finally блока Main.
    /// </summary>
    public static void Shutdown()
    {
        if (!_isInitialized || _processor == null) return;
        Info("=== LOGGER SHUTDOWN ===");
        _processor.Dispose();
        _processor = null;
        _isInitialized = false;
    }

    /// <summary>Асинхронное завершение работы.</summary>
    public static async ValueTask ShutdownAsync()
    {
        if (!_isInitialized || _processor == null) return;
        Info("=== LOGGER SHUTDOWN ===");
        await _processor.DisposeAsync().ConfigureAwait(false);
        _processor = null;
        _isInitialized = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Enqueue(LogLevel level, string message, Exception? ex = null)
    {
        if (!_isInitialized || _processor == null || level < MinLevel) return;
        _processor.Enqueue(new LogMessage(level, message, ex));
    }

    // String overloads — для литералов и уже готовых строк

    /// <summary>Самый детальный уровень. Для трассировки потока выполнения.</summary>
    public static void Trace(string message) => Enqueue(LogLevel.Trace, message);

    /// <summary>Отладочная информация.</summary>
    public static void Debug(string message) => Enqueue(LogLevel.Debug, message);

    /// <summary>Информационные сообщения. Основной уровень.</summary>
    public static void Info(string message) => Enqueue(LogLevel.Info, message);

    /// <summary>Предупреждения.</summary>
    public static void Warn(string message) => Enqueue(LogLevel.Warning, message);

    /// <summary>Ошибки.</summary>
    public static void Error(string message, Exception? ex = null) => Enqueue(LogLevel.Error, message, ex);

    /// <summary>Фатальные ошибки.</summary>
    public static void Fatal(string message, Exception? ex = null) => Enqueue(LogLevel.Fatal, message, ex);

    // InterpolatedStringHandler overloads — zero-alloc при отключённом уровне

    /// <summary>Trace с zero-alloc интерполяцией.</summary>
    public static void Trace(TraceInterpolatedStringHandler handler)
    {
        if (handler.IsEnabled) Enqueue(LogLevel.Trace, handler.ToStringAndClear());
    }

    /// <summary>Debug с zero-alloc интерполяцией.</summary>
    public static void Debug(DebugInterpolatedStringHandler handler)
    {
        if (handler.IsEnabled) Enqueue(LogLevel.Debug, handler.ToStringAndClear());
    }

    /// <summary>Info с zero-alloc интерполяцией.</summary>
    public static void Info(InfoInterpolatedStringHandler handler)
    {
        if (handler.IsEnabled) Enqueue(LogLevel.Info, handler.ToStringAndClear());
    }

    /// <summary>Warn с zero-alloc интерполяцией.</summary>
    public static void Warn(WarnInterpolatedStringHandler handler)
    {
        if (handler.IsEnabled) Enqueue(LogLevel.Warning, handler.ToStringAndClear());
    }

    /// <summary>Error с zero-alloc интерполяцией.</summary>
    public static void Error(ErrorInterpolatedStringHandler handler, Exception? ex = null)
    {
        if (handler.IsEnabled) Enqueue(LogLevel.Error, handler.ToStringAndClear(), ex);
    }

    /// <summary>Fatal с zero-alloc интерполяцией.</summary>
    public static void Fatal(FatalInterpolatedStringHandler handler, Exception? ex = null)
    {
        if (handler.IsEnabled) Enqueue(LogLevel.Fatal, handler.ToStringAndClear(), ex);
    }

    /// <summary>Перегрузка для объектов (как Console.WriteLine).</summary>
    public static void Info(object? obj) => Info(obj?.ToString() ?? "null");

    /// <summary>Перегрузка для объектов.</summary>
    public static void Debug(object? obj) => Debug(obj?.ToString() ?? "null");

    // InterpolatedStringHandler per level

    /// <inheritdoc cref="LogInterpolatedStringHandlerBase"/>
    [InterpolatedStringHandler]
    public ref struct TraceInterpolatedStringHandler
    {
        private LogInterpolatedStringHandlerBase _impl;
        internal readonly bool IsEnabled;

        public TraceInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            _impl = new LogInterpolatedStringHandlerBase(literalLength, formattedCount, LogLevel.Trace, out isEnabled);
            IsEnabled = isEnabled;
        }

        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendLiteral"/>
        public void AppendLiteral(string s) => _impl.AppendLiteral(s);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) => _impl.AppendFormatted(value, alignment, format);
        internal string ToStringAndClear() => _impl.ToStringAndClear();
    }

    /// <inheritdoc cref="LogInterpolatedStringHandlerBase"/>
    [InterpolatedStringHandler]
    public ref struct DebugInterpolatedStringHandler
    {
        private LogInterpolatedStringHandlerBase _impl;
        internal readonly bool IsEnabled;

        public DebugInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            _impl = new LogInterpolatedStringHandlerBase(literalLength, formattedCount, LogLevel.Debug, out isEnabled);
            IsEnabled = isEnabled;
        }

        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendLiteral"/>
        public void AppendLiteral(string s) => _impl.AppendLiteral(s);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) => _impl.AppendFormatted(value, alignment, format);
        internal string ToStringAndClear() => _impl.ToStringAndClear();
    }

    /// <inheritdoc cref="LogInterpolatedStringHandlerBase"/>
    [InterpolatedStringHandler]
    public ref struct InfoInterpolatedStringHandler
    {
        private LogInterpolatedStringHandlerBase _impl;
        internal readonly bool IsEnabled;

        public InfoInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            _impl = new LogInterpolatedStringHandlerBase(literalLength, formattedCount, LogLevel.Info, out isEnabled);
            IsEnabled = isEnabled;
        }

        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendLiteral"/>
        public void AppendLiteral(string s) => _impl.AppendLiteral(s);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) => _impl.AppendFormatted(value, alignment, format);
        internal string ToStringAndClear() => _impl.ToStringAndClear();
    }

    /// <inheritdoc cref="LogInterpolatedStringHandlerBase"/>
    [InterpolatedStringHandler]
    public ref struct WarnInterpolatedStringHandler
    {
        private LogInterpolatedStringHandlerBase _impl;
        internal readonly bool IsEnabled;

        public WarnInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            _impl = new LogInterpolatedStringHandlerBase(literalLength, formattedCount, LogLevel.Warning, out isEnabled);
            IsEnabled = isEnabled;
        }

        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendLiteral"/>
        public void AppendLiteral(string s) => _impl.AppendLiteral(s);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) => _impl.AppendFormatted(value, alignment, format);
        internal string ToStringAndClear() => _impl.ToStringAndClear();
    }

    /// <inheritdoc cref="LogInterpolatedStringHandlerBase"/>
    [InterpolatedStringHandler]
    public ref struct ErrorInterpolatedStringHandler
    {
        private LogInterpolatedStringHandlerBase _impl;
        internal readonly bool IsEnabled;

        public ErrorInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            _impl = new LogInterpolatedStringHandlerBase(literalLength, formattedCount, LogLevel.Error, out isEnabled);
            IsEnabled = isEnabled;
        }

        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendLiteral"/>
        public void AppendLiteral(string s) => _impl.AppendLiteral(s);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) => _impl.AppendFormatted(value, alignment, format);
        internal string ToStringAndClear() => _impl.ToStringAndClear();
    }

    /// <inheritdoc cref="LogInterpolatedStringHandlerBase"/>
    [InterpolatedStringHandler]
    public ref struct FatalInterpolatedStringHandler
    {
        private LogInterpolatedStringHandlerBase _impl;
        internal readonly bool IsEnabled;

        public FatalInterpolatedStringHandler(int literalLength, int formattedCount, out bool isEnabled)
        {
            _impl = new LogInterpolatedStringHandlerBase(literalLength, formattedCount, LogLevel.Fatal, out isEnabled);
            IsEnabled = isEnabled;
        }

        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendLiteral"/>
        public void AppendLiteral(string s) => _impl.AppendLiteral(s);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T)"/>
        public void AppendFormatted<T>(T value) => _impl.AppendFormatted(value);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,string?)"/>
        public void AppendFormatted<T>(T value, string? format) => _impl.AppendFormatted(value, format);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int)"/>
        public void AppendFormatted<T>(T value, int alignment) => _impl.AppendFormatted(value, alignment);
        /// <inheritdoc cref="LogInterpolatedStringHandlerBase.AppendFormatted{T}(T,int,string?)"/>
        public void AppendFormatted<T>(T value, int alignment, string? format) => _impl.AppendFormatted(value, alignment, format);
        internal string ToStringAndClear() => _impl.ToStringAndClear();
    }

    /// <summary>
    /// Общая реализация строителя интерполированной строки для всех уровней логов.
    /// </summary>
    public ref struct LogInterpolatedStringHandlerBase
    {
        private DefaultInterpolatedStringHandler _inner;
        private readonly bool _enabled;

        internal LogInterpolatedStringHandlerBase(
            int literalLength,
            int formattedCount,
            LogLevel level,
            out bool isEnabled)
        {
            _enabled = isEnabled = IsLevelEnabled(level);
            _inner = _enabled
                ? new DefaultInterpolatedStringHandler(literalLength, formattedCount)
                : default;
        }

        /// <summary>Добавляет строковый литерал. No-op если уровень отключён.</summary>
        public void AppendLiteral(string s)
        {
            if (_enabled) _inner.AppendLiteral(s);
        }

        /// <summary>Добавляет форматируемое значение. No-op если уровень отключён.</summary>
        public void AppendFormatted<T>(T value)
        {
            if (_enabled) _inner.AppendFormatted(value);
        }

        /// <summary>Добавляет форматируемое значение с format-строкой. No-op если уровень отключён.</summary>
        public void AppendFormatted<T>(T value, string? format)
        {
            if (_enabled) _inner.AppendFormatted(value, format);
        }

        /// <summary>
        /// Добавляет форматируемое значение с выравниванием: <c>{x,-45}</c>, <c>{x,15}</c>.
        /// No-op если уровень отключён.
        /// </summary>
        public void AppendFormatted<T>(T value, int alignment)
        {
            if (_enabled) _inner.AppendFormatted(value, alignment);
        }

        /// <summary>
        /// Добавляет форматируемое значение с выравниванием и format-строкой: <c>{x,15:F2}</c>.
        /// No-op если уровень отключён.
        /// </summary>
        public void AppendFormatted<T>(T value, int alignment, string? format)
        {
            if (_enabled) _inner.AppendFormatted(value, alignment, format);
        }

        internal string ToStringAndClear() =>
            _enabled ? _inner.ToStringAndClear() : string.Empty;
    }
}