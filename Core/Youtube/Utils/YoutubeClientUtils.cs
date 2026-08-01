using LMP.Core.Audio.Http;
using LMP.Core.Helpers.Extensions;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace LMP.Core.Youtube.Utils;

public static class YoutubeClientUtils
{
	public static YoutubeClientProfile CurrentProfile { get; set; } = YoutubeClientProfile.AndroidVR;

	private const string DefaultVisitorData = "CgtsZG1ySnZiQUkSbyiMjuGSBg%3D%3D";
	private static string _visitorData = DefaultVisitorData;
	private static Task<string>? _fetchTask;
	private static CookieAuthService? _authService;
	private static readonly Lock _visitorDataLock = new();

	/// <summary>
	/// Инициализирует статический провайдер кук авторизации для VisitorData.
	/// </summary>
	public static void Initialize(CookieAuthService authService)
	{
		_authService = authService;
	}

	/// <summary>
	/// Единый, глобально синхронизированный токен VisitorData для всех клиентов YouTube.
	/// Предотвращает дублирующие сетевые запросы к sw.js_data на горячих путях воспроизведения.
	/// </summary>
	public static string VisitorData
	{
		get { lock (_visitorDataLock) return _visitorData; }
		set
		{
			if (string.IsNullOrWhiteSpace(value)) return;
			lock (_visitorDataLock) _visitorData = value;
		}
	}

	/// <summary>
	/// Гарантирует наличие свежего VisitorData.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <paramref name="ct"/> используется для отмены <b>ожидания</b> результата,
	/// но не отменяет сам сетевой запрос — fetch живёт независимо от вызывающей операции.
	/// Это предотвращает ситуацию когда отмена одного трека портит глобальный кэш VisitorData.
	/// </para>
	/// </remarks>
	/// <param name="forceRefresh">Принудительное обновление даже при наличии актуального значения.</param>
	/// <param name="ct">Токен отмены ожидания.</param>
	public static Task<string> EnsureVisitorDataAsync(bool forceRefresh = false, CancellationToken ct = default)
	{
		lock (_visitorDataLock)
		{
			if (!forceRefresh && !string.Equals(_visitorData, DefaultVisitorData, StringComparison.Ordinal))
				return Task.FromResult(_visitorData);

			// Переиспользуем активный незавершённый таск — deduplication параллельных вызовов
			if (!forceRefresh && _fetchTask is { IsCompleted: false } activeTask)
				return ct.CanBeCanceled ? activeTask.WaitAsync(ct) : activeTask;

			// Завершённый (в т.ч. faulted/cancelled) таск заменяем новым
			_fetchTask = FetchVisitorDataInternalAsync(ct);
			return ct.CanBeCanceled ? _fetchTask.WaitAsync(ct) : _fetchTask;
		}
	}

	/// <summary>
	/// Централизованный метод выполнения сетевых запросов к sw.js_data.
	/// Возвращает VisitorData и новые заголовки кук для сессии.
	/// </summary>
	public static async Task<(string? VisitorData, IEnumerable<string>? SetCookieHeaders, bool IsSuccess)> FetchSwDataAsync(
		string? cookiesHeader, CancellationToken ct)
	{
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/sw.js_data")
			{
				Version = System.Net.HttpVersion.Version11 // Исключаем медленные UDP-согласования HTTP/3 при старте
			};
			request.Headers.UserAgent.ParseAdd(UaWeb);
			request.Headers.Add("Referer", "https://www.youtube.com/");

			if (!string.IsNullOrEmpty(cookiesHeader))
			{
				request.Headers.Add("Cookie", cookiesHeader);
			}

			using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(TimeSpan.FromSeconds(10));

			using var response = await SharedHttpClient.Instance.SendAsync(
					request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
			{
				return (null, null, false);
			}

			var jsonStr = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
			if (jsonStr.StartsWith(")]}'"))
				jsonStr = jsonStr[4..];

			var json = Json.Parse(jsonStr);
			var value = json[0][2][0][0][13].GetStringOrNull();

			response.Headers.TryGetValues("Set-Cookie", out var setCookies);

			return (value, setCookies, true);
		}
		catch (Exception ex)
		{
			Log.Warn($"[YoutubeClientUtils] Failed to fetch sw.js_data: {ex.Message}");
			return (null, null, false);
		}
	}

