using System.Text;
using System.Text.Json;
using AgentDock.Core.Models;
using AgentDock.Core.Security;
using AgentDock.Core.Services;
using Azure.Identity;

namespace AgentDock.Tests.Integration;

/// <summary>Opt-in smoke tests against real Key Vault and the real Anthropic API. Skipped without credentials.</summary>
[TestClass]
public class LiveTests
{
    [TestMethod]
    public async Task KeyVault_WrapUnwrap_RoundTrip_StoresKeyVersionUri()
    {
        var creds = Environment.GetEnvironmentVariable("AZURE_CREDENTIALS_AGENTDOCK");
        var kek = Environment.GetEnvironmentVariable("AGENTDOCK_KEYVAULT_KEY_ID");
        if (string.IsNullOrWhiteSpace(creds) || string.IsNullOrWhiteSpace(kek)) Assert.Inconclusive("Key Vault credentials not set.");

        using var doc = JsonDocument.Parse(creds);
        var r = doc.RootElement;
        var cred = new ClientSecretCredential(r.GetProperty("tenantId").GetString(), r.GetProperty("clientId").GetString(), r.GetProperty("clientSecret").GetString());
        var protector = new EnvelopeSecretProtector(new KeyVaultKeyWrapper(new Uri(kek), cred));
        var aad = new SecretAad(Guid.NewGuid(), Guid.NewGuid(), SecretPurpose.AnthropicApiKey);

        var payload = await protector.ProtectAsync(Encoding.UTF8.GetBytes("live-roundtrip-value"), aad);
        StringAssert.Contains(payload.KekId, "/keys/credential-kek/");
        Assert.IsTrue(payload.KekId.Split('/').Length >= 6, "KekId includes the key version");

        var fresh = new EnvelopeSecretProtector(new KeyVaultKeyWrapper(new Uri(kek), cred));
        Assert.AreEqual("live-roundtrip-value", Encoding.UTF8.GetString(await fresh.UnprotectAsync(payload, aad)));
    }

    [TestMethod]
    public async Task Anthropic_ListModels_ResolvesAllThreeFamilies()
    {
        var key = Environment.GetEnvironmentVariable("AGENTDOCK_CLAUDE_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) Assert.Inconclusive("AGENTDOCK_CLAUDE_API_KEY not set.");

        using var http = new HttpClient { BaseAddress = new Uri("https://api.anthropic.com") };
        var result = await new AnthropicModelsClient(http).ListModelsAsync(key);
        Assert.AreEqual(ModelsFetchStatus.Ok, result.Status);
        var families = ModelResolver.ResolveFamilies(result.Models, DateTimeOffset.UtcNow);
        foreach (var f in Enum.GetValues<ModelFamily>())
        {
            Assert.IsTrue(families.ContainsKey(f), $"{f} resolved");
            Console.WriteLine($"{f}: {families[f].ModelId} (created {families[f].CreatedAt:yyyy-MM-dd}, web search {families[f].SupportsWebSearch})");
        }

        var bad = await new AnthropicModelsClient(http).ListModelsAsync("sk-ant-invalid-key-0000000000000000");
        Assert.AreEqual(ModelsFetchStatus.Unauthorized, bad.Status);
    }

    [TestMethod]
    public async Task Anthropic_Messages_StreamsHaikuReply_ThroughGateway()
    {
        var key = Environment.GetEnvironmentVariable("AGENTDOCK_CLAUDE_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) Assert.Inconclusive("AGENTDOCK_CLAUDE_API_KEY not set.");

        using var http = new HttpClient { BaseAddress = new Uri("https://api.anthropic.com") };
        var models = await new AnthropicModelsClient(http).ListModelsAsync(key);
        var haiku = ModelResolver.ResolveFamilies(models.Models, DateTimeOffset.UtcNow)[ModelFamily.Haiku];

        var gateway = new AgentDock.Core.Runtime.ClaudeGateway(http);
        var request = new AgentDock.Core.Runtime.ClaudeRequest(haiku.ModelId, 64,
            AgentDock.Core.Runtime.InstructionAssembler.Assemble("Reply with one short word.", DateTimeOffset.UtcNow),
            [new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "Say hello." }]);
        var acc = new AgentDock.Core.Runtime.StreamingContentAccumulator();
        var deltas = 0;
        await foreach (var ev in gateway.StreamAsync(request, key, default))
            if (!string.IsNullOrEmpty(acc.Apply(ev))) deltas++;

        Assert.IsNull(acc.ErrorType, acc.ErrorMessage);
        Assert.IsTrue(deltas >= 1, "streamed at least one text delta");
        Assert.IsTrue(acc.DisplayText().Length > 0);
        Assert.IsTrue(acc.OutputTokens > 0);
        Console.WriteLine($"{haiku.ModelId}: \"{acc.DisplayText()}\" stop={acc.StopReason} in={acc.InputTokens} out={acc.OutputTokens} deltas={deltas}");
    }

    [TestMethod]
    public async Task Anthropic_WebSearch_ReturnsCitations_ThroughGateway()
    {
        var key = Environment.GetEnvironmentVariable("AGENTDOCK_CLAUDE_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) Assert.Inconclusive("AGENTDOCK_CLAUDE_API_KEY not set.");

        using var http = new HttpClient { BaseAddress = new Uri("https://api.anthropic.com"), Timeout = TimeSpan.FromMinutes(3) };
        var models = await new AnthropicModelsClient(http).ListModelsAsync(key);
        var haiku = ModelResolver.ResolveFamilies(models.Models, DateTimeOffset.UtcNow)[ModelFamily.Haiku];
        if (!haiku.SupportsWebSearch) Assert.Inconclusive("Resolved haiku does not support web search.");
        var agent = new AgentDock.Core.Data.Agent { WebSearchEnabled = true, WebSearchMaxUses = 2 };
        var tool = AgentDock.Core.Runtime.WebSearchTool.Build(agent, haiku)!;

        var gateway = new AgentDock.Core.Runtime.ClaudeGateway(http);
        var messages = new List<System.Text.Json.Nodes.JsonNode>
        { new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "Search the web: what is the latest stable version of .NET? One sentence." } };
        var reg = new AgentDock.Core.Runtime.CitationRegistry();
        var display = "";
        var sources = 0;
        for (var i = 0; i < 4; i++)
        {
            var acc = new AgentDock.Core.Runtime.StreamingContentAccumulator(reg);
            var request = new AgentDock.Core.Runtime.ClaudeRequest(haiku.ModelId, 1024,
                AgentDock.Core.Runtime.InstructionAssembler.Assemble("Answer using web search.", DateTimeOffset.UtcNow), messages, [tool]);
            await foreach (var ev in gateway.StreamAsync(request, key, default)) acc.Apply(ev);
            Assert.IsNull(acc.ErrorType, acc.ErrorMessage);
            display += acc.DisplayText();
            sources = Math.Max(sources, acc.UsedSources.Count);
            if (acc.StopReason != "pause_turn") break;
            messages.Add(new System.Text.Json.Nodes.JsonObject { ["role"] = "assistant", ["content"] = acc.ContentArray() });
        }
        Assert.IsTrue(sources >= 1, "at least one citation: " + display);
        Console.WriteLine($"{display} (sources={sources})");
    }
}
