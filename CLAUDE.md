# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A semantic (LLM-driven) chat API over The Movie DB. Users ask natural-language questions ("what action movies in the 90s did the main actor also star in a comedy with Meg Ryan in the 80s?"); Semantic Kernel plans and auto-invokes kernel functions against TMDb/OMDb/Wikipedia to answer. See `Readme.md` for the product framing and example queries.

Single .NET 8 isolated-worker Azure Functions project: `src/MovieTracker.Backend`. Infrastructure is Bicep in `infrastructure/`. There is no test project.

## Commands

Build / restore (run from repo root):

```sh
dotnet build src/MovieTracker.Backend/MovieTracker.Backend.sln
```

Run locally — requires Azure Functions Core Tools v4 and an `az login` session (config is pulled from Key Vault with `DefaultAzureCredential`):

```sh
cd src/MovieTracker.Backend
func start
```

`VaultUri` must be set in the environment. `Properties/launchSettings.json` sets it for the Visual Studio/`dotnet run` profile; for `func start` it must come from `local.settings.json` (gitignored) or the shell. `Program.cs` throws `ConfigurationErrorsException("Missing VaultUri")` at startup if it's absent.

Smoke test the two endpoints:

```sh
curl http://localhost:7187/api/chat/start
curl -X POST http://localhost:7187/api/chat/<chatId>/ask -H "Content-Type: application/json" -d '{"Input":"what movies did Jonathan Pryce do in the mid 80s?"}'
```

Deploy infrastructure (see `Readme.md` for parameter details):

```sh
az deployment group create --resource-group <rg> --template-file infrastructure/main.bicep \
  --parameters adminPrincipalIds="['<objectId>']" open_ai_api_key='<key>' the_movie_db_api_key='<key>'
```

CI does not build or test — `.github/workflows/pr-function.yml` deploys an ephemeral function app per PR (`infrastructure/func-pr.bicep`) and deletes it on close; `deploy-main.yml` publishes to the existing prod function app when a PR merges to `main`. Both install the .NET 9 SDK even though the project targets `net8.0`.

## Architecture

### Request flow

Two HTTP functions, both in `Functions/Function.cs`:

- `Chat-Start` (`GET /api/chat/start`) — creates a `ChatHistory` seeded with the **system prompt defined inline in this method**, persists it to Cosmos, returns a short `chatId`. That prompt is the contract for the whole app: it forces the model to reply with a JSON object of `SystemMessage` + `MovieList[{MovieId, MovieName}]`, and to wrap trailer URLs in `[TRAILER]...[/TRAILER]` for the frontend to render inline. Changing response shape means changing this prompt *and* the `MovieListResponse` type used as the structured-output schema.
- `Chat-Ask` (`POST /api/chat/{chatId}/ask`) — loads the session, runs the planner, calls the LLM with `ToolCallBehavior.AutoInvokeKernelFunctions` and `ResponseFormat = typeof(MovieListResponse)` (SKEXP0010 structured outputs), then **hydrates** the returned `MovieId`s.

Hydration is the key two-layer design: the LLM only ever returns TMDb movie IDs; `ProcessMovieAsync` fans out over them, calls TMDbLib for full details plus the best YouTube trailer, and builds the `MovieViewModel` the frontend consumes. Results are cached per `movieId` in `IDistributedCache` (registered as `AddDistributedMemoryCache` — in-process, per-instance, no expiry set).

Note that `Chat-Ask` re-walks and re-hydrates the *entire* chat history on every call, so latency and TMDb calls grow with conversation length. A single `/ask` currently takes roughly 30–70s.

Anything the model supplies — movie ids, release years, cast/genre/keyword id lists — is treated as untrusted input. It routinely invents non-numeric ids, so parsing goes through `TryGetTmdbId`/`ParseIdList` in `TheMovieDBKernelFunctions`, which hand the model a correctable error instead of throwing; `SafeProcessMovieAsync` isolates per-movie hydration so one bad entry cannot fail the whole response. A bare `int.Parse` on a model-supplied value is a bug waiting to happen.

The named `"HttpClient"` in `Program.cs` is used **only** by the Kernel (Cosmos takes the unnamed default), and its resilience timeouts are widened well past the 30s default because reasoning models are slow. They stay inside the platform's ~230s hard limit on HTTP triggers.

### Semantic Kernel wiring

