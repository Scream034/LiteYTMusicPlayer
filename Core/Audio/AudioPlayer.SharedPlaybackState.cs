namespace LMP.Core.Audio;

public partial class AudioPlayer
{
    /// <summary>
    /// Атомарное, сверхбыстрое lock-free хранилище для бесшовной интерполяции положения воспроизведения.
    /// Позволяет UI-потоку "вытягивать" позицию на любой частоте (например, 60 FPS) с идеальной плавностью.
    /// </summary>
    private sealed class SharedPlaybackState
    {
        private long _baseSamples;
        private int _bufferedSamples;
        private long _baseTimestamp;
        private int _sampleRate;
        private int _channels;
        private int _isPlaying;
        private long _durationMs;
        private double _lastReturnedSeconds;

        /// <summary>
        /// Атомарно обновляет базовые показатели физического воспроизведения.
        /// </summary>
        public void Update(long baseSamples, int bufferedSamples, int sampleRate, int channels, bool isPlaying, long durationMs)
        {
            Volatile.Write(ref _baseSamples, baseSamples);
            Volatile.Write(ref _bufferedSamples, bufferedSamples);
            Volatile.Write(ref _baseTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            Volatile.Write(ref _sampleRate, sampleRate);
            Volatile.Write(ref _channels, channels);
            Volatile.Write(ref _isPlaying, isPlaying ? 1 : 0);
            Volatile.Write(ref _durationMs, durationMs);
        }

        /// <summary>
        /// Возвращает экстраполированное монотонное время с защитой от микро-вибраций таймера
        /// </summary>
        public TimeSpan GetCurrentPosition()
        {
            long baseSamples = Interlocked.Read(ref _baseSamples);
            int bufferedSamples = Volatile.Read(ref _bufferedSamples);
            long baseTimestamp = Interlocked.Read(ref _baseTimestamp);
            int sampleRate = Volatile.Read(ref _sampleRate);
            int channels = Volatile.Read(ref _channels);
            bool isPlaying = Volatile.Read(ref _isPlaying) == 1;
            long durationMs = Interlocked.Read(ref _durationMs);

            if (sampleRate <= 0 || channels <= 0) return TimeSpan.Zero;

            double baseSeconds = (double)baseSamples / (sampleRate * channels);
            double maxExtrapolation = baseSeconds + ((double)bufferedSamples / (sampleRate * channels));
            double maxSeconds = durationMs / 1000.0;

            if (!isPlaying)
            {
                double finalSec = Math.Clamp(baseSeconds, 0.0, maxSeconds);
                _lastReturnedSeconds = finalSec;
                return TimeSpan.FromSeconds(finalSec);
            }

            long currentTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            double elapsedSeconds = (double)(currentTimestamp - baseTimestamp) / System.Diagnostics.Stopwatch.Frequency;
            double extrapolated = baseSeconds + elapsedSeconds;

            // HARD CLAMP: Prevent slider ghosting during network starvation.
            // Extrapolation cannot exceed the amount of actual audio data resident in hardware buffer.
            if (extrapolated > maxExtrapolation) extrapolated = maxExtrapolation;

            if (extrapolated > maxSeconds) extrapolated = maxSeconds;
            if (extrapolated < 0.0) extrapolated = 0.0;

            // Clock Jitter Guard
            if (extrapolated < _lastReturnedSeconds && extrapolated >= _lastReturnedSeconds - 0.2)
            {
                extrapolated = _lastReturnedSeconds;
            }

            _lastReturnedSeconds = extrapolated;
            return TimeSpan.FromSeconds(extrapolated);
        }
    }
}