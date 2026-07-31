using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LMP.UI.Services;

/// <summary>
/// Генерирует мозаичную обложку плейлиста из обложек треков.
/// 
/// <para><b>Поддерживаемые раскладки:</b></para>
/// <list type="bullet">
///   <item>1 обложка: масштабирование под полный размер</item>
///   <item>2 обложки: две половинки по горизонтали</item>
///   <item>3 обложки: 1 большая слева + 2 маленькие справа</item>
///   <item>4 обложки: сетка 2×2</item>
/// </list>
/// 
/// <para><b>Результат:</b> PNG файл в AppData/LMP/Covers/,
/// имя файла = хеш от ID треков для идемпотентности.</para>
/// </summary>
public static class MosaicGenerator
{
    /// <summary>Размер результирующего изображения (квадрат).</summary>
    private const int OutputSize = 512;

    /// <summary>Зазор между частями мозаики в пикселях.</summary>
    private const int Gap = 2;

    /// <summary>Папка для сохранения мозаик.</summary>
    private static readonly string CoversFolder = Path.Combine(G.Folder.Data, "Covers");

    /// <summary>
    /// Генерирует мозаику из переданных изображений и сохраняет как PNG.
    /// 
    /// <para><b>Потокобезопасность:</b> метод запускает рендер на UI-потоке
    /// (Avalonia требует это для RenderTargetBitmap), но подготовку выполняет
    /// на текущем потоке.</para>
    /// </summary>
    /// <param name="bitmaps">
    /// Список загруженных изображений (1-4 штуки).
    /// Caller отвечает за их Dispose после вызова.
    /// </param>
    /// <param name="trackIds">
    /// ID треков — используются для генерации уникального имени файла.
    /// </param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Полный путь к сохранённому PNG файлу.</returns>
    /// <exception cref="ArgumentException">Если список пуст.</exception>
    public static async Task<string> GenerateAsync(
        IReadOnlyList<Bitmap> bitmaps,
        IReadOnlyList<string> trackIds,
        CancellationToken ct = default)
    {
        if (bitmaps.Count == 0)
            throw new ArgumentException("At least one bitmap is required.", nameof(bitmaps));

        // Гарантируем существование папки
        Directory.CreateDirectory(CoversFolder);

        // Генерируем уникальное имя файла из ID треков
        var fileName = GenerateFileName(trackIds);
        var filePath = Path.Combine(CoversFolder, fileName);

        // Если файл уже существует (идемпотентность) — возвращаем путь
        if (File.Exists(filePath))
            return filePath;

        ct.ThrowIfCancellationRequested();

        // Рендер мозаики — должен выполняться на UI-потоке (Avalonia rendering)
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            using var target = RenderMosaic(bitmaps);
            target.Save(filePath, PngBitmapEncoderOptions.Default);
        });

        return filePath;
    }

    /// <summary>
    /// Генерирует превью мозаики для отображения в UI (без сохранения на диск).
    /// Должен вызываться на UI-потоке.
    /// </summary>
    /// <param name="bitmaps">Загруженные обложки (1-4).</param>
    /// <returns>Bitmap для привязки к Image.Source.</returns>
    public static RenderTargetBitmap GeneratePreview(IReadOnlyList<Bitmap> bitmaps)
    {
        return RenderMosaic(bitmaps);
    }

    /// <summary>
    /// Рендерит мозаику в RenderTargetBitmap.
    /// Общая логика для GenerateAsync и GeneratePreview.
    /// </summary>
    private static RenderTargetBitmap RenderMosaic(IReadOnlyList<Bitmap> bitmaps)
    {
        var target = new RenderTargetBitmap(new PixelSize(OutputSize, OutputSize));

        using (var ctx = target.CreateDrawingContext())
        {
            // Заливаем фон тёмным цветом (для gap)
            ctx.DrawRectangle(
                Brushes.Black,
                null,
                new Rect(0, 0, OutputSize, OutputSize));

            var count = Math.Min(bitmaps.Count, 4);

            switch (count)
            {
                case 1:
                    DrawSingle(ctx, bitmaps[0]);
                    break;
                case 2:
                    DrawTwo(ctx, bitmaps[0], bitmaps[1]);
                    break;
                case 3:
                    DrawThree(ctx, bitmaps[0], bitmaps[1], bitmaps[2]);
                    break;
                case 4:
                    DrawFour(ctx, bitmaps[0], bitmaps[1], bitmaps[2], bitmaps[3]);
                    break;
            }
        }

        return target;
    }

    // ═══ Раскладки ═══

    /// <summary>1 обложка: масштабируется на весь квадрат.</summary>
    private static void DrawSingle(DrawingContext ctx, Bitmap bmp)
    {
        DrawBitmapToRect(ctx, bmp, new Rect(0, 0, OutputSize, OutputSize));
    }

    /// <summary>2 обложки: две половинки по горизонтали с зазором.</summary>
    private static void DrawTwo(DrawingContext ctx, Bitmap left, Bitmap right)
    {
        var halfW = (OutputSize - Gap) / 2.0;
        DrawBitmapToRect(ctx, left, new Rect(0, 0, halfW, OutputSize));
        DrawBitmapToRect(ctx, right, new Rect(halfW + Gap, 0, halfW, OutputSize));
    }

    /// <summary>3 обложки: 1 большая слева + 2 маленькие справа (стопкой).</summary>
    private static void DrawThree(DrawingContext ctx, Bitmap big, Bitmap topRight, Bitmap bottomRight)
    {
        var halfW = (OutputSize - Gap) / 2.0;
        var halfH = (OutputSize - Gap) / 2.0;

        // Большая слева — занимает полную высоту
        DrawBitmapToRect(ctx, big, new Rect(0, 0, halfW, OutputSize));
        // Верхняя правая
        DrawBitmapToRect(ctx, topRight, new Rect(halfW + Gap, 0, halfW, halfH));
        // Нижняя правая
        DrawBitmapToRect(ctx, bottomRight, new Rect(halfW + Gap, halfH + Gap, halfW, halfH));
    }

    /// <summary>4 обложки: сетка 2×2.</summary>
    private static void DrawFour(DrawingContext ctx, Bitmap tl, Bitmap tr, Bitmap bl, Bitmap br)
    {
        var halfW = (OutputSize - Gap) / 2.0;
        var halfH = (OutputSize - Gap) / 2.0;

        DrawBitmapToRect(ctx, tl, new Rect(0, 0, halfW, halfH));
        DrawBitmapToRect(ctx, tr, new Rect(halfW + Gap, 0, halfW, halfH));
        DrawBitmapToRect(ctx, bl, new Rect(0, halfH + Gap, halfW, halfH));
        DrawBitmapToRect(ctx, br, new Rect(halfW + Gap, halfH + Gap, halfW, halfH));
    }

    /// <summary>
    /// Рисует Bitmap в указанный прямоугольник с обрезкой (UniformToFill).
    /// Центрирует изображение и обрезает выступающие части.
    /// </summary>
    private static void DrawBitmapToRect(DrawingContext ctx, Bitmap bmp, Rect destRect)
    {
        // Рассчитываем source rect для UniformToFill (центрированная обрезка)
        var srcW = bmp.PixelSize.Width;
        var srcH = bmp.PixelSize.Height;
        var destAspect = destRect.Width / destRect.Height;
        var srcAspect = (double)srcW / srcH;

        Rect sourceRect;
        if (srcAspect > destAspect)
        {
            // Изображение шире — обрезаем по бокам
            var cropW = srcH * destAspect;
            var offsetX = (srcW - cropW) / 2.0;
            sourceRect = new Rect(offsetX, 0, cropW, srcH);
        }
        else
        {
            // Изображение выше — обрезаем сверху/снизу
            var cropH = srcW / destAspect;
            var offsetY = (srcH - cropH) / 2.0;
            sourceRect = new Rect(0, offsetY, srcW, cropH);
        }

        // Публичный API DrawingContext.DrawImage
        ctx.DrawImage(bmp, sourceRect, destRect);
    }

    /// <summary>
    /// Генерирует уникальное имя файла из отсортированных ID треков.
    /// Сортировка гарантирует одинаковое имя при одинаковом наборе треков
    /// вне зависимости от порядка выбора.
    /// </summary>
    private static string GenerateFileName(IReadOnlyList<string> trackIds)
    {
        var sorted = trackIds.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var combined = string.Join("|", sorted);

        // Простой хеш (достаточно для имени файла, не криптография)
        var hash = combined.GetHashCode(StringComparison.Ordinal);
        return $"mosaic_{hash:X8}.png";
    }
}