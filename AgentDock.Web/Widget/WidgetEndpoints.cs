using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentDock.Core.Data;
using AgentDock.Core.Models;
using AgentDock.Core.Runtime;
using AgentDock.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace AgentDock.Web.Widget;

public sealed record SessionRequest(string? HostOrigin);
public sealed record TurnBody(string? Text, string? Model);

public static class WidgetEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string BuildCsp(IEnumerable<string> origins, bool preview)
    {
        var ancestors = "'self'" + (preview ? "" : string.Concat(origins.OrderBy(o => o, StringComparer.Ordinal).Select(o => " " + o)));
        return "default-src 'self'; script-src 'self'; connect-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
               $"base-uri 'none'; form-action 'none'; frame-ancestors {ancestors}";
    }

    public static void MapWidget(this WebApplication app)
    {
        app.MapGet("/embed/{agentId:guid}", EmbedAsync).AllowAnonymous();
        app.MapMethods("/api/widget/v1/agents/{agentId:guid}/config", ["GET", "OPTIONS"], ConfigAsync).AllowAnonymous();
        app.MapPost("/api/widget/v1/agents/{agentId:guid}/sessions", CreateSessionAsync).AllowAnonymous();

        var api = app.MapGroup("/api/widget/v1").RequireAuthorization(VisitorAuth.Policy);
        api.MapGet("/conversations", ListConversationsAsync);
        api.MapPost("/conversations", CreateConversationAsync);
        api.MapGet("/conversations/{id:guid}/messages", MessagesAsync);
        api.MapPost("/conversations/{id:guid}/turns", TurnAsync);
        api.MapPost("/conversations/{id:guid}/cancel", CancelAsync);
    }

    private static IResult ErrorJson(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: status);

    private static async Task<Agent?> LoadAgentAsync(AgentDockDbContext db, Guid agentId, CancellationToken ct) =>
        await db.Agents.AsNoTracking().Include(a => a.Origins).FirstOrDefaultAsync(a => a.Id == agentId && a.DeletedUtc == null, ct);

    private static async Task<IResult> EmbedAsync(Guid agentId, bool? preview, HttpContext ctx, AgentDockDbContext db, CancellationToken ct)
    {
        var agent = await LoadAgentAsync(db, agentId, ct);
        var isPreview = preview == true;
        if (agent == null || (agent.Status != "Enabled" && !isPreview)) return Results.NotFound();

        var headers = ctx.Response.Headers;
        headers["Content-Security-Policy"] = BuildCsp(agent.Origins.Select(o => o.Origin), isPreview);
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Cache-Control"] = "no-store";
        var html = $$"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Chat</title><link rel="stylesheet" href="/widget/v1/embed.css"></head>
            <body><div id="app" data-agent-id="{{agentId}}" data-preview="{{(isPreview ? "1" : "0")}}"></div>
            <script src="/widget/v1/vendor/marked.min.js"></script>
            <script src="/widget/v1/vendor/purify.min.js"></script>
            <script src="/widget/v1/embed.js"></script></body></html>
            """;
        return Results.Content(html, "text/html", Encoding.UTF8);
    }

    private static async Task<IResult> ConfigAsync(Guid agentId, HttpContext ctx, AgentDockDbContext db, IVisitorSessionService sessions, CancellationToken ct)
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "authorization, content-type";
        if (HttpMethods.IsOptions(ctx.Request.Method)) return Results.NoContent();

        var agent = await LoadAgentAsync(db, agentId, ct);
        if (agent == null) return Results.NotFound();
        if (agent.Status != "Enabled")
        {
            var bearer = ctx.Request.Headers.Authorization.ToString();
            var s = bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? await sessions.AuthenticateAsync(bearer[7..].Trim(), ct) : null;
            if (s == null || s.AgentId != agent.Id || s.Mode != "Preview") return Results.NotFound();
        }

        var a = AppearanceSettings.FromJson(agent.AppearanceJson);
        var allowed = agent.AllowedModelFamilies.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(f => f.ToLowerInvariant()).ToArray();
        ctx.Response.Headers["Cache-Control"] = "no-store";
        return Results.Json(new
        {
            agentId = agent.Id, name = agent.Name, appearance = a,
            identityMode = agent.IdentityMode,
            modelSelector = agent.ShowModelSelector ? new { families = allowed, defaultFamily = agent.DefaultModelFamily.ToLowerInvariant() } : null,
            maxMessageChars = AgentLimits.FromJson(agent.LimitsJson).MaxMessageChars,
        }, Json);
    }

    private static async Task<IResult> CreateSessionAsync(
        Guid agentId, SessionRequest body, HttpContext ctx, AgentDockDbContext db,
        IVisitorSessionService sessions, SlidingWindowLimiter limiter, CancellationToken ct)
    {
        if (ctx.Request.Headers["Sec-Fetch-Site"].ToString() != "same-origin")
            return ErrorJson(403, "forbidden", "Sessions can only be created from the chat frame.");
        var selfOrigin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !string.Equals(origin, selfOrigin, StringComparison.OrdinalIgnoreCase))
            return ErrorJson(403, "forbidden", "Sessions can only be created from the chat frame.");

        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!limiter.TryAcquire($"sess:{ip}", 10, TimeSpan.FromMinutes(1)))
            return ErrorJson(429, "rate_limited", "Too many sessions. Please try again shortly.");

        var agent = await LoadAgentAsync(db, agentId, ct);
        if (agent == null || agent.Status != "Enabled") return ErrorJson(404, "agent_unavailable", "This assistant is not available.");
        if (agent.IdentityMode != "Public") return ErrorJson(403, "identity_required", "This assistant requires a signed-in visitor.");

        if (!OriginNormalizer.TryNormalize(body?.HostOrigin, out var host, out _)
            || !agent.Origins.Any(o => o.Origin == host))
            return ErrorJson(403, "origin_not_allowed", "This site is not allowed to embed the assistant.");

        var issued = await sessions.CreateAsync(agent, "Public", host, ip, ct);
        return Results.Json(new { token = issued.Token, expiresUtc = issued.Session.ExpiresUtc }, Json);
    }

    private static async Task<IResult> ListConversationsAsync(HttpContext ctx, IConversationService conversations, CancellationToken ct) =>
        Results.Json(await conversations.ListAsync(ctx.Session(), ct), Json);

    private static async Task<IResult> CreateConversationAsync(HttpContext ctx, IConversationService conversations, CancellationToken ct)
    {
        var c = await conversations.CreateAsync(ctx.Session(), ct);
        return Results.Json(new { id = c.Id }, Json, statusCode: 201);
    }

    private static async Task<IResult> MessagesAsync(Guid id, HttpContext ctx, IConversationService conversations, CancellationToken ct)
    {
        var c = await conversations.GetOwnedAsync(ctx.Session(), id, ct);
        if (c == null) return Results.NotFound();
        return Results.Json(new { messages = await conversations.MessagesAsync(c, ct) }, Json);
    }

    private static async Task<IResult> CancelAsync(Guid id, HttpContext ctx, IConversationService conversations, ActiveTurnRegistry registry, CancellationToken ct)
    {
        var c = await conversations.GetOwnedAsync(ctx.Session(), id, ct);
        if (c == null) return Results.NotFound();
        return Results.Json(new { cancelled = registry.Cancel(c.Id) }, Json);
    }

    private static async Task<IResult> TurnAsync(
        Guid id, TurnBody body, HttpContext ctx, IConversationService conversations, IChatTurnService turns,
        AgentDockDbContext db, CancellationToken ct)
    {
        var session = ctx.Session();
        var conversation = await conversations.GetOwnedAsync(session, id, ct);
        if (conversation == null) return Results.NotFound();
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == session.AgentId && a.DeletedUtc == null, ct);
        if (agent == null) return Results.NotFound();

        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        var prepared = await turns.PrepareAsync(new TurnRequest(agent, session, conversation, body?.Text ?? "", body?.Model, ip), ct);
        if (prepared.Rejection is { } rej) return ErrorJson(rej.Status, rej.Code, rej.Message);

        var handle = prepared.Handle!;
        ctx.RequestAborted.Register(handle.Cancel);
        var res = ctx.Response;
        res.StatusCode = 200;
        res.Headers.ContentType = "text/event-stream";
        res.Headers.CacheControl = "no-cache";
        res.Headers["X-Accel-Buffering"] = "no";
        await res.Body.FlushAsync(CancellationToken.None);

        var alive = true;
        async Task WriteAsync(string text)
        {
            if (!alive) return;
            try { await res.WriteAsync(text, CancellationToken.None); await res.Body.FlushAsync(CancellationToken.None); }
            catch { alive = false; handle.Cancel(); }
        }

        var reader = handle.Events;
        while (true)
        {
            using var tick = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                if (!await reader.WaitToReadAsync(tick.Token)) break;
            }
            catch (OperationCanceledException)
            {
                await WriteAsync(": ping\n\n");
                continue;
            }
            while (reader.TryRead(out var ev))
                await WriteAsync($"event: {ev.Name}\ndata: {ev.Data.ToJsonString()}\n\n");
        }
        await handle.Completion;
        return Results.Empty;
    }
}
