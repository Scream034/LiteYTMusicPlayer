using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ReactiveUI;


namespace LMP.UI.Dialogs;

/// <summary>
/// Режим выбора обложки плейлиста.
/// </summary>
public enum CoverMode
{
    /// <summary>Ручной ввод URL.</summary>
    Url,
    /// <summary>Выбор из обложек треков (мозаика).</summary>
    FromTracks,
    /// <summary>Выбор локального файла.</summary>
    File
}

/// <summary>
/// ViewModel редактора плейлиста. Используется как для создания, так и для редактирования.
/// Создаётся через фабричные методы <see cref="ForCreate"/> и <see cref="ForEdit"/>.
/// </summary>
public sealed partial class PlaylistEditorViewModel : ViewModelBase
{
    [Reactive] public partial string Name { get; set; }
    [Reactive] public partial string? ThumbnailUrl { get; set; }
    [Reactive] public partial string? CustomColor { get; set; }
    [Reactive] public partial string? Description { get; set; }

    /// <summary>Исходное описание (для определения изменения).</summary>
    private readonly string? _originalDescription;

    /// <summary>
    /// Оригинальный плейлист (для доступа к YoutubeId и SyncMode).
    /// null для режима создания.
    /// </summary>
    private readonly Playlist? _originalPlaylist;

    /// <summary>true если VM создана для редактирования (а не создания).</summary>
    private readonly bool _isForEdit;

    // ═══ ComputedColor ═══

    /// <summary>
    /// Автоматически вычисленный цвет из обложки (readonly, из БД).
    /// Показывается в UI как информационное поле.
    /// Обновляется при пересчёте через RecalculateColorCommand.
    /// </summary>
    [Reactive] public partial string? ComputedColor { get; private set; }

    /// <summary>Кисть превью вычисленного цвета.</summary>
    [Reactive] public partial IBrush ComputedColorPreviewBrush { get; private set; } = Brushes.Transparent;

    /// <summary>
    /// Идёт ли пересчёт цвета из обложки или загрузка обложки в YouTube.
    /// Используется для блокировки UI во время длительных операций.
    /// </summary>
    [Reactive] public partial bool IsRecalculatingColor { get; set; }

    /// <summary>Команда пересчёта доминантного цвета из текущей обложки.</summary>
    public ReactiveCommand<Unit, Unit> RecalculateColorCommand { get; }

    // ═══ System Playlist ═══

    /// <summary>
    /// true если редактируется системный плейлист (например «Понравившиеся»).
    /// Для системных плейлистов имя задаётся локализацией и недоступно для ручного ввода.
    /// </summary>
    public bool IsSystemPlaylist { get; }

    /// <summary>
    /// Разрешено ли редактировать имя плейлиста.
    /// false для системных плейлистов — имя управляется <see cref="LocalizationService"/>.
    /// </summary>
    public bool IsNameEditable { get; }

    // ═══ For Edit / Create Copy ═══

    /// <summary>
    /// true если VM создана для редактирования существующего плейлиста.
    /// Используется в UI для отображения кнопки «Создать копию».
    /// </summary>
    public bool IsForEdit { get; }

    /// <summary>
    /// Callback, вызываемый при нажатии кнопки «Создать копию».
    /// Устанавливается в <see cref="EditPlaylistDialogViewModel"/>.
    /// </summary>
    public Action? OnCreateCopy { get; set; }

    /// <summary>
    /// Создаёт локальную копию плейлиста с текущими данными из редактора.
    /// Копия всегда локальная (без привязки к YouTube).
    /// </summary>
    public ReactiveCommand<Unit, Unit> CreateCopyCommand { get; }

    // ═══ Cover Mode ═══

    /// <summary>Текущий режим выбора обложки: URL, из треков, или файл.</summary>
    [Reactive] public partial CoverMode SelectedCoverMode { get; set; } = CoverMode.Url;

    /// <summary>true если выбран режим ручного URL.</summary>
    [Reactive] public partial bool IsCoverModeUrl { get; set; } = true;

    /// <summary>true если выбран режим "Из треков".</summary>
    [Reactive] public partial bool IsCoverModeFromTracks { get; set; }

    /// <summary>true если выбран режим "Файл".</summary>
    [Reactive] public partial bool IsCoverModeFile { get; set; }

