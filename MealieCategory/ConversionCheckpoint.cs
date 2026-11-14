using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MealieCategory;

internal sealed class ConversionCheckpoint : IDisposable
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string path;
    private readonly string model;
    private readonly FileStream fileLock;
    private readonly CheckpointData data;

    private ConversionCheckpoint(string path, string model, FileStream fileLock, CheckpointData data)
    {
        this.path = path;
        this.model = model;
        this.fileLock = fileLock;
        this.data = data;
    }

    public static async Task<ConversionCheckpoint> OpenAsync(string path, string model)
    {
        var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var opened = false;
        try
        {
            CheckpointData data;
            if (File.Exists(path))
            {
                await using var file = File.OpenRead(path);
                data = await JsonSerializer.DeserializeAsync<CheckpointData>(file, JsonOptions)
                    ?? throw new InvalidDataException("The conversion checkpoint is empty.");
                if (data.Version != FormatVersion || data.Batches is null
                    || data.Batches.Any(entry => entry.Value is null))
                    throw new InvalidDataException("The conversion checkpoint has an invalid or unsupported format.");
            }
            else
            {
                data = new CheckpointData { Version = FormatVersion, Batches = [] };
            }

            var checkpoint = new ConversionCheckpoint(path, model, fileLock, data);
            opened = true;
            return checkpoint;
        }
        finally
        {
            if (!opened) fileLock.Dispose();
        }
    }

    public string Key(string mealieUrl, string input, List<JsonObject> foods, List<JsonObject> units)
    {
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            planVersion = ConversionPlan.CurrentVersion,
            instructions = OpenAiIngredientInterpreter.Instructions,
            model,
            mealieUrl,
            foodIds = foods.Select(food => food["id"]!.GetValue<string>()),
            unitIds = units.Select(unit => unit["id"]!.GetValue<string>()),
            input
        }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(identity));
    }

    public ConversionBatchResponse? Find(string key) =>
        data.Batches.TryGetValue(key, out var response) ? response : null;

    public async Task SaveAsync(string key, ConversionBatchResponse response)
    {
        data.Batches[key] = response;
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, data, JsonOptions);
                await file.FlushAsync();
                file.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public void Complete()
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public void Dispose() => fileLock.Dispose();

    private sealed class CheckpointData
    {
        public int Version { get; init; }
        public required Dictionary<string, ConversionBatchResponse> Batches { get; init; }
    }
}