`Program.cs` registers `Kernel` as **scoped**, backed by **Azure OpenAI** (`AddAzureOpenAIChatCompletion`) against the `gpt-5-mini` deployment in `infrastructure/openai.bicep`, and adds two plugins at build time:

- `Prompts/TheMovieDBKernelFunctions.cs` — the main TMDb surface (genres, person search, movie search, discover with filters, movie details, trailers/videos).
- `Prompts/DateTimeKernelFunctions.cs` — relative-date helpers so the model can resolve "in the last 10 years" into ISO date ranges rather than hallucinating them.

`Prompts/ChatPlanner.cs` is added as a plugin **per request** inside `Chat-Ask` (`kernel.Plugins.Add(KernelPluginFactory.CreateFromObject(chatPlanner))`). It is the kernel-facing façade over the `Agents/` classes — ratings, trailers, Wikipedia-backed "funny facts" and context.

`Agents/` are plain DI services, not plugins (`WikipediaSearchAgent` carries `[KernelFunction]` attributes but is never registered as a plugin — it's reached through `ChatPlanner`):

- `OpenMovieDbAgent` — OMDb ratings. IMDb rating is intentionally the primary/default rating everywhere; Rotten Tomatoes and Metacritic are secondary context only.
- `WikipediaSearchAgent` — Wikipedia REST summary + Wikidata SPARQL, with a confidence score gating whether the enriched path or the basic path is used.
- `TrailerAgent` — keyword-gated (`CanHandle`) flow: extract title via LLM → TMDb search → best official YouTube trailer → `[TRAILER]url[/TRAILER]`.

**To add a new external data source:** add the class under `Agents/`, register it scoped in `Program.cs`, thread it through `ChatPlanner`'s constructor, and add a `[KernelFunction]` wrapper there. `ChatPlanner` is constructed by hand in `Chat-Ask`, so its constructor args must also be added to the `Function` primary constructor.

### Persistence

`ChatSessionRepository.cs` — Cosmos DB, database `database`, container `chat-sessions`, hardcoded. Document id doubles as the partition key; ids come from `GenId()` (base64 of 5 random bytes, regenerated until URL-safe-clean). The Semantic Kernel `ChatHistory` object is serialized straight into the document via `CosmosSystemTextJsonSerializer`.

### Configuration

All secrets come from Key Vault at startup (`VaultUri` env var + `DefaultAzureCredential`); in Development, `local.settings.json` and user secrets are layered on top. Key Vault secret names use `--` where the config key uses `:`.

| Config key | Key Vault secret | Provisioned by |
|---|---|---|
| `AzureOpenAi:Endpoint` | `AzureOpenAi--Endpoint` | `kv-secrets-openai.bicep` |
| `AzureOpenAi:Api-Key` | `AzureOpenAi--Api-Key` | `kv-secrets-openai.bicep` |
| `AzureOpenAi:Deployment` | `AzureOpenAi--Deployment` | `kv-secrets-openai.bicep` |
| `TheMovieDb:Api-Key` | `TheMovieDb--Api-Key` | `key-vault.bicep` |
| `OpenMovieDb:Api-Key` | `OpenMovieDb--Api-Key` | **not in Bicep — must be added manually** |
| `ConnectionStrings:Cosmos` | `ConnectionStrings--Cosmos` | `kv-secrets-cosmosdb.bicep` |
| `APPLICATIONINSIGHTS-CONNECTION-STRING` | same | `kv-secrets-app-insights.bicep` |

`OpenAi--Api-Key` (the direct OpenAI.com key) is still provisioned by `key-vault.bicep` but is no longer read by the app.

The Azure OpenAI account lives in **westus**, not the resource group's westus2, which offers no OpenAI models. Model choice is constrained by per-subscription quota, not just availability — `az cognitiveservices usage list -l westus` shows which SKUs have a non-zero limit, and Batch SKUs are not usable for these interactive calls.

A missing `OpenMovieDb:Api-Key` fails at DI resolution of `OpenMovieDbAgent`, which surfaces as a failure on every `Chat-Ask` call, not at startup.

### Observability

OpenTelemetry traces export to Azure Monitor. Every function and repository method opens a span named `movie-tracker-func.*` via the injected `Tracer`; keep that convention when adding operations. SK sensitive-diagnostics is enabled in `Program.cs`, so prompts and completions appear in traces.
