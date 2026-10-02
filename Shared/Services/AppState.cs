using System.Text.Json;
using MealApp.Models;

namespace MealApp.Services;

// Central app state + logic — the C# equivalent of what used to be the
// top-level code in js/app.js. Same pattern throughout: render instantly
// from built-in/local data, then asynchronously upgrade from Supabase when
// it resolves, without ever blocking the UI on the network.
public class AppState
{
    // recipes-demo.json is hand-authored with camelCase keys ("ingredients",
    // "proteinG"...) against PascalCase C# properties — without this, Deserialize
    // silently returns empty Ingredients/null Macros for every built-in recipe.
    private static readonly JsonSerializerOptions JsonCaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private readonly SupabaseRestClient _supabase;
    private readonly LocalStorageService _storage;
    private readonly LocalizationService _loc;

    public event Action? OnChange;

    // Derived state (store totals, weekly macros) depends on the meal plan,
    // recipes and pantry, all of which can change independently (and
    // asynchronously, as Supabase data arrives), so it's simplest to just
    // recompute on every state change rather than track dependencies.
    private void NotifyChange()
    {
        RecomputePromotions();
        RecomputeWeeklyMacros();
        OnChange?.Invoke();
    }

    public DataSourceState DataSource { get; private set; } = DataSourceState.Loading;
    public string DataSourceMessage { get; private set; } = "";

    public Dictionary<string, RecipeData> RecipesDb { get; } = new();

    // Label carries the day's translation-key suffix ("monday".."sunday"),
    // not display text — components render it via L.T($"day.{day.Label}").
    public static readonly List<DayInfo> Days = new()
    {
        new DayInfo { Key = "Monday", Label = "monday" },
        new DayInfo { Key = "Tuesday", Label = "tuesday" },
        new DayInfo { Key = "Wednesday", Label = "wednesday" },
        new DayInfo { Key = "Thursday", Label = "thursday" },
        new DayInfo { Key = "Friday", Label = "friday" },
        new DayInfo { Key = "Saturday", Label = "saturday" },
        new DayInfo { Key = "Sunday", Label = "sunday" },
    };

    // Label is a language-neutral category code (translation-key suffix,
    // "category.{Label}"), not display text — it's also used as the
    // GroupedByCategory dictionary key, so it must stay stable across
    // language switches.
    private static readonly List<IngredientCategory> Categories = new()
    {
        new IngredientCategory
        {
            Label = "produce",
            Keywords = new[] { "домат", "краставиц", "лук", "чушк", "морков", "чесън", "магданоз",
                "копър", "зеле", "цвекло", "спанак", "картоф", "гъб", "грах", "царевиц", "маслин", "лимон" },
        },
        new IngredientCategory
        {
            Label = "meatFish",
            Keywords = new[] { "шкембе", "кайма", "пържол", "пиле", "скумрия", "шунка" },
        },
        new IngredientCategory
        {
            Label = "dairyEggs",
            Keywords = new[] { "мляко", "сирене", "кашкавал", "краве масло", "яйц", "жълтъц" },
        },
        new IngredientCategory
        {
            Label = "grainsBread",
            Keywords = new[] { "ориз", "брашно", "хляб", "фиде", "боб", "леща" },
        },
        new IngredientCategory { Label = "spicesOther", Keywords = Array.Empty<string>() },
    };

    // day.Key -> ordered list of meal names — no fixed breakfast/lunch/dinner
    // slots, the user adds as many or as few meals to a day as they want.
    public Dictionary<string, List<string>> MealPlan { get; private set; } = new();
    public List<PantryItem> PantryItems { get; private set; } = new();
    public List<PromoGroup> Promotions { get; private set; } = new();
    public Macros WeeklyMacros { get; private set; } = new();
    public List<string> FavoriteRecipeNames { get; private set; } = new();
    public bool PricesLoading { get; private set; } = true;

    private readonly AuthService _auth;

    // Resolved once per login (or after creating/joining a household). Null
    // means the user is logged in but doesn't belong to a household yet —
    // the UI shows onboarding (create/join) instead of the main tabs.
    public long? CurrentHouseholdId { get; private set; }

    // Billing state for the current household. Null status means "not
    // resolved yet" (first load, or the fetch failed offline) — HasAccess
    // treats that as open rather than locking a household out over a
    // network hiccup; it only becomes a real block once we've actually
    // heard back that the trial ended and there's no active subscription.
    public string? SubscriptionStatus { get; private set; }
    public DateTimeOffset? TrialEndsAt { get; private set; }

    public bool HasAccess =>
        !CurrentHouseholdId.HasValue ||
        SubscriptionStatus is null ||
        SubscriptionStatus == "active" ||
        (SubscriptionStatus == "trial" && TrialEndsAt is { } end && end > DateTimeOffset.UtcNow);

    // The Monday of the week currently displayed. Defaults to the real
    // current week and shifts by ±7 days via GoToPreviousWeekAsync/GoToNextWeekAsync.
    public DateOnly CurrentWeekStart { get; private set; } = MondayOf(DateOnly.FromDateTime(DateTime.Today));

