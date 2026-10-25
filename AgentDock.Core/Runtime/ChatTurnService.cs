using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentDock.Core.Data;
using AgentDock.Core.Mcp;
using AgentDock.Core.Models;
using AgentDock.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentDock.Core.Runtime;

public sealed record WidgetEvent(string Name, JsonObject Data);
public sealed record TurnRequest(Agent Agent, VisitorSession Session, Conversation Conversation, string Text, string? ModelFamily, string? ClientIp);
public sealed record TurnRejection(int Status, string Code, string Message);

public sealed class TurnHandle(Guid turnId, ChannelReader<WidgetEvent> events, CancellationTokenSource cts)
{
    public Guid TurnId { get; } = turnId;
    public ChannelReader<WidgetEvent> Events { get; } = events;
    public Task Completion { get; internal set; } = Task.CompletedTask;
    public void Cancel() { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
}

public sealed record PrepareResult(TurnRejection? Rejection, TurnHandle? Handle);

/// <summary>In-process map of running turns so Stop can cancel the upstream request.</summary>
public sealed class ActiveTurnRegistry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TurnHandle> _turns = new();
    internal void Add(Guid conversationId, TurnHandle h) => _turns[conversationId] = h;
    internal void Remove(Guid conversationId, TurnHandle h) => _turns.TryRemove(new KeyValuePair<Guid, TurnHandle>(conversationId, h));
    public bool Cancel(Guid conversationId)
    {
        if (!_turns.TryGetValue(conversationId, out var h)) return false;
        h.Cancel();
        return true;
    }
}

public interface IChatTurnService
{
    Task<PrepareResult> PrepareAsync(TurnRequest request, CancellationToken ct);
    /// <summary>Decides a pending tool approval and, once every approval of the paused turn is decided, resumes the model loop.</summary>
    Task<PrepareResult> PrepareApprovalAsync(TurnRequest request, Guid toolExecutionId, bool approve, CancellationToken ct);
}

public sealed record ResumeInfo(ClaimResult Claim);

