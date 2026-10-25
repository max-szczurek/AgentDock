using AgentDock.Core.Models;
using AgentDock.Core.Services;

namespace AgentDock.Tests.Infrastructure;

public sealed class FakeModelsClient : IAnthropicModelsClient
{
    public ModelsFetchResult Result { get; set; } = new(ModelsFetchStatus.Ok, Fixtures.Models());
    public List<string> KeysSeen { get; } = [];
    public Func<string, ModelsFetchResult>? Handler { get; set; }

    public Task<ModelsFetchResult> ListModelsAsync(string apiKey, CancellationToken ct = default)
    {
        KeysSeen.Add(apiKey);
        return Task.FromResult(Handler?.Invoke(apiKey) ?? Result);
    }
}

public static class Fixtures
{
    public static IReadOnlyList<ModelInfoDto> Models()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "models.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").EnumerateArray().Select(AnthropicModelsClient.Parse).ToList();
    }
}
