namespace LMP.Core.Audio.Sources;

public sealed partial class CachingStreamSource
{
    /// <summary>
    /// Stream-обёртка над <see cref="CachingStreamSource"/> для парсеров контейнеров (WebM/MP4).
    ///
    /// <para><b>Оптимизация Position setter:</b></para>
    /// <list type="bullet">
    ///   <item><c>Position setter</c> — лёгкий <c>Volatile.Write</c>, без CTS-аллокаций</item>
    ///   <item><see cref="SeekAndCancelPendingReads"/> — явный cancel для внешнего seek</item>
    /// </list>
    /// </summary>
    private sealed class AsyncCachingReadStream : Stream
    {
        private readonly CachingStreamSource _source;
        private long _position;

        /// <summary>
        /// CTS, привязанный к текущей логической позиции потока.
        /// Заменяется только через <see cref="SeekAndCancelPendingReads"/>.
        /// </summary>
        private CancellationTokenSource _readCts = new();

        /// <summary>Создаёт Stream-обёртку над источником.</summary>
        public AsyncCachingReadStream(CachingStreamSource source) => _source = source;

        #region Stream Properties

        /// <inheritdoc/>
        public override bool CanRead => true;

        /// <inheritdoc/>
        public override bool CanSeek => true;

        /// <inheritdoc/>
        public override bool CanWrite => false;

        /// <inheritdoc/>
        public override long Length => _source._contentLength;

        /// <inheritdoc/>
        /// <remarks>
        /// Lightweight setter: только <c>Volatile.Write</c>.
        /// Cancel CTS выполняется только через <see cref="SeekAndCancelPendingReads"/>.
        /// </remarks>
        public override long Position
        {
            get => Volatile.Read(ref _position);
            set => Volatile.Write(ref _position, value);
        }

        #endregion

        #region Seek & Cancel

        /// <summary>
        /// Устанавливает позицию потока И отменяет все pending ReadAsync.
        /// Вызывается ТОЛЬКО из <see cref="SeekAsync"/> при внешнем seek.
        /// </summary>
        /// <param name="position">Новая абсолютная позиция в байтах.</param>
        internal void SeekAndCancelPendingReads(long position)
        {
            var oldCts = Interlocked.Exchange(ref _readCts, new CancellationTokenSource());

            try { oldCts.Cancel(); }
            catch (ObjectDisposedException) { }

            DeferDisposeCancellationTokenSource(oldCts, 500);
            Volatile.Write(ref _position, position);
        }

        /// <summary>
        /// Мгновенно отменяет активные операции чтения синхронно.
        /// </summary>
        internal void CancelActiveReads()
        {
            var oldCts = Interlocked.Exchange(ref _readCts, new CancellationTokenSource());
            if (oldCts == null) return;

            try
            {
                // Синхронная отмена немедленно прерывает блокирующий Read() в DecoderLoop
                oldCts.Cancel();
            }
            catch (ObjectDisposedException) { }

            // Dispose откладываем, чтобы избежать ObjectDisposedException в других потоках
            DeferDisposeCancellationTokenSource(oldCts, 500);
        }

        /// <summary>
        /// Безопасно извлекает <see cref="CancellationToken"/> из текущего <see cref="_readCts"/>.
        /// </summary>
        private CancellationToken GetCurrentReadToken()
        {
            try
            {
                return Volatile.Read(ref _readCts)?.Token ?? CancellationToken.None;
            }
            catch (ObjectDisposedException)
            {
                return new CancellationToken(canceled: true);
            }
        }

        #endregion

        #region Read

        /// <inheritdoc/>
        /// <remarks>
        /// Горячий путь: если данные в RAM — синхронное копирование без планировщика.
        /// Холодный путь: sync-over-async fallback для парсеров, вызывающих <c>Read</c>.
        /// </remarks>
        public override int Read(byte[] buffer, int offset, int count)
        {
            long posBefore = Volatile.Read(ref _position);
            if (posBefore >= _source._contentLength) return 0;

            // Быстрый путь: данные уже в RAM
            if (_source._ramCache.TryRead(posBefore, buffer.AsMemory(offset, count), out int ramRead))
            {
                Volatile.Write(ref _position, posBefore + ramRead);
                return ramRead;
            }

            // Медленный путь: сетевой запрос через sync-over-async
            try
            {
                return ReadAsyncCore(buffer.AsMemory(offset, count), GetCurrentReadToken())
                    .AsTask()
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException) { return 0; }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { return 0; }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            {
                return 0;
            }
        }

        /// <inheritdoc/>
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        /// <inheritdoc/>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var readToken = GetCurrentReadToken();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, readToken);
            return await ReadAsyncCore(buffer, linkedCts.Token).ConfigureAwait(false);
        }

        /// <summary>
        /// Ядро чтения: делегирует в <see cref="ReadAtAsync"/> и
        /// атомарно продвигает позицию только если seek не произошёл во время чтения.
        /// </summary>
        private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken ct)
        {
            long posBefore = Volatile.Read(ref _position);
            int read = await _source.ReadAtAsync(posBefore, buffer, ct).ConfigureAwait(false);

            // Если seek произошёл во время I/O — позиция уже обновлена.
            // Возвращаем 0, чтобы парсер не продвинулся по устаревшим данным.
            if (Volatile.Read(ref _position) != posBefore)
                return 0;

            Interlocked.CompareExchange(ref _position, posBefore + read, posBefore);
            return read;
        }

        #endregion

        #region Seek

        /// <inheritdoc/>
        public override long Seek(long offset, SeekOrigin origin)
        {
            long length = _source._contentLength;
            long newPos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Volatile.Read(ref _position) + offset,
                SeekOrigin.End => length + offset,
                _ => Volatile.Read(ref _position)
            };

            newPos = Math.Clamp(newPos, 0, length);
            Position = newPos;
            return newPos;
        }

        #endregion

        #region Not Supported

        /// <inheritdoc/>
        public override void Flush() { }

        /// <inheritdoc/>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc/>
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        #endregion

        #region Dispose

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var cts = Interlocked.Exchange(ref _readCts, null!);
                if (cts != null)
                {
                    try { cts.Cancel(); } catch (ObjectDisposedException) { }
                    try { cts.Dispose(); } catch (ObjectDisposedException) { }
                }
            }

            base.Dispose(disposing);
        }

        #endregion
    }
}