    /// <summary>
    /// ViewModel выбора обложки из треков. null если треки не предоставлены.
    /// </summary>
    [Reactive] public partial PlaylistCoverPickerViewModel? CoverPicker { get; set; }

    /// <summary>
    /// Показывать ли переключатель режима обложки.
    /// Всегда true — минимум URL + File.
    /// </summary>
    public bool ShowCoverModeSwitch { get; } = true;

    /// <summary>Показывать ли вкладку "Из треков".</summary>
    public bool HasTracksCoverOption { get; }

    /// <summary>Путь выбранного файла (для отображения в UI).</summary>
    [Reactive] public partial string? SelectedFilePath { get; set; }

    /// <summary>Команда выбора файла через системный диалог.</summary>
    public ReactiveCommand<Unit, Unit> SelectFileCommand { get; }

    // ═══ Upload Thumbnail to YouTube ═══

    /// <summary>
    /// Показывать ли кнопку загрузки обложки в YouTube.
    /// Видна только для TwoWaySync плейлистов с непустой обложкой.
    /// </summary>
    [Reactive] public partial bool ShowUploadThumbnailButton { get; private set; }

    /// <summary>Загрузить текущую обложку в YouTube.</summary>
    public ReactiveCommand<Unit, Unit> UploadThumbnailCommand { get; }

    // ═══ Sync ═══

    [Reactive] public partial bool ShowSyncSection { get; set; }
    [Reactive] public partial bool IsSyncedToCloud { get; set; }
    public bool IsAuthenticated { get; }
    public bool HasYoutubeBinding { get; }
    public bool OriginalSyncState { get; }

    // ═══ Validation ═══
    [Reactive] public partial string? ErrorMessage { get; set; }
    [Reactive] public partial bool HasErrors { get; private set; }
    public IObservable<bool> CanSave { get; }

    // ═══ Preview ═══

    /// <summary>
    /// URL или путь для превью обложки (HTTP URL или локальный путь).
    /// </summary>
    [Reactive] public partial string? ThumbnailPreviewUrl { get; private set; }

    /// <summary>Есть ли превью для отображения.</summary>
    [Reactive] public partial bool HasThumbnailPreview { get; private set; }

    /// <summary>Превью — это HTTP URL (для AsyncImageLoader).</summary>
    [Reactive] public partial bool IsPreviewHttp { get; private set; }

    /// <summary>Превью — это локальный файл (для LocalFileImageConverter).</summary>
    [Reactive] public partial bool IsPreviewLocal { get; private set; }

    /// <summary>
    /// Bitmap превью для локальных файлов (загружается напрямую).
    /// Для HTTP URL остаётся null — используется AsyncImageLoader.
    /// </summary>
    [Reactive] public partial Bitmap? LocalPreviewBitmap { get; private set; }

    [Reactive] public partial IBrush ColorPreviewBrush { get; private set; } = Brushes.Transparent;

