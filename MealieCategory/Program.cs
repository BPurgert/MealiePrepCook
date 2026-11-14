using System.Net.Http.Headers;
using System.ClientModel;
using System.Text.Json;
using MealieCategory;

var baseUrl = Environment.GetEnvironmentVariable("MEALIE_URL")?.Trim();
var token = Environment.GetEnvironmentVariable("MEALIE_API_TOKEN")?.Trim();
const string reviewFile = "mealie-conversion-review.json";
const string checkpointFile = "mealie-conversion-checkpoint.json";
const string labelReviewFile = "mealie-label-review.json";

if (args.Length > 1 || (args.Length == 1 && args[0] is not ("--propose" or "--apply" or "--propose-labels" or "--apply-labels")))
{
    Console.Error.WriteLine("Usage: run without arguments, or use --propose, --apply, --propose-labels, or --apply-labels.");
    return 1;
}

if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
    || !string.IsNullOrEmpty(uri.UserInfo)
    || !string.IsNullOrEmpty(uri.Query)
    || !string.IsNullOrEmpty(uri.Fragment))
{
    Console.Error.WriteLine("Set MEALIE_URL to the absolute HTTP(S) URL of your Mealie instance.");
    return 1;
}

if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Set MEALIE_API_TOKEN to a Mealie API token.");
    return 1;
}

