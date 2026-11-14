using System.Text.Json;
using MealieCategory;
using Xunit;

namespace MealieCategory.Tests;

public class IngredientClassificationTests
{
    [Theory]
    [InlineData("""{"food":{"name":"flour"},"unit":{"name":"cup"},"note":"sifted"}""", "FoodWithUnit")]
    [InlineData("""{"food":{"name":"salt"},"note":"to taste","title":"Sauce"}""", "FoodWithoutUnit")]
    [InlineData("""{"unit":{"name":"cup"},"note":"flour"}""", "UnitWithoutFood")]
    [InlineData("""{"quantity":2,"note":"eggs"}""", "QuantityWithoutFoodOrUnit")]
    [InlineData("""{"note":"salt to taste"}""", "FreeForm")]
    [InlineData("""{"originalText":"one onion"}""", "FreeForm")]
    [InlineData("""{"title":"Sauce","note":"one onion"}""", "FreeForm")]
    [InlineData("""{"title":"Sauce"}""", "SectionHeading")]
    [InlineData("""{}""", "Empty")]
    [InlineData("""{"referencedRecipe":{"name":"Stock"},"food":{"name":"stock"},"unit":{"name":"cup"}}""", "ReferencedRecipe")]
    public void ClassifiesRecipeEntriesFromTheirApiFields(string json, string expected)
    {
        var ingredient = JsonSerializer.Deserialize<Ingredient>(json, JsonSerializerOptions.Web)!;

        Assert.Equal(expected, ingredient.Kind.ToString());
    }

    [Fact]
    public void DisplaysReferencedRecipeNameWhenNoDisplayTextExists()
    {
        var ingredient = JsonSerializer.Deserialize<Ingredient>(
            """{"referencedRecipe":{"name":"Stock"}}""", JsonSerializerOptions.Web)!;

        Assert.Equal("Stock [Label: none]", ingredient.ToDisplayString());
    }
}
