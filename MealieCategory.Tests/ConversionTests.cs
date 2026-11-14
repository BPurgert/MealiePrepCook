using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.ClientModel;
using System.ClientModel.Primitives;
using MealieCategory;
using OpenAI;
using Xunit;

namespace MealieCategory.Tests;

public class ConversionTests
{
    [Fact]
    public async Task AgentUsesResponsesApiWithStructuredOutputAndStorageDisabled()
    {
        JsonNode? requestBody = null;
        var handler = new FakeHandler((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/responses", request.RequestUri!.AbsolutePath);
            requestBody = JsonNode.Parse(body!);
            return JsonResponse("""
                {"id":"resp_123","object":"response","created_at":1,"status":"completed","model":"test-model",
                 "output":[{"type":"message","id":"msg_123","status":"completed","role":"assistant",
                   "content":[{"type":"output_text","text":"{\"items\":[{\"index\":0,\"foodCode\":\"f0\",\"newFoodName\":null,\"unitCode\":null,\"newUnitName\":null,\"quantity\":null,\"note\":null,\"skipReason\":null}]}","annotations":[]}]}],
                 "usage":{"input_tokens":10,"output_tokens":10,"total_tokens":20}}
                """);
        });
        using var http = new HttpClient(handler);
        var client = new OpenAIClient(new ApiKeyCredential("test-key"), new OpenAIClientOptions
        {
            Endpoint = new Uri("https://openai.example.test/v1"),
            Transport = new HttpClientPipelineTransport(http)
        });
        var interpreter = new OpenAiIngredientInterpreter(client, "test-model");

        var result = await interpreter.InterpretAsync("""{"ingredients":[{"index":0}]}""");

        Assert.Equal("f0", Assert.Single(result.Items).FoodCode);
        Assert.False(requestBody!["store"]!.GetValue<bool>());
        Assert.Equal("json_schema", requestBody["text"]!["format"]!["type"]!.GetValue<string>());
        var schema = requestBody["text"]!["format"]!["schema"]!;
        Assert.NotNull(schema["properties"]!["items"]);
    }

