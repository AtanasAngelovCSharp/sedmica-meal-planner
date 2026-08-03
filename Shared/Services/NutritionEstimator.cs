using MealApp.Models;

namespace MealApp.Services;

// Estimates a recipe's per-serving macros from its ingredient list, for
// recipes that don't already carry hand-curated macro numbers (i.e. every
// household-created recipe — the built-in library ships its own figures).
// This is a smart estimate driven by real per-100g nutrition reference
// values and Bulgarian cooking-unit conversions, not a lab measurement —
// good enough to make the weekly totals meaningfully accurate instead of
// silently showing 0 for anything the household added themselves.
public static class NutritionEstimator
{
    private readonly record struct Density(double Kcal, double Protein, double Carbs, double Fat);

    // Per 100g edible portion. Order matters — the first substring match
    // wins, so more specific phrases (e.g. "кисело мляко") must come before
    // the bare word they contain (e.g. "мляко").
    private static readonly (string Keyword, Density D)[] DensityTable =
    {
        ("кисело зеле", new Density(19, 0.9, 4.3, 0.1)),
        ("зеле", new Density(25, 1.3, 5.8, 0.1)),
        ("кисело мляко", new Density(66, 3.5, 4.7, 3.3)),
        ("прясно мляко", new Density(61, 3.2, 4.8, 3.3)),
        ("мляко", new Density(61, 3.2, 4.8, 3.3)),
        ("краве масло", new Density(717, 0.9, 0.1, 81)),
        ("масло", new Density(717, 0.9, 0.1, 81)),
        ("олио", new Density(884, 0, 0, 100)),
        ("зехтин", new Density(884, 0, 0, 100)),
        ("краве сирене", new Density(265, 17, 3, 21)),
        ("сирене", new Density(265, 17, 3, 21)),
        ("кашкавал", new Density(350, 25, 2, 27)),
        ("жълтъ", new Density(322, 16, 3.6, 27)),
        ("яйц", new Density(143, 13, 1.1, 10)),
        ("телешко шкембе", new Density(95, 15, 0, 4)),
        ("шкембе", new Density(95, 15, 0, 4)),
        ("телешки крачета", new Density(172, 24, 0, 8)),
        ("агнешко", new Density(294, 25, 0, 21)),
        ("свинско", new Density(242, 27, 0, 14)),
        ("пържол", new Density(231, 25, 0, 14)),
        ("пилешко", new Density(165, 31, 0, 3.6)),
        ("пиле", new Density(165, 31, 0, 3.6)),
        ("скумрия", new Density(205, 19, 0, 14)),
        ("шунка", new Density(145, 21, 1.5, 5)),
        ("кайма", new Density(250, 17, 0, 20)),
        ("домат", new Density(18, 0.9, 3.9, 0.2)),
        ("краставиц", new Density(15, 0.7, 3.6, 0.1)),
        ("чушк", new Density(31, 1, 6, 0.3)),
        ("морков", new Density(41, 0.9, 10, 0.2)),
        ("чесън", new Density(149, 6.4, 33, 0.5)),
        ("магданоз", new Density(36, 3, 6.3, 0.8)),
        ("копър", new Density(43, 3.5, 7, 1.1)),
        ("спанак", new Density(23, 2.9, 3.6, 0.4)),
        ("картоф", new Density(77, 2, 17, 0.1)),
        ("гъб", new Density(22, 3.1, 3.3, 0.3)),
        ("грах", new Density(81, 5.4, 14, 0.4)),
        ("царевиц", new Density(86, 3.3, 19, 1.2)),
        ("маслин", new Density(115, 0.8, 6, 11)),
        ("лимон", new Density(29, 1.1, 9, 0.3)),
        ("цвекло", new Density(43, 1.6, 10, 0.2)),
        ("тиква", new Density(26, 1, 6.5, 0.1)),
        ("лук", new Density(40, 1.1, 9.3, 0.1)),
        ("ориз", new Density(365, 7.1, 80, 0.7)),
        ("брашно", new Density(364, 10, 76, 1)),
        ("хляб", new Density(265, 9, 49, 3.2)),
        ("фиде", new Density(371, 13, 75, 1.5)),
        ("кори за баница", new Density(300, 9, 55, 5)),
        ("тесто", new Density(280, 8, 55, 3)),
        ("боб", new Density(333, 24, 60, 1.2)),
        ("леща", new Density(353, 25, 60, 1.1)),
        ("мая", new Density(105, 8, 41, 2)),
        ("захар", new Density(387, 0, 100, 0)),
        ("оцет", new Density(19, 0, 0.9, 0)),
        ("орех", new Density(654, 15, 14, 65)),
        ("бульон", new Density(250, 8, 15, 15)),
        ("сол", new Density(0, 0, 0, 0)),
        ("сода бикарбонат", new Density(0, 0, 0, 0)),
        // Dried herbs/spices are used in gram-scale amounts too small to move
        // a recipe's total macros much either way; the exact density mostly
        // doesn't matter, it's here so the ingredient isn't left unmatched.
        ("пипер", new Density(280, 11, 60, 4)),
        ("канела", new Density(247, 4, 81, 1.2)),
        ("ванилия", new Density(288, 0, 13, 0)),
        ("чубрица", new Density(300, 10, 60, 8)),
        ("риган", new Density(265, 9, 69, 4)),
        ("мащерка", new Density(276, 9, 64, 7)),
        ("девесил", new Density(300, 10, 60, 5)),
        ("джоджен", new Density(70, 3.8, 15, 0.9)),
        ("дафинов лист", new Density(313, 8, 75, 8)),
        ("подправка", new Density(50, 3, 10, 1)),
    };

