using System.Net;
using System.Text;
using System.Text.Json;
using MealieCategory;
using Xunit;

namespace MealieCategory.Tests;

public class MealieClientTests
{
    [Fact]
    public async Task FetchesEveryPageOfUnits()
    {
        var responses = new Dictionary<string, string>
        {
            ["/mealie/api/units?page=1&perPage=100"] =
                """{"items":[{"name":"cup"}],"total_pages":2}""",
            ["/mealie/api/units?page=2&perPage=100"] =
                """{"items":[{"name":"gram"}],"total_pages":2}"""
        };
        var handler = new FakeHandler(request =>
            responses.TryGetValue(request.RequestUri!.PathAndQuery, out var json)
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/mealie/") };

        var units = new List<IngredientName>();
        await foreach (var unit in new MealieClient(http).GetUnitsAsync())
            units.Add(unit);

        Assert.Equal(new[] { "cup", "gram" }, units.Select(unit => unit.Name));
        Assert.Equal(responses.Keys, handler.Requests);
    }

    [Fact]
    public async Task EmptyUnitListIsAllowed()
    {
        var handler = new FakeHandler(_ => JsonResponse("""{"items":[],"total_pages":0}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        var units = new List<IngredientName>();
        await foreach (var unit in new MealieClient(http).GetUnitsAsync())
            units.Add(unit);

        Assert.Empty(units);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UnitFetchFailureIsReported()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in new MealieClient(http).GetUnitsAsync()) { }
        });
    }

    [Fact]
    public async Task UnitWithoutNameIsRejected()
    {
        var handler = new FakeHandler(_ => JsonResponse("""{"items":[{"name":null}],"total_pages":1}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in new MealieClient(http).GetUnitsAsync()) { }
        });
    }

    [Fact]
    public async Task FetchesEveryPageOfFoods()
    {
        var responses = new Dictionary<string, string>
        {
            ["/mealie/api/foods?page=1&perPage=100"] =
                """{"items":[{"name":"Carrot"}],"total_pages":2}""",
            ["/mealie/api/foods?page=2&perPage=100"] =
                """{"items":[{"name":"Unused food"}],"total_pages":2}"""
        };
        var handler = new FakeHandler(request =>
            responses.TryGetValue(request.RequestUri!.PathAndQuery, out var json)
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/mealie/") };

        var foods = new List<IngredientFood>();
        await foreach (var food in new MealieClient(http).GetFoodsAsync())
            foods.Add(food);

        Assert.Equal(new[] { "Carrot", "Unused food" }, foods.Select(food => food.Name));
        Assert.Equal(responses.Keys, handler.Requests);
    }

    [Fact]
    public async Task EmptyFoodListIsAllowed()
    {
        var handler = new FakeHandler(_ => JsonResponse("""{"items":[],"total_pages":0}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        var foods = new List<IngredientFood>();
        await foreach (var food in new MealieClient(http).GetFoodsAsync())
            foods.Add(food);

        Assert.Empty(foods);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FoodFetchFailureIsReported()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in new MealieClient(http).GetFoodsAsync()) { }
        });
    }

    [Fact]
    public async Task FoodWithoutNameIsRejected()
    {
        var handler = new FakeHandler(_ => JsonResponse("""{"items":[{"name":null}],"total_pages":1}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in new MealieClient(http).GetFoodsAsync()) { }
        });
    }

    [Fact]
    public async Task FetchesEveryPageOfLabelsIncludingUnusedLabels()
    {
        var responses = new Dictionary<string, string>
        {
            ["/mealie/api/groups/labels?page=1&perPage=100"] =
                """{"items":[{"name":"Produce"}],"total_pages":2}""",
            ["/mealie/api/groups/labels?page=2&perPage=100"] =
                """{"items":[{"name":"Unused"}],"total_pages":2}"""
        };
        var handler = new FakeHandler(request =>
            responses.TryGetValue(request.RequestUri!.PathAndQuery, out var json)
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/mealie/") };

        var labels = new List<MealieLabel>();
        await foreach (var label in new MealieClient(http).GetLabelsAsync())
            labels.Add(label);

        Assert.Equal(new[] { "Produce", "Unused" }, labels.Select(label => label.Name));
        Assert.Equal(responses.Keys, handler.Requests);
    }

    [Fact]
    public async Task EmptyLabelListIsAllowed()
    {
        var handler = new FakeHandler(_ => JsonResponse("""{"items":[],"total_pages":0}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        var labels = new List<MealieLabel>();
        await foreach (var label in new MealieClient(http).GetLabelsAsync())
            labels.Add(label);

        Assert.Empty(labels);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LabelFetchFailureIsReported()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in new MealieClient(http).GetLabelsAsync()) { }
        });
    }

    [Fact]
    public async Task LabelWithoutNameIsRejected()
    {
        var handler = new FakeHandler(_ => JsonResponse("""{"items":[{"name":null}],"total_pages":1}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in new MealieClient(http).GetLabelsAsync()) { }
        });
    }