    [Fact]
    public async Task PlannerBatchesAndDeduplicatesEligibleEntriesWithoutWriting()
    {
        var ingredients = new JsonArray();
        for (var i = 0; i < 41; i++) ingredients.Add(new JsonObject { ["note"] = $"ingredient {i}" });
        ingredients.Add(new JsonObject { ["note"] = "ingredient 0" });
        ingredients.Add(new JsonObject { ["food"] = new JsonObject { ["id"] = "food-id", ["name"] = "flour" }, ["unit"] = new JsonObject { ["id"] = "unit-id", ["name"] = "cup" } });
        var recipe = new JsonObject { ["id"] = "recipe-id", ["name"] = "Private recipe name", ["recipeIngredient"] = ingredients };
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/foods" => JsonResponse("""{"items":[{"id":"food-id","name":"flour"}],"total_pages":1}"""),
            "/api/units" => JsonResponse("""{"items":[{"id":"unit-id","name":"cup"}],"total_pages":1}"""),
            "/api/recipes" => JsonResponse("""{"items":[{"slug":"recipe"}],"total_pages":1}"""),
            "/api/recipes/recipe" => JsonResponse(recipe.ToJsonString()),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var interpreter = new FakeInterpreter();
        var progress = new List<string>();

        var plan = await new ConversionPlanner(new MealieClient(http), interpreter, progress.Add).CreateAsync("https://example.test/");

        Assert.Equal(new[] { 40, 1 }, interpreter.BatchSizes);
        Assert.Contains("Fetching foods from Mealie...", progress);
        Assert.Contains("Recipes read: 1. Eligible ingredients: 42 (41 distinct).", progress);
        Assert.Contains("Sending OpenAI batch 1/2 (40 distinct ingredients)...", progress);
        Assert.Contains("Completed OpenAI batch 2/2.", progress);
        Assert.DoesNotContain(progress, message => message.Contains("Private recipe name"));
        Assert.DoesNotContain("Private recipe name", string.Join("", interpreter.Requests));
        Assert.Equal(42, Assert.Single(plan.Recipes).Proposals.Count);
        Assert.All(plan.Recipes[0].Proposals, proposal => Assert.False(proposal.Approved));
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task PlannerReportsWhileOpenAiRequestIsPending()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath == "/api/foods"
            ? JsonResponse("""{"items":[{"id":"food-id","name":"flour"}],"total_pages":1}""")
            : RouteBasic(request, """[{"note":"flour"}]"""));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var interpreter = new WaitingInterpreter();
        var progress = new List<string>();
        void Report(string message)
        {
            progress.Add(message);
            if (message == "Still waiting for OpenAI batch 1/1...")
                interpreter.Release();
        }

        var plan = await new ConversionPlanner(new MealieClient(http), interpreter, Report)
            .CreateAsync("https://example.test/").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Contains("Still waiting for OpenAI batch 1/1...", progress);
        Assert.Contains("Completed OpenAI batch 1/1.", progress);
        Assert.Single(Assert.Single(plan.Recipes).Proposals);
    }

    [Fact]
    public async Task CheckpointResumesCompletedBatchesAfterFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mealie-checkpoint-test-{Guid.NewGuid():N}.json");
        var ingredients = new JsonArray();
        for (var i = 0; i < 41; i++) ingredients.Add(new JsonObject { ["note"] = $"ingredient {i}" });
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/foods" => JsonResponse("""{"items":[{"id":"food-id","name":"flour"}],"total_pages":1}"""),
            "/api/recipes/recipe" => JsonResponse($$"""{"id":"recipe-id","name":"Recipe","recipeIngredient":{{ingredients.ToJsonString()}}}"""),
            _ => RouteBasic(request, "[]")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var client = new MealieClient(http);
        try
        {
            var first = new FakeInterpreter { FailOnCall = 2 };
            using (var checkpoint = await ConversionCheckpoint.OpenAsync(path, "model"))
            {
                await Assert.ThrowsAsync<HttpRequestException>(() =>
                    new ConversionPlanner(client, first, checkpoint: checkpoint).CreateAsync("https://example.test/"));
            }
            Assert.Equal(new[] { 40, 1 }, first.BatchSizes);
            Assert.True(File.Exists(path));

            var second = new FakeInterpreter();
            var progress = new List<string>();
            using (var checkpoint = await ConversionCheckpoint.OpenAsync(path, "model"))
            {
                var plan = await new ConversionPlanner(client, second, progress.Add, checkpoint)
                    .CreateAsync("https://example.test/");
                Assert.Equal(41, Assert.Single(plan.Recipes).Proposals.Count);
                Assert.Equal(new[] { 1 }, second.BatchSizes);
                Assert.Contains("Resumed OpenAI batch 1/2 from checkpoint.", progress);
                checkpoint.Complete();
            }
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task CheckpointDoesNotReuseChangedModelInstanceCatalogOrInput()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mealie-checkpoint-test-{Guid.NewGuid():N}.json");
        var foodId = "food-id";
        var note = "ingredient";
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/foods" => JsonResponse($$"""{"items":[{"id":"{{foodId}}","name":"flour"}],"total_pages":1}"""),
            "/api/recipes/recipe" => JsonResponse($$"""{"id":"recipe-id","name":"Recipe","recipeIngredient":[{"note":"{{note}}"}]}"""),
            _ => RouteBasic(request, "[]")
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var client = new MealieClient(http);
        async Task<int> RunAsync(string model, string url)
        {
            using var checkpoint = await ConversionCheckpoint.OpenAsync(path, model);
            var interpreter = new FakeInterpreter();
            await new ConversionPlanner(client, interpreter, checkpoint: checkpoint).CreateAsync(url);
            return interpreter.BatchSizes.Count;
        }
        try
        {
            Assert.Equal(1, await RunAsync("model", "https://example.test/"));
            Assert.Equal(0, await RunAsync("model", "https://example.test/"));
            foodId = "different-food-id";
            Assert.Equal(1, await RunAsync("model", "https://example.test/"));
            note = "different ingredient";
            Assert.Equal(1, await RunAsync("model", "https://example.test/"));
            Assert.Equal(1, await RunAsync("other-model", "https://example.test/"));
            Assert.Equal(1, await RunAsync("model", "https://other.test/"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task CheckpointRejectsCorruptFilesAndConcurrentRuns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mealie-checkpoint-test-{Guid.NewGuid():N}.json");
        try
        {
            using (await ConversionCheckpoint.OpenAsync(path, "model"))
            {
                await Assert.ThrowsAsync<IOException>(() => ConversionCheckpoint.OpenAsync(path, "model"));
            }
            await File.WriteAllTextAsync(path, "{invalid");
            await Assert.ThrowsAsync<JsonException>(() => ConversionCheckpoint.OpenAsync(path, "model"));
            Assert.Equal("{invalid", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task CheckpointDoesNotSaveInvalidModelResults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mealie-checkpoint-test-{Guid.NewGuid():N}.json");
        var handler = BasicHandler("""[{"note":"ingredient"}]""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        try
        {
            using var checkpoint = await ConversionCheckpoint.OpenAsync(path, "model");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ConversionPlanner(new MealieClient(http), new FakeInterpreter { FoodCode = "f99" }, checkpoint: checkpoint)
                    .CreateAsync("https://example.test/"));
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task PlannerRejectsUnknownModelCodes()
    {
        var handler = BasicHandler("""[{"note":"one ingredient"}]""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var interpreter = new FakeInterpreter { FoodCode = "f99" };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ConversionPlanner(new MealieClient(http), interpreter).CreateAsync("https://example.test/"));
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task PlannerRejectsReplacingAnExistingUnit()
    {
        const string original = """[{"unit":{"id":"unit-id","name":"cup"},"note":"flour"}]""";
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/foods" => JsonResponse("""{"items":[{"id":"food-id","name":"flour"}],"total_pages":1}"""),
            "/api/units" => JsonResponse("""{"items":[{"id":"unit-id","name":"cup"}],"total_pages":1}"""),
            _ => RouteBasic(request, original)
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ConversionPlanner(new MealieClient(http), new FakeInterpreter()).CreateAsync("https://example.test/"));
    }

    [Fact]
    public async Task DisabledDefaultQuantityIsParsedAndAppliedWithFoodFlagsEnabled()
    {
        const string original = """[{"quantity":1,"unit":null,"food":null,"note":"20 ounce cauliflower florets","display":"20 ounce cauliflower florets","isFood":false,"disableAmount":true,"referenceId":"ref-1"}]""";
        JsonNode? patch = null;
        var handler = new FakeHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Patch)
            {
                patch = JsonNode.Parse(body!);
                return JsonResponse("{}");
            }
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/foods" => JsonResponse("""{"items":[{"id":"food-id","name":"cauliflower"}],"total_pages":1}"""),
                "/api/units" => JsonResponse("""{"items":[{"id":"unit-id","name":"ounce"}],"total_pages":1}"""),
                _ => RouteBasic(request, original)
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var client = new MealieClient(http);
        var interpreter = new FakeInterpreter { UnitCode = "u0", Quantity = 20 };

        var plan = await new ConversionPlanner(client, interpreter).CreateAsync("https://example.test/");
        var proposal = Assert.Single(Assert.Single(plan.Recipes).Proposals);
        Assert.Null(JsonNode.Parse(Assert.Single(interpreter.Requests))!["ingredients"]![0]!["quantity"]);
        Assert.Equal("20 ounce cauliflower florets", proposal.Original);
        Assert.Equal(20m, proposal.Quantity);
        Assert.Null(proposal.Note);
        proposal.Approved = true;
        await new ConversionApplier(client).ApplyAsync(plan, "https://example.test/");

        var ingredient = patch!["recipeIngredient"]![0]!;
        Assert.Equal(20m, ingredient["quantity"]!.GetValue<decimal>());
        Assert.True(ingredient["isFood"]!.GetValue<bool>());
        Assert.False(ingredient["disableAmount"]!.GetValue<bool>());
        Assert.Equal("20 ounce cauliflower florets", ingredient["originalText"]!.GetValue<string>());
        Assert.Null(ingredient["note"]);
    }

    [Fact]
    public async Task DisabledDefaultQuantityIsClearedWhenNoAmountWasInferred()
    {
        const string original = """[{"quantity":1,"note":"salt to taste","disableAmount":true,"isFood":false}]""";
        JsonNode? patch = null;
        var handler = new FakeHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Patch)
            {
                patch = JsonNode.Parse(body!);
                return JsonResponse("{}");
            }
            if (request.RequestUri!.AbsolutePath == "/api/foods")
                return JsonResponse("""{"items":[{"id":"food-id","name":"salt"}],"total_pages":1}""");
            return RouteBasic(request, original);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var client = new MealieClient(http);
        var plan = await new ConversionPlanner(client, new FakeInterpreter()).CreateAsync("https://example.test/");
        Assert.Single(Assert.Single(plan.Recipes).Proposals).Approved = true;

        await new ConversionApplier(client).ApplyAsync(plan, "https://example.test/");

        Assert.Null(patch!["recipeIngredient"]![0]!["quantity"]);
    }

    [Theory]
    [InlineData(1, false, true)]
    [InlineData(2, true, false)]
    public void EffectiveQuantityPreservesNonPlaceholderValues(int quantity, bool disableAmount, bool isFood)
    {
        var ingredient = new Ingredient { Quantity = quantity, DisableAmount = disableAmount, IsFood = isFood };

        Assert.Equal(quantity, ingredient.EffectiveQuantity);
    }

    [Fact]
    public void IsFoodFalseAlsoIdentifiesHiddenDefaultQuantity()
    {
        var ingredient = new Ingredient { Quantity = 1, IsFood = false, Note = "salt" };

        Assert.Null(ingredient.EffectiveQuantity);
        Assert.Equal(IngredientKind.FreeForm, ingredient.Kind);
        Assert.Equal("salt [Label: none]", ingredient.ToDisplayString());
    }

    [Theory]
    [InlineData("""[{"quantity":1,"isFood":true,"disableAmount":false,"note":"flour"}]""")]
    [InlineData("""[{"quantity":2,"isFood":false,"disableAmount":true,"note":"flour"}]""")]
    public async Task PlannerRejectsChangesToMeaningfulQuantities(string original)
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath == "/api/foods"
            ? JsonResponse("""{"items":[{"id":"food-id","name":"flour"}],"total_pages":1}""")
            : RouteBasic(request, original));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ConversionPlanner(new MealieClient(http), new FakeInterpreter { Quantity = 20 }).CreateAsync("https://example.test/"));
    }

    [Fact]
    public async Task OldReviewFilesAreRejectedBeforeAnyRequests()
    {
        var handler = BasicHandler("""[{"note":"original"}]""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var current = Plan("""[{"note":"original"}]""", approved: true);
        var old = new ConversionPlan { MealieUrl = current.MealieUrl, Recipes = current.Recipes };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ConversionApplier(new MealieClient(http)).ApplyAsync(old, "https://example.test/"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ProposalOriginalUsesOriginalTextWithoutAppendedLabel()
    {
        const string original = """[{"food":{"id":"food-id","name":"flour","label":{"name":"Baking"}},"originalText":"original flour","display":"different display"}]""";
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath == "/api/foods"
            ? JsonResponse("""{"items":[{"id":"food-id","name":"flour"}],"total_pages":1}""")
            : RouteBasic(request, original));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        var plan = await new ConversionPlanner(new MealieClient(http), new FakeInterpreter()).CreateAsync("https://example.test/");

        Assert.Equal("original flour", Assert.Single(Assert.Single(plan.Recipes).Proposals).Original);
    }

    [Fact]
    public async Task PlannerAndApplierPreserveExistingPartialFields()
    {
        const string original = """[{"food":{"id":"food-id","name":"flour"},"quantity":3,"note":"sifted"}]""";
        JsonNode? patch = null;
        var handler = new FakeHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Patch)
            {
                patch = JsonNode.Parse(body!);
                return JsonResponse("{}");
            }
            if (request.RequestUri!.AbsolutePath == "/api/foods")
                return JsonResponse("""{"items":[{"id":"food-id","name":"flour"}],"total_pages":1}""");
            return RouteBasic(request, original);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var client = new MealieClient(http);
        var plan = await new ConversionPlanner(client, new FakeInterpreter()).CreateAsync("https://example.test/");
        Assert.Single(Assert.Single(plan.Recipes).Proposals).Approved = true;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        plan = JsonSerializer.Deserialize<ConversionPlan>(JsonSerializer.Serialize(plan, options), options)!;

        await new ConversionApplier(client).ApplyAsync(plan, "https://example.test/");

        var ingredient = patch!["recipeIngredient"]![0]!;
        Assert.Equal("food-id", ingredient["food"]!["id"]!.GetValue<string>());
        Assert.Equal(3m, ingredient["quantity"]!.GetValue<decimal>());
        Assert.Equal("sifted", ingredient["originalText"]!.GetValue<string>());
    }

    [Fact]
    public async Task ApplyStopsIfRecipeChangesBeforePatch()
    {
        const string original = """[{"note":"original"}]""";
        var recipeReads = 0;
        var handler = new FakeHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("foods")
                    ? """{"id":"food","name":"onion"}"""
                    : """{"id":"unit","name":"cup"}""");
            if (request.RequestUri!.AbsolutePath == "/api/recipes/recipe" && ++recipeReads == 2)
                return RouteBasic(request, """[{"note":"changed concurrently"}]""");
            return RouteBasic(request, original);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ConversionApplier(new MealieClient(http)).ApplyAsync(Plan(original, approved: true), "https://example.test/"));
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task ApplyRequiresApprovalAndRejectsChangedRecipesBeforeWrites()
    {
        var handler = BasicHandler("""[{"note":"changed"}]""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var plan = Plan("""[{"note":"original"}]""", approved: false);
        var applier = new ConversionApplier(new MealieClient(http));

        await Assert.ThrowsAsync<InvalidDataException>(() => applier.ApplyAsync(plan, "https://example.test/"));
        plan.Recipes[0].Proposals[0].Approved = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => applier.ApplyAsync(plan, "https://example.test/"));
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task ApplySkipsApprovedProposalsWithSkipReasonAndReportsThem()
    {
        const string original = """[{"note":"mystery"},{"note":"1 onion"}]""";
        JsonNode? patch = null;
        var handler = new FakeHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Post)
                return JsonResponse("""{"id":"new-food","name":"onion","labelId":null}""");
            if (request.Method == HttpMethod.Patch)
            {
                patch = JsonNode.Parse(body!);
                return JsonResponse("{}");
            }
            return RouteBasic(request, original);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var plan = Plan(original, approved: true);
        plan.Recipes[0].Proposals.Clear();
        plan.Recipes[0].Proposals.Add(new ConversionProposal
        {
            IngredientIndex = 0, Original = "mystery", Approved = true, SkipReason = "Unclear"
        });
        plan.Recipes[0].Proposals.Add(new ConversionProposal
        {
            IngredientIndex = 1, Original = "1 onion", Approved = true, NewFoodName = "onion", Quantity = 1
        });

        var output = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(output);
        try { await new ConversionApplier(new MealieClient(http)).ApplyAsync(plan, "https://example.test/"); }
        finally { Console.SetOut(previous); }

        Assert.Contains("Unclear", output.ToString());
        var entries = patch!["recipeIngredient"]!.AsArray();
        Assert.Null(entries[0]!["food"]);
        Assert.NotNull(entries[1]!["food"]);
    }

    [Fact]
    public async Task ApplyCreatesApprovedItemsAndPreservesOtherIngredientData()
    {
        const string original = """[{"referenceId":"ref-1","note":"2 cups onions, chopped","extra":"keep"},{"note":"another onion"},{"note":"untouched","quantity":1,"disableAmount":true}]""";
        JsonNode? patch = null;
        var handler = new FakeHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/foods")
                return JsonResponse("""{"id":"new-food","name":"onion","labelId":null}""");
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/units")
                return JsonResponse("""{"id":"new-unit","name":"cup","fraction":true}""");
            if (request.Method == HttpMethod.Patch)
            {
                patch = JsonNode.Parse(body!);
                return JsonResponse("{}");
            }
            return RouteBasic(request, original);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var plan = Plan(original, approved: true);
        plan.Recipes[0].Proposals.Add(new ConversionProposal
        {
            IngredientIndex = 1, Original = "another onion", Approved = true,
            NewFoodName = "Onion", NewUnitName = "Cup"
        });

        await new ConversionApplier(new MealieClient(http)).ApplyAsync(plan, "https://example.test/");

        var entries = patch!["recipeIngredient"]!.AsArray();
        Assert.Equal(3, entries.Count);
        Assert.Equal("ref-1", entries[0]!["referenceId"]!.GetValue<string>());
        Assert.Equal("keep", entries[0]!["extra"]!.GetValue<string>());
        Assert.Equal("2 cups onions, chopped", entries[0]!["originalText"]!.GetValue<string>());
        Assert.Equal("chopped", entries[0]!["note"]!.GetValue<string>());
        Assert.Equal("new-food", entries[0]!["food"]!["id"]!.GetValue<string>());
        Assert.Equal("new-unit", entries[0]!["unit"]!["id"]!.GetValue<string>());
        Assert.Equal(2m, entries[0]!["quantity"]!.GetValue<decimal>());
        Assert.Equal("new-food", entries[1]!["food"]!["id"]!.GetValue<string>());
        Assert.Equal("new-unit", entries[1]!["unit"]!["id"]!.GetValue<string>());
        Assert.Equal("untouched", entries[2]!["note"]!.GetValue<string>());
        Assert.Equal(0m, entries[2]!["quantity"]!.GetValue<decimal>());
        Assert.False(patch["settings"]!["disableAmount"]!.GetValue<bool>());
        Assert.False(patch["settings"]!["public"]!.GetValue<bool>());
        Assert.Equal(2, patch.AsObject().Count);
        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task ApplyReportsFailureAfterCatalogCreation()
    {
        const string original = """[{"note":"original"}]""";
        var handler = new FakeHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("foods")
                    ? """{"id":"food","name":"onion"}"""
                    : """{"id":"unit","name":"cup"}""");
            if (request.Method == HttpMethod.Patch)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return RouteBasic(request, original);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new ConversionApplier(new MealieClient(http)).ApplyAsync(Plan(original, approved: true), "https://example.test/"));
        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Post));
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Patch);
    }

    private static ConversionPlan Plan(string ingredients, bool approved) => new()
    {
        Version = ConversionPlan.CurrentVersion,
        MealieUrl = "https://example.test/",
        Recipes =
        [
            new ConversionRecipe
            {
                Slug = "recipe",
                Name = "Recipe",
                RecipeId = "recipe-id",
                OriginalIngredients = JsonNode.Parse(ingredients)!.AsArray(),
                Proposals =
                [
                    new ConversionProposal
                    {
                        IngredientIndex = 0, Original = "original", Approved = approved,
                        NewFoodName = "onion", NewUnitName = "cup", Quantity = 2, Note = "chopped"
                    }
                ]
            }
        ]
    };

    private static FakeHandler BasicHandler(string ingredients) => new((request, _) => RouteBasic(request, ingredients));

    private static HttpResponseMessage RouteBasic(HttpRequestMessage request, string ingredients) =>
        request.RequestUri!.AbsolutePath switch
        {
            "/api/foods" => JsonResponse("""{"items":[],"total_pages":0}"""),
            "/api/units" => JsonResponse("""{"items":[],"total_pages":0}"""),
            "/api/recipes" => JsonResponse("""{"items":[{"slug":"recipe"}],"total_pages":1}"""),
            "/api/recipes/recipe" => JsonResponse($$"""{"id":"recipe-id","name":"Recipe","settings":{"public":false,"disableAmount":true},"recipeIngredient":{{ingredients}}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request, body);
        }
    }

    private sealed class FakeInterpreter : IIngredientInterpreter
    {
        public List<int> BatchSizes { get; } = [];
        public List<string> Requests { get; } = [];
        public string FoodCode { get; init; } = "f0";
        public string? UnitCode { get; init; }
        public decimal? Quantity { get; init; }
        public int? FailOnCall { get; init; }

        public Task<ConversionBatchResponse> InterpretAsync(string input)
        {
            Requests.Add(input);
            var ingredients = JsonNode.Parse(input)!["ingredients"]!.AsArray();
            BatchSizes.Add(ingredients.Count);
            if (BatchSizes.Count == FailOnCall) throw new HttpRequestException("Simulated network failure.");
            return Task.FromResult(new ConversionBatchResponse
            {
                Items = ingredients.Select(item => new ConversionBatchItem
                {
                    Index = item!["index"]!.GetValue<int>(),
                    FoodCode = FoodCode,
                    NewFoodName = null,
                    UnitCode = UnitCode,
                    NewUnitName = null,
                    Quantity = Quantity,
                    Note = null,
                    SkipReason = null
                }).ToList()
            });
        }
    }

    private sealed class WaitingInterpreter : IIngredientInterpreter
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => release.SetResult();

        public async Task<ConversionBatchResponse> InterpretAsync(string input)
        {
            await release.Task;
            return await new FakeInterpreter().InterpretAsync(input);
        }
    }
}
