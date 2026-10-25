using System.Net;
using System.Text.Json;
using AgentDock.Core.Models;

namespace AgentDock.Core.Services;

public enum ModelsFetchStatus { Ok, Unauthorized, Unavailable }

public sealed record ModelsFetchResult(ModelsFetchStatus Status, IReadOnlyList<ModelInfoDto> Models, string? ErrorCode = null);

public interface IAnthropicModelsClient
{
    Task<ModelsFetchResult> ListModelsAsync(string apiKey, CancellationToken ct = default);
}

/// <summary>Pages through GET /v1/models. Uses an injected HttpClient so tests can fake the API.</summary>
public sealed class AnthropicModelsClient(HttpClient http) : IAnthropicModelsClient
{
    public const string ApiVersion = "2026-10-21";

    public async Task<ModelsFetchResult> ListModelsAsync(string apiKey, CancellationToken ct = default)
    {
        var all = new List<ModelInfoDto>();
        string? after = null;
        for (var page = 0; page < 20; page++)
        {
            var url = "/v1/models?limit=1000" + (after == null ? "" : "&after_id=" + Uri.EscapeDataString(after));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", ApiVersion);

            HttpResponseMessage resp;
            try { resp = await http.SendAsync(req, ct); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new ModelsFetchResult(ModelsFetchStatus.Unavailable, [], "network_error");
            }

            using (resp)
            {
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return new ModelsFetchResult(ModelsFetchStatus.Unauthorized, [], $"http_{(int)resp.StatusCode}");
                if (!resp.IsSuccessStatusCode)
                    return new ModelsFetchResult(ModelsFetchStatus.Unavailable, [], $"http_{(int)resp.StatusCode}");

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                foreach (var m in root.GetProperty("data").EnumerateArray()) all.Add(Parse(m));
                if (root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True
                    && root.TryGetProperty("last_id", out var last) && last.GetString() is { } lastId)
                    after = lastId;
                else
                    break;
            }
        }
        return new ModelsFetchResult(ModelsFetchStatus.Ok, all);
    }

    public static ModelInfoDto Parse(JsonElement m)
    {
        static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        static DateTimeOffset? Date(string? s) => DateTimeOffset.TryParse(s, out var d) ? d : null;
        static bool Supported(JsonElement e, params string[] path)
        {
            foreach (var p in path)
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return false;
            return e.ValueKind == JsonValueKind.True;
        }

        var caps = m.TryGetProperty("capabilities", out var c) ? c : default;
        return new ModelInfoDto(
            Str(m, "id") ?? "", Str(m, "display_name") ?? Str(m, "id") ?? "", Date(Str(m, "created_at")),
            Str(m, "lifecycle") ?? "active", Str(m, "line"), Int(m, "max_tokens"), Int(m, "max_input_tokens"),
            Date(Str(m, "retires_at")),
            Supported(caps, "server_tools", "web_search", "supported"),
            Supported(caps, "code_execution", "supported"));
    }
}
