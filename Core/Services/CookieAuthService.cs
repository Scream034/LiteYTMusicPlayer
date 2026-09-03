using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LMP.Core.Youtube;
using LMP.Core.Helpers.Extensions;
using System.Text.RegularExpressions;
using LMP.Core.Youtube.Utils;

namespace LMP.Core.Services;

public partial class CookieAuthService
{
    // Два независимых семафора: auth_data.json и cookies.txt могут сохраняться параллельно
    private readonly SemaphoreSlim _authSaveSemaphore = new(1, 1);
    private readonly SemaphoreSlim _cookieSaveSemaphore = new(1, 1);
    private CancellationTokenSource? _validateCts;
    private readonly Lock _validateLock = new();

    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _cookieMap = new(32, StringComparer.Ordinal);
    private string _cachedHeaderString = "";
    private readonly string _authDataPath = G.FilePath.AuthData;

    private string? _profileLoadError;
    public bool HasProfileLoadError => _profileLoadError != null;

    public AuthState State { get; private set; } = new();

    public bool IsAuthenticated
    {
        get
        {
            lock (_lock) return _cookieMap.ContainsKey("SAPISID");
        }
    }

    public event Action? OnAuthStateChanged;

    /// <summary>
    /// Инициализирует сервис без файлового I/O.
    /// Файловые операции выполняются в <see cref="InitializeAsync"/>.
    /// </summary>
    public CookieAuthService() { }

    /// <summary>
    /// Выполняет файловый I/O старта: загрузку кук, профиля и одноразовую миграцию.
    /// Должен вызываться из <c>App.axaml.cs</c> до <c>LibraryService.InitializeAsync</c>.
    /// </summary>
    public async Task InitializeAsync()
    {
        await LoadCookiesAsync().ConfigureAwait(false);
        await LoadAuthDataAsync().ConfigureAwait(false);
        UpdateStateAuthStatus();
        await MigrateLegacyAuthFileAsync().ConfigureAwait(false);
    }

    private async Task LoadCookiesAsync()
    {
        try
        {
            var (raw, recovered) = await AtomicFile.ReadTextWithFallbackAsync(G.FilePath.Cookie).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (recovered)
                    Log.Warn("[Auth] Recovered auth cookies from backup (.bak)");

                ParseAndSetCookies(raw);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Failed to load cookies: {ex.Message}");
        }
    }

    private async Task LoadAuthDataAsync()
    {
        try
        {
            var (json, recovered) = await AtomicFile.ReadTextWithFallbackAsync(_authDataPath).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return;

            if (recovered)
                Log.Warn("[Auth] Recovered auth profile from backup (.bak)");

            var loadedState = JsonSerializer.Deserialize(json, AppJsonContext.DefaultCompact.AuthState);
            if (loadedState != null)
            {
                State = loadedState;
                Log.Info($"[Auth] Profile restored from cache: {State.UserName}");
            }
        }
        catch (Exception ex)
        {
            _profileLoadError = ex.Message;
            Log.Error($"[Auth] Failed to load auth data: {ex.Message}");
        }
    }

