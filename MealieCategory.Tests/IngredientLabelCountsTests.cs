using MealieCategory;
using Xunit;

namespace MealieCategory.Tests;

public class IngredientLabelCountsTests
{
    [Fact]
    public void CountsRecipeEntriesByLabelIncludingUnusedAndUnlabeled()
    {
        var counts = new IngredientLabelCounts([
            new MealieLabel { Id = "produce-id", Name = "Produce" },
            new MealieLabel { Id = "unused-id", Name = "Unused" },
            new MealieLabel { Id = "none-id", Name = "none" }
        ]);
        var carrot = new Ingredient { Food = new IngredientFood
        {
            LabelId = "produce-id",
            Label = new IngredientName { Id = "produce-id", Name = "Produce" }
        } };

        counts.Add(carrot);
        counts.Add(carrot);
        counts.Add(new Ingredient { Food = new IngredientFood { LabelId = "produce-id" } });
        counts.Add(new Ingredient { Food = new IngredientFood { LabelId = "none-id" } });
        counts.Add(new Ingredient { Note = "salt to taste" });
        counts.Add(new Ingredient { Food = new IngredientFood { Name = "pepper" } });

        Assert.Equal(new[] { 3, 0, 1 }, counts.Labels.Select(label => label.Count));
        Assert.Equal(2, counts.NoneCount);
    }

    [Fact]
    public void KeepsUnknownLabelIdsSeparateFromUnlabeledEntries()
    {
        var counts = new IngredientLabelCounts([]);
        counts.Add(new Ingredient { Food = new IngredientFood { LabelId = "missing-id" } });
        counts.Add(new Ingredient { Food = new IngredientFood { LabelId = "missing-id" } });
        counts.Add(new Ingredient());

        var label = Assert.Single(counts.Labels);
        Assert.Equal("ID missing-id (name unavailable)", label.Label.DisplayName);
        Assert.Equal(2, label.Count);
        Assert.Equal(1, counts.NoneCount);
    }

    [Fact]
    public void MatchesNameOnlyLabelsAndSeparatesDifferentLabelIds()
    {
        var counts = new IngredientLabelCounts([
            new MealieLabel { Id = "first", Name = "Shared" },
            new MealieLabel { Id = "second", Name = "Shared" }
        ]);
        counts.Add(new Ingredient { Food = new IngredientFood { Label = new IngredientName { Name = "Shared" } } });
        counts.Add(new Ingredient { Food = new IngredientFood { LabelId = "second", Label = new IngredientName { Name = "Shared" } } });

        Assert.Equal(new[] { 1, 1 }, counts.Labels.Select(label => label.Count));
    }

    [Fact]
    public void FillsInNameWhenAnIdOnlyLabelIsLaterDescribed()
    {
        var counts = new IngredientLabelCounts([]);
        counts.Add(new Ingredient { Food = new IngredientFood { LabelId = "id" } });
        counts.Add(new Ingredient { Food = new IngredientFood
        {
            LabelId = "id", Label = new IngredientName { Name = "Produce" }
        } });

        var label = Assert.Single(counts.Labels);
        Assert.Equal("Produce", label.Label.DisplayName);
        Assert.Equal(2, label.Count);
    }
}
