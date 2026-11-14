namespace MealieCategory;

internal sealed class IngredientLabelCounts(IEnumerable<MealieLabel> labels)
{
    private readonly List<LabelCount> counts = labels
        .Select(label => new LabelCount(new IngredientLabel(label.Id, label.Name)))
        .ToList();

    public IReadOnlyList<LabelCount> Labels => counts;
    public int NoneCount { get; private set; }

    public void Add(Ingredient ingredient)
    {
        var label = ingredient.GetLabel();
        if (label is null)
        {
            NoneCount++;
            return;
        }

        var count = !string.IsNullOrWhiteSpace(label.Id)
            ? counts.FirstOrDefault(item => string.Equals(label.Id, item.Label.Id, StringComparison.OrdinalIgnoreCase))
            : null;
        count ??= counts.FirstOrDefault(item =>
            (string.IsNullOrWhiteSpace(label.Id) || string.IsNullOrWhiteSpace(item.Label.Id))
            && label.Name is not null && string.Equals(label.Name, item.Label.Name, StringComparison.Ordinal));
        if (count is null)
        {
            count = new LabelCount(label);
            counts.Add(count);
        }
        else if (count.Label.Name is null || string.IsNullOrWhiteSpace(count.Label.Id))
        {
            count.Label = new IngredientLabel(
                string.IsNullOrWhiteSpace(count.Label.Id) ? label.Id : count.Label.Id,
                count.Label.Name ?? label.Name);
        }
        count.Count++;
    }
}

internal sealed class LabelCount(IngredientLabel label)
{
    public IngredientLabel Label { get; set; } = label;
    public int Count { get; set; }
}