    // Short/ambiguous unit tokens — matched only on a whole-token boundary
    // (the char right after the match can't itself be a letter), so "г"
    // doesn't accidentally match inside "глава".
    private static readonly (string Unit, double Grams)[] BoundaryUnits =
    {
        ("кг", 1000), ("литър", 1000), ("литра", 1000), ("л", 1000),
        ("мл", 1), ("гр", 1), ("г", 1),
        ("ч.л", 5), ("с.л", 15), ("ч.ч", 200),
    };

    // Longer, unambiguous stems (covers both singular/plural Bulgarian
    // endings, e.g. "скилидка"/"скилидки") — safe to match as a plain prefix.
    private static readonly (string Stem, double Grams)[] StemUnits =
    {
        ("щипк", 1), ("връзк", 50), ("шеп", 30), ("скилидк", 5),
        ("пакетче", 10), ("кубче", 10), ("купичк", 200),
    };

    // "2 бр.", "1 глава"... — default single-item weight per ingredient
    // when no measuring unit is recognized at all.
    private static readonly (string Keyword, double Grams)[] PieceWeights =
    {
        ("чесън", 40),   // "1 глава" of garlic
        ("лук", 110),    // "1 глава" of onion
        ("домат", 120),
        ("краставиц", 100),
        ("чушк", 120),
        ("картоф", 150),
        ("яйц", 55),
        ("лимон", 60),
        ("магданоз", 2),
        ("копър", 2),
        ("скумрия", 300),
        ("пиле", 1200),
        ("хляб", 25),
    };

    private const double DefaultPieceWeight = 80;
    private const double DefaultServings = 4;

    public static Macros Estimate(IReadOnlyList<Ingredient> ingredients, int? servings)
    {
        double kcal = 0, protein = 0, carbs = 0, fat = 0;
        foreach (var ing in ingredients)
        {
            var grams = EstimateGrams(ing.Amount, ing.Name);
            if (grams <= 0) continue;
            var density = FindDensity(ing.Name);
            var factor = grams / 100.0;
            kcal += density.Kcal * factor;
            protein += density.Protein * factor;
            carbs += density.Carbs * factor;
            fat += density.Fat * factor;
        }

        var portions = servings is > 0 ? servings.Value : DefaultServings;
        return new Macros
        {
            Calories = (int)Math.Round(kcal / portions),
            ProteinG = Math.Round((decimal)(protein / portions), 1),
            CarbsG = Math.Round((decimal)(carbs / portions), 1),
            FatG = Math.Round((decimal)(fat / portions), 1),
        };
    }

    private static Density FindDensity(string ingredientName)
    {
        var lower = ingredientName.ToLowerInvariant();
        foreach (var (keyword, density) in DensityTable)
        {
            if (lower.Contains(keyword)) return density;
        }
        return new Density(50, 2, 8, 1); // unrecognized ingredient: modest generic estimate
    }

