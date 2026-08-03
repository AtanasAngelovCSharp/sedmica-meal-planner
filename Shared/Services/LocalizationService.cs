namespace MealApp.Services;

// Drives the app's UI language. Bulgarian ("bg") is the fallback for any
// missing key, so a partially-translated future addition never shows a raw
// key instead of text. LanguageChosen gates the first-launch language
// picker — false until the user picks one (or we restore a saved choice),
// so Home.razor can show the picker before anything else, even the auth
// screen.
public class LocalizationService
{
    public event Action? OnChange;

    private readonly LocalStorageService _storage;
    private const string StorageKey = "appLanguage";

    public string CurrentLanguage { get; private set; } = "bg";
    public bool LanguageChosen { get; private set; }

    public static readonly (string Code, string NativeName)[] SupportedLanguages =
    {
        ("bg", "Български"),
        ("en", "English"),
        ("de", "Deutsch"),
        ("ru", "Русский"),
        ("fr", "Français"),
        ("es", "Español"),
        ("it", "Italiano"),
    };

    public LocalizationService(LocalStorageService storage)
    {
        _storage = storage;
    }

    public async Task InitializeAsync()
    {
        var saved = await _storage.GetAsync(StorageKey);
        if (!string.IsNullOrEmpty(saved) && Translations.Data.ContainsKey(saved))
        {
            CurrentLanguage = saved;
            LanguageChosen = true;
        }
    }

    public async Task SetLanguageAsync(string code)
    {
        if (!Translations.Data.ContainsKey(code)) code = "bg";
        CurrentLanguage = code;
        LanguageChosen = true;
        await _storage.SetAsync(StorageKey, code);
        OnChange?.Invoke();
    }

    public string T(string key)
    {
        if (Translations.Data.TryGetValue(CurrentLanguage, out var dict) && dict.TryGetValue(key, out var value))
            return value;
        // Fall back to Bulgarian (the source language) so a missing
        // translation shows real text instead of a raw "some.key" string.
        if (Translations.Data["bg"].TryGetValue(key, out var fallback))
            return fallback;
        return key;
    }

    public string T(string key, params object[] args) => string.Format(T(key), args);

    // Translates a raw error string coming straight from a Postgres RPC
    // (RAISE EXCEPTION, always English in the SQL) or Supabase Auth/GoTrue.
    // Unmapped messages pass through untranslated rather than being hidden.
    public string TranslateServerError(string raw) =>
        Translations.ServerErrorKeys.TryGetValue(raw, out var key) ? T(key) : raw;
}
