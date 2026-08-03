namespace MealApp.Models;

public class Ingredient
{
    public string Name { get; set; } = "";
    public string Amount { get; set; } = "";
}

public class Macros
{
    public int? Calories { get; set; }
    public decimal? ProteinG { get; set; }
    public decimal? CarbsG { get; set; }
    public decimal? FatG { get; set; }

    public static Macros operator +(Macros a, Macros b) => new()
    {
        Calories = (a.Calories ?? 0) + (b.Calories ?? 0),
        ProteinG = (a.ProteinG ?? 0) + (b.ProteinG ?? 0),
        CarbsG = (a.CarbsG ?? 0) + (b.CarbsG ?? 0),
        FatG = (a.FatG ?? 0) + (b.FatG ?? 0),
    };
}

public class RecipeData
{
    public long? Id { get; set; }
    public long? HouseholdId { get; set; }
    public string? ShareCode { get; set; }
    public int? Servings { get; set; }
    public decimal? PortionGrams { get; set; }
    public string? PhotoUrl { get; set; }
    public string? Instructions { get; set; }
    public List<Ingredient> Ingredients { get; set; } = new();
    public Macros? Macros { get; set; }
}

public class PantryItem
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Amount { get; set; } = "";
}

public class DayInfo
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
}

public class ShoppingListIngredient
{
    public string DisplayName { get; set; } = "";
    public List<string> Amounts { get; } = new();
    public string Category { get; set; } = "";
}

public class ShoppingListResult
{
    public int MealCount { get; set; }
    public List<string> UnmatchedMeals { get; } = new();
    public Dictionary<string, List<ShoppingListIngredient>> GroupedByCategory { get; } = new();
    public List<ShoppingListIngredient> PantryMatched { get; } = new();
}

public class PromoOption
{
    public string StoreName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public decimal Price { get; set; }
    public decimal? OldPrice { get; set; }
    public string Unit { get; set; } = "";
}

public class PromoGroup
{
    public string IngredientName { get; set; } = "";
    public List<PromoOption> Options { get; set; } = new();
}

public class IngredientCategory
{
    public string Label { get; set; } = "";
    public string[] Keywords { get; set; } = Array.Empty<string>();
}

public enum DataSourceState { Loading, Local, Supabase, Error }
