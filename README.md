# AgentDock

AgentDock is a small multi-tenant web app for creating Claude chat agents and embedding them in other websites. Each account brings its own Anthropic API key, so agents run on the key of the person who owns them.

## What you can do

- Write an agent's instructions and choose which Claude models it can use.
- Attach skill files to give an agent reusable guidance.
- Turn on web search with cited sources.
- Connect MCP servers and custom HTTP tools so an agent can take actions, with confirmation before sensitive ones.
- Preview the chat, then copy an embed snippet onto any site you have allowed.

## Try it

A staging deployment is running at:

**https://agentdock-staging-fzeccvfrddemc5fj.eastus-01.azurewebsites.net/**

Sign-in uses Google. The app is still in testing on its Google OAuth credentials, so only approved accounts can sign in. To get access:

1. You need a Gmail address.
2. [Open an issue](https://github.com/max-szczurek/AgentDock/issues/new) titled "Access request" with the Gmail address you want approved. The owner will add it to the allowlist and reply on the issue.

Once you are approved, sign in with that Gmail account, add your Anthropic API key in Settings, and create your first agent.

This is an early test deployment, not a public launch. Expect rough edges, and expect data to change or be reset.

## How it fits together

- **AgentDock.Web**: ASP.NET Core Razor Pages on .NET 10. It serves the management UI, the widget API under `/api/widget/v1`, and the embeddable widget (`/embed/{agentId}` plus static files under `wwwroot/widget/v1`).
- **AgentDock.Core**: the Entity Framework Core model, the Claude runtime (streaming, tool loop, usage limits), skills, MCP and HTTP tools, and the SQL migrations.
- **AgentDock.Tests**: MSTest unit and integration tests.

Anthropic keys and connection credentials are encrypted before they are stored. A key held in Azure Key Vault wraps a per-secret data key, so the database never holds a usable secret on its own. Calls to MCP servers and HTTP tools go through a single outbound client that blocks private and self-referencing addresses.

## Azure resources

AgentDock is built to run on Azure. To host your own copy you need:

| Resource | What it is used for |
|---|---|
| App Service (Linux, .NET 10) | Runs `AgentDock.Web`. Turn on HTTPS only, Always On, and a system-assigned managed identity. Set the health check path to `/health`. |
| Azure SQL Database | Stores accounts, agents, conversations, usage, encrypted secrets, and the ASP.NET Core Data Protection key ring. Use SQL authentication with `Encrypt=True`. |
| Key Vault | Holds an RSA key that wraps stored secrets, and optionally a second RSA key that protects the Data Protection key ring. |
| Key Vault access for the app | Give the App Service managed identity `get`, `wrapKey`, and `unwrapKey` on those keys. This can be an access policy or the Key Vault Crypto User role. |
| Google OAuth client | Provides sign-in. Add `https://<your-host>/signin-google` as an authorized redirect URI. |

Database tables are created and updated by the app at startup from the embedded scripts in `AgentDock.Core/Migrations`. The scripts are additive and idempotent, and a SQL application lock keeps two instances from migrating at once.

On Azure the app authenticates to Key Vault with its managed identity, so no Azure credentials go in app settings. The Anthropic API key is not an app setting at all: each user enters their own in Settings.

## Configuration

Settings use flat names and can be supplied as environment variables, App Service application settings, or `dotnet user-secrets`. Nested settings use `:` in configuration files and `__` in environment variables.

| Setting | Required | Purpose |
|---|---|---|
| `AGENTDOCK_SQL_CONNECTION_STRING` | Yes | Connection string for the Azure SQL database. Outside Windows it must set `Trusted_Connection=False`, or the app refuses to start. |
| `AGENTDOCK_KEYVAULT_KEY_ID` | Yes | URL of the Key Vault key that wraps stored secrets, for example `https://<vault>.vault.azure.net/keys/<key-name>`. A versionless URL uses the current version. |
| `AGENTDOCK_GOOGLE_CLIENT_ID` | For sign-in | Google OAuth client ID. Without it and the secret below, the sign-in page reports that Google is not configured. |
| `AGENTDOCK_GOOGLE_CLIENT_SECRET` | For sign-in | Google OAuth client secret. |
| `AGENTDOCK_ALLOWED_EMAILS` | Outside Development | Comma-separated list of emails allowed to sign in. It is checked on every request, so removing an address locks that user out. If it is empty, everyone is denied except in the `Development` environment. |
| `AGENTDOCK_DATAPROTECTION_KEY_ID` | No | URL of a Key Vault key that protects the Data Protection key ring at rest. The key ring is stored in SQL either way, which keeps sign-in cookies valid across restarts and instances. |
| `AGENTDOCK_PUBLIC_BASE_URL` | Recommended | Public base URL of your deployment, used when building the embed snippet. |
| `ASPNETCORE_ENVIRONMENT` | No | `Development`, `Staging`, or `Production`. It controls the allowlist default, dev sign-in, and which credential is used for Key Vault. |
| `AGENTDOCK_DEV_LOGIN_SECRET` | No | Enables `/dev/login`, a shared-secret sign-in for testing. It must be at least 32 characters, and it only works in `Development` and `Staging`. In `Production` the page returns 404. |
| `AGENTDOCK_DEV_LOGIN_EMAILS` | With dev login | Comma-separated emails that dev sign-in may sign in as. |
| `AgentDock:DevLogin:Disabled` | No | Set to `true` to switch dev sign-in off without removing the secret. |
| `AgentDock:Outbound:AllowedPorts` | No | Ports that MCP and HTTP tools may call. Defaults to `443`. |
| `AgentDock:Outbound:AllowHttpForDevelopment` | No | Allow plain HTTP for outbound calls. It is honored only in `Development`. |
| `AgentDock:Outbound:SelfHosts` | No | Extra host names of this deployment that outbound calls must refuse. The App Service host name is added automatically. |
| `AZURE_CREDENTIALS_AGENTDOCK` | No | Development only. Service principal JSON with `tenantId`, `clientId`, and `clientSecret`, used to reach Key Vault. If it is absent, the app uses `DefaultAzureCredential`, for example your `az login`. |
| `AGENTDOCK_CLAUDE_API_KEY` | No | Used only by the live smoke tests. It is never used to answer for an agent. |

Keep real values out of the repository. Use `dotnet user-secrets` locally and application settings or Key Vault references in Azure.

## Run it locally

You need the .NET 10 SDK, a SQL Server or Azure SQL database you can write to, a Key Vault key, and a Google OAuth client with `https://localhost:7294/signin-google` as a redirect URI.

```bash
dotnet dev-certs https --trust
az login   # lets DefaultAzureCredential reach Key Vault

cd AgentDock.Web
dotnet user-secrets set AGENTDOCK_SQL_CONNECTION_STRING "<connection string>"
dotnet user-secrets set AGENTDOCK_KEYVAULT_KEY_ID "https://<vault>.vault.azure.net/keys/<key-name>"
dotnet user-secrets set AGENTDOCK_GOOGLE_CLIENT_ID "<client id>"
dotnet user-secrets set AGENTDOCK_GOOGLE_CLIENT_SECRET "<client secret>"

dotnet run --launch-profile https
```

The app listens on `https://localhost:7294` and `http://localhost:5294`. In `Development` an empty `AGENTDOCK_ALLOWED_EMAILS` lets any Google account in, so you do not need the allowlist locally. To skip Google while testing, set `AGENTDOCK_DEV_LOGIN_SECRET` (32 or more characters) and `AGENTDOCK_DEV_LOGIN_EMAILS`, then open `/dev/login`. A red banner shows whenever dev sign-in is on.

## Tests

```bash
dotnet test AgentDock.Tests
```

Unit tests need nothing. Integration tests that use SQL run only when `AGENTDOCK_SQL_CONNECTION_STRING` is set, and are skipped otherwise. They create throwaway `test-{guid}@agentdock.test` users and delete them afterward, but point them at a development database, not one with data you care about. The live Claude tests also need `AGENTDOCK_CLAUDE_API_KEY` and Key Vault access, and skip when those are missing.

## Deploying

Publish with `dotnet publish AgentDock.Web -c Release` and deploy the output to the App Service, for example with the `azure/webapps-deploy` GitHub Action. Set the configuration above as application settings first.

## Embedding an agent

On an agent's Embed page, AgentDock shows a script tag to paste before the closing `</body>` of any page on an allowed origin. The snippet contains no secrets. Chat is served from a frame with a per-agent content security policy, so a site that is not on the agent's allowed-origin list cannot embed it.