try
{
    using var handler = new HttpClientHandler { AllowAutoRedirect = false };
    using var http = new HttpClient(handler)
    {
        BaseAddress = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/")
    };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    var client = new MealieClient(http);
    if (args is ["--propose"])
    {
        if (File.Exists(reviewFile))
            throw new IOException($"'{reviewFile}' already exists. Move it before creating another proposal.");
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("Set OPENAI_API_KEY before generating conversion proposals.");
            return 1;
        }

        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL")?.Trim();
        if (string.IsNullOrWhiteSpace(model)) model = "gpt-6-luna";
        using var checkpoint = await ConversionCheckpoint.OpenAsync(checkpointFile, model);
        var planner = new ConversionPlanner(client, new OpenAiIngredientInterpreter(apiKey, model), Console.Error.WriteLine, checkpoint);
        var plan = await planner.CreateAsync(http.BaseAddress.AbsoluteUri);
        await using (var file = new FileStream(reviewFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(file, plan, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            await file.FlushAsync();
            file.Flush(flushToDisk: true);
        }
        checkpoint.Complete();
        Console.WriteLine($"Saved {plan.Recipes.Sum(recipe => recipe.Proposals.Count)} proposals to {reviewFile}.");
        Console.WriteLine("Review each proposal and set approved to true before running --apply.");
        return 0;
    }

    if (args is ["--propose-labels"])
    {
        if (File.Exists(labelReviewFile))
            throw new IOException($"'{labelReviewFile}' already exists. Move it before creating another proposal.");
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("Set OPENAI_API_KEY before generating label proposals.");
            return 1;
        }

        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL")?.Trim();
        if (string.IsNullOrWhiteSpace(model)) model = "gpt-6-luna";
        var plan = await new LabelPlanner(new OpenAiLabelInterpreter(apiKey, model), client, Console.Error.WriteLine)
            .CreateAsync(http.BaseAddress.AbsoluteUri);
        await using (var file = new FileStream(labelReviewFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(file, plan, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            await file.FlushAsync();
            file.Flush(flushToDisk: true);
        }
        Console.WriteLine($"Saved {plan.Foods.Count} label proposals to {labelReviewFile}.");
        Console.WriteLine("Review each proposal and set approved to true before running --apply-labels.");
        return 0;
    }

    if (args is ["--apply-labels"])
    {
        await using var file = File.OpenRead(labelReviewFile);
        var plan = await JsonSerializer.DeserializeAsync<LabelPlan>(file, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The label proposal is empty.");
        await new LabelApplier(client).ApplyAsync(plan, http.BaseAddress.AbsoluteUri);
        return 0;
    }

    if (args is ["--apply"])
    {
        await using var file = File.OpenRead(reviewFile);
        var plan = await JsonSerializer.DeserializeAsync<ConversionPlan>(file, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The conversion proposal is empty.");
        await new ConversionApplier(client).ApplyAsync(plan, http.BaseAddress.AbsoluteUri);
        return 0;
    }

    Console.WriteLine("Labels:");
    var labels = new List<MealieLabel>();
    await foreach (var label in client.GetLabelsAsync())
    {
        labels.Add(label);
        Console.WriteLine($"  - {label.Name}");
    }
    if (labels.Count == 0)
    {
        Console.WriteLine("  (none)");
    }

    Console.WriteLine();
    Console.WriteLine("Recipes:");
    var counts = new IngredientLabelCounts(labels);
    var kinds = Enum.GetValues<IngredientKind>().ToDictionary(kind => kind, _ => 0);
    var recipeCount = 0;
    await foreach (var recipe in client.GetRecipesAsync())
    {
        recipeCount++;
        Console.WriteLine($"{recipe.Name}:");
        foreach (var ingredient in recipe.RecipeIngredient)
        {
            var kind = ingredient.Kind;
            Console.WriteLine($"  - {ingredient.ToDisplayString()} [Type: {kind.DisplayName()}]");
            counts.Add(ingredient);
            kinds[kind]++;
        }
    }

    if (recipeCount == 0)
    {
        Console.WriteLine("No accessible recipes found.");
    }

    Console.WriteLine();
    Console.WriteLine("Ingredient counts by label:");
    foreach (var count in counts.Labels)
    {
        Console.WriteLine($"  {count.Label.DisplayName}: {count.Count}");
    }
    Console.WriteLine($"  [none]: {counts.NoneCount}");

    Console.WriteLine();
    Console.WriteLine("Foods:");
    var foodCount = 0;
    await foreach (var food in client.GetFoodsAsync())
    {
        foodCount++;
        Console.WriteLine($"  - {food.Name}");
    }
    if (foodCount == 0)
    {
        Console.WriteLine("  (none)");
    }

    Console.WriteLine();
    Console.WriteLine("Units:");
    var unitCount = 0;
    await foreach (var unit in client.GetUnitsAsync())
    {
        unitCount++;
        Console.WriteLine($"  - {unit.Name}");
    }
    if (unitCount == 0)
    {
        Console.WriteLine("  (none)");
    }

    Console.WriteLine();
    Console.WriteLine("Ingredient types:");
    foreach (var (kind, count) in kinds)
    {
        Console.WriteLine($"  {kind.DisplayName()}: {count}");
    }

    return 0;
}
catch (FormatException)
{
    Console.Error.WriteLine("Invalid configuration or response format.");
    if (args is ["--apply"] or ["--apply-labels"])
        Console.Error.WriteLine("Some approved changes may already have been applied. Check Mealie before retrying.");
    return 1;
}
catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
                           or TaskCanceledException or ClientResultException
                           || ex is AggregateException aggregate && aggregate.Flatten().InnerExceptions.All(
                               inner => inner is ClientResultException or HttpRequestException or IOException or TaskCanceledException))
{
    Console.Error.WriteLine($"Operation failed: {ex.Message}");
    if (args is ["--apply"])
        Console.Error.WriteLine("Some approved changes may already have been applied. Check Mealie before retrying.");
    else if (args is ["--apply-labels"])
        Console.Error.WriteLine("Some approved labels may already have been applied. Re-running is safe: foods already labeled are skipped.");
    else if (args is ["--propose"] && File.Exists(checkpointFile))
        Console.Error.WriteLine(File.Exists(reviewFile)
            ? "A review file exists and the checkpoint was retained. Check the review file before retrying."
            : "Completed batches are saved. Retry --propose to resume matching batches.");
    return 1;
}