    private static double EstimateGrams(string amount, string ingredientName)
    {
        if (string.IsNullOrWhiteSpace(amount)) return 0;
        var text = amount.ToLowerInvariant();

        // Strip parenthetical asides ("1 бр. (~1.5 кг)", "3 бр. (сос)") — they
        // usually just annotate which sub-step the ingredient belongs to.
        var parenIdx = text.IndexOf('(');
        if (parenIdx >= 0) text = text[..parenIdx];
        text = text.Trim();
        if (text.Length == 0) return 0;

        // Vague, non-measured mentions contribute nothing rather than a wild
        // guess ("на вкус", "по желание"...) — except a fat/oil used "за
        // пържене/печене/намазване" (frying/baking/greasing), which is a real,
        // sizeable ingredient too common to zero out without badly
        // underestimating fried and oven dishes.
        if (text.StartsWith("за ") && (text.Contains("пържене") || text.Contains("печене") || text.Contains("намазване")))
        {
            var lowerFatName = ingredientName.ToLowerInvariant();
            if (lowerFatName.Contains("олио") || lowerFatName.Contains("масло") || lowerFatName.Contains("зехтин"))
                return 30;
            return 0;
        }
        if (text.StartsWith("за ") || text.StartsWith("по ") || text.Contains("вкус"))
            return 0;

        var quantity = ParseLeadingQuantity(text, out var rest);
        if (quantity <= 0) return 0;
        rest = rest.Trim();

        foreach (var (stem, grams) in StemUnits)
            if (rest.StartsWith(stem, StringComparison.Ordinal)) return quantity * grams;

        foreach (var (unit, grams) in BoundaryUnits)
            if (MatchesUnit(rest, unit)) return quantity * grams;

        // No recognized unit ("2 бр.", "1 глава", "6 риби"...) — treat the
        // number as a piece count using the ingredient's typical item weight.
        var lowerName = ingredientName.ToLowerInvariant();
        foreach (var (keyword, grams) in PieceWeights)
            if (lowerName.Contains(keyword)) return quantity * grams;

        return quantity * DefaultPieceWeight;
    }

    private static bool MatchesUnit(string rest, string unit) =>
        rest.StartsWith(unit, StringComparison.Ordinal) &&
        (rest.Length == unit.Length || !char.IsLetter(rest[unit.Length]));

    // Reads a leading quantity from a Bulgarian recipe amount: a plain or
    // decimal number ("500", "1.5"), a simple or mixed fraction ("1/2",
    // "1 1/2"), or a range ("4-5", "10-12") — ranges use their midpoint.
    // Returns -1 (via `rest` unchanged) when there's no leading number.
    private static double ParseLeadingQuantity(string text, out string rest)
    {
        var i = 0;
        double ReadNumber()
        {
            var start = i;
            while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
            return start == i ? -1 : double.Parse(text[start..i], System.Globalization.CultureInfo.InvariantCulture);
        }

        var first = ReadNumber();
        if (first < 0) { rest = text; return -1; }
        var afterFirst = i;

        // "1 1/2" — mixed fraction.
        while (i < text.Length && text[i] == ' ') i++;
        if (i < text.Length && char.IsDigit(text[i]))
        {
            var num2 = ReadNumber();
            if (i < text.Length && text[i] == '/')
            {
                i++;
                var den = ReadNumber();
                if (num2 >= 0 && den > 0)
                {
                    rest = text[i..];
                    return first + num2 / den;
                }
            }
        }
        i = afterFirst;

        // "1/2" — simple fraction right after the leading number.
        if (i < text.Length && text[i] == '/')
        {
            i++;
            var den = ReadNumber();
            if (den > 0)
            {
                rest = text[i..];
                return first / den;
            }
            i = afterFirst;
        }

        // "4-5" / "10-12" — range, take the midpoint.
        if (i < text.Length && text[i] == '-')
        {
            i++;
            var second = ReadNumber();
            if (second > 0)
            {
                rest = text[i..];
                return (first + second) / 2.0;
            }
            i = afterFirst;
        }

        rest = text[i..];
        return first;
    }
}
