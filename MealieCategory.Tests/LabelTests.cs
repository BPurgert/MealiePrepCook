using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MealieCategory;
using Xunit;

namespace MealieCategory.Tests;

public class LabelTests
{
    private const string Labels = """{"items":[{"id":"l-prod","name":"Produce"},{"id":"l-dairy","name":"Dairy"}],"total_pages":1}""";
    private const string Foods = """
        {"items":[
          {"id":"f-lime","name":"lime","labelId":null},
          {"id":"f-feta","name":"feta cheese","labelId":null},
          {"id":"f-milk","name":"milk","labelId":"l-dairy"}
        ],"total_pages":1}
        """;

    [Fact]
    public async Task PlannerSendsOnlyUnlabeledFoodsAndMapsLabels()
    {
        using var http = new HttpClient(new Handler(Respond(null))) { BaseAddress = new Uri("https://example.test/") };
        var interpreter = new FakeLabelInterpreter();

        var plan = await new LabelPlanner(interpreter, new MealieClient(http)).CreateAsync("https://example.test/");

        var sent = JsonNode.Parse(interpreter.Input!)!["foods"]!.AsArray();
        Assert.Equal(["lime", "feta cheese"], sent.Select(food => food!["name"]!.GetValue<string>()));
        Assert.Equal(["f-lime", "f-feta"], plan.Foods.Select(food => food.FoodId));
        Assert.Equal("Produce", plan.Foods[0].LabelName);
        Assert.Null(plan.Foods[1].LabelId);
        Assert.Equal("No fit", plan.Foods[1].SkipReason);
        Assert.All(plan.Foods, food => Assert.False(food.Approved));
    }

    [Fact]
    public async Task ApplierPutsFullFoodForApprovedOnlyAndNeverOverwritesLabels()
    {
        var puts = new List<JsonNode>();
        using var http = new HttpClient(new Handler(Respond(puts))) { BaseAddress = new Uri("https://example.test/") };
        var plan = new LabelPlan
        {
            Version = LabelPlan.CurrentVersion, MealieUrl = "https://example.test/",
            Foods =
            [
                new() { Approved = true, FoodId = "f-lime", FoodName = "lime", LabelId = "l-prod", LabelName = "Produce" },
                new() { Approved = false, FoodId = "f-feta", FoodName = "feta cheese", LabelId = "l-dairy", LabelName = "Dairy" },
                new() { Approved = true, FoodId = "f-milk", FoodName = "milk", LabelId = "l-prod", LabelName = "Produce" },
                new() { Approved = true, FoodId = "f-x", FoodName = "x", SkipReason = "Unclear" }
            ]
        };

        await new LabelApplier(new MealieClient(http)).ApplyAsync(plan, "https://example.test/");

        var put = Assert.Single(puts);
        Assert.Equal("l-prod", put["labelId"]!.GetValue<string>());
        Assert.Equal("keep", put["extra"]!.GetValue<string>());
        Assert.Equal("Produce", put["label"]!["name"]!.GetValue<string>());
    }

    private static Func<HttpRequestMessage, string?, HttpResponseMessage> Respond(List<JsonNode>? puts) => (request, body) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Put)
        {
            puts!.Add(JsonNode.Parse(body!)!);
            return Json("{}");
        }
        if (path == "/api/groups/labels") return Json(Labels);
        if (path == "/api/foods") return Json(Foods);
        var id = path["/api/foods/".Length..];
        var items = JsonNode.Parse(Foods)!["items"]!.AsArray();
        var food = items.First(item => item!["id"]!.GetValue<string>() == id)!.AsObject();
        return Json(new JsonObject(food.Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone())))
        {
            ["extra"] = "keep"
        }.ToJsonString());
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
    }

    private sealed class FakeLabelInterpreter : ILabelInterpreter
    {
        public string? Input { get; private set; }

        public Task<LabelBatchResponse> InterpretAsync(string input)
        {
            Input = input;
            return Task.FromResult(new LabelBatchResponse
            {
                Items =
                [
                    new() { Index = 0, LabelCode = "l0", SkipReason = null },
                    new() { Index = 1, LabelCode = null, SkipReason = "No fit" }
                ]
            });
        }
    }
}
