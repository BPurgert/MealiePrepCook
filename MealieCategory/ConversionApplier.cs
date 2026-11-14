using System.Text.Json;
using System.Text.Json.Nodes;

namespace MealieCategory;

internal sealed class ConversionApplier(MealieClient mealie)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ApplyAsync(ConversionPlan plan, string mealieUrl)
    {
        if (plan.Version != ConversionPlan.CurrentVersion)
            throw new InvalidDataException("This proposal uses an older or unsupported format. Generate a new proposal before applying.");
        if (!string.Equals(plan.MealieUrl, mealieUrl, StringComparison.Ordinal))
            throw new InvalidDataException("The proposal was created for a different Mealie URL.");
        if (plan.Recipes is null || plan.Recipes.Any(recipe => recipe is null || recipe.Proposals is null
            || recipe.OriginalIngredients is null || string.IsNullOrWhiteSpace(recipe.RecipeId)
            || recipe.Proposals.Any(proposal => proposal is null)))
            throw new InvalidDataException("The proposal has invalid recipe data.");

        foreach (var recipe in plan.Recipes)
            foreach (var skipped in recipe.Proposals.Where(proposal => proposal.Approved && IsSkipped(proposal)))
                Console.WriteLine($"Skipping approved ingredient {skipped.IngredientIndex} in recipe '{recipe.Name}' ({recipe.Slug}): {skipped.SkipReason} [{skipped.Original}]");

        var selected = plan.Recipes.Where(recipe => recipe.Proposals.Any(IsApplicable)).ToList();
        if (selected.Count == 0)
            throw new InvalidDataException("No applicable proposals. Set approved to true on entries without a skipReason.");
        if (selected.Select(recipe => recipe.Slug).Distinct(StringComparer.Ordinal).Count() != selected.Count)
            throw new InvalidDataException("The proposal contains duplicate recipes.");

        var foods = await mealie.GetCatalogAsync("api/foods");
        var units = await mealie.GetCatalogAsync("api/units");
        var prepared = new List<(ConversionRecipe Recipe, JsonArray Ingredients)>();
        foreach (var recipe in selected)
        {
            var current = await mealie.GetRecipeDocumentAsync(recipe.Slug);
            if (current["id"]?.GetValue<string>() != recipe.RecipeId
                || !JsonNode.DeepEquals(current["recipeIngredient"], recipe.OriginalIngredients))
            {
                throw new InvalidDataException($"Recipe '{recipe.Slug}' changed since the proposal was created. Generate a new proposal.");
            }

            var ingredients = (JsonArray)current["recipeIngredient"]!.DeepClone();
            var seen = new HashSet<int>();
            foreach (var proposal in recipe.Proposals.Where(IsApplicable))
            {
                if (!seen.Add(proposal.IngredientIndex) || proposal.IngredientIndex < 0
                    || proposal.IngredientIndex >= ingredients.Count
                    || ingredients[proposal.IngredientIndex] is not JsonObject node)
                {
                    throw new InvalidDataException($"Invalid ingredient index in '{recipe.Slug}'.");
                }
                ValidateProposal(proposal, node, foods, units);
            }
            prepared.Add((recipe, ingredients));
        }

        foreach (var (recipe, ingredients) in prepared)
        {
            foreach (var proposal in recipe.Proposals.Where(IsApplicable))
            {
                var ingredient = (JsonObject)ingredients[proposal.IngredientIndex]!;
                var original = ingredient.Deserialize<Ingredient>(JsonOptions)!;
                if (original.Food is null)
                {
                    ingredient["food"] = (await ResolveAsync("api/foods", proposal.FoodId, proposal.NewFoodName, foods)).DeepClone();
                }
                if (original.Unit is null && (proposal.UnitId is not null || proposal.NewUnitName is not null))
                {
                    ingredient["unit"] = (await ResolveAsync("api/units", proposal.UnitId, proposal.NewUnitName, units)).DeepClone();
                }
                if (original.EffectiveQuantity is null or 0 && (proposal.Quantity is not null || original.Quantity == 1))
                    ingredient["quantity"] = proposal.Quantity;
                if (ingredient.ContainsKey("isFood"))
                    ingredient["isFood"] = true;
                if (ingredient.ContainsKey("disableAmount"))
                    ingredient["disableAmount"] = false;
                if (ingredient["originalText"] is null || string.IsNullOrWhiteSpace(ingredient["originalText"]?.GetValue<string>()))
                    ingredient["originalText"] = !string.IsNullOrWhiteSpace(original.Note) ? original.Note : original.Display;
                ingredient["note"] = proposal.Note;
                ingredient["display"] = "";
            }

            var latest = await mealie.GetRecipeDocumentAsync(recipe.Slug);
            if (latest["id"]?.GetValue<string>() != recipe.RecipeId
                || !JsonNode.DeepEquals(latest["recipeIngredient"], recipe.OriginalIngredients))
                throw new InvalidDataException($"Recipe '{recipe.Slug}' changed during application; it was not updated.");

            // Mealie shows only each ingredient's note while the recipe-level disableAmount
            // setting is true, so structured ingredients appear blank unless it is turned off.
            if (latest["settings"] is not JsonObject settings)
                throw new InvalidDataException($"Recipe '{recipe.Slug}' has no settings; it was not updated.");
            settings = (JsonObject)settings.DeepClone();
            settings["disableAmount"] = false;
            foreach (var entry in ingredients.OfType<JsonObject>())
            {
                // Unconverted entries keep their text in note; drop the hidden default quantity
                // so it does not appear in front of that text once amounts are shown.
                var remaining = entry.Deserialize<Ingredient>(JsonOptions);
                if (remaining is { Food: null, Unit: null, Quantity: not null, EffectiveQuantity: null })
                    entry["quantity"] = 0;
            }

            await mealie.PatchRecipeAsync(recipe.Slug, ingredients, settings);
            var verified = await mealie.GetRecipeDocumentAsync(recipe.Slug);
            if (verified["settings"]?["disableAmount"]?.GetValue<bool>() != false)
                Console.WriteLine($"WARNING: Mealie did not turn off 'disable ingredient amounts' for '{recipe.Name}'; its ingredients may display blank.");
            Console.WriteLine($"Updated recipe: {recipe.Name}");
        }
    }

    private static bool IsSkipped(ConversionProposal proposal) => !string.IsNullOrWhiteSpace(proposal.SkipReason);

    private static bool IsApplicable(ConversionProposal proposal) => proposal.Approved && !IsSkipped(proposal);

    private static void ValidateProposal(ConversionProposal proposal, JsonObject node, List<JsonObject> foods, List<JsonObject> units)
    {
        var original = node.Deserialize<Ingredient>(JsonOptions)
            ?? throw new InvalidDataException("Invalid ingredient in proposal.");
        if (original.Kind is not (IngredientKind.FreeForm or IngredientKind.UnitWithoutFood
            or IngredientKind.QuantityWithoutFoodOrUnit or IngredientKind.FoodWithoutUnit))
            throw new InvalidDataException("An approved entry is no longer eligible for conversion.");
        if (!string.IsNullOrWhiteSpace(proposal.SkipReason))
            throw new InvalidDataException("A skipped proposal cannot be approved.");
        ValidateSelection(proposal.FoodId, proposal.FoodName, proposal.NewFoodName, foods, "food");
        ValidateSelection(proposal.UnitId, proposal.UnitName, proposal.NewUnitName, units, "unit");
        if (original.Food is null && proposal.FoodId is null && proposal.NewFoodName is null)
            throw new InvalidDataException("An approved proposal must identify a food.");
        if (original.Food is not null && (proposal.FoodId != original.Food.Id || proposal.NewFoodName is not null))
            throw new InvalidDataException("A proposal cannot replace an existing food.");
        if (original.Unit is not null && (proposal.UnitId != original.Unit.Id || proposal.NewUnitName is not null))
            throw new InvalidDataException("A proposal cannot replace an existing unit.");
        if (proposal.Quantity is < 0 or > 1000000
            || (original.EffectiveQuantity is not null and not 0 && proposal.Quantity is not null && original.EffectiveQuantity != proposal.Quantity))
            throw new InvalidDataException("A proposal has an invalid or changed quantity.");
    }

    private static void ValidateSelection(string? id, string? name, string? newName, List<JsonObject> catalog, string kind)
    {
        if (id is not null && newName is not null)
            throw new InvalidDataException($"A proposal cannot select and create a {kind}.");
        if (newName is not null && (string.IsNullOrWhiteSpace(newName) || newName.Length > 200
            || newName.Contains('\n') || newName.Contains('\r')))
            throw new InvalidDataException($"A proposal has an invalid new {kind} name.");
        if (newName is not null && catalog.Count(item =>
                string.Equals(item["name"]?.GetValue<string>(), newName.Trim(), StringComparison.OrdinalIgnoreCase)) > 1)
            throw new InvalidDataException($"Multiple existing {kind} entries match '{newName}'. Select an existing ID instead.");
        if (id is not null)
        {
            var item = catalog.SingleOrDefault(item => string.Equals(item["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase));
            if (item is null || !string.Equals(item["name"]?.GetValue<string>(), name, StringComparison.Ordinal))
                throw new InvalidDataException($"The selected {kind} no longer matches the proposal.");
        }
        else if (name is not null)
        {
            throw new InvalidDataException($"A selected {kind} is missing its ID.");
        }
    }

    private async Task<JsonObject> ResolveAsync(string endpoint, string? id, string? newName, List<JsonObject> catalog)
    {
        var existing = id is not null
            ? catalog.Single(item => string.Equals(item["id"]?.GetValue<string>(), id, StringComparison.OrdinalIgnoreCase))
            : catalog.FirstOrDefault(item => string.Equals(item["name"]?.GetValue<string>(), newName?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var created = await mealie.CreateCatalogItemAsync(endpoint, newName!.Trim());
        if (string.IsNullOrWhiteSpace(created["id"]?.GetValue<string>()) || string.IsNullOrWhiteSpace(created["name"]?.GetValue<string>()))
            throw new InvalidDataException($"Mealie returned an invalid new item from {endpoint}.");
        catalog.Add(created);
        Console.WriteLine($"Created {endpoint["api/".Length..]} item: {created["name"]!.GetValue<string>()}");
        return created;
    }
}
