using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MealApp.Services;

public class AuthSession
{
    [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; set; }
    [JsonPropertyName("userId")] public string UserId { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
}

// Thin wrapper around Supabase Auth's REST API (GoTrue). Sessions are cached
// in localStorage (via LocalStorageService, which works the same in both the
// WASM and MAUI WebView hosts) so a user stays logged in across restarts.
public class AuthService
{
    private const string StorageKey = "authSession";
    private readonly HttpClient _http;
    private readonly LocalStorageService _storage;
    private readonly LocalizationService _loc;

    public AuthSession? CurrentSession { get; private set; }
    public bool IsLoggedIn => CurrentSession != null;
    public event Action? OnAuthChanged;

    public AuthService(IHttpClientFactory factory, LocalStorageService storage, LocalizationService loc)
    {
        _http = factory.CreateClient();
        _http.BaseAddress = new Uri(SupabaseConfig.Url.TrimEnd('/') + "/auth/v1/");
        _http.DefaultRequestHeaders.Add("apikey", SupabaseConfig.AnonKey);
        _storage = storage;
        _loc = loc;
    }

    public async Task RestoreSessionAsync()
    {
        var raw = await _storage.GetAsync(StorageKey);
        if (string.IsNullOrEmpty(raw)) return;
        try
        {
            var session = JsonSerializer.Deserialize<AuthSession>(raw);
            if (session == null) return;
            if (session.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                CurrentSession = session;
            }
            else
            {
                await RefreshAsync(session.RefreshToken);
            }
        }
        catch (JsonException) { /* ignore corrupt cache */ }
    }

    // Returns null on success, "CONFIRM_EMAIL" if a confirmation link was sent
    // instead of an immediate session, or a human-readable error otherwise.
    public async Task<string?> SignUpAsync(string email, string password)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("signup", new { email, password });
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return ExtractError(body);

            var root = JsonDocument.Parse(body).RootElement;
            if (root.TryGetProperty("access_token", out _))
            {
                await SetSessionFromJsonAsync(root);
                return null;
            }
            return "CONFIRM_EMAIL";
        }
        catch (Exception ex)
        {
            return _loc.T("error.networkFormat", ex.Message);
        }
    }

    public async Task<string?> LogInAsync(string email, string password)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("token?grant_type=password", new { email, password });
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return ExtractError(body);
            await SetSessionFromJsonAsync(JsonDocument.Parse(body).RootElement);
            return null;
        }
        catch (Exception ex)
        {
            return _loc.T("error.networkFormat", ex.Message);
        }
    }

    public async Task<string?> RequestPasswordResetAsync(string email)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(
                $"recover?redirect_to={Uri.EscapeDataString(SupabaseConfig.PasswordResetRedirectUrl)}",
                new { email });
            if (!resp.IsSuccessStatusCode) return ExtractError(await resp.Content.ReadAsStringAsync());
            return null;
        }
        catch (Exception ex)
        {
            return _loc.T("error.networkFormat", ex.Message);
        }
    }

    // Called once, right after the user follows the "reset password" email
    // link — that link's URL fragment carries a fresh access/refresh token
    // pair (GoTrue's implicit recovery flow). We adopt it as the current
    // session so UpdatePasswordAsync can use it, and so the user ends up
    // logged in once they're done.
    public async Task<string?> EstablishRecoverySessionAsync(string accessToken, string refreshToken, int expiresIn)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "user");
            req.Headers.Add("Authorization", $"Bearer {accessToken}");
            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return ExtractError(body);

            var user = JsonDocument.Parse(body).RootElement;
            CurrentSession = new AuthSession
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                UserId = user.GetProperty("id").GetString()!,
                Email = user.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
            };
            await _storage.SetAsync(StorageKey, JsonSerializer.Serialize(CurrentSession));
            return null;
        }
        catch (Exception ex)
        {
            return _loc.T("error.networkFormat", ex.Message);
        }
    }

    public async Task<string?> UpdatePasswordAsync(string newPassword)
    {
        if (CurrentSession == null) return _loc.T("auth.noSession");
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Put, "user");
            req.Headers.Add("Authorization", $"Bearer {CurrentSession.AccessToken}");
            req.Content = JsonContent.Create(new { password = newPassword });
            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return ExtractError(body);
            OnAuthChanged?.Invoke();
            return null;
        }
        catch (Exception ex)
        {
            return _loc.T("error.networkFormat", ex.Message);
        }
    }

    public async Task LogOutAsync()
    {
        if (CurrentSession != null)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "logout");
                req.Headers.Add("Authorization", $"Bearer {CurrentSession.AccessToken}");
                await _http.SendAsync(req);
            }
            catch { /* best-effort server-side invalidation */ }
        }
        CurrentSession = null;
        await _storage.RemoveAsync(StorageKey);
        OnAuthChanged?.Invoke();
    }

    private async Task RefreshAsync(string refreshToken)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("token?grant_type=refresh_token", new { refresh_token = refreshToken });
            if (!resp.IsSuccessStatusCode) { CurrentSession = null; return; }
            await SetSessionFromJsonAsync(JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement);
        }
        catch
        {
            CurrentSession = null;
        }
    }

    private async Task SetSessionFromJsonAsync(JsonElement root)
    {
        var user = root.GetProperty("user");
        CurrentSession = new AuthSession
        {
            AccessToken = root.GetProperty("access_token").GetString()!,
            RefreshToken = root.GetProperty("refresh_token").GetString()!,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()),
            UserId = user.GetProperty("id").GetString()!,
            Email = user.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
        };
        await _storage.SetAsync(StorageKey, JsonSerializer.Serialize(CurrentSession));
        OnAuthChanged?.Invoke();
    }

    // GoTrue's own error text is always in English regardless of app language;
    // TranslateServerError covers the common cases (wrong password, unconfirmed
    // email...) and passes anything unmapped through untranslated.
    private string ExtractError(string body)
    {
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            foreach (var prop in new[] { "msg", "error_description", "message" })
            {
                if (root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
                    return _loc.TranslateServerError(v.GetString()!);
            }
        }
        catch (JsonException) { /* fall through to generic message */ }
        return _loc.T("error.generic");
    }
}
