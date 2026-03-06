# Chronicles of Arthus (Web RPG)

An episodic fantasy RPG web app in .NET.  
The story is generated live by an LLM and reacts to player text input.

## Requirements

- .NET 8 SDK
- An OpenAI-compatible API key

## Configure

Set these environment variables:

```bash
export OPENAI_API_KEY="your_api_key_here"
```

Optional:

```bash
export OPENAI_MODEL="gpt-5-mini"
export OPENAI_BASE_URL="https://api.openai.com/v1"
export ASPNETCORE_URLS="http://0.0.0.0:8080"
```

## Run Locally

```bash
dotnet run --project /Users/samhaddad/Documents/Codex/AdventureGame/BreakroomAdventure/BreakroomAdventure.csproj
```

Then open:

- [http://localhost:8080](http://localhost:8080) if you set `ASPNETCORE_URLS`
- otherwise use the URL printed by ASP.NET in the terminal

## Deploy On Render

This repo includes a Render Blueprint file: `render.yaml`.

1. Push this folder to GitHub.
2. In Render, choose **New +** -> **Blueprint**.
3. Connect your repo and select this project folder.
4. Render will detect `render.yaml` and create the web service.
5. In the service environment variables, set:
   - `OPENAI_API_KEY` (required secret)
   - `OPENAI_MODEL` (defaults to `gpt-5-mini`)
   - `OPENAI_BASE_URL` (defaults to `https://api.openai.com/v1`)

Then deploy and share the public Render URL with your friends.

## Behavior

- Every run is a fresh story with Arthus as the hero.
- Players type free-text actions to branch the plot.
- Sessions are short (about 4 to 7 turns).
- Story text is concise for middle-to-high-school readability.
