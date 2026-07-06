using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace LMP.Core.Youtube.Bridge.PoToken;

internal static class WaaClient
{
    private const string CreateUrl = "https://jnn-pa.googleapis.com/$rpc/google.internal.waa.v1.Waa/Create";
    private const string GenerateItUrl = "https://jnn-pa.googleapis.com/$rpc/google.internal.waa.v1.Waa/GenerateIT";
    private const string ApiKey = "AIzaSyDyT5W0Jh49F30Pqqtyfdf7pDLFKLJoAnw";
    private const string RequestKey = "O43z0dpjhgX20SCx4KAo";

    public static async Task<BotGuardChallenge?> FetchChallengeAsync(
        HttpClient http, string? cachedInterpreterHash = null, CancellationToken ct = default)
    {
        var payload = cachedInterpreterHash is not null
            ? (object)new object[] { RequestKey, cachedInterpreterHash }
            : new object[] { RequestKey };

        using var req = BuildRequest(CreateUrl, payload);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseChallenge(json);
    }

    public static async Task<IntegrityTokenData?> GenerateIntegrityTokenAsync(
    HttpClient http, string botguardResponse, CancellationToken ct = default)
    {
        var payload = new object[] { RequestKey, botguardResponse };
        using var req = BuildRequest(GenerateItUrl, payload);

        HttpResponseMessage resp;
        string json;
        try
        {
            resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"[WaaClient] GenerateIT HTTP failed: {ex.Message}");
            return null;
        }

        if (!resp.IsSuccessStatusCode)
        {
            Log.Error($"[WaaClient] GenerateIT HTTP {(int)resp.StatusCode}: {Truncate(json, 200)}");
            return null;
        }

        Log.Debug($"[WaaClient] GenerateIT raw response ({json.Length}ch): {Truncate(json, 120)}");

