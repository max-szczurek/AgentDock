# CLAUDE.md (AgentDock)

SaaS for creating Claude chat agents and embedding them in other web apps (first host: Engager). Full design: [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) — read §0 first.

## Layout
- `AgentDock.Web` — ASP.NET Core Razor Pages (net10.0). Cookie auth + Google, dev sign-in, `/health`.
- `AgentDock.Core` — EF Core `AgentDockDbContext` (Identity + `Account` + Data Protection keys), `SchemaMigrator`, embedded SQL in `Migrations/NNNN_name.sql`.
- `AgentDock.Tests` — MSTest unit + integration (`WebApplicationFactory`). SQL-backed tests skip when `AGENTDOCK_SQL_CONNECTION_STRING` is unset.

## Runtime and widget (M4)
- `AgentDock.Core/Runtime`: `ClaudeGateway` (raw SSE, behind `IClaudeGateway`), `StreamingContentAccumulator`, `ChatTurnService` (bounded loop, per-conversation lease, limits), `VisitorSessionService`, `ConversationService`, `UsageService`.
- `AgentDock.Web/Widget`: `/api/widget/v1/*` endpoints, `VisitorToken` bearer auth (policy `Visitor`), `/embed/{agentId}` with per-agent CSP. Static widget in `wwwroot/widget/v1` (`agentdock.js` loader, `embed.js`, vendored marked/DOMPurify).
- Tests fake the model with `FakeClaudeGateway` (`Infrastructure/FakeClaude.cs`); the live Haiku smoke test needs `AGENTDOCK_CLAUDE_API_KEY`.
- Manual widget test: serve a page that loads `agentdock.js` from another local origin (e.g. `python3 -m http.server 3000`) and list that origin on the agent.
- Web search (M6): `WebSearchTool` builds the server tool, `CitationRegistry` + `StreamingContentAccumulator` insert `⟦n⟧` markers, `WebSearchProbe` backs the Edit page test button.
- Skills (M7): `SkillService`/`SkillRules` (limits, strict UTF-8), `InstructionAssembler.Assemble(prompt, skills, now)`, pages under `Pages/Skills`, assignment on the agent Edit page, Markdig preview in `Web/Services/SkillMarkdown`.
- Outbound (M8): all user-configured outbound calls (MCP, HTTP tools) must go through `IOutboundHttpClientFactory` (`AgentDock.Core/Outbound`); never create a raw `HttpClient` for user-supplied URLs.
- MCP tools (M9): the model only sees tools in the per-turn `TurnToolRegistry` (DB allowlist); every `tool_use` goes through `IToolExecutor`, which blocks unknown names, validates input, re-checks the connection, and records a `ToolExecution`. Never call an MCP server except through `IMcpGateway`.

## Commands
```bash
dotnet build apps/AgentDock/AgentDock.slnx
dotnet test apps/AgentDock/AgentDock.Tests
dotnet run --project apps/AgentDock/AgentDock.Web      # https://localhost:7294 / http://localhost:5294
```
Configuration uses flat env/app-setting names: `AGENTDOCK_SQL_CONNECTION_STRING` (needs `Trusted_Connection=False` on Linux; startup fails fast otherwise), `AGENTDOCK_GOOGLE_CLIENT_ID/SECRET`, `AGENTDOCK_DATAPROTECTION_KEY_ID`, `AGENTDOCK_KEYVAULT_KEY_ID`, `AGENTDOCK_ALLOWED_EMAILS`, `AGENTDOCK_DEV_LOGIN_SECRET`, `AGENTDOCK_DEV_LOGIN_EMAILS`.

## Database rules (one shared `AgentDockDB` for dev/tests/staging)
- Migrations are additive and idempotent (tracked in `SchemaVersion`, serialised with `sp_getapplock`). Never rename/drop in the same release that stops using an object.
- Tests create `test-{guid}@agentdock.test` users and delete them; `TestDataJanitor` clears stale ones.

## Pre-launch access controls (temporary, plan §0.4)
- Sign-in allowlist `AGENTDOCK_ALLOWED_EMAILS`; empty = deny all except in Development.
- Dev sign-in at `/dev/login`: only in Development/Staging, needs `AGENTDOCK_DEV_LOGIN_SECRET` (>=32 chars), 404 otherwise.
  Scripted: `curl -c jar -X POST -H "X-Dev-Login-Secret: $S" -d "email=dev-agent@agentdock.dev" $BASE/dev/login`, then reuse the cookie jar.
  Browser: open `/dev/login`, enter the secret, pick `dev-agent@agentdock.dev`. A red "DEV LOGIN ACTIVE" banner is shown while enabled.
  Never print the secret. Read it via ARM from the `agentdock-staging` app settings (plan §0.5) or generate a fresh one locally.

## Approvals and session IDs (M10)
- Confirmation tools park in `ToolExecution` (`PendingApproval`, 15 min expiry); `ApprovalService` claims/settles/assembles; resume via `ChatTurnService.PrepareApprovalAsync`. Expired = denied.
- Developer session IDs: optional `data-session-id` GUID, stored as `ExternalSessionId`, scoped per agent, bearer-secret semantics; never log it.

## HTTP tools (M11)
- A `Connection` has `Kind` `Mcp` or `Http` (fixed at creation). `HttpTool` = model-facing `InputSchemaJson` + server-side `ExecutionConfigJson`; the connection holds URL and credentials. Exposed to the model as `http__{name}`.
- Runtime lives in `AgentDock.Core/HttpTools` (`HttpToolRules`, `HttpToolRunner`, `HttpToolService`); `ToolExecutor` dispatches on `RegisteredTool.Http`. Never put credentials in static query, headers or the schema.