    private static DateOnly MondayOf(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    public AppState(SupabaseRestClient supabase, LocalStorageService storage, LocalizationService loc, AuthService auth)
    {
        _supabase = supabase;
        _storage = storage;
        _loc = loc;
        _auth = auth;
        DataSourceMessage = _loc.T("data.connecting");
    }

    // ======================================================================
    // STARTUP — only called once the user is logged in (see Home.razor).
    // ======================================================================
    public async Task InitializeAsync()
    {
        await LoadBuiltInRecipesAsync();
        await ResolveHouseholdIdAsync();
        await ResolveSubscriptionAsync();

        // Local-first for household data too: whatever was cached from the
        // last successful sync renders immediately (works offline), then the
        // Supabase passes below refresh it in the background as usual.
        if (CurrentHouseholdId.HasValue)
        {
            foreach (var (name, data) in await LoadRecipesCacheAsync()) RecipesDb[name] = data;
        }

        MealPlan = await LoadMealPlanLocalAsync();
        PantryItems = await LoadPantryLocalAsync();
        FavoriteRecipeNames = await LoadFavoritesLocalAsync();
        NotifyChange();

        // Recipes are visible even without a household (the built-in library
        // is global), but everything else needs one to sync against.
        _ = UpgradeRecipesFromSupabaseAsync();
        if (CurrentHouseholdId.HasValue)
        {
            _ = UpgradeMealPlanFromSupabaseAsync();
            _ = UpgradePantryFromSupabaseAsync();
            _ = UpgradeFavoritesFromSupabaseAsync();
            _ = LoadPricesAsync();
        }
        else
        {
            PricesLoading = false;
        }
    }

    private async Task ResolveHouseholdIdAsync()
    {
        if (!_auth.IsLoggedIn) { CurrentHouseholdId = null; return; }
        try
        {
            var rows = await _supabase.SelectAsync<HouseholdMembershipRow>(
                "household_members", $"select=household_id&user_id=eq.{_auth.CurrentSession!.UserId}&limit=1");
            CurrentHouseholdId = rows is { Count: > 0 } ? rows[0].HouseholdId : (long?)null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Household resolve error: {ex}");
            CurrentHouseholdId = null;
        }
    }

    // Every household gets a subscriptions row the moment it's created (see
    // create_household_for_user in schema_and_seed.sql), so a missing row
    // here means something's genuinely wrong rather than "not subscribed
    // yet" — SubscriptionStatus is left null either way, which HasAccess
    // treats as open rather than guessing.
    public async Task ResolveSubscriptionAsync()
    {
        if (!CurrentHouseholdId.HasValue) { SubscriptionStatus = null; TrialEndsAt = null; return; }
        try
        {
            var rows = await _supabase.SelectAsync<SubscriptionRow>(
                "subscriptions", $"select=status,trial_ends_at,current_period_end&household_id=eq.{CurrentHouseholdId.Value}&limit=1");
            if (rows is { Count: > 0 })
            {
                SubscriptionStatus = rows[0].Status;
                TrialEndsAt = rows[0].TrialEndsAt;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Subscription resolve error: {ex}");
        }
    }

    // ======================================================================
    // HOUSEHOLD ONBOARDING + RECIPE SHARING
    // ======================================================================
    public async Task<string?> CreateHouseholdAsync(string name)
    {
        try
        {
            await _supabase.RpcAsync<HouseholdRow>("create_household_for_user", new { p_name = name });
            await InitializeAsync();
            return null;
        }
        catch (SupabaseRpcException ex)
        {
            return _loc.TranslateServerError(ex.Message);
        }
    }

    public async Task<string?> JoinHouseholdAsync(string code)
    {
        try
        {
            // Wrong code returns null (not an exception) — see the SQL
            // function's comment: an uncaught RAISE would roll back its own
            // rate-limit attempt-log insert along with it.
            var result = await _supabase.RpcAsync<HouseholdRow>("join_household_by_code", new { p_code = code });
            if (result == null) return _loc.T("server.invalidInviteCode");
            await InitializeAsync();
            return null;
        }
        catch (SupabaseRpcException ex)
        {
            return _loc.TranslateServerError(ex.Message);
        }
    }

    public async Task<string?> LeaveHouseholdAsync()
    {
        try
        {
            await _supabase.RpcAsync<object>("leave_household");
            await InitializeAsync();
            return null;
        }
        catch (SupabaseRpcException ex)
        {
            return _loc.TranslateServerError(ex.Message);
        }
    }

    public async Task<string?> GetHouseholdInviteCodeAsync()
    {
        if (!CurrentHouseholdId.HasValue) return null;
        var rows = await _supabase.SelectAsync<HouseholdRow>("households", $"select=invite_code&id=eq.{CurrentHouseholdId.Value}");
        return rows is { Count: > 0 } ? rows[0].InviteCode : null;
    }

    public async Task<(string? Code, string? Error)> ShareRecipeAsync(string recipeName)
    {
        if (!RecipesDb.TryGetValue(recipeName, out var recipe) || recipe.Id is not { } id)
            return (null, _loc.T("recipebook.notSynced"));
        try
        {
            var code = await _supabase.RpcAsync<string>("share_recipe", new { p_recipe_id = id });
            recipe.ShareCode = code;
            NotifyChange();
            return (code, null);
        }
        catch (SupabaseRpcException ex)
        {
            return (null, _loc.TranslateServerError(ex.Message));
        }
    }

    // ======================================================================
    // RECIPE BOOK — recipes the current household created themselves
    // (as opposed to the global built-in library, household_id == null).
    // ======================================================================
    public List<(string Name, RecipeData Data)> MyRecipes =>
        RecipesDb.Where(kv => kv.Value.HouseholdId == CurrentHouseholdId)
            .Select(kv => (kv.Key, kv.Value))
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .ToList();

    // What the meal planner offers: the household's own recipes plus the
    // global built-in library, so a brand-new household can plan a week
    // before writing any recipe of its own.
    public List<(string Name, RecipeData Data)> AvailableRecipes =>
        RecipesDb.Where(kv => kv.Value.HouseholdId == CurrentHouseholdId || kv.Value.HouseholdId == null)
            .Select(kv => (kv.Key, kv.Value))
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .ToList();

    // Favorites can point at built-in library recipes too, not just this
    // household's own — so this looks up the full RecipesDb, not MyRecipes.
    public List<(string Name, RecipeData Data)> FavoriteRecipes =>
        FavoriteRecipeNames
            .Where(RecipesDb.ContainsKey)
            .Select(n => (n, RecipesDb[n]))
            .OrderBy(r => r.n, StringComparer.Ordinal)
            .ToList();

    public async Task<string?> CreateRecipeAsync(string name, int? servings, decimal? portionGrams, List<Ingredient> ingredients, string? photoUrl = null, string? instructions = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return _loc.T("recipebook.nameEmpty");
        if (!CurrentHouseholdId.HasValue) return _loc.T("recipebook.noHousehold");
        try
        {
            var householdId = RequireHouseholdId();
            var inserted = await _supabase.InsertOneAsync<RecipeRow>("recipes", new RecipeInsert
            {
                HouseholdId = householdId,
                Name = name.Trim(),
                Servings = servings,
                PortionGrams = portionGrams,
                PhotoUrl = photoUrl,
                Instructions = instructions,
            });
            if (inserted == null) throw new Exception("Insert returned no row");

            if (ingredients.Count > 0)
            {
                var rows = ingredients.Select((ing, i) => new RecipeIngredientInsert
                {
                    RecipeId = inserted.Id,
                    IngredientName = ing.Name,
                    Amount = ing.Amount,
                    SortOrder = i,
                }).ToList();
                await _supabase.InsertManyAsync("recipe_ingredients", rows);
            }

            RecipesDb[inserted.Name] = new RecipeData
            {
                Id = inserted.Id,
                HouseholdId = inserted.HouseholdId,
                Servings = inserted.Servings,
                PortionGrams = inserted.PortionGrams,
                PhotoUrl = inserted.PhotoUrl,
                Instructions = inserted.Instructions,
                Ingredients = ingredients.ToList(),
                Macros = NutritionEstimator.Estimate(ingredients, inserted.Servings),
            };
            await SaveRecipesCacheAsync();
            NotifyChange();
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Create recipe error: {ex}");
            return _loc.T("recipebook.saveError");
        }
    }

    public async Task<string?> DeleteRecipeAsync(string recipeName)
    {
        if (!RecipesDb.TryGetValue(recipeName, out var recipe) || recipe.Id is not { } id)
            return _loc.T("recipebook.notFound");
        try
        {
            await _supabase.DeleteAsync("recipe_ingredients", $"recipe_id=eq.{id}");
            await _supabase.DeleteAsync("recipes", $"id=eq.{id}");
            RecipesDb.Remove(recipeName);
            await SaveRecipesCacheAsync();
            NotifyChange();
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Delete recipe error: {ex}");
            return _loc.T("recipebook.deleteError");
        }
    }

    public async Task<string?> ImportRecipeByCodeAsync(string code)
    {
        try
        {
            var imported = await _supabase.RpcAsync<RecipeRow>("import_recipe_by_code", new { p_code = code });
            if (imported == null) return _loc.T("server.invalidShareCode");
            _ = UpgradeRecipesFromSupabaseAsync();
            return null;
        }
        catch (SupabaseRpcException ex)
        {
            return _loc.TranslateServerError(ex.Message);
        }
    }

    // Only estimates from ingredients when nothing explicit was supplied —
    // the built-in library ships its own curated macro numbers and those
    // take priority over a guess.
    private static void EnsureMacros(RecipeData data)
    {
        var m = data.Macros;
        var hasExplicit = m != null && (m.Calories.HasValue || m.ProteinG.HasValue || m.CarbsG.HasValue || m.FatG.HasValue);
        if (!hasExplicit) data.Macros = NutritionEstimator.Estimate(data.Ingredients, data.Servings);
    }

    // Local cache of this household's own recipes (not the built-in library,
    // which is already embedded in the app and needs no caching). Read back
    // on startup so the Recipe Book works offline; refreshed after every
    // successful create/delete/Supabase sync.
    private string RecipesCacheKey => $"recipesCache_{CurrentHouseholdId}";

    private async Task<Dictionary<string, RecipeData>> LoadRecipesCacheAsync()
    {
        if (!CurrentHouseholdId.HasValue) return new();
        var raw = await _storage.GetAsync(RecipesCacheKey);
        if (string.IsNullOrEmpty(raw)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, RecipeData>>(raw) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private Task SaveRecipesCacheAsync()
    {
        if (!CurrentHouseholdId.HasValue) return Task.CompletedTask;
        var mine = RecipesDb.Where(kv => kv.Value.HouseholdId == CurrentHouseholdId)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return _storage.SetAsync(RecipesCacheKey, JsonSerializer.Serialize(mine)).AsTask();
    }

    private Task LoadBuiltInRecipesAsync()
    {
        try
        {
            using var stream = typeof(AppState).Assembly.GetManifestResourceStream("MealApp.Shared.recipes-demo.json");
            if (stream == null) return Task.CompletedTask;
            var demo = JsonSerializer.Deserialize<Dictionary<string, RecipeData>>(stream, JsonCaseInsensitive);
            if (demo == null) return Task.CompletedTask;
            foreach (var (name, data) in demo)
            {
                EnsureMacros(data);
                RecipesDb[name] = data;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Built-in recipes load error: {ex}");
        }
        return Task.CompletedTask;
    }

    // ======================================================================
    // RECIPES (Supabase upgrade pass)
    // ======================================================================
    private async Task UpgradeRecipesFromSupabaseAsync()
    {
        if (!_supabase.IsConfigured)
        {
            DataSource = DataSourceState.Local;
            DataSourceMessage = _loc.T("data.local");
            NotifyChange();
            return;
        }

        try
        {
            var recipes = await _supabase.SelectAsync<RecipeRow>("recipes", "select=id,name,household_id,share_code,servings,portion_grams,photo_url,instructions,calories,protein_g,carbs_g,fat_g");
            var ingredients = await _supabase.SelectAsync<RecipeIngredientRow>(
                "recipe_ingredients", "select=recipe_id,ingredient_name,amount,sort_order&order=sort_order.asc");

            if (recipes == null || recipes.Count == 0)
            {
                DataSource = DataSourceState.Error;
                DataSourceMessage = _loc.T("data.emptyTable");
                NotifyChange();
                return;
            }

            var idToName = recipes.ToDictionary(r => r.Id, r => r.Name);
            // Built with a loop (not ToDictionary) because recipe names aren't
            // guaranteed unique once shared/imported copies exist across households.
            var fresh = new Dictionary<string, RecipeData>();
            foreach (var r in recipes)
            {
                fresh[r.Name] = new RecipeData
                {
                    Id = r.Id,
                    HouseholdId = r.HouseholdId,
                    ShareCode = r.ShareCode,
                    Servings = r.Servings,
                    PortionGrams = r.PortionGrams,
                    PhotoUrl = r.PhotoUrl,
                    Instructions = r.Instructions,
                    Macros = new Macros { Calories = r.Calories, ProteinG = r.ProteinG, CarbsG = r.CarbsG, FatG = r.FatG },
                };
            }
            foreach (var ing in ingredients ?? new())
            {
                if (!idToName.TryGetValue(ing.RecipeId, out var recipeName)) continue;
                if (!fresh.TryGetValue(recipeName, out var data)) continue;
                data.Ingredients.Add(new Ingredient { Name = ing.IngredientName, Amount = ing.Amount ?? "" });
            }

            foreach (var (name, data) in fresh)
            {
                EnsureMacros(data);
                RecipesDb[name] = data;
            }
            await SaveRecipesCacheAsync();

            DataSource = DataSourceState.Supabase;
            DataSourceMessage = _loc.T("data.connectedFormat", recipes.Count);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Supabase connection error: {ex}");
            DataSource = DataSourceState.Error;
            DataSourceMessage = _loc.T("data.noConnection");
        }
        NotifyChange();
    }

    // ======================================================================
    // MEAL PLAN — each day holds an open-ended, ordered list of meals (no
    // fixed breakfast/lunch/dinner slots); meals can only be added by
    // picking a recipe from the Recipe Book.
    // ======================================================================
    private static Dictionary<string, List<string>> EmptyWeek() =>
        Days.ToDictionary(d => d.Key, _ => new List<string>());

    // Scoped by household (or "local" pre-household) so cached weeks never
    // leak between households sharing a device, and this doubles as the
    // offline cache once a household exists, not just the local-only store
    // used before joining/creating one.
    private string MealPlanStorageKey => $"mealPlanV3_{CurrentHouseholdId?.ToString() ?? "local"}_{CurrentWeekStart:yyyy-MM-dd}";

    private Task<Dictionary<string, List<string>>> LoadMealPlanLocalAsync() =>
        LoadMealPlanForCurrentWeekAsync(seedDemoIfEmpty: true);

    private async Task<Dictionary<string, List<string>>> LoadMealPlanForCurrentWeekAsync(bool seedDemoIfEmpty)
    {
        var raw = await _storage.GetAsync(MealPlanStorageKey);
        if (!string.IsNullOrEmpty(raw))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(raw);
                if (parsed != null) return parsed;
            }
            catch (JsonException) { /* fall through to demo data */ }
        }

        // No cache yet for this week — a household's Supabase pass will
        // populate it shortly; an anonymous user gets the demo template.
        if (CurrentHouseholdId.HasValue) return EmptyWeek();
        if (!seedDemoIfEmpty) return EmptyWeek();

        // First-run demo data: a real week built from the 21 gotvach.bg recipes,
        // so Shopping List has real data to consolidate.
        var demo = EmptyWeek();
        void Add(string day, string meal) => demo[day].Add(meal);
        Add("Monday", "Шкембе чорба с прясно мляко");
        Add("Monday", "Шопска Салата Оригинал");
        Add("Monday", "Класически пържени кюфтета");
        Add("Tuesday", "Класическа мусака с картофи и кайма");
        Add("Tuesday", "Класическа овчарска салата");
        Add("Tuesday", "Пилешка супа с фиде и застройка");
        Add("Wednesday", "Пълнени чушки с кайма, ориз и бял сос");
        Add("Wednesday", "Класически Български Таратор");
        Add("Wednesday", "Кебапчета");
        Add("Thursday", "Боб яхнията на баба");
        Add("Thursday", "Свински пържоли на тиган");
        Add("Thursday", "Лек зеленчуков гювеч");
        Add("Friday", "Вкусна леща яхния");
        Add("Friday", "Пиле с картофи на фурна по селски");
        Add("Friday", "Зелеви сарми с кайма и ориз");
        Add("Saturday", "Перфектната скумрия на скара");
        Add("Saturday", "Салата Снежанка");
        Add("Saturday", "Ориз със спанак");
        Add("Sunday", "Класическа зеле и моркови салата");
        Add("Sunday", "Салата от печено цвекло");
        Add("Sunday", "Пиле с ориз - класическа рецепта");

        await _storage.SetAsync(MealPlanStorageKey, JsonSerializer.Serialize(demo));
        return demo;
    }

    private Task SaveMealPlanLocalAsync() => _storage.SetAsync(MealPlanStorageKey, JsonSerializer.Serialize(MealPlan)).AsTask();

    private static Dictionary<string, List<string>> CloneMealPlan(Dictionary<string, List<string>> src) =>
        src.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value));

    // Full-replace sync: wipes this household's rows for weekStart and
    // re-inserts plan. Both are captured by the caller *before* any await —
    // not read from CurrentWeekStart/MealPlan in here, because this method
    // itself awaits network calls, and reading those mutable shared fields
    // after a yield can race with fast week navigation (SetMeal/ClearMeal
    // fires this fire-and-forget, user flips week while it's in flight, and
    // it ends up deleting/rewriting the *new* week using stale data).
    private async Task SyncMealPlanToSupabaseAsync(DateOnly weekStart, Dictionary<string, List<string>> plan)
    {
        if (!CurrentHouseholdId.HasValue) return;
        try
        {
            var householdId = RequireHouseholdId();
            await _supabase.DeleteAsync("meal_plan_entries", $"household_id=eq.{householdId}&week_start=eq.{weekStart:yyyy-MM-dd}");

            var rows = new List<MealPlanEntryInsert>();
            foreach (var day in Days)
            {
                if (!plan.TryGetValue(day.Key, out var meals)) continue;
                for (var i = 0; i < meals.Count; i++)
                {
                    var mealName = meals[i];
                    if (string.IsNullOrEmpty(mealName)) continue;
                    rows.Add(new MealPlanEntryInsert
                    {
                        HouseholdId = householdId,
                        WeekStart = weekStart.ToString("yyyy-MM-dd"),
                        DayKey = day.Key,
                        MealType = "",
                        MealName = mealName,
                        SortOrder = i,
                    });
                }
            }
            if (rows.Count > 0) await _supabase.InsertManyAsync("meal_plan_entries", rows);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Meal plan sync error: {ex}");
        }
    }

    private async Task UpgradeMealPlanFromSupabaseAsync()
    {
        if (!CurrentHouseholdId.HasValue) return;
        var weekStart = CurrentWeekStart;
        try
        {
            var householdId = RequireHouseholdId();
            var data = await _supabase.SelectAsync<MealPlanEntryRow>(
                "meal_plan_entries", $"select=day_key,meal_type,meal_name,sort_order&household_id=eq.{householdId}&week_start=eq.{weekStart:yyyy-MM-dd}&order=sort_order.asc");

            // The user may have navigated to a different week while this request was in flight.
            if (weekStart != CurrentWeekStart) return;

            var plan = EmptyWeek();
            var hasRemoteData = false;
            foreach (var row in data ?? new())
            {
                if (!plan.ContainsKey(row.DayKey)) continue;
                plan[row.DayKey].Add(row.MealName);
                hasRemoteData = true;
            }

            if (hasRemoteData)
            {
                MealPlan = plan;
                await SaveMealPlanLocalAsync();
                NotifyChange();
            }
            else if (weekStart == MondayOf(DateOnly.FromDateTime(DateTime.Today)))
            {
                // Only auto-push the local template into a brand new household's
                // *current* week — other weeks should stay genuinely empty until set.
                await SyncMealPlanToSupabaseAsync(weekStart, plan);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Meal plan load error: {ex}");
        }
    }

    public async Task AddMealAsync(string dayKey, string mealName)
    {
        if (string.IsNullOrWhiteSpace(mealName)) return;
        if (!MealPlan.ContainsKey(dayKey)) MealPlan[dayKey] = new List<string>();
        MealPlan[dayKey].Add(mealName.Trim());
        var weekStart = CurrentWeekStart;
        var snapshot = CloneMealPlan(MealPlan);
        await SaveMealPlanLocalAsync();
        NotifyChange();
        _ = SyncMealPlanToSupabaseAsync(weekStart, snapshot);
    }

    public async Task RemoveMealAsync(string dayKey, int index)
    {
        if (!MealPlan.TryGetValue(dayKey, out var meals) || index < 0 || index >= meals.Count) return;
        meals.RemoveAt(index);
        var weekStart = CurrentWeekStart;
        var snapshot = CloneMealPlan(MealPlan);
        await SaveMealPlanLocalAsync();
        NotifyChange();
        _ = SyncMealPlanToSupabaseAsync(weekStart, snapshot);
    }

    private string MonthAbbr(int month) => _loc.T($"month.{month}");

    public string DateLabelForDay(int index)
    {
        var d = CurrentWeekStart.AddDays(index);
        return $"{d.Day} {MonthAbbr(d.Month)}";
    }

    public string WeekLabel
    {
        get
        {
            var sunday = CurrentWeekStart.AddDays(6);
            if (CurrentWeekStart.Month == sunday.Month)
                return $"{CurrentWeekStart.Day} – {sunday.Day} {MonthAbbr(CurrentWeekStart.Month)}";
            return $"{CurrentWeekStart.Day} {MonthAbbr(CurrentWeekStart.Month)} – {sunday.Day} {MonthAbbr(sunday.Month)}";
        }
    }

    public async Task GoToPreviousWeekAsync() => await GoToWeekAsync(CurrentWeekStart.AddDays(-7));
    public async Task GoToNextWeekAsync() => await GoToWeekAsync(CurrentWeekStart.AddDays(7));

    private async Task GoToWeekAsync(DateOnly newWeekStart)
    {
        CurrentWeekStart = newWeekStart;
        MealPlan = await LoadMealPlanForCurrentWeekAsync(seedDemoIfEmpty: false);
        NotifyChange();
        if (CurrentHouseholdId.HasValue) _ = UpgradeMealPlanFromSupabaseAsync();
    }

    // ======================================================================
    // FAVORITE RECIPES
    // ======================================================================
    private async Task<List<string>> LoadFavoritesLocalAsync()
    {
        var raw = await _storage.GetAsync(FavoritesStorageKey);
        if (string.IsNullOrEmpty(raw)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(raw) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private string FavoritesStorageKey => $"favoriteRecipes_{CurrentHouseholdId?.ToString() ?? "local"}";

    private Task SaveFavoritesLocalAsync() => _storage.SetAsync(FavoritesStorageKey, JsonSerializer.Serialize(FavoriteRecipeNames)).AsTask();

    private async Task UpgradeFavoritesFromSupabaseAsync()
    {
        if (!CurrentHouseholdId.HasValue) return;
        try
        {
            var householdId = RequireHouseholdId();
            var data = await _supabase.SelectAsync<FavoriteRecipeRow>(
                "favorite_recipes", $"select=id,recipe_name&household_id=eq.{householdId}");
            FavoriteRecipeNames = (data ?? new()).Select(r => r.RecipeName).ToList();
            await SaveFavoritesLocalAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Favorites load error: {ex}");
        }
        NotifyChange();
    }

    public bool IsFavorite(string recipeName) => FavoriteRecipeNames.Contains(recipeName);

    public async Task ToggleFavoriteAsync(string recipeName)
    {
        if (string.IsNullOrWhiteSpace(recipeName)) return;
        var isFav = IsFavorite(recipeName);

        if (CurrentHouseholdId.HasValue)
        {
            try
            {
                var householdId = RequireHouseholdId();
                if (isFav)
                {
                    await _supabase.DeleteAsync("favorite_recipes", $"household_id=eq.{householdId}&recipe_name=eq.{Uri.EscapeDataString(recipeName)}");
                }
                else
                {
                    await _supabase.InsertOneAsync<FavoriteRecipeRow>("favorite_recipes", new FavoriteRecipeInsert
                    {
                        HouseholdId = householdId,
                        RecipeName = recipeName,
                    });
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Favorite toggle error: {ex}");
                return;
            }
        }

        if (isFav) FavoriteRecipeNames.Remove(recipeName);
        else FavoriteRecipeNames.Add(recipeName);

        await SaveFavoritesLocalAsync();
        NotifyChange();
    }

    // ======================================================================
    // PANTRY
    // ======================================================================
    private string PantryStorageKey => $"pantryItems_{CurrentHouseholdId?.ToString() ?? "local"}";

    private async Task<List<PantryItem>> LoadPantryLocalAsync()
    {
        var raw = await _storage.GetAsync(PantryStorageKey);
        if (!string.IsNullOrEmpty(raw))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<PantryItem>>(raw);
                if (parsed != null) return parsed;
            }
            catch (JsonException) { /* fall through */ }
        }

        if (CurrentHouseholdId.HasValue) return new(); // no cache yet; Supabase pass will populate shortly.

        var demo = new List<PantryItem>
        {
            new() { Id = 1, Name = "Ориз", Amount = "1 кг" },
            new() { Id = 2, Name = "Олио", Amount = "500 мл" },
            new() { Id = 3, Name = "Яйца", Amount = "6 бр" },
        };
        await _storage.SetAsync(PantryStorageKey, JsonSerializer.Serialize(demo));
        return demo;
    }

    private Task SavePantryLocalAsync() => _storage.SetAsync(PantryStorageKey, JsonSerializer.Serialize(PantryItems)).AsTask();

    private async Task UpgradePantryFromSupabaseAsync()
    {
        if (!CurrentHouseholdId.HasValue) return;
        try
        {
            var householdId = RequireHouseholdId();
            var data = await _supabase.SelectAsync<PantryItemRow>(
                "pantry_items", $"select=id,ingredient_name,amount&household_id=eq.{householdId}&order=id.asc");
            PantryItems = (data ?? new()).Select(r => new PantryItem { Id = r.Id, Name = r.IngredientName, Amount = r.Amount ?? "" }).ToList();
            await SavePantryLocalAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Pantry load error: {ex}");
            PantryItems = await LoadPantryLocalAsync();
        }
        NotifyChange();
    }

    public async Task<bool> AddPantryItemAsync(string name, string amount)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        amount = amount?.Trim() ?? "";

        if (CurrentHouseholdId.HasValue)
        {
            try
            {
                var householdId = RequireHouseholdId();
                var inserted = await _supabase.InsertOneAsync<PantryItemRow>("pantry_items", new PantryItemInsert
                {
                    HouseholdId = householdId,
                    IngredientName = name,
                    Amount = amount,
                });
                if (inserted == null) throw new Exception("Insert returned no row");
                PantryItems.Add(new PantryItem { Id = inserted.Id, Name = inserted.IngredientName, Amount = inserted.Amount ?? "" });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Pantry add error: {ex}");
                return false;
            }
        }
        else
        {
            PantryItems.Add(new PantryItem { Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Name = name, Amount = amount });
        }

        await SavePantryLocalAsync();
        NotifyChange();
        return true;
    }

    public async Task<bool> DeletePantryItemAsync(long id)
    {
        if (CurrentHouseholdId.HasValue)
        {
            try
            {
                await _supabase.DeleteAsync("pantry_items", $"id=eq.{id}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Pantry delete error: {ex}");
                return false;
            }
        }

        PantryItems.RemoveAll(p => p.Id == id);
        await SavePantryLocalAsync();
        NotifyChange();
        return true;
    }

    public bool IsInPantry(string ingredientName)
    {
        var lower = ingredientName.ToLowerInvariant();
        return PantryItems.Any(p => lower.Contains(p.Name.ToLowerInvariant()));
    }

    // Recipes sorted by how large a fraction of their ingredients are already
    // in the pantry (ties broken by fewest missing ingredients).
    public List<(string Name, int HaveCount, int TotalCount)> RecommendedRecipes(int take = 5)
    {
        return RecipesDb
            .Select(kv => (Name: kv.Key, HaveCount: kv.Value.Ingredients.Count(i => IsInPantry(i.Name)), TotalCount: kv.Value.Ingredients.Count))
            .Where(r => r.TotalCount > 0 && r.HaveCount > 0)
            .OrderByDescending(r => (double)r.HaveCount / r.TotalCount)
            .ThenByDescending(r => r.HaveCount)
            .Take(take)
            .ToList();
    }

    // ======================================================================
    // SHOPPING LIST
    // ======================================================================
    public static string Categorize(string name)
    {
        var lower = name.ToLowerInvariant();
        foreach (var cat in Categories)
        {
            if (cat.Keywords.Any(k => lower.Contains(k))) return cat.Label;
        }
        return "spicesOther";
    }

    public static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private IEnumerable<string> AllScheduledMeals() =>
        Days.SelectMany(day => MealPlan.TryGetValue(day.Key, out var meals) ? meals : new List<string>())
            .Where(m => !string.IsNullOrEmpty(m));

    public ShoppingListResult GenerateShoppingList()
    {
        var result = new ShoppingListResult();
        var ingredientMap = new Dictionary<string, ShoppingListIngredient>();

        foreach (var mealName in AllScheduledMeals())
        {
            result.MealCount++;
            if (!RecipesDb.TryGetValue(mealName, out var recipe))
            {
                result.UnmatchedMeals.Add(mealName);
                continue;
            }
            foreach (var ing in recipe.Ingredients)
            {
                var key = ing.Name.Trim().ToLowerInvariant();
                if (!ingredientMap.TryGetValue(key, out var entry))
                {
                    entry = new ShoppingListIngredient { DisplayName = ing.Name, Category = Categorize(ing.Name) };
                    ingredientMap[key] = entry;
                }
                var amountText = string.IsNullOrEmpty(ing.Amount) ? _loc.T("misc.toTaste") : ing.Amount;
                if (!entry.Amounts.Contains(amountText)) entry.Amounts.Add(amountText);
            }
        }

        var all = ingredientMap.Values.ToList();
        var needed = all.Where(i => !IsInPantry(i.DisplayName)).ToList();
        result.PantryMatched.AddRange(all.Where(i => IsInPantry(i.DisplayName)));

        foreach (var cat in Categories)
        {
            var items = needed.Where(i => i.Category == cat.Label)
                .OrderBy(i => i.DisplayName, StringComparer.Ordinal)
                .ToList();
            if (items.Count > 0) result.GroupedByCategory[cat.Label] = items;
        }

        return result;
    }

    private void RecomputeWeeklyMacros()
    {
        var total = new Macros();
        foreach (var mealName in AllScheduledMeals())
        {
            if (RecipesDb.TryGetValue(mealName, out var recipe) && recipe.Macros != null)
                total += recipe.Macros;
        }
        WeeklyMacros = total;
    }

    // Per-meal breakdown behind the weekly macros summary, day by day, in
    // the same order they're scheduled.
    public List<(string DayLabel, string MealName, Macros? Macros)> WeeklyMealBreakdown =>
        Days.SelectMany(day =>
            (MealPlan.TryGetValue(day.Key, out var meals) ? meals : new List<string>())
                .Where(m => !string.IsNullOrEmpty(m))
                .Select(m => (DayLabel: _loc.T($"day.{day.Label}"), MealName: m, Macros: RecipesDb.TryGetValue(m, out var r) ? r.Macros : null)))
            .ToList();

    // ======================================================================
    // PROMOTIONS — brochure products currently on offer, matched against the
    // ingredients still needed (i.e. not already in the pantry), grouped by
    // ingredient so the user can pick the store/brand themselves.
    // ======================================================================
    private async Task LoadPricesAsync()
    {
        PricesLoading = true;
        NotifyChange();

        if (!_supabase.IsConfigured)
        {
            PricesLoading = false;
            NotifyChange();
            return;
        }

        try
        {
            _priceRows = await _supabase.SelectAsync<PriceDataRow>("price_data", "select=ingredient_name,store_name,product_name,price,old_price,unit") ?? new();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Price data load error: {ex}");
        }

        PricesLoading = false;
        NotifyChange();
    }

    private List<PriceDataRow> _priceRows = new();

    private void RecomputePromotions()
    {
        if (_priceRows.Count == 0) { Promotions = new(); return; }

        var needed = GenerateShoppingList();
        var neededNames = needed.GroupedByCategory.Values.SelectMany(v => v).Select(i => i.DisplayName.ToLowerInvariant()).ToHashSet();

        Promotions = _priceRows
            .Where(r => neededNames.Contains(r.IngredientName.ToLowerInvariant()))
            .GroupBy(r => r.IngredientName.ToLowerInvariant())
            .Select(g => new PromoGroup
            {
                IngredientName = g.Key,
                Options = g
                    .OrderBy(r => r.StoreName).ThenBy(r => r.Price)
                    .Select(r => new PromoOption
                    {
                        StoreName = r.StoreName,
                        ProductName = r.ProductName,
                        Price = r.Price,
                        OldPrice = r.OldPrice,
                        Unit = r.Unit,
                    }).ToList(),
            })
            .OrderBy(g => g.IngredientName, StringComparer.Ordinal)
            .ToList();
    }

    // ======================================================================
    // SHARED
    // ======================================================================
    private long RequireHouseholdId() =>
        CurrentHouseholdId ?? throw new InvalidOperationException("No active household");
}