	private static async Task<string> FetchVisitorDataInternalAsync(CancellationToken ct)
	{
		var cookies = _authService?.GetCookieHeader();
		var (visitorData, setCookies, isSuccess) = await FetchSwDataAsync(cookies, ct).ConfigureAwait(false);

		if (isSuccess && !string.IsNullOrWhiteSpace(visitorData))
		{
			if (setCookies != null && _authService != null)
			{
				_authService.UpdateCookies(setCookies);
			}

			lock (_visitorDataLock)
			{
				_visitorData = visitorData;
			}
			return visitorData;
		}

		return _visitorData;
	}

	// User-Agents
	public const string UaVr = "com.google.android.apps.youtube.vr.oculus/1.61.48 (Linux; U; Android 12; en_US; Quest 3; Build/SQ3A.220605.009.A1; Cronet/132.0.6808.3)";
	public const string UaTv = "Mozilla/5.0 (PlayStation; PlayStation 4/12.02) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.4 Safari/605.1.15";
	public const string UaWeb = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
	public const string UaWebRemix = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
	public const string UaAndroidMusic = "com.google.android.apps.youtube.music/7.27.52 (Linux; U; Android 14; en_US; Pixel 8 Pro; Build/AP2A.240805.005)";
	public const string UaIos = "com.google.ios.youtube/19.29.1 (iPhone16,2; U; CPU iOS 17_5_1 like Mac OS X;)";

	public static string UserAgent => CurrentProfile switch
	{
		YoutubeClientProfile.AndroidVR => UaVr,
		YoutubeClientProfile.TV => UaTv,
		YoutubeClientProfile.Web => UaWeb,
		YoutubeClientProfile.WebRemix => UaWebRemix,
		_ => UaWebRemix
	};

	public static bool RequiresAuth => CurrentProfile is YoutubeClientProfile.Web or YoutubeClientProfile.WebRemix;

	/// <summary>
	/// Клиенты для получения stream URLs.
	/// ANDROID_VR первый — отдаёт прямые URL без sig cipher / n-token / PoToken.
	/// Этот список НЕ зависит от <see cref="CurrentProfile"/> — профиль пользователя
	/// влияет только на metadata/search/browse запросы через <see cref="GetPlayerResponseAsync"/>.
	/// </summary>
	public static readonly string[] StreamFallbackClientsDefault =
	[
		"ANDROID_VR",
	];

	/// <summary>
	/// Stream fallback для авторизованных пользователей.
	/// WEB_REMIX включён потому что с авторизацией pot не нужен.
	/// </summary>
	public static readonly string[] StreamFallbackClientsAuth =
	[
		"ANDROID_VR",
		"WEB_REMIX",
	];

	/// <summary>
	/// Возвращает список клиентов для получения stream URLs.
	/// Порядок фиксирован и не зависит от профиля пользователя —
	/// stream-запросы требуют клиентов, отдающих прямые URL без шифрования.
	/// </summary>
	public static string[] GetStreamFallbackClients(bool isAuthenticated)
	{
		return isAuthenticated ? StreamFallbackClientsAuth : StreamFallbackClientsDefault;
	}

	/// <summary>
	/// Обратная совместимость — без контекста авторизации.
	/// </summary>
	public static string[] StreamFallbackClients => StreamFallbackClientsDefault;

	/// <summary>
	/// Клиенты для получения HLS.
	/// </summary>
	public static readonly string[] HlsFallbackClients =
	[
		"IOS",
		"ANDROID_VR",
		"WEB_REMIX"
	];

