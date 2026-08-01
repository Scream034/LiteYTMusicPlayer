using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace LMP.UI.Controls;

/// <summary>
/// Единый attached property для загрузки изображений с debounce, quality control
/// и единым LRU-кэшем. Полностью заменяет DebouncedImage и QualityImage.
///
/// <para><b>Проблема которую решает:</b>
/// DebouncedImage → AsyncImageLoader → RamCachedWebImageLoader (unbounded, full-resolution).
/// 574 thumbnail × 280KB = до 160MB без eviction.
/// SmartImage → ImageCacheService → FNV-1a LRU, MaxMemoryItems=80, DecodeToWidth(120).
/// 80 × 57KB ≈ 4.5MB потолок.</para>
///
/// <para><b>Debounce:</b>
/// При <see cref="DebounceProperty"/> &gt; 0 — двухфазная задержка.
/// Phase 1: yield на Background priority (UI завершает layout текущего кадра).
/// Phase 2: Task.Delay фильтрует промежуточные рециклинги.
/// Рециклинг до истечения delay → CTS отменяется → загрузка прерывается.</para>
///
/// <para><b>Bitmap lifecycle:</b>
/// HTTP → shared из LRU-кэша (IsOwned=false, не диспозим — Image.Source держит ссылку,
/// GC соберёт после eviction + смены Source).
/// avares:// / local → создан для контрола (IsOwned=true, диспозим при смене Source).</para>
/// </summary>
public static class SmartImage
{
    #region Constants

    /// <summary>
    /// Задержка debounce по умолчанию для виртуализованных списков (мс).
    /// 80ms ≈ 5 кадров при 60fps — достаточно чтобы отфильтровать
    /// промежуточные рециклинги при быстром скролле.
    /// </summary>
    public const int DefaultDebounceMs = 80;

    #endregion

    #region Attached Properties

    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Source", typeof(SmartImage));

    /// <summary>
    /// Качество декодирования. Avalonia автоматически парсит enum из XAML:
    /// <c>ctrl:SmartImage.Quality="Low"</c> → ImageQuality.Low (120px).
    /// </summary>
    public static readonly AttachedProperty<ImageQuality> QualityProperty =
        AvaloniaProperty.RegisterAttached<Image, ImageQuality>("Quality", typeof(SmartImage),
            defaultValue: ImageQuality.Low);

    /// <summary>
    /// Задержка debounce в мс. 0 = без debounce (статические изображения вне списков).
    /// Используй <see cref="DefaultDebounceMs"/> для ItemsRepeater с рециклингом.
    /// </summary>
    public static readonly AttachedProperty<int> DebounceProperty =
        AvaloniaProperty.RegisterAttached<Image, int>("Debounce", typeof(SmartImage),
            defaultValue: 0);

    /// <summary>
    /// Внутренний маркер: true если bitmap создан для этого контрола (avares/local).
    /// false = bitmap из shared LRU-кэша, трогать нельзя.
    /// </summary>
    private static readonly AttachedProperty<bool> IsOwnedProperty =
        AvaloniaProperty.RegisterAttached<Image, bool>("IsOwned", typeof(SmartImage));

    #endregion

    #region Pending Operations

    /// <summary>
    /// ConditionalWeakTable не удерживает Image от GC —
    /// рециклированный контрол собирается автоматически вместе с его CTS.
    /// </summary>
    private static readonly ConditionalWeakTable<Image, CancellationTokenSource> _pending = [];

    /// <summary>
    /// Кэшируем сервис статически: GetService&lt;T&gt;() — это словарный lookup на каждый вызов.
    /// При скролле 574 треков — сотни вызовов без кэша. Инициализируется лениво,
    /// после того как DI-контейнер готов (первое обращение не раньше первой загрузки).
    /// </summary>
    private static ImageCacheService? _imageCache;

    #endregion

    static SmartImage()
    {
        SourceProperty.Changed.AddClassHandler<Image>(static (img, _) => BeginLoad(img));
        QualityProperty.Changed.AddClassHandler<Image>(static (img, _) => BeginLoad(img));
    }

    #region Getters / Setters

    public static string? GetSource(Image image) => image.GetValue(SourceProperty);
    public static void SetSource(Image image, string? value) => image.SetValue(SourceProperty, value);

    public static ImageQuality GetQuality(Image image) => image.GetValue(QualityProperty);
    public static void SetQuality(Image image, ImageQuality value) => image.SetValue(QualityProperty, value);

    public static int GetDebounce(Image image) => image.GetValue(DebounceProperty);
    public static void SetDebounce(Image image, int value) => image.SetValue(DebounceProperty, value);

    #endregion

    #region Core Load Logic