public sealed class ChatTurnService(
    IClaudeGateway gateway,
    IConversationService conversations,
    IAnthropicCredentialService credentials,
    IUsageService usage,
    AgentDock.Core.Services.ISkillService skills,
    SlidingWindowLimiter limiter,
    IToolRegistryFactory toolRegistries,
    IToolExecutor toolExecutor,
    IApprovalService approvals,
    IServiceScopeFactory scopes,
    ActiveTurnRegistry registry,
    TimeProvider time,
    ILogger<ChatTurnService> logger) : IChatTurnService
{
    public const int MaxIterations = 8;
    public const int MaxToolCallsPerTurn = 10;
    public const int MaxParallelTools = 4;
    public static readonly TimeSpan TurnDeadline = TimeSpan.FromSeconds(120);

    public async Task<PrepareResult> PrepareAsync(TurnRequest r, CancellationToken ct)
    {
        var limits = AgentLimits.FromJson(r.Agent.LimitsJson);
        var text = (r.Text ?? "").Trim();
        if (text.Length == 0) return Reject(400, "empty_message", "Type a message first.");
        if (text.Length > limits.MaxMessageChars) return Reject(400, "message_too_long", $"Messages are limited to {limits.MaxMessageChars} characters.");
        if (r.Agent.Status != "Enabled" && r.Session.Mode != "Preview") return Reject(404, "agent_unavailable", "This assistant is not available.");

        var key = await credentials.RevealActiveKeyAsync(r.Agent.AccountId, ct);
        if (key == null) return Reject(503, "agent_unavailable", "This assistant is temporarily unavailable.");

        var turnId = Guid.CreateVersion7();
        if (!await conversations.TryBeginTurnAsync(r.Conversation, turnId, TurnDeadline + TimeSpan.FromSeconds(30), ct))
            return Reject(409, "turn_in_progress", "A response is already in progress.");

        var ok = false;
        try
        {
            if (await approvals.SettleExpiredAsync(r.Conversation, ct))
                return Reject(409, "approval_pending", "Please approve or deny the pending action first.");

            var ip = r.ClientIp ?? "unknown";
            if (!limiter.TryAcquire($"s:{r.Session.Id}", limits.TurnsPerSessionPerHour, TimeSpan.FromHours(1))
                || !limiter.TryAcquire($"i:{r.Agent.Id}:{ip}", limits.TurnsPerIpPerHour, TimeSpan.FromHours(1)))
            {
                await usage.RecordRejectedAsync(r.Agent.AccountId, r.Agent.Id, ct);
                return Reject(429, "rate_limited", "You're sending messages too quickly. Please try again later.");
            }
            if (!await usage.TryReserveTurnAsync(r.Agent.AccountId, r.Agent.Id, limits.TurnsPerDay, ct))
            {
                await usage.RecordRejectedAsync(r.Agent.AccountId, r.Agent.Id, ct);
                return Reject(429, "daily_limit", "This assistant has reached its daily limit. Please try again tomorrow.");
            }

            var model = await ResolveModelAsync(r.Agent, r.ModelFamily, forceRefresh: false, ct);
            if (model == null) return Reject(503, "agent_unavailable", "This assistant is temporarily unavailable.");

            await conversations.AppendAsync(r.Conversation, new ConversationMessage
            {
                TurnId = turnId, Role = "user", DisplayText = text,
                ContentJson = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }).ToJsonString(),
            }, ct);

            var channel = Channel.CreateUnbounded<WidgetEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            var cts = new CancellationTokenSource();
            var handle = new TurnHandle(turnId, channel.Reader, cts);
            registry.Add(r.Conversation.Id, handle);
            ok = true;
            handle.Completion = Task.Run(() => ExecuteAsync(r, key, model, turnId, channel.Writer, cts, handle), CancellationToken.None);
            return new PrepareResult(null, handle);
        }
        finally
        {
            if (!ok) await conversations.EndTurnAsync(r.Conversation, turnId, CancellationToken.None);
        }
    }

    public async Task<PrepareResult> PrepareApprovalAsync(TurnRequest r, Guid toolExecutionId, bool approve, CancellationToken ct)
    {
        if (r.Agent.Status != "Enabled" && r.Session.Mode != "Preview") return Reject(404, "agent_unavailable", "This assistant is not available.");
        var row = await approvals.FindAsync(r.Conversation, toolExecutionId, ct);
        if (row == null) return Reject(404, "not_found", "Not found.");
        if (row.Status != "PendingApproval") return Reject(200, "already_decided", row.Status);

        var key = await credentials.RevealActiveKeyAsync(r.Agent.AccountId, ct);
        if (key == null) return Reject(503, "agent_unavailable", "This assistant is temporarily unavailable.");

        var turnId = row.TurnId;
        if (!await conversations.TryBeginTurnAsync(r.Conversation, turnId, TurnDeadline + TimeSpan.FromSeconds(30), ct))
            return Reject(409, "turn_in_progress", "Another action is in progress. Please try again in a moment.");

        var ok = false;
        try
        {
            var model = await ResolveModelAsync(r.Agent, null, forceRefresh: false, ct);
            if (model == null) return Reject(503, "agent_unavailable", "This assistant is temporarily unavailable.");
            var claim = await approvals.ClaimAsync(r.Conversation, toolExecutionId, approve, ct);
            if (claim.Kind == ClaimKind.NotFound) return Reject(404, "not_found", "Not found.");
            if (claim.Kind == ClaimKind.AlreadyDecided) return Reject(200, "already_decided", claim.CurrentStatus ?? "");

            var channel = Channel.CreateUnbounded<WidgetEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            var cts = new CancellationTokenSource();
            var handle = new TurnHandle(turnId, channel.Reader, cts);
            registry.Add(r.Conversation.Id, handle);
            ok = true;
            handle.Completion = Task.Run(() => ExecuteAsync(r, key, model, turnId, channel.Writer, cts, handle, new ResumeInfo(claim)), CancellationToken.None);
            return new PrepareResult(null, handle);
        }
        finally
        {
            if (!ok) await conversations.EndTurnAsync(r.Conversation, turnId, CancellationToken.None);
        }
    }

    private static PrepareResult Reject(int status, string code, string message) =>
        new(new TurnRejection(status, code, message), null);

    private async Task ExecuteAsync(
        TurnRequest r, string apiKey, ResolvedModel model, Guid turnId,
        ChannelWriter<WidgetEvent> output, CancellationTokenSource cts, TurnHandle handle, ResumeInfo? resume = null)
    {
        using var deadline = new CancellationTokenSource(TurnDeadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, deadline.Token);
        var ct = linked.Token;
        StreamingContentAccumulator? acc = null;
        var persistedPartial = false;
        long inTok = 0, outTok = 0;
        int searches = 0, toolCalls = 0;

        void Emit(string name, JsonObject data) { lock (output) output.TryWrite(new WidgetEvent(name, data)); }

        try
        {
            Emit("turn.started", new JsonObject
            {
                ["turnId"] = turnId, ["conversationId"] = r.Conversation.Id,
                ["model"] = new JsonObject { ["family"] = model.Family.ToString().ToLowerInvariant(), ["name"] = model.DisplayName },
            });

            var maxTokens = Math.Max(256, Math.Min(r.Agent.MaxOutputTokens, model.MaxOutputTokens));
            var skillFiles = await skills.ForAgentAsync(r.Agent.AccountId, r.Agent.Id, CancellationToken.None);
            var system = InstructionAssembler.Assemble(r.Agent.SystemPrompt,
                skillFiles.Select(f => new SkillText(f.DisplayName, f.FileName, f.Version, f.Content)).ToList(), time.GetUtcNow());
            var toolRegistry = await toolRegistries.BuildAsync(r.Agent.AccountId, r.Agent.Id, CancellationToken.None);
            var toolCtx = new ToolCallContext(r.Agent.AccountId, r.Agent.Id, r.Conversation.Id, turnId);
            if (resume != null)
            {
                var row = resume.Claim.Row!;
                if (resume.Claim.Kind == ClaimKind.Approved)
                {
                    var shown = toolRegistry.TryGet(row.ExposedName, out var approvedTool) ? approvedTool.DisplayName : "tool";
                    Emit("tool.started", new JsonObject { ["name"] = shown });
                    toolCalls++;
                    var outcome = await toolExecutor.RunApprovedAsync(toolCtx, toolRegistry, row.Id, ct);
                    if (outcome.IsError) Emit("tool.failed", new JsonObject { ["name"] = shown, ["message"] = outcome.PublicError ?? "The tool call failed." });
                    else Emit("tool.completed", new JsonObject { ["name"] = shown });
                }
                else
                {
                    Emit("approval.decided", new JsonObject { ["toolExecutionId"] = row.Id, ["status"] = resume.Claim.Kind == ClaimKind.Expired ? "Expired" : "Denied" });
                }
                if (!await approvals.TryAssembleAsync(r.Conversation, CancellationToken.None))
                {
                    Emit("message.completed", new JsonObject { ["stopReason"] = "awaiting_approval" });
                    return;
                }
            }

            var retriedModel = false;
            var citations = new CitationRegistry();
            var emittedSources = 0;

            for (var iteration = 1; iteration <= MaxIterations; iteration++)
            {
                var history = await conversations.HistoryAsync(r.Conversation, ConversationService.DefaultHistoryChars, CancellationToken.None);
                var searchTool = WebSearchTool.Build(r.Agent, model);
                var allTools = new List<JsonObject>();
                if (searchTool != null) allTools.Add(searchTool);
                allTools.AddRange(toolRegistry.ClaudeToolDefinitions());
                var request = new ClaudeRequest(model.ModelId, maxTokens, system, history, allTools.Count == 0 ? null : allTools);
                acc = new StreamingContentAccumulator(citations);
                persistedPartial = false;
                try
                {
                    await foreach (var ev in gateway.StreamAsync(request, apiKey, ct))
                    {
                        var delta = acc.Apply(ev);
                        foreach (var (name, data) in SearchEvents(ev, acc)) Emit(name, data);
                        for (; emittedSources < citations.Sources.Count; emittedSources++)
                        {
                            var src = citations.Sources[emittedSources];
                            Emit("citation", new JsonObject { ["index"] = src.Index, ["url"] = src.Url, ["title"] = src.Title, ["citedText"] = src.CitedText });
                        }
                        if (!string.IsNullOrEmpty(delta)) Emit("text.delta", new JsonObject { ["text"] = delta });
                        if (acc.ErrorType != null) break;
                    }
                }
                catch (ClaudeApiException e) when (e.IsModelNotFound && !retriedModel && acc.Blocks.Count == 0)
                {
                    retriedModel = true;
                    var again = await ResolveModelAsync(r.Agent, r.ModelFamily, forceRefresh: true, CancellationToken.None);
                    if (again == null) throw;
                    model = again;
                    request = request with { ModelId = model.ModelId };
                    iteration--;
                    continue;
                }

                inTok += acc.InputTokens; outTok += acc.OutputTokens; searches += acc.WebSearchRequests;

                if (acc.ErrorType != null)
                {
                    await PersistAssistantAsync(r, turnId, model, acc, incomplete: true);
                    persistedPartial = true;
                    EmitError(output, acc.ErrorType is "overloaded_error" or "rate_limit_error" ? "busy" : "upstream_error");
                    return;
                }

                var msg = await PersistAssistantAsync(r, turnId, model, acc, incomplete: false);
                if (acc.StopReason == "pause_turn") continue;
                var toolUses = acc.ClientToolUses();
                if (acc.StopReason == "tool_use" && toolUses.Count > 0)
                {
                    var outcomes = new ToolOutcome?[toolUses.Count];
                    using var gate = new SemaphoreSlim(MaxParallelTools);
                    await Task.WhenAll(toolUses.Select(async (t, i) =>
                    {
                        var toolName = t["name"]?.GetValue<string>() ?? "";
                        if (Interlocked.Increment(ref toolCalls) > MaxToolCallsPerTurn)
                        {
                            outcomes[i] = new ToolOutcome(toolName, true, "Too many tool calls in this turn.", "Blocked", "Too many tool calls in this turn.");
                            return;
                        }
                        var known = toolRegistry.TryGet(toolName, out var rt);
                        var shown = known ? rt.DisplayName : "tool";
                        await gate.WaitAsync(ct);
                        try
                        {
                            if (known && !rt.RequiresConfirmation) Emit("tool.started", new JsonObject { ["name"] = shown });
                            await using var scope = scopes.CreateAsyncScope();
                            var executor = scope.ServiceProvider.GetRequiredService<IToolExecutor>();
                            var outcome = await executor.ExecuteAsync(toolCtx, toolRegistry, t["id"]?.GetValue<string>() ?? "", toolName, t["input"]?.DeepClone(), ct);
                            outcomes[i] = outcome;
                            if (known && !outcome.IsPending)
                            {
                                if (outcome.IsError) Emit("tool.failed", new JsonObject { ["name"] = shown, ["message"] = outcome.PublicError ?? "The tool call failed." });
                                else Emit("tool.completed", new JsonObject { ["name"] = shown });
                            }
                        }
                        finally { gate.Release(); }
                    }));

                    if (outcomes.Any(o => o!.IsPending))
                    {
                        await approvals.ParkAsync(toolCtx, toolUses.Select((t, i) => new ParkedResult(t["id"]?.GetValue<string>() ?? "", t["name"]?.GetValue<string>() ?? "", outcomes[i]!)).ToList(), CancellationToken.None);
                        foreach (var o in outcomes.Where(o => o!.IsPending))
                            Emit("approval.required", new JsonObject
                            {
                                ["toolExecutionId"] = o!.ExecutionId, ["name"] = o.DisplayName, ["summary"] = o.ApprovalSummary ?? "", ["expiresUtc"] = o.ApprovalExpiresUtc,
                            });
                        Emit("message.completed", new JsonObject { ["messageId"] = msg.Id, ["stopReason"] = "awaiting_approval" });
                        return;
                    }

                    var results = new JsonArray(toolUses.Select((t, i) => (JsonNode?)new JsonObject
                    {
                        ["type"] = "tool_result", ["tool_use_id"] = t["id"]?.GetValue<string>(),
                        ["content"] = outcomes[i]!.Content, ["is_error"] = outcomes[i]!.IsError,
                    }).ToArray());
                    await conversations.AppendAsync(r.Conversation, new ConversationMessage
                    {
                        TurnId = turnId, Role = "user", ContentJson = results.ToJsonString(),
                    }, CancellationToken.None);
                    continue;
                }

                Emit("message.completed", new JsonObject { ["messageId"] = msg.Id, ["stopReason"] = acc.StopReason });
                return;
            }

            EmitError(output, "iteration_limit");
        }
        catch (OperationCanceledException)
        {
            if (acc != null && !persistedPartial && acc.DisplayText().Length > 0)
                await PersistAssistantAsync(r, turnId, model, acc, incomplete: true);
            if (deadline.IsCancellationRequested && !cts.IsCancellationRequested)
                EmitError(output, "timeout");
            else
                Emit("message.completed", new JsonObject { ["stopReason"] = "cancelled", ["incomplete"] = true });
        }
        catch (ClaudeApiException e)
        {
            if (acc != null && !persistedPartial && acc.DisplayText().Length > 0)
                await PersistAssistantAsync(r, turnId, model, acc, incomplete: true);
            logger.LogWarning("Claude call failed for agent {Agent}: {Status} {Type}", r.Agent.Id, e.StatusCode, e.ErrorType);
            if (e.IsWebSearchDisabled)
            {
                logger.LogWarning("Web search is disabled for the Anthropic organization behind agent {Agent}", r.Agent.Id);
                EmitError(output, "agent_unavailable");
            }
            else if (e.IsAuth)
            {
                await credentials.MarkInvalidAsync(r.Agent.AccountId, "messages_" + e.StatusCode, CancellationToken.None);
                EmitError(output, "agent_unavailable");
            }
            else EmitError(output, e.IsBusy ? "busy" : "upstream_error");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Turn {Turn} failed", turnId);
            if (acc != null && !persistedPartial && acc.DisplayText().Length > 0)
                try { await PersistAssistantAsync(r, turnId, model, acc, incomplete: true); } catch { }
            EmitError(output, "internal_error");
        }
        finally
        {
            try
            {
                await usage.AddTokensAsync(r.Agent.Id, inTok, outTok, searches, toolCalls, CancellationToken.None);
                await conversations.EndTurnAsync(r.Conversation, turnId, CancellationToken.None);
            }
            catch (Exception e) { logger.LogError(e, "Turn cleanup failed for {Turn}", turnId); }
            registry.Remove(r.Conversation.Id, handle);
            output.TryWrite(new WidgetEvent("done", new JsonObject()));
            output.TryComplete();
        }
    }

    private static IEnumerable<(string Name, JsonObject Data)> SearchEvents(JsonObject ev, StreamingContentAccumulator acc)
    {
        switch (ev["type"]?.GetValue<string>())
        {
            case "content_block_start" when ev["content_block"] is JsonObject b && b["type"]?.GetValue<string>() == "web_search_tool_result":
                if (b["content"] is JsonArray results)
                    yield return ("search.completed", new JsonObject { ["resultCount"] = results.Count });
                else
                    yield return ("search.failed", new JsonObject { ["code"] = b["content"]?["error_code"]?.GetValue<string>() ?? "unavailable" });
                break;
            case "content_block_stop" when acc.BlockAt(ev["index"]?.GetValue<int>() ?? -1) is { } block
                && block["type"]?.GetValue<string>() == "server_tool_use" && block["name"]?.GetValue<string>() == "web_search":
                var query = block["input"]?["query"]?.GetValue<string>() ?? "";
                yield return ("search.started", new JsonObject { ["query"] = query.Length > 200 ? query[..200] : query });
                break;
        }
    }

    private static void EmitError(ChannelWriter<WidgetEvent> output, string code) =>
        output.TryWrite(new WidgetEvent("error", new JsonObject { ["code"] = code, ["message"] = ErrorText(code) }));

    public static string ErrorText(string code) => code switch
    {
        "busy" => "The assistant is busy right now. Please try again in a moment.",
        "timeout" => "The response took too long and was stopped.",
        "iteration_limit" => "The assistant could not finish this request.",
        "agent_unavailable" => "This assistant is temporarily unavailable.",
        _ => "Something went wrong. Please try again.",
    };

    private async Task<ConversationMessage> PersistAssistantAsync(
        TurnRequest r, Guid turnId, ResolvedModel model, StreamingContentAccumulator acc, bool incomplete) =>
        await conversations.AppendAsync(r.Conversation, new ConversationMessage
        {
            TurnId = turnId, Role = "assistant", ContentJson = acc.ContentArray().ToJsonString(), DisplayText = acc.DisplayText(),
            ModelId = acc.ModelId ?? model.ModelId, ModelFamily = model.Family.ToString().ToLowerInvariant(),
            StopReason = acc.StopReason, InputTokens = acc.InputTokens, OutputTokens = acc.OutputTokens,
            CacheReadTokens = acc.CacheReadTokens, CacheWriteTokens = acc.CacheWriteTokens,
            WebSearchRequests = acc.WebSearchRequests, IsIncomplete = incomplete,
            CitationsJson = acc.UsedSources.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(acc.UsedSources),
        }, CancellationToken.None);

    private async Task<ResolvedModel?> ResolveModelAsync(Agent agent, string? requestedFamily, bool forceRefresh, CancellationToken ct)
    {
        var catalog = await credentials.GetCatalogAsync(agent.AccountId, forceRefresh, ct);
        if (!catalog.Available) return null;

        var allowed = agent.AllowedModelFamilies.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => Enum.TryParse<ModelFamily>(f, true, out var v) ? v : (ModelFamily?)null).Where(v => v != null).Select(v => v!.Value).ToHashSet();
        ModelFamily? chosen = null;
        if (agent.ShowModelSelector && Enum.TryParse<ModelFamily>(requestedFamily, true, out var req) && allowed.Contains(req)) chosen = req;

        if (chosen == null && !string.IsNullOrEmpty(agent.PinnedModelId) && catalog.Models != null
            && ModelResolver.ResolvePinned(catalog.Models, agent.PinnedModelId) is { } pinned)
            return pinned;

        var preferred = chosen ?? (Enum.TryParse<ModelFamily>(agent.DefaultModelFamily, true, out var d) ? d : ModelFamily.Sonnet);
        var family = ModelResolver.FallbackOrder(preferred, catalog.Families);
        return family == null ? null : catalog.Families[family.Value];
    }
}