	/// <summary>
	/// Возвращает строковый идентификатор клиента для YouTube API.
	/// Enum.ToString() даёт "AndroidVR", а API ожидает "ANDROID_VR" — прямое
	/// преобразование через switch исключает ошибки при добавлении новых клиентов.
	/// </summary>
	public static string GetClientApiName(YoutubeClientProfile profile) => profile switch
	{
		YoutubeClientProfile.AndroidVR => "ANDROID_VR",
		YoutubeClientProfile.AndroidMusic => "ANDROID_MUSIC",
		YoutubeClientProfile.WebRemix => "WEB_REMIX",
		YoutubeClientProfile.Web => "WEB",
		YoutubeClientProfile.TV => "TVHTML5_SIMPLY_EMBEDDED_PLAYER",
		YoutubeClientProfile.Ios => "IOS",
		_ => "WEB_REMIX",
	};

	// Заменить GeneratePlayerContext:
	/// <summary>
	/// Генерирует контекст плеера для текущего профиля клиента.
	/// </summary>
	public static string GeneratePlayerContext(string videoId, string? visitorData)
	{
		return GeneratePlayerContextForClient(
			GetClientApiName(CurrentProfile),
			videoId,
			visitorData);
	}

	/// <summary>
	/// Генерирует контекст для конкретного клиента.
	/// </summary>
	public static string GeneratePlayerContextForClient(string clientName, string videoId, string? visitorData, string? signatureTimestamp = null)
	{
		var hl = YoutubeHttpHandler.GetHl();
		var gl = YoutubeHttpHandler.GetGl();

		var vidJson = Json.Serialize(videoId);
		var vdJson = Json.Serialize(visitorData);
		var hlJson = Json.Serialize(hl);
		var glJson = Json.Serialize(gl);

		var playbackContextJson = signatureTimestamp != null
			? $@", ""playbackContext"": {{ ""contentPlaybackContext"": {{ ""signatureTimestamp"": {Json.Serialize(signatureTimestamp)} }} }}"
			: "";

		return clientName switch
		{
			"WEB_REMIX" => $$"""
			{
				"videoId": {{vidJson}},
				"contentCheckOk": true,
				"racyCheckOk": true,
				"context": {
					"client": {
						"clientName": "WEB_REMIX",
						"clientVersion": "{{YoutubeHttpHandler.MusicClientVersion}}",
						"visitorData": {{vdJson}},
						"hl": {{hlJson}},
						"gl": {{glJson}},
						"utcOffsetMinutes": 0
					}
				}{{playbackContextJson}}
			}
			""",

			"ANDROID_VR" => $$"""
			{
				"videoId": {{vidJson}},
				"contentCheckOk": true,
				"racyCheckOk": true,
				"context": {
					"client": {
						"clientName": "ANDROID_VR",
						"clientVersion": "1.61.48",
						"deviceMake": "Oculus",
						"deviceModel": "Quest 3",
						"osName": "Android",
						"osVersion": "12",
						"androidSdkVersion": "32",
						"platform": "MOBILE",
						"visitorData": {{vdJson}},
						"hl": "en",
						"gl": "US",
						"utcOffsetMinutes": 0
					}
				}
			}
			""",

			"ANDROID_MUSIC" => $$"""
			{
				"videoId": {{vidJson}},
				"contentCheckOk": true,
				"racyCheckOk": true,
				"context": {
					"client": {
						"clientName": "ANDROID_MUSIC",
						"clientVersion": "7.27.52",
						"androidSdkVersion": "34",
						"osName": "Android",
						"osVersion": "14",
						"platform": "MOBILE",
						"visitorData": {{vdJson}},
						"hl": {{hlJson}},
						"gl": {{glJson}},
						"utcOffsetMinutes": 0
					}
				}
			}
			""",

			"WEB" => $$"""
			{
				"videoId": {{vidJson}},
				"contentCheckOk": true,
				"racyCheckOk": true,
				"context": {
					"client": {
						"clientName": "WEB",
						"clientVersion": "{{YoutubeHttpHandler.WebClientVersion}}",
						"visitorData": {{vdJson}},
						"hl": {{hlJson}},
						"gl": {{glJson}},
						"utcOffsetMinutes": 0
					}
				}
			}
			""",

			"IOS" => $$"""
			{
				"videoId": {{vidJson}},
				"contentCheckOk": true,
				"racyCheckOk": true,
				"context": {
					"client": {
						"clientName": "IOS",
						"clientVersion": "19.29.1",
						"deviceMake": "Apple",
						"deviceModel": "iPhone16,2",
						"osName": "iOS",
						"osVersion": "17.5.1",
						"platform": "MOBILE",
						"visitorData": {{vdJson}},
						"hl": {{hlJson}},
						"gl": {{glJson}},
						"utcOffsetMinutes": 0
					}
				}
			}
			""",

			"TVHTML5_SIMPLY_EMBEDDED_PLAYER" => $$"""
			{
				"videoId": {{vidJson}},
				"context": {
					"client": {
						"clientName": "TVHTML5_SIMPLY_EMBEDDED_PLAYER",
						"clientVersion": "2.0",
						"visitorData": {{vdJson}},
						"hl": {{hlJson}},
						"gl": {{glJson}},
						"utcOffsetMinutes": 0,
						"platform": "TV"
					},
					"thirdParty": {
						"embedUrl": "https://www.youtube.com"
					}
				}{{playbackContextJson}}
			}
			""",

			_ => GeneratePlayerContextForClient("WEB_REMIX", videoId, visitorData)
		};
	}