        return ParseIntegrityToken(json);
    }

    private static BotGuardChallenge? ParseChallenge(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 1) return null;

            // Дескрэмблинг
            // Порт из BgUtils: challengeFetcher.ts → descramble()
            // Формат ответа: [null, "<base64_scrambled>"]
            // Дескрэмблинг: Base64 → каждый байт += 97 → UTF-8 → JSON
            JsonElement arr;
            if (root.GetArrayLength() >= 2
                && root[0].ValueKind == JsonValueKind.Null
                && root[1].ValueKind == JsonValueKind.String)
            {
                var scrambled = root[1].GetString();
                if (string.IsNullOrEmpty(scrambled)) return null;

                var bytes = Convert.FromBase64String(scrambled);
                for (int i = 0; i < bytes.Length; i++)
                    bytes[i] = (byte)((bytes[i] + 97) % 256);

                var descrambled = System.Text.Encoding.UTF8.GetString(bytes);
                using var inner = JsonDocument.Parse(descrambled);
                arr = inner.RootElement.Clone();
            }
            else
            {
                arr = root;
            }

            if (arr.ValueKind != JsonValueKind.Array) return null;
            int len = arr.GetArrayLength();

            // Строгий позиционный парсинг JSPB
            // Структура из BgUtils challengeFetcher.ts parseChallengeData():
            //   [0] messageId
            //   [1] wrappedScript  — Array, ищем первую строку = inline JS
            //   [2] wrappedUrl     — Array, ищем первую строку = URL bg.js
            //   [3] interpreterHash
            //   [4] program        — байт-код задания (строка)
            //   [5] globalName     — имя конструктора VM в globalScope

            string? inlineScript = null;
            string? scriptUrl = null;
            string? interpreterHash = null;
            string? program = null;
            string? globalName = null;

            // [1] wrappedScript → inline JS
            if (len > 1 && arr[1].ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr[1].EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var val = item.GetString();
                        if (!string.IsNullOrEmpty(val))
                        {
                            inlineScript = val;
                            break;
                        }
                    }
                }
            }

            // [2] wrappedUrl → URL bg.js
            if (len > 2 && arr[2].ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr[2].EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var val = item.GetString();
                        if (!string.IsNullOrEmpty(val))
                        {
                            scriptUrl = val.StartsWith("//") ? "https:" + val : val;
                            break;
                        }
                    }
                }
            }

            // [3] interpreterHash
            if (len > 3 && arr[3].ValueKind == JsonValueKind.String)
                interpreterHash = arr[3].GetString();

            // [4] program (bytecode)
            if (len > 4 && arr[4].ValueKind == JsonValueKind.String)
                program = arr[4].GetString();

            // [5] globalName
            if (len > 5 && arr[5].ValueKind == JsonValueKind.String)
                globalName = arr[5].GetString();

            if (string.IsNullOrEmpty(program) || string.IsNullOrEmpty(globalName))
            {
                Log.Error($"[WaaClient] Parse failed. " +
                          $"Len={len}, Program={program?.Length ?? 0}ch, " +
                          $"GlobalName='{globalName}', " +
                          $"Hash={Truncate(interpreterHash)}, " +
                          $"InlineScript={inlineScript?.Length ?? 0}ch, " +
                          $"ScriptUrl={Truncate(scriptUrl)}");

                // Debug: распечатаем массив для диагностики
                for (int i = 0; i < Math.Min(len, 8); i++)
                    Log.Debug($"[WaaClient] arr[{i}] = {arr[i].ValueKind}: {Truncate(arr[i].ToString(), 60)}");

                return null;
            }

            var scriptType = inlineScript != null
                ? $"INLINE({inlineScript.Length / 1024}KB)"
                : scriptUrl != null ? $"URL({Truncate(scriptUrl)})" : "NONE";

            Log.Info($"[WaaClient] Challenge OK. VM='{globalName}', " +
                     $"Program={program.Length}ch, Script={scriptType}, " +
                     $"Hash={Truncate(interpreterHash)}");

            return new BotGuardChallenge
            {
                ScriptUrl = scriptUrl ?? "",
                InterpreterHash = interpreterHash ?? "",
                Program = program,
                GlobalName = globalName,
                InlineScript = inlineScript
            };
        }
        catch (Exception ex)
        {
            Log.Error($"[WaaClient] Parse error: {ex.Message}");
            return null;
        }
    }

    private static IntegrityTokenData? ParseIntegrityToken(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
            {
                Log.Error($"[WaaClient] GenerateIT: expected Array, got {root.ValueKind}. " +
                          $"Raw: {Truncate(rawJson, 200)}");
                return null;
            }

            // Разворачиваем вложенный массив если нужно: [[...]] → [...]
            JsonElement data = root;
            if (root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array)
            {
                Log.Debug("[WaaClient] GenerateIT: unwrapping nested array");
                data = root[0];
            }

            int len = data.GetArrayLength();
            if (len < 1)
            {
                Log.Error($"[WaaClient] GenerateIT: empty array");
                return null;
            }

            // Структура (из LuanRT/BgUtils webPoClient.ts):
            //   [0] integrityToken         — может быть null
            //   [1] estimatedTtlSecs       — число
            //   [2] mintRefreshThreshold   — число или null
            //   [3] websafeFallbackToken   — строка, используется когда [0] == null
            string? integrityToken = len > 0 && data[0].ValueKind == JsonValueKind.String
                ? data[0].GetString()
                : null;

            int ttl = len > 1 && data[1].ValueKind == JsonValueKind.Number
                ? data[1].GetInt32()
                : 43200;

            int threshold = len > 2 && data[2].ValueKind == JsonValueKind.Number
                ? data[2].GetInt32()
                : 0;

            string? websafeFallbackToken = len > 3 && data[3].ValueKind == JsonValueKind.String
                ? data[3].GetString()
                : null;

            // Используем websafeFallbackToken если основной токен отсутствует
            var effectiveToken = integrityToken ?? websafeFallbackToken;

            if (string.IsNullOrEmpty(effectiveToken))
            {
                Log.Error($"[WaaClient] GenerateIT: both integrityToken and websafeFallbackToken are null. " +
                          $"Raw: {Truncate(rawJson, 200)}");
                return null;
            }

            if (integrityToken is null)
            {
                Log.Debug($"[WaaClient] GenerateIT: using websafeFallbackToken " +
                          $"({Truncate(effectiveToken, 20)}...)");
            }

            Log.Debug($"[WaaClient] GenerateIT parsed: token={Truncate(effectiveToken, 20)}, " +
                      $"ttl={ttl}s, threshold={threshold}, " +
                      $"isFallback={integrityToken is null}");

            return new IntegrityTokenData
            {
                IntegrityToken = effectiveToken,
                EstimatedTtlSecs = ttl,
                MintRefreshThreshold = threshold,
                IsFallbackToken = integrityToken is null
            };
        }
        catch (Exception ex)
        {
            Log.Error($"[WaaClient] GenerateIT parse exception: {ex.Message}. " +
                      $"Raw: {Truncate(rawJson, 200)}");
            return null;
        }
    }

    [UnconditionalSuppressMessage(
    "Trimming",
    "IL2026:RequiresUnreferencedCode",
    Justification = "WaaClient serializes object[] payloads for PoToken Google API requests. " +
                    "Payload contains only primitive types (string, object[]) — " +
                    "no custom types requiring reflection preservation.")]
    private static HttpRequestMessage BuildRequest(string url, object payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json+protobuf")
        };
        req.Headers.TryAddWithoutValidation("x-goog-api-key", ApiKey);
        req.Headers.TryAddWithoutValidation("x-user-agent", "grpc-web-javascript/0.1");
        return req;
    }

    private static string Truncate(string? s, int len = 20) =>
        s is null ? "null" : s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");
}