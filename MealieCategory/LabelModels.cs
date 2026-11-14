namespace MealieCategory;

internal sealed class LabelPlan
{
    public const int CurrentVersion = 1;
    public int Version { get; init; }
    public required string MealieUrl { get; init; }
    public required List<LabelProposal> Foods { get; init; }
}

internal sealed class LabelProposal
{
    public bool Approved { get; set; }
    public required string FoodId { get; init; }
    public required string FoodName { get; init; }
    public string? LabelId { get; init; }
    public string? LabelName { get; init; }
    public string? SkipReason { get; init; }
}

internal sealed record LabelFoodInput(int Index, string Name);

public sealed class LabelBatchResponse
{
    public required List<LabelBatchItem> Items { get; init; }
}

public sealed class LabelBatchItem
{
    public required int Index { get; init; }
    public required string? LabelCode { get; init; }
    public required string? SkipReason { get; init; }
}