    public PlaylistEditorViewModel(
        string name = "",
        string? thumbnailUrl = null,
        string? customColor = null,
        string? description = null,
        string? computedColor = null,
        bool showSync = false,
        bool isSynced = false,
        bool isAuthenticated = false,
        bool hasYoutubeBinding = false,
        IReadOnlyList<TrackInfo>? playlistTracks = null,
        Playlist? originalPlaylist = null,
        bool isForEdit = false,
        bool isSystemPlaylist = false)
    {
        Name = name;
        ThumbnailUrl = thumbnailUrl;
        CustomColor = customColor;
        Description = description;
        _originalDescription = description;
        _originalPlaylist = originalPlaylist;
        _isForEdit = isForEdit;
        IsForEdit = isForEdit;
        IsSystemPlaylist = isSystemPlaylist;
        IsNameEditable = !isSystemPlaylist;
        ComputedColor = computedColor;
        ShowSyncSection = showSync;
        IsSyncedToCloud = isSynced;
        OriginalSyncState = isSynced;
        IsAuthenticated = isAuthenticated;
        HasYoutubeBinding = hasYoutubeBinding;

        ComputedColorPreviewBrush = TryParseColor(computedColor);

        HasTracksCoverOption = playlistTracks != null && playlistTracks.Any(t => t.HasThumbnail);
        if (HasTracksCoverOption)
        {
            CoverPicker = new PlaylistCoverPickerViewModel(playlistTracks!);

            CoverPicker.WhenAnyValue(x => x.ResultPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(path =>
                {
                    ThumbnailUrl = path;
                    SelectedCoverMode = CoverMode.Url;
                })
                .DisposeWith(Disposables);
        }

        SelectFileCommand = CreateCommand(ReactiveCommand.CreateFromTask(SelectFileAsync));

        var canRecalculate = this.WhenAnyValue(
            x => x.ThumbnailUrl,
            x => x.IsRecalculatingColor,
            (url, isRecalc) => !string.IsNullOrWhiteSpace(url) && !isRecalc);

        RecalculateColorCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(RecalculateColorFromCoverAsync, canRecalculate));

        var canUpload = this.WhenAnyValue(
            x => x.ThumbnailUrl,
            x => x.IsRecalculatingColor,
            x => x.ShowUploadThumbnailButton,
            (url, busy, show) => !string.IsNullOrEmpty(url) && !busy && show);

        UploadThumbnailCommand = CreateCommand(
            ReactiveCommand.CreateFromTask(UploadThumbnailAsync, canUpload));

        // ═══ Команда создания копии ═══
        CreateCopyCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            OnCreateCopy?.Invoke();
        }));

        this.WhenAnyValue(x => x.SelectedCoverMode)
            .Subscribe(mode =>
            {
                IsCoverModeUrl = mode == CoverMode.Url;
                IsCoverModeFromTracks = mode == CoverMode.FromTracks;
                IsCoverModeFile = mode == CoverMode.File;
            })
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.Name, x => x.ThumbnailUrl, x => x.CustomColor)
            .Subscribe(_ => UpdateValidation())
            .DisposeWith(Disposables);

        CanSave = this.WhenAnyValue(x => x.HasErrors, errors => !errors);

        this.WhenAnyValue(x => x.ThumbnailUrl)
            .Throttle(TimeSpan.FromMilliseconds(400))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(UpdateThumbnailPreview)
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.ThumbnailUrl)
            .Subscribe(_ => UpdateUploadButtonVisibility())
            .DisposeWith(Disposables);

        this.WhenAnyValue(x => x.CustomColor)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(colorStr => ColorPreviewBrush = TryParseColor(colorStr))
            .DisposeWith(Disposables);
    }

    #region Upload Thumbnail to YouTube

    /// <summary>
    /// Обновляет видимость кнопки загрузки обложки в YouTube.
    /// Показываем только для TwoWaySync плейлистов с непустой обложкой.
    /// </summary>
    private void UpdateUploadButtonVisibility()
    {
        ShowUploadThumbnailButton =
            _isForEdit &&
            _originalPlaylist?.SyncMode == PlaylistSyncMode.TwoWaySync &&
            !string.IsNullOrEmpty(_originalPlaylist.YoutubeId) &&
            !string.IsNullOrEmpty(ThumbnailUrl);
    }

    /// <summary>
    /// Загружает текущую обложку в YouTube через Scotty Upload Protocol.
    /// Используется для ручной загрузки без синхронизации всего плейлиста.
    ///
    /// <para><b>Поддерживаемые источники:</b></para>
    /// <list type="bullet">
    ///   <item>HTTP/HTTPS URL — скачивается, затем загружается</item>
    ///   <item>file:// URI — читается как локальный файл</item>
    ///   <item>Абсолютный путь — читается напрямую</item>
    /// </list>
    /// </summary>
    private async Task UploadThumbnailAsync()
    {
        if (_originalPlaylist == null || string.IsNullOrEmpty(_originalPlaylist.YoutubeId))
            return;

        if (string.IsNullOrEmpty(ThumbnailUrl))
        {
            SetError(SL["EditPlaylist_NoThumbnail"] ?? "No thumbnail to upload");
            return;
        }

        IsRecalculatingColor = true;
        ClearError();

        try
        {
            var youtube = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Lazy<YoutubeProvider>>(AppEntry.Services);

            byte[] imageData;

            if (IsHttpUrl(ThumbnailUrl))
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(15);

                var response = await httpClient.GetAsync(ThumbnailUrl);
                response.EnsureSuccessStatusCode();
                imageData = await response.Content.ReadAsByteArrayAsync();
            }
            else if (ThumbnailUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(ThumbnailUrl);
                var localPath = uri.LocalPath;

                if (!File.Exists(localPath))
                {
                    SetError(SL["EditPlaylist_FileNotFound"] ?? "File not found");
                    return;
                }

                imageData = await File.ReadAllBytesAsync(localPath);
            }
            else if (Path.IsPathRooted(ThumbnailUrl) && File.Exists(ThumbnailUrl))
            {
                imageData = await File.ReadAllBytesAsync(ThumbnailUrl);
            }
            else
            {
                SetError(SL["EditPlaylist_InvalidThumbnailUrl"] ?? "Invalid thumbnail URL");
                return;
            }

            if (imageData.Length == 0)
            {
                SetError(SL["EditPlaylist_EmptyImage"] ?? "Image file is empty");
                return;
            }

            if (imageData.Length > 20 * 1024 * 1024)
            {
                SetError(SL["PlaylistSync_ThumbnailTooLarge"] ?? "Image too large (max 20MB)");
                return;
            }

            var success = await youtube.Value.UploadPlaylistThumbnailAsync(
                _originalPlaylist.YoutubeId, imageData);

            if (success)
            {
                Log.Info($"[PlaylistEditor] Thumbnail uploaded for {_originalPlaylist.YoutubeId}");
                ErrorMessage = "✓ " + (SL["PlaylistSync_ThumbnailUploaded"] ?? "Thumbnail uploaded to YouTube");
                HasErrors = false;
                _ = ClearSuccessMessageAsync();
            }
            else
            {
                SetError(SL["EditPlaylist_UploadFailed"] ?? "Failed to upload thumbnail");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistEditor] Thumbnail upload error: {ex.Message}");
            SetError(ex.Message);
        }
        finally
        {
            IsRecalculatingColor = false;
        }
    }

    /// <summary>
    /// Очищает сообщение об успехе через 3 секунды.
    /// </summary>
    private async Task ClearSuccessMessageAsync()
    {
        try
        {
            await Task.Delay(3000);
            if (ErrorMessage?.StartsWith("✓") == true)
                ErrorMessage = null;
        }
        catch { /* ignore */ }
    }

    #endregion

    #region Recalculate Color

    /// <summary>
    /// Пересчитывает доминантный цвет из текущей обложки.
    /// Результат записывается в ComputedColor (будет сохранён при Apply).
    /// </summary>
    private async Task RecalculateColorFromCoverAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ThumbnailUrl)) return;
        if (!IsValidUri(ThumbnailUrl)) return;

        IsRecalculatingColor = true;
        try
        {
            var dominantColorService = Microsoft.Extensions.DependencyInjection
                .ServiceProviderServiceExtensions
                .GetRequiredService<DominantColorService>(AppEntry.Services);

            var color = await dominantColorService.GetDominantColorAsync(ThumbnailUrl, ct);

            if (color.HasValue)
            {
                var hex = $"#{color.Value.R:X2}{color.Value.G:X2}{color.Value.B:X2}";
                ComputedColor = hex;
                ComputedColorPreviewBrush = new SolidColorBrush(color.Value);
                Log.Info($"[PlaylistEditor] Recalculated color: {hex}");
            }
            else
            {
                Log.Warn("[PlaylistEditor] Could not extract dominant color");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[PlaylistEditor] Color recalculation failed: {ex.Message}");
        }
        finally
        {
            IsRecalculatingColor = false;
        }
    }

    #endregion

    #region Thumbnail Preview

    /// <summary>Определяет, является ли URL HTTP/HTTPS ссылкой.</summary>
    private static bool IsHttpUrl(string url) =>
        url.StartsWith(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Преобразует file:// URI или абсолютный путь в локальный путь.
    /// </summary>
    private static string? ResolveLocalPath(string url)
    {
        if (url.StartsWith(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var fileUri))
                return fileUri.LocalPath;
            return null;
        }

        if (Path.IsPathRooted(url))
            return url;

        return null;
    }

    /// <summary>
    /// Обновляет превью обложки с автоматическим определением типа источника.
    /// HTTP/HTTPS/avares → AsyncImageLoader, локальный файл → Bitmap напрямую.
    /// </summary>
    private void UpdateThumbnailPreview(string? url)
    {
        var oldBitmap = LocalPreviewBitmap;

        if (string.IsNullOrWhiteSpace(url))
        {
            HasThumbnailPreview = false;
            IsPreviewHttp = false;
            IsPreviewLocal = false;
            ThumbnailPreviewUrl = null;
            LocalPreviewBitmap = null;
            oldBitmap?.Dispose();
            return;
        }

        if (IsHttpUrl(url))
        {
            HasThumbnailPreview = true;
            IsPreviewHttp = true;
            IsPreviewLocal = false;
            ThumbnailPreviewUrl = url;
            LocalPreviewBitmap = null;
            oldBitmap?.Dispose();
            return;
        }

        if (url.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
        {
            HasThumbnailPreview = true;
            IsPreviewHttp = true;
            IsPreviewLocal = false;
            ThumbnailPreviewUrl = url;
            LocalPreviewBitmap = null;
            oldBitmap?.Dispose();
            return;
        }

        var localPath = ResolveLocalPath(url);
        if (localPath != null && File.Exists(localPath))
        {
            try
            {
                using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bitmap = new Bitmap(stream);

                HasThumbnailPreview = true;
                IsPreviewHttp = false;
                IsPreviewLocal = true;
                ThumbnailPreviewUrl = null;
                LocalPreviewBitmap = bitmap;
                oldBitmap?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"[PlaylistEditor] Failed to load local preview '{localPath}': {ex.Message}");
                HasThumbnailPreview = false;
                IsPreviewHttp = false;
                IsPreviewLocal = false;
                ThumbnailPreviewUrl = null;
                LocalPreviewBitmap = null;
                oldBitmap?.Dispose();
            }
            return;
        }

        HasThumbnailPreview = false;
        IsPreviewHttp = false;
        IsPreviewLocal = false;
        ThumbnailPreviewUrl = null;
        LocalPreviewBitmap = null;
        oldBitmap?.Dispose();
    }

    #endregion

    #region File Selection

    /// <summary>
    /// Открывает системный диалог выбора файла изображения.
    /// </summary>
    private async Task SelectFileAsync(CancellationToken ct)
    {
        var topLevel = GetTopLevel();
        if (topLevel == null)
        {
            Log.Warn("[PlaylistEditor] Cannot open file picker: no TopLevel found");
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = SL["CoverPicker_SelectFile"] ?? "Select image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(SL["CoverPicker_ImageFiles"] ?? "Images")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"],
                    MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/bmp"]
                }
            ]
        });

        if (files.Count == 0) return;

        var filePath = files[0].Path.LocalPath;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        SelectedFilePath = filePath;
        ThumbnailUrl = filePath;
        UpdateThumbnailPreview(filePath);
    }

    private static TopLevel? GetTopLevel()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                if (window.IsActive) return window;
            }
            return desktop.MainWindow;
        }
        return null;
    }

    #endregion

    #region Validation

    private void UpdateValidation()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            SetError(SL["Error_EmptyName"] ?? "Name cannot be empty");
            return;
        }

        if (!string.IsNullOrWhiteSpace(ThumbnailUrl) && !IsValidUri(ThumbnailUrl))
        {
            SetError(SL["Error_InvalidUrl"] ?? "Invalid cover URL format");
            return;
        }

        if (!string.IsNullOrWhiteSpace(CustomColor) && !IsValidColor(CustomColor))
        {
            SetError(SL["Error_InvalidColor"] ?? "Invalid HEX color (example: #FF5555)");
            return;
        }

        ClearError();
    }

    private void SetError(string msg) { ErrorMessage = msg; HasErrors = true; }
    private void ClearError() { ErrorMessage = null; HasErrors = false; }

    #endregion

    #region Helpers

    /// <summary>
    /// Проверяет, является ли строка валидным источником изображения.
    ///
    /// <para><b>Допустимые форматы:</b></para>
    /// <list type="bullet">
    ///   <item><c>http://</c> / <c>https://</c> — URL из интернета</item>
    ///   <item><c>avares://</c> — встроенный ресурс Avalonia</item>
    ///   <item><c>file://</c> — явный file URI</item>
    ///   <item>Абсолютный путь файловой системы (C:\..., /home/...)</item>
    /// </list>
    ///
    /// <para><b>ВАЖНО:</b> <c>Uri.TryCreate</c> с <c>UriKind.Absolute</c> парсит
    /// hex-строки вида "F1A2B3..." как scheme "f" с хостом "1a2b3...:80",
    /// что приводит к HTTP-запросам на несуществующие хосты.
    /// Поэтому проверяем scheme по белому списку.</para>
    /// </summary>
    internal static bool IsValidUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        if (Path.IsPathRooted(url))
            return true;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Scheme switch
            {
                "http" or "https" => true,
                "avares" => true,
                "file" => true,
                _ => false
            };
        }

        return false;
    }

    private static bool IsValidColor(string? colorStr)
    {
        if (string.IsNullOrWhiteSpace(colorStr)) return false;
        try { Color.Parse(colorStr); return true; }
        catch { return false; }
    }

    private static IBrush TryParseColor(string? colorStr)
    {
        if (IsValidColor(colorStr))
            return new SolidColorBrush(Color.Parse(colorStr!));
        return Brushes.Transparent;
    }

    #endregion

    #region Cover Mode Commands

    public void SetCoverModeUrl() => SelectedCoverMode = CoverMode.Url;
    public void SetCoverModeFromTracks() => SelectedCoverMode = CoverMode.FromTracks;
    public void SetCoverModeFile() => SelectedCoverMode = CoverMode.File;

    #endregion

    #region Factory Methods

    /// <summary>
    /// Создаёт VM для создания нового плейлиста.
    /// </summary>
    public static PlaylistEditorViewModel ForCreate() =>
        new(name: "", thumbnailUrl: null, customColor: null, description: null,
            computedColor: null,
            showSync: false, isSynced: false,
            isAuthenticated: false, hasYoutubeBinding: false,
            playlistTracks: null,
            originalPlaylist: null,
            isForEdit: false,
            isSystemPlaylist: false);

    /// <summary>
    /// Создаёт VM для редактирования существующего плейлиста.
    /// </summary>
    /// <param name="playlist">Редактируемый плейлист из БД.</param>
    /// <param name="isAuthenticated">Авторизован ли пользователь в YouTube.</param>
    /// <param name="playlistTracks">
    /// Треки плейлиста для <see cref="PlaylistCoverPickerViewModel"/>.
    /// null — вкладка «Из треков» скрыта.
    /// </param>
    public static PlaylistEditorViewModel ForEdit(
        Playlist playlist,
        bool isAuthenticated,
        IReadOnlyList<TrackInfo>? playlistTracks = null)
    {
        var isSystem = LibraryService.IsSystemPlaylist(playlist.Id);

        return new(
            name: playlist.Name,
            thumbnailUrl: playlist.ThumbnailUrl,
            customColor: playlist.CustomColor,
            description: playlist.Description,
            computedColor: playlist.ComputedColor,
            // Sync-секция скрыта для системных плейлистов:
            // «Понравившиеся» управляется через like/unlike API, а не прямой привязкой к YouTube.
            showSync: !isSystem && (isAuthenticated || playlist.IsFromAccount),
            isSynced: playlist.IsFromAccount,
            isAuthenticated: isAuthenticated,
            hasYoutubeBinding: !string.IsNullOrEmpty(playlist.YoutubeId),
            playlistTracks: playlistTracks,
            originalPlaylist: playlist,
            isForEdit: true,
            isSystemPlaylist: isSystem);
    }

    #endregion

    #region Result

    /// <summary>
    /// Собирает результат редактирования.
    /// Description включается всегда — PlaylistEditService сам определит, изменилось ли.
    /// ComputedColor включается если был пересчитан.
    /// </summary>
    public EditPlaylistResult ToResult() => new()
    {
        Name = Name.Trim(),
        ThumbnailUrl = string.IsNullOrWhiteSpace(ThumbnailUrl) ? null : ThumbnailUrl.Trim(),
        CustomColor = string.IsNullOrWhiteSpace(CustomColor) ? null : CustomColor.Trim(),
        Description = Description?.Trim(),
        ComputedColor = ComputedColor
    };

    public bool SyncStateChanged => IsSyncedToCloud != OriginalSyncState;
    public bool DescriptionChanged => !string.Equals(Description?.Trim(), _originalDescription?.Trim(), StringComparison.Ordinal);

    #endregion

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            LocalPreviewBitmap?.Dispose();
            CoverPicker?.Dispose();
        }
        base.Dispose(disposing);
    }
}