    /// <summary>
    /// Запускает загрузку изображения для контрола. Вызывается при изменении
    /// <see cref="SourceProperty"/> или <see cref="QualityProperty"/>.
    /// </summary>
    private static async void BeginLoad(Image image)
    {
        // Quality == 0 означает default(ImageQuality): шаблон ещё не применил значение.
        // Дожидаемся QualityProperty.Changed, который вызовет BeginLoad с корректным quality.
        if (GetQuality(image) == 0) return;

        // Отменяем предыдущую pending-загрузку (рециклинг элемента)
        if (_pending.TryGetValue(image, out var oldCts))
        {
            await oldCts.CancelAsync();
            oldCts.Dispose();
            _pending.Remove(image);
        }

        var url = GetSource(image);

        if (string.IsNullOrEmpty(url))
        {
            DisposeOwned(image);
            image.Source = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _pending.AddOrUpdate(image, cts);

        try
        {
            var debounceMs = GetDebounce(image);

            if (debounceMs > 0)
            {
                // Phase 1: yield — UI thread завершает layout + render текущего кадра.
                // Без этого декодирование блокирует Dispatcher до отрисовки placeholders.
                await Dispatcher.UIThread.InvokeAsync(
                    static () => { }, DispatcherPriority.Background);

                if (cts.IsCancellationRequested) return;

                // Phase 2: фильтруем промежуточные рециклинги при быстром скролле.
                await Task.Delay(debounceMs);

                if (cts.IsCancellationRequested) return;
            }

            if (cts.IsCancellationRequested) return;

            // URL мог смениться за время debounce (новый BeginLoad отменил бы CTS,
            // но проверяем явно как defence-in-depth).
            if (GetSource(image) != url) return;

            var decodeWidth = (int)GetQuality(image);
            var urlSpan = url.AsSpan();

            if (urlSpan.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                await LoadHttpAsync(image, url, decodeWidth, cts.Token);
            else if (urlSpan.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
                await LoadAvaresAsync(image, url, decodeWidth, cts.Token);
            else
                await LoadLocalAsync(image, url, decodeWidth, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Ожидаемо: элемент рециклирован до завершения загрузки
        }
        catch (Exception ex)
        {
            Log.Debug($"[SmartImage] Load failed for '{url}': {ex.Message}");
            DisposeOwned(image);
            image.Source = null;
        }
        finally
        {
            // Удаляем только свой CTS — к этому моменту мог появиться более новый
            if (_pending.TryGetValue(image, out var current) && ReferenceEquals(current, cts))
                _pending.Remove(image);

            cts.Dispose();
        }
    }

    #endregion

    #region Loaders

    /// <summary>
    /// HTTP/HTTPS: bitmap из единого LRU-кэша ImageCacheService.
    /// Выполняется на UI-потоке: вызывающая цепочка BeginLoad не использует
    /// ConfigureAwait(false), continuation возвращается через UI SynchronizationContext.
    /// Shared bitmap — IsOwned=false, Dispose не вызываем.
    /// </summary>
    private static async Task LoadHttpAsync(Image image, string url, int decodeWidth, CancellationToken ct)
    {
        // Ленивая инициализация: всегда на UI-потоке, гонки нет.
        _imageCache ??= AppEntry.Services.GetService<ImageCacheService>();
        if (_imageCache == null) return;

        var bitmap = await _imageCache.GetImageAsync(url, decodeWidth, ct);

        // bitmap == null: CT отменён (рециклинг) или ошибка загрузки.
        if (bitmap == null || ct.IsCancellationRequested || GetSource(image) != url) return;

        DisposeOwned(image);
        image.Source = bitmap;
        image.SetValue(IsOwnedProperty, false);
    }

    /// <summary>
    /// avares:// embedded resource: decode на threadpool, bitmap принадлежит контролу.
    /// Task.Run вынесен из UI-потока: DecodeToWidth на больших ресурсах может
    /// занять несколько мс и вызвать jank при синхронном вызове.
    /// </summary>
    private static async Task LoadAvaresAsync(Image image, string url, int decodeWidth, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (!Avalonia.Platform.AssetLoader.Exists(uri)) return;

        // Читаем байты на UI-потоке (AssetLoader требует UI context), декодируем на threadpool
        byte[] bytes;
        using (var assetStream = Avalonia.Platform.AssetLoader.Open(uri))
        {
            bytes = new byte[assetStream.Length];
            _ = await assetStream.ReadAsync(bytes, ct);
        }

        if (ct.IsCancellationRequested || GetSource(image) != url) return;

        var bitmap = await Task.Run(() =>
        {
            using var stream = new MemoryStream(bytes);
            return decodeWidth > 0
                ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality)
                : new Bitmap(stream);
        }, ct);

        if (ct.IsCancellationRequested || GetSource(image) != url)
        {
            bitmap.Dispose();
            return;
        }

        DisposeOwned(image);
        image.Source = bitmap;
        image.SetValue(IsOwnedProperty, true);
    }

    /// <summary>
    /// Локальный файл: decode на threadpool, bitmap принадлежит контролу.
    /// FileStream с useAsync:false эффективнее на threadpool (нет overhead async I/O).
    /// </summary>
    private static async Task LoadLocalAsync(Image image, string url, int decodeWidth, CancellationToken ct)
    {
        var path = ResolveLocalPath(url);
        if (path == null || !File.Exists(path))
        {
            DisposeOwned(image);
            image.Source = null;
            return;
        }

        var bitmap = await Task.Run(() =>
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: false);

            return decodeWidth > 0
                ? Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.MediumQuality)
                : new Bitmap(stream);
        }, ct);

        if (ct.IsCancellationRequested || GetSource(image) != url)
        {
            bitmap.Dispose();
            return;
        }

        DisposeOwned(image);
        image.Source = bitmap;
        image.SetValue(IsOwnedProperty, true);
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// Диспозит текущий bitmap только если он "owned" (avares/local).
    /// HTTP-bitmaps из LRU-кэша не диспозим: кэш управляет lifecycle,
    /// Image.Source держит последнюю ссылку до смены Source.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DisposeOwned(Image image)
    {
        if (image.Source is Bitmap old && image.GetValue(IsOwnedProperty))
        {
            image.SetValue(IsOwnedProperty, false);
            old.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string? ResolveLocalPath(string url)
    {
        if (url.StartsWith(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.LocalPath : null;

        return Path.IsPathRooted(url) ? url : null;
    }

    #endregion
}