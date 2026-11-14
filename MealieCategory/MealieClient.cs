using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MealieCategory;

internal sealed class MealieClient(HttpClient http)
{
    public async IAsyncEnumerable<MealieLabel> GetLabelsAsync()
    {
        await foreach (var label in GetItemsAsync<MealieLabel>("api/groups/labels", "label"))
        {
            if (label is null || string.IsNullOrWhiteSpace(label.Name))
            {
                throw new InvalidDataException("Mealie returned a label without a name.");
            }

            yield return label;
        }
    }

    public async IAsyncEnumerable<IngredientFood> GetFoodsAsync()
    {
        await foreach (var food in GetItemsAsync<IngredientFood>("api/foods", "food"))
        {
            if (food is null || string.IsNullOrWhiteSpace(food.Name))
            {
                throw new InvalidDataException("Mealie returned a food without a name.");
            }

            yield return food;
        }
    }

    public async IAsyncEnumerable<IngredientName> GetUnitsAsync()
    {
        await foreach (var unit in GetItemsAsync<IngredientName>("api/units", "unit"))
        {
            if (unit is null || string.IsNullOrWhiteSpace(unit.Name))
            {
                throw new InvalidDataException("Mealie returned a unit without a name.");
            }

            yield return unit;
        }
    }

    public async IAsyncEnumerable<Recipe> GetRecipesAsync()
    {
        await foreach (var summary in GetItemsAsync<RecipeSummary>("api/recipes", "recipe"))
        {
            if (summary is null || string.IsNullOrWhiteSpace(summary.Slug)
                || summary.Slug is "." or ".." || summary.Slug.Contains('/') || summary.Slug.Contains('\\'))
            {
                throw new InvalidDataException("Mealie returned a recipe with an invalid slug.");
            }

            var recipe = await http.GetFromJsonAsync<Recipe>($"api/recipes/{Uri.EscapeDataString(summary.Slug)}")
                ?? throw new InvalidDataException($"Mealie returned an empty recipe for '{summary.Slug}'.");
            if (string.IsNullOrWhiteSpace(recipe.Name) || recipe.RecipeIngredient is null
                || recipe.RecipeIngredient.Any(ingredient => ingredient is null))
            {
                throw new InvalidDataException($"Mealie returned invalid ingredient data for '{summary.Slug}'.");
            }

            yield return recipe;
        }
    }

    public async IAsyncEnumerable<(string Slug, JsonObject Document)> GetRecipeDocumentsAsync()
    {
        await foreach (var summary in GetItemsAsync<RecipeSummary>("api/recipes", "recipe"))
        {
            if (summary is null || string.IsNullOrWhiteSpace(summary.Slug)
                || summary.Slug is "." or ".." || summary.Slug.Contains('/') || summary.Slug.Contains('\\'))
                throw new InvalidDataException("Mealie returned a recipe with an invalid slug.");

            yield return (summary.Slug, await GetRecipeDocumentAsync(summary.Slug));
        }
    }

    public async Task<JsonObject> GetRecipeDocumentAsync(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug is "." or ".." || slug.Contains('/') || slug.Contains('\\'))
            throw new InvalidDataException("Invalid recipe slug in conversion proposal.");

        var document = await http.GetFromJsonAsync<JsonObject>($"api/recipes/{Uri.EscapeDataString(slug)}")
            ?? throw new InvalidDataException($"Mealie returned an empty recipe for '{slug}'.");
        if (document["recipeIngredient"] is not JsonArray || document["name"] is null)
            throw new InvalidDataException($"Mealie returned invalid ingredient data for '{slug}'.");
        return document;
    }

    public async Task<List<JsonObject>> GetCatalogAsync(string endpoint)
    {
        if (endpoint is not ("api/foods" or "api/units" or "api/groups/labels"))
            throw new ArgumentException("Unsupported catalog endpoint.", nameof(endpoint));

        var items = new List<JsonObject>();
        await foreach (var item in GetItemsAsync<JsonObject>(endpoint, endpoint))
        {
            if (item is null || string.IsNullOrWhiteSpace(item["id"]?.GetValue<string>())
                || string.IsNullOrWhiteSpace(item["name"]?.GetValue<string>()))
                throw new InvalidDataException($"Mealie returned an invalid item from {endpoint}.");
            items.Add(item);
        }
        return items;
    }

    public async Task<JsonObject> CreateCatalogItemAsync(string endpoint, string name)
    {
        using var response = await http.PostAsJsonAsync(endpoint, new { name });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonObject>()
            ?? throw new InvalidDataException($"Mealie returned an empty response when creating {name}.");
    }

    public async Task<JsonObject> GetFoodDocumentAsync(string id)
    {
        var food = await http.GetFromJsonAsync<JsonObject>($"api/foods/{Uri.EscapeDataString(id)}")
            ?? throw new InvalidDataException("Mealie returned an empty food.");
        if (string.IsNullOrWhiteSpace(food["id"]?.GetValue<string>()) || string.IsNullOrWhiteSpace(food["name"]?.GetValue<string>()))
            throw new InvalidDataException("Mealie returned an invalid food.");
        return food;
    }

    // Mealie replaces the whole food on PUT, so callers must send the full existing document.
    public async Task UpdateFoodAsync(string id, JsonObject food)
    {
        using var response = await http.PutAsJsonAsync($"api/foods/{Uri.EscapeDataString(id)}", food);
        response.EnsureSuccessStatusCode();
    }

    public async Task PatchRecipeAsync(string slug, JsonArray ingredients, JsonObject settings)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"api/recipes/{Uri.EscapeDataString(slug)}")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["recipeIngredient"] = ingredients.DeepClone(),
                ["settings"] = settings.DeepClone()
            })
        };
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async IAsyncEnumerable<T> GetItemsAsync<T>(string endpoint, string description)
    {
        var pageNumber = 1;
        int totalPages;
        do
        {
            var page = await http.GetFromJsonAsync<MealiePage<T>>($"{endpoint}?page={pageNumber}&perPage=100")
                ?? throw new InvalidDataException($"Mealie returned an empty {description} list response.");
            if (page.Items is null || page.TotalPages < 0 || (page.TotalPages == 0 && page.Items.Count > 0))
            {
                throw new InvalidDataException($"Mealie returned invalid {description} pagination data.");
            }

            totalPages = page.TotalPages;
            foreach (var item in page.Items)
            {
                yield return item;
            }

            pageNumber++;
        } while (pageNumber <= totalPages);
    }
}

