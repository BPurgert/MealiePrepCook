using System.Text.Json.Nodes;

namespace MealieCategory;

internal sealed class ConversionPlan
{
    public const int CurrentVersion = 2;
    public int Version { get; init; }
    public required string MealieUrl { get; init; }
    public required List<ConversionRecipe> Recipes { get; init; }
}

internal sealed class ConversionRecipe
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required string RecipeId { get; init; }
    public required JsonArray OriginalIngredients { get; init; }
    public required List<ConversionProposal> Proposals { get; init; }
}

internal sealed class ConversionProposal
{
    public bool Approved { get; set; }
    public required int IngredientIndex { get; init; }
    public required string Original { get; init; }
    public string? FoodId { get; init; }
    public string? FoodName { get; init; }
    public string? NewFoodName { get; init; }
    public string? UnitId { get; init; }
    public string? UnitName { get; init; }
    public string? NewUnitName { get; init; }
    public decimal? Quantity { get; init; }
    public string? Note { get; init; }
    public string? SkipReason { get; init; }
}

internal sealed record ConversionInput(
    int Index, string? OriginalText, string? Display, string? Note,
    decimal? Quantity, string? FoodCode, string? UnitCode);

public sealed class ConversionBatchResponse
{
    public required List<ConversionBatchItem> Items { get; init; }
}

public sealed class ConversionBatchItem
{
    public required int Index { get; init; }
    public required string? FoodCode { get; init; }
    public required string? NewFoodName { get; init; }
    public required string? UnitCode { get; init; }
    public required string? NewUnitName { get; init; }
    public required decimal? Quantity { get; init; }
    public required string? Note { get; init; }
    public required string? SkipReason { get; init; }
}
