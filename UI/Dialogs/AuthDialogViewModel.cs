using System.IO.Compression;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using Avalonia.Threading;
using LMP.Core.Audio.Http;
using LMP.UI.Features.Shell;
using ReactiveUI;


namespace LMP.UI.Dialogs;

/// <summary>
/// Модель представления для диалога авторизации через расширение браузера.
/// </summary>
public sealed partial class AuthDialogViewModel : ViewModelBase
{
    private readonly CookieAuthService _auth;
    private readonly YoutubeUserDataService _userData;
    private readonly LocalAuthServer _localServer;

    [Reactive] public partial string CookiesText { get; set; } = string.Empty;
    [Reactive] public partial bool IsAuthenticating { get; private set; }
    [Reactive] public partial string StatusText { get; private set; } = string.Empty;
    [Reactive] public partial bool IsError { get; private set; }
    [Reactive] public partial int AttemptCount { get; private set; }

    [Reactive] public partial bool IsExtensionDownloading { get; private set; }
    [Reactive] public partial bool IsExtensionReady { get; private set; }
    [Reactive] public partial bool IsGuideExpanded { get; set; } = true;
    [Reactive] public partial string ExtensionFolderPath { get; private set; } = string.Empty;

    [Reactive] public partial bool IsPathCopied { get; private set; }
    [Reactive] public partial int SelectedBrowserTabIndex { get; set; }
    [Reactive] public partial string InstalledExtensionVersion { get; private set; } = "—";

    /// <summary>
    /// Возвращает текстовое представление установленной версии расширения на основе текущей локализации.
    /// </summary>
    public string ExtensionVersionText
    {
        get
        {
            var format = SL["Auth_Extension_Version_Format"];
            if (string.IsNullOrEmpty(format) || !format.Contains("{0}"))
            {
                format = "Extension: v{0}";
            }
            return string.Format(format, InstalledExtensionVersion);
        }
    }

    public bool IsFirefoxWarningVisible => IsGuideExpanded && SelectedBrowserTabIndex == 2;
    public bool IsWarningVisible => !IsGuideExpanded || IsFirefoxWarningVisible;

    /// <summary>
    /// Возвращает значение, указывающее, выполняется ли в данный момент сетевая операция.
    /// </summary>
    public bool IsSpinnerActive => IsAuthenticating || IsExtensionDownloading;

    public Action<bool>? OnResult { get; set; }