internal sealed class MealiePage<T>
{
    [JsonPropertyName("items")]
    public required List<T> Items { get; init; }

    [JsonPropertyName("total_pages")]
    public required int TotalPages { get; init; }
}

internal sealed class MealieLabel
{
    public required string Name { get; init; }
    public string? Id { get; init; }
}

internal sealed class RecipeSummary
{
    public required string Slug { get; init; }
}

internal sealed class Recipe
{
    public required string Name { get; init; }
    public required List<Ingredient> RecipeIngredient { get; init; }
}

internal sealed class Ingredient
{
    public string? Display { get; init; }
    public string? OriginalText { get; init; }
    public string? Note { get; init; }
    public string? Title { get; init; }
    public decimal? Quantity { get; init; }
    public bool? IsFood { get; init; }
    public bool? DisableAmount { get; init; }
    public IngredientFood? Food { get; init; }
    public IngredientName? Unit { get; init; }
    public ReferencedRecipe? ReferencedRecipe { get; init; }

    // Older Mealie versions default free-form entries to a hidden quantity of one.
    public decimal? EffectiveQuantity =>
        Quantity == 1 && (DisableAmount == true || IsFood == false) ? null : Quantity;

    public IngredientKind Kind =>
        ReferencedRecipe is not null ? IngredientKind.ReferencedRecipe
        : Food is not null ? Unit is not null ? IngredientKind.FoodWithUnit : IngredientKind.FoodWithoutUnit
        : Unit is not null ? IngredientKind.UnitWithoutFood
        : EffectiveQuantity is not null and not 0 ? IngredientKind.QuantityWithoutFoodOrUnit
        : !string.IsNullOrWhiteSpace(Note) || !string.IsNullOrWhiteSpace(OriginalText)
            || !string.IsNullOrWhiteSpace(Display) ? IngredientKind.FreeForm
        : !string.IsNullOrWhiteSpace(Title) ? IngredientKind.SectionHeading
        : IngredientKind.Empty;

    public string ToDisplayString()
    {
        var label = GetLabel();
        return $"{ToIngredientText()} [Label: {label?.DisplayName ?? "none"}]";
    }

    public string ToIngredientText()
    {
        var parts = new[]
        {
            EffectiveQuantity is not null and not 0 ? EffectiveQuantity.Value.ToString(CultureInfo.InvariantCulture) : null,
            Unit?.Name,
            ReferencedRecipe?.Name ?? Food?.Name,
            Note,
            Title
        }.Where(part => !string.IsNullOrWhiteSpace(part));
        var fallback = string.Join(" ", parts);
        var text = !string.IsNullOrWhiteSpace(Display) ? Display
            : !string.IsNullOrWhiteSpace(OriginalText) ? OriginalText
            : fallback.Length > 0 ? fallback : "(empty ingredient)";

        return text;
    }

    public IngredientLabel? GetLabel()
    {
        if (Food?.Label is not null && string.IsNullOrWhiteSpace(Food.Label.Name))
        {
            throw new InvalidDataException("Mealie returned a food label without a name.");
        }

        var id = !string.IsNullOrWhiteSpace(Food?.LabelId) ? Food.LabelId : Food?.Label?.Id;
        var name = Food?.Label?.Name;
        return string.IsNullOrWhiteSpace(id) && name is null ? null : new IngredientLabel(id, name);
    }
}

internal enum IngredientKind
{
    FoodWithUnit,
    FoodWithoutUnit,
    UnitWithoutFood,
    QuantityWithoutFoodOrUnit,
    FreeForm,
    ReferencedRecipe,
    SectionHeading,
    Empty
}

internal static class IngredientKindExtensions
{
    public static string DisplayName(this IngredientKind kind) => kind switch
    {
        IngredientKind.FoodWithUnit => "Food with unit",
        IngredientKind.FoodWithoutUnit => "Food without unit",
        IngredientKind.UnitWithoutFood => "Unit without food",
        IngredientKind.QuantityWithoutFoodOrUnit => "Quantity without food or unit",
        IngredientKind.FreeForm => "Free form",
        IngredientKind.ReferencedRecipe => "Referenced recipe",
        IngredientKind.SectionHeading => "Section heading only",
        IngredientKind.Empty => "Empty entry",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

internal sealed class ReferencedRecipe
{
    public string? Name { get; init; }
}

internal sealed record IngredientLabel(string? Id, string? Name)
{
    public string DisplayName => Name ?? $"ID {Id} (name unavailable)";
}

internal sealed class IngredientFood
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? LabelId { get; init; }
    public IngredientName? Label { get; init; }
}

internal sealed class IngredientName
{
    public string? Id { get; init; }
    public string? Name { get; init; }
}
