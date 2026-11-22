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
2. Email it to [max@cassaveconsulting.com](mailto:max@cassaveconsulting.com) and it will be added to the allowlist.

Once you are approved, sign in with that Gmail account, add your Anthropic API key in Settings, and create your first agent.

## Status

This is an early test deployment, not a public launch. Expect rough edges, and expect data to change or be reset.