    [Fact]
    public async Task FetchesEveryPageAndRecipeWithTheirIngredientEntries()
    {
        var responses = new Dictionary<string, string>
        {
            ["/mealie/api/recipes?page=1&perPage=100"] =
                """{"items":[{"slug":"first"}],"total_pages":2}""",
            ["/mealie/api/recipes/first"] =
                """{"name":"First","recipeIngredient":[{"display":"2 cups flour","food":{"name":"flour","label":{"name":"Baking"}}},{"originalText":"salt to taste","food":{"name":"salt","label":null}},{"note":"a pinch of pepper"}]}""",
            ["/mealie/api/recipes?page=2&perPage=100"] =
                """{"items":[{"slug":"second"}],"total_pages":2}""",
            ["/mealie/api/recipes/second"] =
                """{"name":"Second","recipeIngredient":[{"food":{"name":"flour","labelId":"label-123"},"quantity":1,"unit":{"name":"cup"}}]}"""
        };
        var handler = new FakeHandler(request =>
            responses.TryGetValue(request.RequestUri!.PathAndQuery, out var json)
                ? JsonResponse(json)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/mealie/") };

        var recipes = new List<Recipe>();
        await foreach (var recipe in new MealieClient(http).GetRecipesAsync())
            recipes.Add(recipe);

        Assert.Equal(new[] { "First", "Second" }, recipes.Select(recipe => recipe.Name));
        Assert.Equal(new[] { "2 cups flour [Label: Baking]", "salt to taste [Label: none]", "a pinch of pepper [Label: none]" },
            recipes[0].RecipeIngredient.Select(ingredient => ingredient.ToDisplayString()));
        Assert.Equal("1 cup flour [Label: ID label-123 (name unavailable)]", recipes[1].RecipeIngredient[0].ToDisplayString());
        Assert.Equal(responses.Keys, handler.Requests);
    }

    [Fact]
    public async Task EmptyRecipeListDoesNotFetchDetails()
    {
        var handler = new FakeHandler(_ => JsonResponse("""{"items":[],"total_pages":0}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        var recipes = new List<Recipe>();
        await foreach (var recipe in new MealieClient(http).GetRecipesAsync())
            recipes.Add(recipe);

        Assert.Empty(recipes);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task MissingIngredientDataFailsInsteadOfReturningAnEmptyList()
    {
        var handler = new FakeHandler(request => JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("/recipes")
            ? """{"items":[{"slug":"first"}],"total_pages":1}"""
            : """{"name":"First"}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        await Assert.ThrowsAsync<JsonException>(async () =>
        {
            await foreach (var _ in new MealieClient(http).GetRecipesAsync()) { }
        });
    }

    [Fact]
    public async Task RecipeFetchFailureIsReported()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/recipes")
            ? JsonResponse("""{"items":[{"slug":"first"}],"total_pages":1}""")
            : new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in new MealieClient(http).GetRecipesAsync()) { }
        });
    }

    [Fact]
    public async Task InvalidRecipeSlugIsRejected()
    {
        var handler = new FakeHandler(_ => JsonResponse("""{"items":[{"slug":".."}],"total_pages":1}"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in new MealieClient(http).GetRecipesAsync()) { }
        });
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void LabelWithoutNameIsReportedAsInvalid()
    {
        var ingredient = new Ingredient { Food = new IngredientFood { Label = new IngredientName() } };

        Assert.Throws<InvalidDataException>(() => ingredient.ToDisplayString());
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }
}