    public ReactiveCommand<Unit, Unit> AuthenticateCommand { get; }
    public ReactiveCommand<Unit, Unit> DownloadExtensionCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyPathCommand { get; }
    public ReactiveCommand<string, Unit> CopyLinkCommand { get; }
    public ReactiveCommand<Unit, bool> ToggleGuideCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="AuthDialogViewModel"/>.
    /// </summary>
    public AuthDialogViewModel(
        CookieAuthService auth,
        YoutubeUserDataService userData,
        LocalAuthServer localServer)
    {
        _auth = auth;
        _userData = userData;
        _localServer = localServer;

        AuthenticateCommand = CreateCommand(ReactiveCommand.CreateFromTask(AuthenticateAsync));
        DownloadExtensionCommand = CreateCommand(ReactiveCommand.CreateFromTask(DownloadExtensionAsync));
        CopyPathCommand = CreateCommand(ReactiveCommand.CreateFromTask(CopyPathToClipboardAsync));
        CopyLinkCommand = CreateCommand(ReactiveCommand.CreateFromTask<string>(CopyLinkAsync));
        ToggleGuideCommand = CreateCommand(ReactiveCommand.Create(() => IsGuideExpanded = !IsGuideExpanded));
        CloseCommand = CreateCommand(ReactiveCommand.Create(() => OnResult?.Invoke(false)));

        StatusText = SL["Dialog_Login_WaitingStatus"] ?? "Ожидаем запрос от расширения или введите куки вручную...";

        this.WhenAnyValue(x => x.IsAuthenticating, x => x.IsExtensionDownloading)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsSpinnerActive)));

        this.WhenAnyValue(x => x.SelectedBrowserTabIndex, x => x.IsGuideExpanded)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(IsFirefoxWarningVisible));
                this.RaisePropertyChanged(nameof(IsWarningVisible));
            });

        this.WhenAnyValue(x => x.InstalledExtensionVersion)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ExtensionVersionText)));

        _ = StartListeningAsync(_cts.Token);
        _ = CheckExtensionVersionAsync(_cts.Token);
    }

    /// <summary>
    /// Динамически вычисляет и извлекает текущий синглтон MainWindowViewModel для блокировок TitleBar.
    /// </summary>
    private static MainWindowViewModel? GetMainWindowViewModel()
    {
        try
        {
            return Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<MainWindowViewModel>(AppEntry.Services);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Выполняет автоматическую проверку соответствия локальной и удаленной версий расширения.
    /// </summary>
    private async Task CheckExtensionVersionAsync(CancellationToken ct)
    {
        var localVersionStr = GetLocalExtensionVersion();
        if (string.IsNullOrEmpty(localVersionStr))
        {
            InstalledExtensionVersion = "—";
            IsExtensionReady = false;
            IsGuideExpanded = true;
            return;
        }

        var extractedFolder = Path.Combine(G.Folder.Extension, "LMP-Auth-main");
        ExtensionFolderPath = Directory.Exists(extractedFolder) ? extractedFolder : G.Folder.Extension;
        InstalledExtensionVersion = localVersionStr;
        IsExtensionReady = true;
        IsGuideExpanded = false;

        try
        {
            var remoteManifestUrl = GetRemoteManifestUrl();
            using var response = await SharedHttpClient.Instance.GetAsync(
                remoteManifestUrl,
                HttpCompletionOption.ResponseContentRead,
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var remoteVerProp))
            {
                var remoteVersionStr = remoteVerProp.GetString();
                if (!string.IsNullOrEmpty(remoteVersionStr) &&
                    Version.TryParse(localVersionStr, out var localVer) &&
                    Version.TryParse(remoteVersionStr, out var remoteVer))
                {
                    if (localVer < remoteVer)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            IsExtensionReady = false;
                            IsGuideExpanded = true;
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Auth] Automatic extension version check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Возвращает версию расширения, обнаруженного в локальном каталоге приложения.
    /// </summary>
    private static string? GetLocalExtensionVersion()
    {
        try
        {
            var extractedFolder = Path.Combine(G.Folder.Extension, "LMP-Auth-main");
            var folder = Directory.Exists(extractedFolder) ? extractedFolder : G.Folder.Extension;
            var manifestPath = Path.Combine(folder, "manifest.json");

            if (!File.Exists(manifestPath)) return null;

            var json = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var versionProp))
            {
                return versionProp.GetString();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Auth] Failed to read local extension version: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Формирует адрес для получения манифеста из удаленного репозитория.
    /// </summary>
    private static string GetRemoteManifestUrl()
    {
        var url = G.AuthExtensionDownloadUrl;
        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var uri = new Uri(url);
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2)
                {
                    var owner = segments[0];
                    var repo = segments[1];
                    var branch = "main";

                    int headsIdx = url.IndexOf("heads/", StringComparison.OrdinalIgnoreCase);
                    if (headsIdx >= 0)
                    {
                        var remaining = url.Substring(headsIdx + 6);
                        int zipIdx = remaining.IndexOf(".zip", StringComparison.OrdinalIgnoreCase);
                        if (zipIdx >= 0)
                        {
                            branch = remaining.Substring(0, zipIdx);
                        }
                    }

                    return $"https://raw.githubusercontent.com/{owner}/{repo}/refs/heads/{branch}/manifest.json";
                }
            }
            catch { /* fallback */ }
        }

        return "https://raw.githubusercontent.com/Scream034/LMP-Auth/refs/heads/main/manifest.json";
    }

    private async Task StartListeningAsync(CancellationToken ct)
    {
        try
        {
            var cookies = await _localServer.WaitForCookiesAsync(ct);
            if (!string.IsNullOrEmpty(cookies) && !ct.IsCancellationRequested)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CookiesText = cookies;
                    AuthenticateCommand.Execute(Unit.Default);
                });
            }
        }
        catch (OperationCanceledException) { /* ignore */ }
    }

    private async Task AuthenticateAsync()
    {
        if (string.IsNullOrWhiteSpace(CookiesText))
        {
            SetStatus(SL["Dialog_Login_WaitingStatus"], isError: true);
            return;
        }

        IsAuthenticating = true;
        AttemptCount++;
        SetStatus($"{SL["Splash_ConnectingYouTube"]} ({AttemptCount})", isError: false);

        var mainWindow = GetMainWindowViewModel();
        mainWindow?.LockNavigation(SL["Splash_ConnectingYouTube"] ?? "Подключение к YouTube...");

        try
        {
            _auth.SaveCookies(CookiesText);

            if (!_auth.IsAuthenticated)
            {
                SetStatus(SL["Auth_LoginError_SAPISID"], isError: true);
                return;
            }

            SetStatus(SL["Nav_PleaseWait"], isError: false);

            var (isValid, error, isNetworkError) = await _auth.ValidateSessionAsync();
            if (!isValid)
            {
                if (isNetworkError)
                {
                    SetStatus($"{SL["Search_NetworkError"]} ({error})", isError: true);
                }
                else
                {
                    SetStatus(SL["Auth_SessionExpired_Message"], isError: true);
                    _auth.Logout();
                }
                return;
            }

            var accounts = await _userData.GetAvailableAccountsAsync();
            string finalName, finalEmail, finalAvatar, finalGaiaId;

            if (accounts.Count > 1)
            {
                IsAuthenticating = false;
                mainWindow?.UnlockNavigation();

                var dialogService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                                     .GetRequiredService<DialogService>(AppEntry.Services);

                var selectedAccount = await dialogService.ShowAccountSelectionDialogAsync(accounts);

                if (selectedAccount == null)
                {
                    SetStatus(SL["Common_Cancel"], isError: true);
                    _auth.Logout();
                    return;
                }

                _auth.SetAuthUser(selectedAccount.AuthUser);
                IsAuthenticating = true;
                mainWindow?.LockNavigation(SL["Splash_ConnectingYouTube"] ?? "Подключение к YouTube...");

                // БЕРЕМ ДАННЫЕ ИЗ ВЫБРАННОГО АККАУНТА, а не запрашиваем заново
                finalName = selectedAccount.Name;
                finalEmail = selectedAccount.Email;
                finalAvatar = selectedAccount.AvatarUrl;
                finalGaiaId = selectedAccount.GaiaId;
            }
            else
            {
                var singleAccount = accounts.FirstOrDefault();
                _auth.SetAuthUser(singleAccount?.AuthUser ?? AuthState.DefaultAuthUser);

                // Если аккаунт один, пробуем взять данные сразу из него
                if (singleAccount != null && !string.IsNullOrEmpty(singleAccount.Name))
                {
                    finalName = singleAccount.Name;
                    finalEmail = singleAccount.Email;
                    finalAvatar = singleAccount.AvatarUrl;
                    finalGaiaId = singleAccount.GaiaId;
                }
                else
                {
                    // Иначе делаем запасной сетевой запрос
                    var (Name, Email, AvatarUrl, GaiaId) = await _userData.GetAccountInfoAsync();
                    finalName = Name;
                    finalEmail = Email;
                    finalAvatar = AvatarUrl;
                    finalGaiaId = GaiaId;
                }
            }

            if (string.IsNullOrEmpty(finalName) || (finalName.Equals("User", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(finalAvatar)))
            {
                SetStatus(SL["Auth_ProfileLoadError_Message"], isError: true);
                _auth.Logout();
                return;
            }

            _auth.UpdateUserProfile(finalName, finalEmail, finalAvatar, finalGaiaId);

            SetStatus($"{SL["Dialog_Success"]}! {finalName}", isError: false);
            await Task.Delay(1000);

            OnResult?.Invoke(true);
        }
        catch (Exception ex)
        {
            SetStatus(SL["Dialog_Error_Title"] + ": " + ex.Message, isError: true);
        }
        finally
        {
            IsAuthenticating = false;
            mainWindow?.UnlockNavigation();
        }
    }

    private async Task DownloadExtensionAsync()
    {
        try
        {
            IsExtensionDownloading = true;
            SetStatus(SL["Splash_Initializing"], isError: false);

            SafeDeleteDirectory(G.Folder.Extension);
            Directory.CreateDirectory(G.Folder.Extension);

            using var response = await SharedHttpClient.Instance.GetAsync(
                G.AuthExtensionDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                _cts.Token).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;

            await using (var downloadStream = await response.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false))
            await using (var fs = new FileStream(G.FilePath.TempAuthExtensionZipFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buffer = new byte[8192];
                long totalBytesRead = 0;
                int bytesRead;

                while ((bytesRead = await downloadStream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead), _cts.Token).ConfigureAwait(false);
                    totalBytesRead += bytesRead;

                    if (contentLength.HasValue && contentLength.Value > 0)
                    {
                        var progressPercent = Math.Clamp((double)totalBytesRead / contentLength.Value * 100, 0, 100);
                        var totalMb = (double)totalBytesRead / (1024 * 1024);
                        var contentMb = (double)contentLength.Value / (1024 * 1024);
                        var progressText = string.Format(SL["Extension_Install_Downloading_Progress"], progressPercent, totalMb, contentMb);
                        Dispatcher.UIThread.Post(() => SetStatus(progressText, isError: false));
                    }
                    else
                    {
                        var totalMb = (double)totalBytesRead / (1024 * 1024);
                        var progressText = string.Format(SL["Extension_Install_Downloading_Indeterminate"], totalMb);
                        Dispatcher.UIThread.Post(() => SetStatus(progressText, isError: false));
                    }
                }
            }

            Dispatcher.UIThread.Post(() => SetStatus(SL["Splash_PreparingImages"], isError: false));

            ZipFile.ExtractToDirectory(G.FilePath.TempAuthExtensionZipFile, G.Folder.Extension);

            var extractedFolder = Path.Combine(G.Folder.Extension, "LMP-Auth-main");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ExtensionFolderPath = Directory.Exists(extractedFolder) ? extractedFolder : G.Folder.Extension;
                InstalledExtensionVersion = GetLocalExtensionVersion() ?? "—";
                IsExtensionReady = true;
                IsGuideExpanded = true;
                SetStatus(SL["Dialog_Login_WaitingStatus"], isError: false);
            });

            await CopyPathToClipboardAsync();
        }
        catch (Exception)
        {
            SetStatus(SL["Extension_Install_DownloadError"], isError: true);
        }
        finally
        {
            IsExtensionDownloading = false;
            try { if (File.Exists(G.FilePath.TempAuthExtensionZipFile)) File.Delete(G.FilePath.TempAuthExtensionZipFile); } catch { /* ignore */ }
        }
    }

    private async Task CopyPathToClipboardAsync()
    {
        if (string.IsNullOrEmpty(ExtensionFolderPath)) return;
        await Clipboard.SetTextAsync(ExtensionFolderPath);
        IsPathCopied = true;
        CopyHintService.Instance.Show(SL["Extension_Path_Copied_Toast"], CopyHintKind.Success);
    }

    private async Task CopyLinkAsync(string url)
    {
        await Clipboard.SetTextAsync(url);
        CopyHintService.Instance.Show(SL["Extension_Link_Copied_Toast"], CopyHintKind.Success);
    }

    private void SetStatus(string text, bool isError)
    {
        StatusText = text;
        IsError = isError;
    }

    private static void SafeDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, true); }
        catch (IOException)
        {
            try
            {
                var tempPath = path + "_deleted_" + Path.GetRandomFileName();
                Directory.Move(path, tempPath);
                _ = Task.Run(() => { try { Directory.Delete(tempPath, true); } catch { } });
            }
            catch { }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
