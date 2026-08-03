using System.Text.Json.Serialization;

namespace MealApp.Models;

// Row shapes matching the Supabase (PostgREST) table columns exactly —
// property names are mapped via JsonPropertyName to the snake_case column names.

public class HouseholdRow
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("invite_code")] public string? InviteCode { get; set; }
}

public class HouseholdMembershipRow
{
    [JsonPropertyName("household_id")] public long HouseholdId { get; set; }
}

public class RecipeRow
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("household_id")] public long? HouseholdId { get; set; }
    [JsonPropertyName("share_code")] public string? ShareCode { get; set; }
    [JsonPropertyName("servings")] public int? Servings { get; set; }
    [JsonPropertyName("portion_grams")] public decimal? PortionGrams { get; set; }
    [JsonPropertyName("photo_url")] public string? PhotoUrl { get; set; }
    [JsonPropertyName("instructions")] public string? Instructions { get; set; }
    [JsonPropertyName("calories")] public int? Calories { get; set; }
    [JsonPropertyName("protein_g")] public decimal? ProteinG { get; set; }
    [JsonPropertyName("carbs_g")] public decimal? CarbsG { get; set; }
    [JsonPropertyName("fat_g")] public decimal? FatG { get; set; }
}

public class RecipeInsert
{
    [JsonPropertyName("household_id")] public long HouseholdId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("servings")] public int? Servings { get; set; }
    [JsonPropertyName("portion_grams")] public decimal? PortionGrams { get; set; }
    [JsonPropertyName("photo_url")] public string? PhotoUrl { get; set; }
    [JsonPropertyName("instructions")] public string? Instructions { get; set; }
}

public class RecipeIngredientRow
{
    [JsonPropertyName("recipe_id")] public long RecipeId { get; set; }
    [JsonPropertyName("ingredient_name")] public string IngredientName { get; set; } = "";
    [JsonPropertyName("amount")] public string Amount { get; set; } = "";
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
}

public class RecipeIngredientInsert
{
    [JsonPropertyName("recipe_id")] public long RecipeId { get; set; }
    [JsonPropertyName("ingredient_name")] public string IngredientName { get; set; } = "";
    [JsonPropertyName("amount")] public string Amount { get; set; } = "";
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
}

public class PantryItemRow
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("household_id")] public long HouseholdId { get; set; }
    [JsonPropertyName("ingredient_name")] public string IngredientName { get; set; } = "";
    [JsonPropertyName("amount")] public string Amount { get; set; } = "";
}

public class PantryItemInsert
{
    [JsonPropertyName("household_id")] public long HouseholdId { get; set; }
    [JsonPropertyName("ingredient_name")] public string IngredientName { get; set; } = "";
    [JsonPropertyName("amount")] public string Amount { get; set; } = "";
}

public class MealPlanEntryRow
{
    [JsonPropertyName("day_key")] public string DayKey { get; set; } = "";
    [JsonPropertyName("meal_type")] public string MealType { get; set; } = "";
    [JsonPropertyName("meal_name")] public string MealName { get; set; } = "";
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
}

public class MealPlanEntryInsert
{
    [JsonPropertyName("household_id")] public long HouseholdId { get; set; }
    [JsonPropertyName("week_start")] public string WeekStart { get; set; } = "";
    [JsonPropertyName("day_key")] public string DayKey { get; set; } = "";
    [JsonPropertyName("meal_type")] public string MealType { get; set; } = "";
    [JsonPropertyName("meal_name")] public string MealName { get; set; } = "";
    [JsonPropertyName("sort_order")] public int SortOrder { get; set; }
}

public class FavoriteRecipeRow
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("recipe_name")] public string RecipeName { get; set; } = "";
}

public class FavoriteRecipeInsert
{
    [JsonPropertyName("household_id")] public long HouseholdId { get; set; }
    [JsonPropertyName("recipe_name")] public string RecipeName { get; set; } = "";
}

public class SubscriptionRow
{
    [JsonPropertyName("status")] public string Status { get; set; } = "trial";
    [JsonPropertyName("trial_ends_at")] public DateTimeOffset? TrialEndsAt { get; set; }
    [JsonPropertyName("current_period_end")] public DateTimeOffset? CurrentPeriodEnd { get; set; }
}

public class PriceDataRow
{
    [JsonPropertyName("ingredient_name")] public string IngredientName { get; set; } = "";
    [JsonPropertyName("store_name")] public string StoreName { get; set; } = "";
    [JsonPropertyName("product_name")] public string ProductName { get; set; } = "";
    [JsonPropertyName("price")] public decimal Price { get; set; }
    [JsonPropertyName("old_price")] public decimal? OldPrice { get; set; }
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
}