    private async Task MigrateLegacyAuthFileAsync()
    {
        try
        {
            var legacyPath = Path.Combine(AppContext.BaseDirectory, "auth.json");
            if (!File.Exists(legacyPath)) return;

            if (!File.Exists(_authDataPath) || new FileInfo(_authDataPath).Length == 0)
            {
                // File.Copy: BCL не предоставляет async-аналога; для <10 KB файла Task.Run избыточен
                File.Copy(legacyPath, _authDataPath, overwrite: true);
                Log.Info($"[Auth] Migrated auth.json to {_authDataPath}");
                await LoadAuthDataAsync().ConfigureAwait(false);
            }
            File.Delete(legacyPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"[Auth] Failed to migrate legacy auth.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Обновляет профиль пользователя и публикует событие изменения авторизации
    /// только при фактическом изменении данных.
    /// </summary>
    /// <param name="name">Имя пользователя.</param>
    /// <param name="email">Email пользователя.</param>
    /// <param name="avatarUrl">URL аватара пользователя.</param>
    /// <param name="activeGaiaId">Gaia ID активного аккаунта.</param>
    public void UpdateUserProfile(string name, string email, string avatarUrl, string activeGaiaId)
    {
        bool currentAuth = IsAuthenticated;

        bool previousAuth = State.IsAuthenticated;
        string previousUserName = previousAuth ? State.UserName : string.Empty;
        string previousEmail = State.UserEmail;
        string previousAvatarUrl = State.AvatarUrl;
        string previousGaiaId = State.ActiveGaiaId;

        State.UserName = name;
        State.UserEmail = email;
        State.AvatarUrl = avatarUrl;
        State.LastUpdated = DateTime.UtcNow;
        State.IsAuthenticated = currentAuth;
        State.ActiveGaiaId = activeGaiaId;

        SaveAuthData();

        bool changed =
            previousAuth != currentAuth ||
            !string.Equals(previousUserName, name, StringComparison.Ordinal) ||
            !string.Equals(previousEmail, email, StringComparison.Ordinal) ||
            !string.Equals(previousAvatarUrl, avatarUrl, StringComparison.Ordinal) ||
            !string.Equals(previousGaiaId, activeGaiaId, StringComparison.Ordinal);

        if (changed)
            OnAuthStateChanged?.Invoke();
    }

    public void UpdateCachedAccounts(List<YoutubeAccountItem> accounts)
    {
        lock (_lock)
        {
            State.CachedAccounts = accounts;
        }
        SaveAuthData();
    }

    private void UpdateStateAuthStatus()
    {
        bool currentAuth = IsAuthenticated;
        if (State.IsAuthenticated != currentAuth)
        {
            State.IsAuthenticated = currentAuth;
            if (!HasProfileLoadError) SaveAuthData();
        }
    }

    /// <summary>
    /// Универсальный извлекатель текста, поддерживающий как плоский формат simpleText,
    /// так и сложную структуру с массивом runs.
    /// </summary>
    private static string? GetStringValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined) return null;

        // 1. Проверяем наличие прямого simpleText
        if (element.TryGetProperty("simpleText", out var simpleTextProp))
        {
            return simpleTextProp.GetString();
        }

        // 2. Проверяем наличие массива форматирования runs
        if (element.TryGetProperty("runs", out var runsProp) && runsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var run in runsProp.EnumerateArray())
            {
                if (run.TryGetProperty("text", out var textProp))
                {
                    return textProp.GetString();
                }
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    /// <summary>
    /// Проверяет валидность текущей сессии на серверах YouTube.
    /// Парсит единый ответ switcher для одновременного извлечения текущего профиля и списка всех каналов мульти-авторизации.
    /// Автоматически завершает сессию (Logout) при получении подтвержденного отказа от Google во избежание бесконечных 401 ошибок.
    /// </summary>
    /// <param name="ct">Токен отмены асинхронной операции.</param>
    /// <returns>Кортеж, содержащий признак валидности сессии, строку ошибки и признак сетевой ошибки в случае неудачи.</returns>
    public async Task<(bool IsValid, string? Error, bool IsNetworkError)> ValidateSessionAsync(CancellationToken ct = default)
    {
        if (!IsAuthenticated)
            return (false, "Not authenticated", false);

        try
        {
            var cookiesHeader = GetCookieHeader();
            var sapisid = GetCookieValue("SAPISID");

            var mainOrigin = "https://www.youtube.com";
            var authHeader = YoutubeHttpHandler.GetAuthHeader(sapisid, mainOrigin);

            // Используем стандартный GET эндпоинт получения свичера
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/getAccountSwitcherEndpoint");
            request.Headers.UserAgent.ParseAdd(YoutubeClientUtils.UaWeb);
            request.Headers.Add("Cookie", cookiesHeader);
            request.Headers.Add("Origin", mainOrigin);
            request.Headers.Add("X-Origin", mainOrigin);
            request.Headers.Add("Referer", mainOrigin + "/");

            if (!string.IsNullOrEmpty(authHeader))
                request.Headers.Add("Authorization", authHeader);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            using var response = await Audio.Http.SharedHttpClient.Instance.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);

            // Если пришел явный 401 — сессия гарантированно мертва. Принудительно выкидываем из аккаунта
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Log.Warn("[Auth] 401 Unauthorized received during session validation. Session expired. Automatically logging out...");
                Logout();
                return (false, "Unauthorized", false);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                    UpdateCookies(setCookies);

                var rawJson = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

                // Защита от XSSI: Отсекаем префикс )]}' перед парсингом JSON
                int jsonStart = rawJson.IndexOf('{');
                if (jsonStart >= 0)
                {
                    rawJson = rawJson.Substring(jsonStart);
                }

                using var jsonDoc = JsonDocument.Parse(rawJson);

                var results = new List<YoutubeAccountItem>();
                bool hasActiveAccount = false;
                string? activeName = null, activeEmail = null, activeAvatar = null, activeGaiaId = null;
                string? activeHandle = null;

                // getAccountSwitcherEndpoint оборачивает весь InnerTube ответ в свойство "data"
                var dataElement = jsonDoc.RootElement.GetPropertyOrNull("data") ?? jsonDoc.RootElement;

                // Унифицированный парсинг структуры меню switcher
                var menu = dataElement.GetPropertyOrNull("actions")
                    ?.EnumerateArrayOrNull()?.FirstOrDefault()
                    .GetPropertyOrNull("getMultiPageMenuAction")?.GetPropertyOrNull("menu")?.GetPropertyOrNull("multiPageMenuRenderer")
                    ?? dataElement.GetPropertyOrNull("actions")
                    ?.EnumerateArrayOrNull()?.FirstOrDefault()
                    .GetPropertyOrNull("openPopupAction")?.GetPropertyOrNull("popup")?.GetPropertyOrNull("multiPageMenuRenderer");

                // Локальная функция для парсинга отдельного врапмера аккаунта во избежание дублирования
                void ProcessWrap(JsonElement wrap, string sectionEmail)
                {
                    var accountItem = wrap.GetPropertyOrNull("accountItem");
                    if (accountItem == null) return;

                    var nameEl = accountItem.Value.GetPropertyOrNull("accountName");
                    var name = nameEl.HasValue ? GetStringValue(nameEl.Value) ?? "Unknown" : "Unknown";

                    var avatar = accountItem.Value.GetPropertyOrNull("accountPhoto")?.GetPropertyOrNull("thumbnails")?.EnumerateArrayOrNull()?.LastOrDefault().GetPropertyOrNull("url")?.GetStringOrNull() ?? "";
                    bool isSelected = accountItem.Value.GetPropertyOrNull("isSelected")?.GetBoolean() ?? false;

                    string gaiaId = "";

                    var handleEl = accountItem.Value.GetPropertyOrNull("channelHandle");
                    string handle = handleEl.HasValue ? GetStringValue(handleEl.Value) ?? "" : "";

                    string parsedAuthUser = AuthState.DefaultAuthUser;

                    var tokens = accountItem.Value.GetPropertyOrNull("serviceEndpoint")?.GetPropertyOrNull("selectActiveIdentityEndpoint")?.GetPropertyOrNull("supportedTokens")?.EnumerateArrayOrNull();
                    if (tokens != null)
                    {
                        foreach (var token in tokens.Value)
                        {
                            var stateToken = token.GetPropertyOrNull("accountStateToken");
                            if (stateToken != null) gaiaId = stateToken.Value.GetPropertyOrNull("obfuscatedGaiaId")?.GetStringOrNull() ?? gaiaId;

                            var signinToken = token.GetPropertyOrNull("accountSigninToken");
                            if (signinToken != null)
                            {
                                var signinUrl = signinToken.Value.GetPropertyOrNull("signinUrl")?.GetStringOrNull();
                                if (!string.IsNullOrEmpty(signinUrl))
                                {
                                    int targetIdx = signinUrl.IndexOf("authuser=");
                                    if (targetIdx >= 0)
                                    {
                                        var remaining = signinUrl.Substring(targetIdx + 9);
                                        int ampIdx = remaining.IndexOf('&');
                                        parsedAuthUser = ampIdx >= 0 ? remaining.Substring(0, ampIdx) : remaining;
                                    }
                                }
                            }
                        }
                    }

                    results.Add(new YoutubeAccountItem
                    {
                        Index = results.Count + 1,
                        Name = name,
                        Email = sectionEmail,
                        AvatarUrl = avatar,
                        GaiaId = gaiaId,
                        Handle = handle,
                        AuthUser = parsedAuthUser,
                        IsSelected = isSelected
                    });

                    if (isSelected)
                    {
                        hasActiveAccount = true;
                        activeName = name;
                        activeEmail = sectionEmail;
                        activeAvatar = avatar;
                        activeHandle = handle;
                        activeGaiaId = gaiaId;
                    }
                }

                var sections = menu?.GetPropertyOrNull("sections")?.EnumerateArrayOrNull();
                if (sections != null)
                {
                    foreach (var section in sections.Value)
                    {
                        var accountSection = section.GetPropertyOrNull("accountSectionListRenderer");
                        if (accountSection == null) continue;

                        string sectionEmail = "";

                        var header = accountSection.Value.GetPropertyOrNull("header");
                        if (header != null)
                        {
                            var googleHeader = header.Value.GetPropertyOrNull("googleAccountHeaderRenderer");
                            if (googleHeader != null)
                            {
                                var emailEl = googleHeader.Value.GetPropertyOrNull("email");
                                sectionEmail = emailEl.HasValue ? GetStringValue(emailEl.Value) ?? "" : "";
                            }
                            else
                            {
                                var itemHeader = header.Value.GetPropertyOrNull("accountItemSectionHeaderRenderer");
                                if (itemHeader != null)
                                {
                                    var titleEl = itemHeader.Value.GetPropertyOrNull("title");
                                    sectionEmail = titleEl.HasValue ? GetStringValue(titleEl.Value) ?? "" : "";
                                }
                            }
                        }

                        var contents = accountSection.Value.GetPropertyOrNull("contents")?.EnumerateArrayOrNull();
                        if (contents == null) continue;

                        foreach (var itemWrap in contents.Value)
                        {
                            var accItemSection = itemWrap.GetPropertyOrNull("accountItemSectionRenderer");
                            var subItems = accItemSection?.GetPropertyOrNull("contents")?.EnumerateArrayOrNull();

                            if (subItems != null)
                            {
                                foreach (var subItemWrap in subItems.Value)
                                {
                                    ProcessWrap(subItemWrap, sectionEmail);
                                }
                            }
                            else
                            {
                                ProcessWrap(itemWrap, sectionEmail);
                            }
                        }
                    }
                }

                if (hasActiveAccount)
                {
                    UpdateCachedAccounts(results);
                    UpdateUserProfile(activeName ?? "User", activeEmail ?? "", activeAvatar ?? "", activeGaiaId ?? "");
                    return (true, null, false);
                }

                // Google успешно вернул 200 OK, но в нем нет активного аккаунта (меню гостя).
                // Это значит, что сессия аннулирована сервером. Принудительно выходим!
                Log.Warn("[Auth] Validation returned guest configuration. Session expired. Automatically logging out...");
                Logout();
                return (false, "Session expired or returned guest menu configuration.", false);
            }

            return (false, $"Validation returned status {response.StatusCode}", true);
        }
        catch (OperationCanceledException)
        {
            // Внешняя отмена — пробрасываем, чтобы вызывающий код отличил
            // user cancel от внутреннего таймаута
            if (ct.IsCancellationRequested)
                throw;

            // Внутренний CancelAfter(8s) сработал — это сетевой таймаут, не user cancel
            Log.Warn("[Auth] Session validation timed out (8s internal deadline)");
            return (false, "Connection timed out", true);
        }
        catch (Exception ex)
        {
            // Важно: При сетевых таймаутах мы НЕ вызываем Logout(),
            // так как куки могут быть валидными, просто временно отсутствует соединение.
            Log.Error($"[Auth] Session validation exception: {ex.Message}");
            return (false, ex.Message, true);
        }
    }

    /// <summary>
    /// Устанавливает индекс активной мульти-сессии Google.
    /// Событие изменения публикуется только при фактическом изменении значения.
    /// </summary>
    /// <param name="authUser">Индекс активного authuser.</param>
    public void SetAuthUser(string authUser)
    {
        bool changed;
        lock (_lock)
        {
            changed = !string.Equals(State.AuthUser, authUser, StringComparison.Ordinal);
            if (!changed)
                return;

            State.AuthUser = authUser;
        }

        SaveAuthData();
        OnAuthStateChanged?.Invoke();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetCookieHeader()
    {
        lock (_lock) return _cachedHeaderString;
    }

    public string? GetCookieValue(string key)
    {
        lock (_lock)
            return _cookieMap.TryGetValue(key, out var val) ? val : null;
    }

    /// <summary>
    /// Сохраняет куки, полученные от браузерного расширения, и обновляет состояние авторизации.
    /// <para>
    /// При свежем логине (unauth → auth) <b>не стреляет</b> <see cref="OnAuthStateChanged"/>,
    /// так как <see cref="AuthState.ActiveGaiaId"/> ещё не установлен и <c>CurrentOwnerId</c>
    /// библиотеки вернёт <c>"guest"</c>. Вместо этого запускает фоновую валидацию сессии,
    /// по завершении которой <see cref="UpdateUserProfile"/> установит корректный профиль
    /// и выстрелит <see cref="OnAuthStateChanged"/> с правильным контекстом владельца.
    /// </para>
    /// </summary>
    public void SaveCookies(string cookies)
    {
        if (string.IsNullOrWhiteSpace(cookies)) return;

        var span = cookies.AsSpan().Trim().Trim('"');
        var clean = span.ToString().Replace("\r", "").Replace("\n", "");
        clean = FindCookieTextRegex().Replace(clean, "");

        if (!clean.Contains("SAPISID", StringComparison.Ordinal))
        {
            Log.Warn("[Auth] Attempt to save cookies without SAPISID. Ignoring.");
            return;
        }

        bool wasAuthenticated;
        lock (_lock)
        {
            wasAuthenticated = _cookieMap.ContainsKey("SAPISID");
        }

        ParseAndSetCookies(clean);
        SaveCookiesToFile();
        UpdateStateAuthStatus();

        bool isFreshLogin = !wasAuthenticated && IsAuthenticated;

        if (isFreshLogin)
        {
            // Свежий логин: ActiveGaiaId ещё не установлен → CurrentOwnerId = "guest".
            // НЕ стреляем OnAuthStateChanged — иначе LibraryService гидрирует для "guest".
            // Запускаем фоновую валидацию → UpdateUserProfile → OnAuthStateChanged с корректным GaiaId.
            Log.Info("[Auth] Fresh authentication detected. Deferring auth state notification until profile is validated.");
            _ = ValidateAndNotifyAsync();
        }
        else
        {
            // Обновление кук существующей сессии — профиль уже установлен
            OnAuthStateChanged?.Invoke();
        }

        Log.Info($"[Auth] Cookies saved. Fresh login: {isFreshLogin}. Total keys: {_cookieMap.Count}");
    }

    /// <summary>
    /// Выполняет отложенную валидацию сессии после свежего логина.
    /// Предыдущий незавершённый вызов отменяется через <see cref="_validateCts"/>.
    /// При сетевой ошибке отображает пользователю информативное уведомление.
    /// </summary>
    private async Task ValidateAndNotifyAsync()
    {
        CancellationTokenSource cts;
        lock (_validateLock)
        {
            _validateCts?.Cancel();
            _validateCts?.Dispose();
            cts = _validateCts = new CancellationTokenSource();
        }

        try
        {
            var (isValid, error, isNetworkError) = await ValidateSessionAsync(cts.Token).ConfigureAwait(false);

            if (cts.IsCancellationRequested) return;

            if (isNetworkError)
            {
                Log.Warn($"[Auth] Post-login validation failed due to network: {error}. Firing fallback auth state change.");
                _profileLoadError = error;
                OnAuthStateChanged?.Invoke();
            }
            // Успех → UpdateUserProfile уже вызвал OnAuthStateChanged
            // 401   → Logout() уже вызвал OnAuthStateChanged
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                Log.Warn($"[Auth] Deferred session validation failed: {ex.Message}. Firing fallback auth state change.");
                _profileLoadError = ex.Message;
                OnAuthStateChanged?.Invoke();
            }
        }
        finally
        {
            lock (_validateLock)
            {
                if (_validateCts == cts)
                {
                    cts.Dispose();
                    _validateCts = null;
                }
            }
        }
    }

    public bool UpdateCookies(IEnumerable<string> setCookieHeaders)
    {
        bool criticalCookieUpdated = false;
        lock (_lock)
        {
            foreach (var header in setCookieHeaders)
            {
                var headerSpan = header.AsSpan();
                int semicolonIdx = headerSpan.IndexOf(';');
                var firstPart = semicolonIdx >= 0 ? headerSpan[..semicolonIdx] : headerSpan;

                int equalIdx = firstPart.IndexOf('=');
                if (equalIdx <= 0) continue;

                var key = firstPart[..equalIdx].Trim().ToString();
                var value = firstPart[(equalIdx + 1)..].Trim().ToString();

                if (string.IsNullOrEmpty(value) ||
                    value.Equals("deleted", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("expired", StringComparison.OrdinalIgnoreCase))
                {
                    if (_cookieMap.Remove(key))
                    {
                        Log.Debug($"[Auth] Cookie '{key}' deleted by server request.");
                    }
                    continue;
                }

                if (!_cookieMap.TryGetValue(key, out var existingVal) || existingVal != value)
                {
                    _cookieMap[key] = value;
                    criticalCookieUpdated = true;
                }
            }

            if (criticalCookieUpdated || _cookieMap.Count > 0)
                RebuildHeaderString();
        }

        if (criticalCookieUpdated)
        {
            SaveCookiesToFile();
            UpdateStateAuthStatus();
        }

        return criticalCookieUpdated;
    }

    public void Logout()
    {
        // Отменяем до OnAuthStateChanged — гарантируем что validate не выстрелит после выхода
        lock (_validateLock)
        {
            _validateCts?.Cancel();
            _validateCts?.Dispose();
            _validateCts = null;
        }

        lock (_lock)
        {
            _cookieMap.Clear();
            _cachedHeaderString = "";
        }

        if (File.Exists(G.FilePath.Cookie)) File.Delete(G.FilePath.Cookie);
        var cookieBak = string.Concat(G.FilePath.Cookie, ".bak");
        if (File.Exists(cookieBak)) File.Delete(cookieBak);

        State = new AuthState();
        if (File.Exists(_authDataPath)) File.Delete(_authDataPath);
        var authBak = string.Concat(_authDataPath, ".bak");
        if (File.Exists(authBak)) File.Delete(authBak);

        OnAuthStateChanged?.Invoke();
    }

    private void ParseAndSetCookies(string raw)
    {
        lock (_lock)
        {
            _cookieMap.Clear();
            var remaining = raw.AsSpan();

            while (remaining.Length > 0)
            {
                int semicolonIdx = remaining.IndexOf(';');
                ReadOnlySpan<char> part;

                if (semicolonIdx >= 0)
                {
                    part = remaining[..semicolonIdx];
                    remaining = remaining[(semicolonIdx + 1)..];
                }
                else
                {
                    part = remaining;
                    remaining = [];
                }

                int equalIdx = part.IndexOf('=');
                if (equalIdx <= 0) continue;

                var key = part[..equalIdx].Trim();
                var value = part[(equalIdx + 1)..].Trim();

                if (key.Length > 0)
                    _cookieMap[key.ToString()] = value.ToString();
            }

            RebuildHeaderString();
        }
    }

    private void RebuildHeaderString()
    {
        if (_cookieMap.Count == 0)
        {
            _cachedHeaderString = "";
            return;
        }

        int estimatedLen = _cookieMap.Count * 40;
        var sb = new StringBuilder(estimatedLen);

        foreach (var kvp in _cookieMap)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(kvp.Key);
            sb.Append('=');
            sb.Append(kvp.Value);
        }

        _cachedHeaderString = sb.ToString();
    }

    // --- Non-blocking Save ---

    /// <summary>
    /// Сериализует профиль синхронно (CPU-bound, ~0.1 мс),
    /// запись на диск — fire-and-forget через <see cref="_authSaveSemaphore"/>.
    /// </summary>
    public void SaveAuthData()
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(State, AppJsonContext.DefaultCompact.AuthState);
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Failed to serialize auth data: {ex.Message}");
            return;
        }
        _ = WriteAuthDataAsync(json);
    }

    private async Task WriteAuthDataAsync(string json)
    {
        await _authSaveSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await AtomicFile.WriteTextAsync(_authDataPath, json, createBackup: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Failed to save auth data: {ex.Message}");
        }
        finally
        {
            _authSaveSemaphore.Release();
        }
    }

    private void SaveCookiesToFile()
    {
        string? content;
        lock (_lock)
        {
            if (!_cookieMap.ContainsKey("SAPISID")) return;
            content = _cachedHeaderString;
        }
        _ = WriteCookiesToFileAsync(content);
    }

    private async Task WriteCookiesToFileAsync(string content)
    {
        await _cookieSaveSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await AtomicFile.WriteTextAsync(G.FilePath.Cookie, content, createBackup: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[Auth] Failed to save updated cookies: {ex.Message}");
        }
        finally
        {
            _cookieSaveSemaphore.Release();
        }
    }

    [GeneratedRegex(@"^Cookie:\s*", RegexOptions.IgnoreCase, "ru-RU")]
    private static partial Regex FindCookieTextRegex();
}