	/// <summary>
	/// Возвращает User-Agent для конкретного клиента.
	/// </summary>
	public static string GetUserAgentForClient(string clientName) => clientName switch
	{
		"WEB_REMIX" => UaWebRemix,
		"ANDROID_VR" => UaVr,
		"ANDROID_MUSIC" => UaAndroidMusic,
		"WEB" => UaWeb,
		"IOS" => UaIos,
		"TVHTML5_SIMPLY_EMBEDDED_PLAYER" => UaTv,
		_ => UaWebRemix
	};

	/// <summary>
	/// Высокопроизводительный парсер длительности медиафайлов YouTube.
	/// Исключает аллокации строк и оптимизирован под частые форматы времени.
	/// </summary>
	public static class DurationParser
	{
		private static readonly string[] DurationFormats =
			[@"m\:ss", @"mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss"];

		/// <summary>
		/// Выполняет быстрое Span-based чтение и парсинг формата времени.
		/// </summary>
		/// <param name="text">Строка времени из API InnerTube.</param>
		/// <returns>Разобранный интервал времени, либо null.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public static TimeSpan? Parse(string? text)
		{
			if (string.IsNullOrWhiteSpace(text)) return null;

			var span = text.AsSpan().Trim();
			int colonCount = 0;
			int firstColon = -1, secondColon = -1;

			for (int i = 0; i < span.Length; i++)
			{
				if (span[i] == ':')
				{
					colonCount++;
					if (colonCount == 1) firstColon = i;
					else if (colonCount == 2) secondColon = i;
				}
			}

			if (colonCount == 1 && firstColon > 0)
			{
				if (int.TryParse(span[..firstColon], out var m) &&
				int.TryParse(span[(firstColon + 1)..], out var s))
				{
					return new TimeSpan(0, m, s);
				}
			}
			else if (colonCount == 2 && firstColon > 0 && secondColon > firstColon)
			{
				if (int.TryParse(span[..firstColon], out var h) &&
				int.TryParse(span[(firstColon + 1)..secondColon], out var m) &&
				int.TryParse(span[(secondColon + 1)..], out var s))
				{
					return new TimeSpan(h, m, s);
				}
			}

			if (TimeSpan.TryParseExact(text, DurationFormats, CultureInfo.InvariantCulture, out var ts))
			{
				return ts;
			}

			return null;
		}
	}
}