using System.Net.Http.Json;
using System.Text.Json;

namespace MealApp.Services;

// Thin wrapper around Supabase's auto-generated REST API (PostgREST).
// The anon/publishable key is safe to embed client-side by design — it only
// grants what the table's Row Level Security policies allow. Once a user is
// logged in (AuthService), requests carry their access token instead, so RLS
// policies that check auth.uid() see the right person.
public class SupabaseRestClient
{
    private readonly HttpClient _http;
    private readonly AuthService _auth;
    private readonly LocalizationService _loc;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public bool IsConfigured { get; }

    public SupabaseRestClient(IHttpClientFactory factory, AuthService auth, LocalizationService loc)
    {
        _auth = auth;
        _loc = loc;
        var url = SupabaseConfig.Url;
        var key = SupabaseConfig.AnonKey;
        IsConfigured = url != "YOUR_SUPABASE_URL" && key != "YOUR_SUPABASE_ANON_KEY";

        _http = factory.CreateClient();
        if (IsConfigured)
        {
            _http.BaseAddress = new Uri(url.TrimEnd('/') + "/rest/v1/");
            _http.DefaultRequestHeaders.Add("apikey", key);
        }
    }

    private string BearerToken => _auth.CurrentSession?.AccessToken ?? SupabaseConfig.AnonKey;

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("Authorization", $"Bearer {BearerToken}");
        return req;
    }

    public async Task<List<T>?> SelectAsync<T>(string table, string query = "select=*")
    {
        var resp = await _http.SendAsync(NewRequest(HttpMethod.Get, $"{table}?{query}"));
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<T>>(JsonOptions);
    }

    public async Task<T?> InsertOneAsync<T>(string table, object payload)
    {
        var req = NewRequest(HttpMethod.Post, table);
        req.Content = JsonContent.Create(payload);
        req.Headers.Add("Prefer", "return=representation");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var rows = await resp.Content.ReadFromJsonAsync<List<T>>(JsonOptions);
        return rows is { Count: > 0 } ? rows[0] : default;
    }

    public async Task InsertManyAsync(string table, object payload)
    {
        var req = NewRequest(HttpMethod.Post, table);
        req.Content = JsonContent.Create(payload);
        req.Headers.Add("Prefer", "return=minimal");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(string table, string filter)
    {
        var resp = await _http.SendAsync(NewRequest(HttpMethod.Delete, $"{table}?{filter}"));
        resp.EnsureSuccessStatusCode();
    }

    // Recipe photos go through the upload-recipe-photo Edge Function (service-role
    // write server-side) rather than direct Storage REST calls — see that
    // function's comments for why storage.objects can't get a client-side RLS
    // policy in this project. Returns the public URL on success, or an error message.
    public async Task<(string? Url, string? Error)> UploadRecipePhotoAsync(byte[] bytes, string contentType)
    {
        try
        {
            var url = new Uri($"{SupabaseConfig.Url.TrimEnd('/')}/functions/v1/upload-recipe-photo");
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("apikey", SupabaseConfig.AnonKey);
            req.Headers.Add("Authorization", $"Bearer {BearerToken}");
            req.Content = new ByteArrayContent(bytes);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            var root = JsonDocument.Parse(body).RootElement;
            if (!resp.IsSuccessStatusCode || !root.GetProperty("ok").GetBoolean())
            {
                var err = root.TryGetProperty("error", out var e) ? e.GetString() : _loc.T("error.uploadFailed");
                return (null, err);
            }
            return (root.GetProperty("url").GetString(), null);
        }
        catch (Exception ex)
        {
            return (null, _loc.T("error.networkFormat", ex.Message));
        }
    }

    // Asks the create-checkout-session Edge Function for a Stripe Checkout
    // URL for the current household's subscription, to redirect the browser
    // to. That function does the actual Stripe Customer/Session creation
    // server-side with the secret key — the client never touches it.
    public async Task<(string? Url, string? Error)> CreateCheckoutSessionAsync(string successUrl, string cancelUrl)
    {
        try
        {
            var url = new Uri($"{SupabaseConfig.Url.TrimEnd('/')}/functions/v1/create-checkout-session");
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("apikey", SupabaseConfig.AnonKey);
            req.Headers.Add("Authorization", $"Bearer {BearerToken}");
            req.Content = JsonContent.Create(new { success_url = successUrl, cancel_url = cancelUrl });

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            var root = JsonDocument.Parse(body).RootElement;
            if (!resp.IsSuccessStatusCode || !root.GetProperty("ok").GetBoolean())
            {
                var err = root.TryGetProperty("error", out var e) ? e.GetString() : _loc.T("error.checkoutFailed");
                return (null, err);
            }
            return (root.GetProperty("url").GetString(), null);
        }
        catch (Exception ex)
        {
            return (null, _loc.T("error.networkFormat", ex.Message));
        }
    }

    // Calls a Postgres function exposed via PostgREST's /rpc/{name} endpoint.
    public async Task<T?> RpcAsync<T>(string function, object? args = null)
    {
        var req = NewRequest(HttpMethod.Post, $"rpc/{function}");
        req.Content = JsonContent.Create(args ?? new { });
        var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new SupabaseRpcException(ExtractPgError(body));
        if (string.IsNullOrWhiteSpace(body) || body == "null") return default;
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    // Returns the raw (English) Postgres RAISE EXCEPTION message — callers
    // translate it via LocalizationService.TranslateServerError, since only
    // they know which of their own known messages this might be.
    private string ExtractPgError(string body)
    {
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString()!;
        }
        catch (JsonException) { /* fall through to generic message */ }
        return _loc.T("error.server");
    }
}

public class SupabaseRpcException : Exception
{
    public SupabaseRpcException(string message) : base(message) { }
}

public static class SupabaseConfig
{
    // Same project the JS version used — Project Settings → API in the Supabase dashboard.
    public const string Url = "https://xdrrumrjrbihidqniyvc.supabase.co";
    public const string AnonKey = "sb_publishable_CEoX6qcAbd-JfOJT1oB7bw_r7StYdcc";

    // Password-recovery emails always land on the web build — the MAUI/Android
    // WebView isn't a real navigable HTTPS origin GoTrue can redirect to, so
    // resetting a password is a "do it from the web" flow even for app users.
    public const string PasswordResetRedirectUrl = "https://stalwart-arithmetic-1b8533.netlify.app/";
}
