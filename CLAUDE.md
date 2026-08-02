# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A semantic (LLM-driven) chat API over The Movie DB. Users ask natural-language questions ("what action movies in the 90s did the main actor also star in a comedy with Meg Ryan in the 80s?"); **Microsoft Agent Framework** plans and auto-invokes tools against TMDb/OMDb/Wikipedia to answer. See `Readme.md` for the product framing and example queries.

Migrated off Semantic Kernel in July 2026 — see `AGENT_FRAMEWORK_MIGRATION.md` for the reference guide the migration followed, and "Agent Framework wiring" below for what the result looks like. There is no Semantic Kernel left in the project.

Single .NET 10 isolated-worker Azure Functions project: `src/MovieTracker.Backend`. Infrastructure is Bicep in `infrastructure/`. There is no test project.

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

CI does not build or test — `.github/workflows/pr-function.yml` deploys an ephemeral function app per PR (`infrastructure/func-pr.bicep`) and deletes it on close; `deploy-main.yml` publishes to the existing prod function app when a PR merges to `main`. Both install the .NET 10 SDK, matching the project's `net10.0` target and the function apps' `netFrameworkVersion: v10.0`.

`Microsoft.ApplicationInsights.WorkerService` is deliberately held at the 2.x line. Application Insights 3.x is the OpenTelemetry-based rewrite and removes `ITelemetryInitializer`, which `Microsoft.Azure.Functions.Worker.ApplicationInsights` still binds to — and that package's open-ended `PerfCounterCollector [2.23.0, )` range will happily pull 3.x in. The result is a worker that builds fine and then dies at startup with `TypeLoadException` (surfacing as exit code `0xE0434352`). A clean build does not prove this app boots; run `func start` before publishing.

## Architecture

### Request flow

Three HTTP functions, all in `Functions/Function.cs`:

- `Chat-Start` (`GET /api/chat/start`) — creates a `List<ChatMessage>` seeded with the **system prompt defined inline in this method**, persists it to Cosmos, returns a short `chatId`. That prompt is the contract for the whole app: it forces the model to reply with a JSON object of `SystemMessage` + `MovieList[{MovieId, MovieName}]`, wrap trailer URLs in `[TRAILER]...[/TRAILER]` for the frontend to render inline, and treat **IMDb as the default rating** (see "Ratings default to IMDb" below). Changing response shape means changing this prompt *and* the `MovieListResponse` type used as the structured-output schema. The prompt is stored per session at `/start`, so edits only affect sessions created afterwards.
- `Chat-Ask` (`POST /api/chat/{chatId}/ask`) — loads the session, runs the turn, then **hydrates** the returned `MovieId`s for the *entire* transcript, which is what this route returns.
- `Chat-Ask-V2` (`POST /api/chat/{chatId}/ask/v2`) — identical turn and identical persistence, but returns `{ FunnyFact, Turn }` — only the assistant turn just produced, hydrated once. Purely additive; v1 is untouched and the two can be interleaved on one session because both read and write the same stored history.

Both ask routes share `RunTurnAsync`. They differ only in how much of the conversation they hydrate afterwards.

`MovieListResponse` carries **only** `SystemMessage` and `MovieList`. It used to have a `FunnyFact` too, which `RequireAllProperties` made mandatory — so the model paid output tokens for it every turn while `ToClientMessageAsync` read only the other two and the fact the client sees came from `chatSession.FunnyFact` out of band. Adding a field here costs a completion on every request; only add one something actually reads.

**Structured-output trap.** `MovieListResponseFormat` in `Function.cs` is built with `AIJsonUtilities.CreateJsonSchema` and `AIJsonSchemaTransformOptions { DisallowAdditionalProperties = true, RequireAllProperties = true }`, not with the obvious `ChatResponseFormat.ForJsonSchema(typeof(MovieListResponse), ...)`. The convenient overload emits a schema with no `required` array and no `additionalProperties: false`, which Azure OpenAI's strict structured outputs reject with HTTP 400 — Semantic Kernel's `ResponseFormat = typeof(T)` used to emit the strict dialect for you. The serializer options also pin `PropertyNamingPolicy = null`, because Microsoft.Extensions.AI defaults to camelCase and the system prompt and `ProcessMovieAsync` both depend on PascalCase `SystemMessage`/`MovieList`/`MovieId`.

Hydration is the key two-layer design: the LLM only ever returns TMDb movie IDs; `ProcessMovieAsync` fans out over them, calls TMDbLib for full details plus the best YouTube trailer, and builds the `MovieViewModel` the frontend consumes. Lookup is three tiers, in order:

`MovieViewModel`'s trailing `GenreNames`, `Runtime` and `Tagline` are additive and **nullable on purpose**: `HydratedMovies` persists in Cosmos across deploys, so documents written before a field existed deserialize it as null. A null `GenreNames` means "unknown", not "no genres" — any new field here inherits that constraint.

1. `MoviceTrackerChatSession.HydratedMovies` — a `Dictionary<string, MovieViewModel>` **stored in the session's own Cosmos document**, so it arrives free with the load at the top of the request and survives restarts and scale-out. Capped at `HydratedMovieLimit` (150) purely to bound document size; anything evicted is simply re-fetched.
2. `IDistributedCache` — `AddDistributedMemoryCache`, so process-wide and shared across sessions, but cold on every new instance.
3. TMDb — **one** round trip per movie: `GetMovieAsync(id, MovieMethods.Videos)`. It was two (details, then videos) until append-to-response was wired up; this is the hottest TMDb path in the app, since every movie of a fresh answer is a cache miss.

Hydration writes into a pre-sized array **by index**, not by appending under a lock. The tasks run concurrently, so appending produced completion order and silently discarded the ordering the model chose — an answer that says "best first" would render shuffled.

Tier 1 is what stops `/ask` re-fetching every movie of every past turn from TMDb on every call. Without it, TMDb traffic grows with conversation length and is cold on each new instance. A single `/ask` runs roughly 5–10s.

**What is stored and what is sent are deliberately different.** Cosmos keeps every message forever because `/ask` replays the whole transcript to the frontend; the model sees a compacted view built per call by `ContextReducer`. See "Context compaction" below.

The funny fact is generated **concurrently** with the main model call, not before it. It only reads the user's raw input, and running it first put two completions plus a Wikipedia REST fetch and a Wikidata SPARQL query on the critical path. `SafeGenerateFunnyFactAsync` swallows its own failures — decorative output must not fail the turn, and a task nobody awaits until later must not fault unobserved.

Anything the model supplies — movie ids, release years, cast/genre/keyword id lists — is treated as untrusted input. It routinely invents non-numeric ids, so parsing goes through `TryGetTmdbId`/`ParseIdList` in `TheMovieDBKernelFunctions`, which hand the model a correctable error instead of throwing; `SafeProcessMovieAsync` isolates per-movie hydration so one bad entry cannot fail the whole response. A bare `int.Parse` on a model-supplied value is a bug waiting to happen.

The named `"HttpClient"` in `Program.cs` is used **only** by the Azure OpenAI chat client (Cosmos takes the unnamed default), and its resilience timeouts are widened well past the 30s default because reasoning models are slow. They stay inside the platform's ~230s hard limit on HTTP triggers. It reaches the SDK through `AzureOpenAIClientOptions.Transport = new HttpClientPipelineTransport(httpClient)` — if that is dropped, the widened timeouts silently stop applying. `Retry.MaxRetryAttempts` is 3 rather than the previous 1 because the funny-fact and main calls now fire together, which is burstier against the 200K TPM cap; the standard handler already counts 408/429/5xx as transient and honours `Retry-After`, so no custom predicate is needed.

### Context compaction

`ContextReducer` in `Function.cs` reduces the conversation *sent to the model* while the full history stays in Cosmos. It is a `PipelineCompactionStrategy` of `ToolResultCompactionStrategy` (collapse old tool-call groups) then `SlidingWindowCompactionStrategy` (drop oldest turns past `MaxModelTurns`), bridged to a plain message list with `.AsChatReducer()`.

Things worth knowing before touching it:

- **`CompactionMessageIndex` is not public.** The strategies operate on it, but the only supported way to run one over a `List<ChatMessage>` is `AsChatReducer()`. There is no public `CompactionMessageIndex.Create`.
- The whole `Microsoft.Agents.AI.Compaction` namespace is gated behind **`MAAI001`** (evaluation-only) and is the one preview API this project depends on. The pragmas are scoped, the way `OPENAI001` is around `ReasoningEffortLevel`.
- **The default `ToolCallFormatter` is close to useless here.** It inlines every tool result verbatim, which is fine for `"Sunny and 72F"` and not for a twenty-movie `DiscoverMovies` array — measured on a synthetic six-turn conversation it reduced context by 3%. The custom `SummariseToolCalls` gets 68% on the same input (41% at two turns, 90% at thirty).
- `SummariseToolCalls` records the **call arguments** and reduces array results to a count plus the leading names, rather than truncating raw JSON. The truncation-only version dropped arguments entirely, so a later turn could see that `DiscoverMovies` had been called twice but not that one was a cast filter and the other a genre filter — and 160 chars of a movie array is about one and a half objects of mid-array noise. Measured on the same history the newer form is *shorter* (435 vs 469 chars) and says what the call was for. Non-array results keep the old truncation, and the JSON parse is guarded — this runs on every turn.
- Reduction runs over a **copy** of the stored list. The strategies rewrite the list they are given; if that reached `chatSession.ChatHistory`, the `/ask` transcript would quietly start losing turns.
- Compaction always runs over the full stored history, never over its own output, so it never compounds: turn 20 sees the same reduction of turns 1–10 that turn 11 saw.
- Safe by construction, and verified: system message preserved, every user question and assistant answer preserved inside the window, and tool calls never separated from their results (the index groups an assistant call plus its results atomically). **Re-verified** by running the exact reducer configuration over synthetic histories: at 60 turns (241 stored messages) the output is 38 messages with `ChatRole.System` still at index 0. Nothing pins message 0 explicitly, so it *looks* droppable when you read the code — it isn't. Don't "fix" it without reproducing a loss first.

### Ratings default to IMDb

IMDb-as-default is a deliberate product decision, and it is carried in three places that must agree — the system prompt's "Ratings" block, `ChatPlanner.GetMovieRating`'s `[Description]`, and the field names in the TMDb tool payloads. `DescribeMovie` used to return TMDb's vote average under the bare name `Rating`, which invited the model to answer "which had the highest rating?" straight from it; it is now `TmdbVoteAverage`. `GetMovieDetails` kept emitting a bare `VoteAverage` for a while after that rename, which made the prompt's claim false for half the detail tools — it has since been renamed too and then unregistered entirely (see the tool list below).

`DiscoverMovies` has a `sortBy` of `rating`, which orders by the **TMDb** vote average, not IMDb. Its `[Description]` says so and tells the model to still call `GetMovieRating` to answer a rating question. Sorting by rating also applies a 200-vote floor when the caller sets none, because TMDb ranks on raw average and would otherwise surface films with a single 10/10 vote.

Measured on the same "Nolan sci-fi → which had the highest rating?" pair, six passes each: **3/6 answered from IMDb before these changes, 6/6 after.** The failure mode is pre-existing model non-determinism, not something the tool pruning introduced — confirm with a `git stash` A/B before concluding otherwise.

### Agent Framework wiring

`Program.cs` registers two things as **scoped**:

- `IChatClient` — `AzureOpenAIClient(...).GetChatClient(deployment).AsIChatClient()`, against **Azure OpenAI in Microsoft Foundry** (`gpt-5-4-mini`, see `infrastructure/ai-foundry.bicep`), wrapped in `.UseOpenTelemetry(..., EnableSensitiveData = true)`. `GetChatClient` returns the *OpenAI SDK* `ChatClient`, so `AsIChatClient()` is mandatory — omitting it is compile error CS1929.
- `AIAgent` — `chatClient.AsAIAgent(...)` with the full tool list, then `.AsBuilder().Use(...).Build(...)`. `ChatClientAgent` decorates the chat client with automatic function invocation by default, which is what `ToolCallBehavior.AutoInvokeKernelFunctions` used to do.

The `.Use(...)` is function-invocation middleware that caps the tool-calling loop at `MaxToolIterations` (12) by setting `FunctionInvocationContext.Terminate`. `FunctionInvokingChatClient` allows **40** iterations by default, which at a few seconds per round trip blows through the 120s HttpClient budget and then the ~230s trigger limit before it gives up — the caller gets a timeout instead of an answer. Note `AsBuilder().Build()` returns a **new** agent; registering the pre-builder one silently skips the middleware.

**The split matters.** `ChatPlanner` and `TrailerAgent` take the bare `IChatClient`, *not* the agent, because their prompts (entity detection, funny facts, trailer-title extraction) must run without the agent's tool set and JSON response format. Under Semantic Kernel they resolved `IChatCompletionService` off the kernel for the same reason.

There is no attribute-driven tool discovery any more. `Plugins.AddFromType<T>()` is replaced by explicit `CreateTools()` methods returning `IEnumerable<AITool>` built with `AIFunctionFactory.Create`, and `Program.cs` unions the three sources. **Adding a `[Description]`-annotated method without adding it to the matching `CreateTools()` leaves it invisible to the model** — this is the single easiest thing to get wrong.

The model sees **21** tools (8 TMDb + 7 date + 6 `ChatPlanner`). Every tool schema rides in the cached prompt prefix on every call, so the count is a permanent per-request cost.

Not offered, on purpose:

- `ChatPlanner.GenerateEnhancedFunnyFact` — still called, just out of band, racing the main model call. As a tool it cost a nested completion plus Wikipedia/Wikidata inside a call the model was waiting on, and the fact reaches the client through the response's own field.
- `TheMovieDBKernelFunctions.GetMovieDetails` and `GetMovieWithTrailer` — the same `GetMovieAsync` lookup as `DescribeMovie` under different names, with overlapping-but-inconsistent field sets. `DescribeMovie` is the single detail tool and absorbed what they carried (genre ids as well as names, `TmdbVoteCount`, poster/backdrop). Kept in the file, just unregistered.
- `HandleGenericTrailerRequest` — made no TMDb call at all; it ignored its argument and returned a fixed "which movie did you mean?" string.

`GenerateFunnyFact`, `GenerateRequiredSteps` and `GetMovieRatingGeneric` were withheld at the migration and have since been **deleted** — nothing called them, and `GenerateRequiredSteps`'s worked example contained a fabricated `"MovieId": "1"` plus an `ImdbId` field the strict schema rejects, which `HandleTrailerRequest` was feeding the model as a fallback.

The seven date helpers were kept on purpose: their schemas are tiny and they exist precisely to stop the model inventing date ranges.

**Person questions need the right one of three filters, and getting it wrong fails silently.** TMDb files directing/writing/producing credits separately from acting credits, and originally only `with_cast` was wired up — so "sci-fi directed by Christopher Nolan" resolved him correctly to PersonId 525, filtered it as *cast*, and got **zero rows**. Measured: `with_cast=525` + genre 878 → 0 results; `with_cast=525` alone → documentaries *about* him; `with_crew=525` + genre 878 → Interstellar, Inception, The Prestige, Tenet. An empty result is exactly when the model stops calling functions and answers from memory, which is how "Nolan sci-fi" came back as *Harry Potter* and *Die Hard 2*. `DiscoverMovies` now also returns an explicit `Hint` instead of a bare `[]` when a cast filter matches nothing.

`with_crew` alone is *not* "directed by" either — discover can filter by crew membership but **not by job**. Measured on Tarantino's 1990s crew results: alongside four directing credits it returns *True Romance* (Writer), *Natural Born Killers* (Story), *Killing Zoe* (Executive Producer) and *Jackie Chan: My Story*, where his only credit is **`Thanks`**. That is why `GetPersonMovieCredits` exists — one call returns every credit already carrying its `Job`, so it is both more accurate than discover-then-filter and cheaper. The rule the system prompt teaches:

| Question | Tool |
|---|---|
| "what did X direct / write" | `GetPersonMovieCredits(personId, job: "Director")` |
| "what has X been in" | `GetPersonMovieCredits(personId)` — no job filter |
| person **combined with** genre/date/rating filters | `DiscoverMovies(castIds:)` or `(crewIds:)` |

**Two upstream traps found by stress-testing the tool surface, both of which produce confidently wrong answers:**

- **`job` is not a raw TMDb crew job.** The model naturally passes `job=Actor` for "his movies" — but acting is not a crew job in TMDb, so an exact crew-only match returns **zero** and the question dies. It also passes `job=Writer`, while TMDb spreads writing across `Writer`, `Screenplay` and `Story`. `ResolveJobFilter` maps job words onto cast-vs-crew and onto every spelling TMDb uses; unrecognised values fall through as an exact crew match so the long tail still works.
- **TMDb's `with_runtime` filter contradicts TMDb's own runtime field.** Measured: `with_runtime.gte=181` returned Spider-Man (121 min), Interstellar (169) and Fellowship of the Ring (179) — **six of the first ten results violated the filter**, because it matches alternate release cuts. `DiscoverMovies` therefore uses it only to narrow, then re-checks every result against the canonical runtime and drops the rest. Do not remove that verification pass; without it "under two hours" returns 124-minute films.

`GetPersonMovieCredits` sorts by popularity where it exists and falls back to release date. Only `MovieRole` (cast) carries `Popularity`; `MovieJob` (crew) does not. That split is deliberate: sorting acting credits by date makes "what has Meg Ryan been in?" lead with her most recent obscure work instead of *When Harry Met Sally*, while a job-filtered crew list is short and complete so newest-first is fine. Capped at `MaxPersonCredits` (25) with the withheld count stated in the payload rather than truncated silently.

One thing that looks like a bug and is not: a person's credits can contain the same title twice with **different TMDb ids** — Tarantino has *Reservoir Dogs* as both `500` (1992 feature) and `443129` (1991 short), and *From Dusk Till Dawn* as `755` and `1623134`. Both are real upstream records. They are not deduplicated, because collapsing by title would silently drop a genuine credit.

- `Prompts/TheMovieDBKernelFunctions.cs` — the main TMDb surface (genres, person search, movie search, discover with filters and sort, movie details, trailers/videos). Takes an **injected singleton `TMDbClient`**; `new TMDbClient(apiKey)` used to appear 14 times across the app (once per tool method, twice in `TrailerAgent`, twice per HTTP function), so nothing reused a connection and TMDb's API config was re-fetched per instance. The DI registration is also the one place a default language or region could go.

  **No sequential per-result TMDb calls.** `SearchMovies` and `DiscoverMovies` need one `GetMovieExternalIdsAsync` per hit and TMDb has no batch endpoint, but running those in a `foreach` put up to 20 serial round trips inside one tool call. They now run concurrently behind a `SemaphoreSlim(8)`, writing by index so TMDb's ranking survives. Benchmarked on 20-result searches: 2182ms→208ms, 2375ms→148ms, 2218ms→106ms — **10–21×**, identical ids in identical order.
- `Prompts/DateTimeKernelFunctions.cs` — relative-date helpers so the model can resolve "in the last 10 years" into ISO date ranges rather than hallucinating them. Static methods.
- `Prompts/ChatPlanner.cs` — the façade over the `Agents/` classes (ratings, trailers, Wikipedia-backed "funny facts" and context). Now a normal scoped DI service; it used to be constructed by hand in `Chat-Ask` and bolted onto the kernel per request.

The class names still end in `KernelFunctions` for continuity with the file history; there are no kernels involved.

`Agents/` are plain DI services, never exposed to the model directly (`WikipediaSearchAgent` keeps `[Description]` attributes but is reached only through `ChatPlanner`):

- `OpenMovieDbAgent` — OMDb ratings. IMDb rating is intentionally the primary/default rating everywhere; Rotten Tomatoes and Metacritic are secondary context only. One OMDb response carries 38 fields and this agent used to read six; it now also surfaces `Awards`, `Plot`, `Director`, `Writer`, `Actors`, `Genre`, `Runtime`, `Rated` and `ImdbVotes`, which is where the "trivia and behind-the-scenes facts" the system prompt asks for actually come from. No extra HTTP — they were already in the body being parsed and discarded. `CompareMovieRatings`/`FilterMoviesByRating` stay lean (Awards only) because they are list-shaped.
- `WikipediaSearchAgent` — Wikipedia REST summary + Wikidata SPARQL, with a confidence score gating whether the grounded path runs at all. **Four separate faults kept this from ever working in production; if facts stop appearing, check these first:**
  1. **`User-Agent` is mandatory.** Wikimedia enforces its API etiquette — both `en.wikipedia.org/api/rest_v1` and `query.wikidata.org` return **403** without one, 200 with one. `AddHttpClient<T>()` sets none, so it is configured explicitly in `Program.cs`.
  2. **Do not add `services.AddScoped<WikipediaSearchAgent>()`.** `AddHttpClient<T>` already registers `T`; a second descriptor shadows it (last registration wins) and the agent gets an unconfigured client.
  3. **The summary DTO needs `[JsonPropertyName]`.** Wikipedia returns `"extract"`, System.Text.Json matches case-sensitively, and deserializing with no options bound `Extract` to null on every request. `PropertyNameCaseInsensitive = true` is *not* the fix — it makes `"pageid"` (a number) bind to a string property and throw, which `GetEnhancedInfo`'s catch swallows into the same silent null.
  4. **No uncorrelated `COUNT` subqueries in the SPARQL.** The person queries had subqueries that did not project `?item`, so they counted globally over every film with any cast member; Wikidata answered 504 for "Tom Hanks". A timeout is indistinguishable from "no facts".

  Confidence weights the Wikipedia summary at 0.6 so it clears the 0.5 gate alone — it is the only thing `GenerateEnhancedFunnyFact` interpolates, so requiring Wikidata to bind as well gated on data the prompt never reads. The fact prompt is told to use only the supplied summary and answer `NONE` otherwise; `NONE` becomes a null and the field is simply omitted. **There is deliberately no ungrounded fallback** — the previous one invented biography about real people and shipped it to the user verbatim. Measured hit rate on an identical query: 7 of 8.
- `TrailerAgent` — keyword-gated (`CanHandle`) flow: extract title via LLM → TMDb search → best official YouTube trailer → `[TRAILER]url[/TRAILER]`. The gate needs a trailer noun, or a playback verb next to a video noun; it used to fire on bare `"show"`, `"watch"` and `"play"`, so "show me Tom Hanks movies" was routed into a trailer lookup.

**To add a new external data source:** add the class under `Agents/`, register it scoped in `Program.cs`, thread it through `ChatPlanner`'s constructor, add a `[Description]`-annotated wrapper method there, **and add that method to `ChatPlanner.CreateTools()`** — the last step is what actually exposes it to the model. If it talks HTTP, use `AddHttpClient<T>` and set a `User-Agent`, and do **not** also register the type with `AddScoped<T>` (see the `WikipediaSearchAgent` notes above — that combination cost this app its entire enrichment path).

### Persistence

`ChatSessionRepository.cs` — Cosmos DB, database `database`, container `chat-sessions`, hardcoded. Document id doubles as the partition key; ids come from `GenId()` (base64 of 5 random bytes, regenerated until URL-safe-clean). The conversation is a `List<Microsoft.Extensions.AI.ChatMessage>` serialized straight into the document via `CosmosSystemTextJsonSerializer`, alongside `HydratedMovies` (see hydration tiers above).

The ask routes mutate the document they loaded and call `SaveChatSession`. The old `UpdateChatSession(id, ...)` overloads did a `ReadItemAsync` first, so saving one turn cost two Cosmos round trips; they are gone.

Two constraints on that serializer, both load-bearing:

- Its options come from `AIJsonUtilities.DefaultOptions`, because `ChatMessage.Contents` is a polymorphic `IList<AIContent>`. Plain reflection-based options write the `$type` discriminators but cannot read `FunctionCallContent`/`FunctionResultContent` back, so a session with tool calls fails to load.
- `PropertyNamingPolicy` is pinned to `null`. `AIJsonUtilities.DefaultOptions` is camelCase, which would rename `PartitionKey` and break every existing document.

The `ChatHistory` *property name* is kept, but its serialized element shape is not the old Semantic Kernel one, so **chat sessions created before the migration cannot be loaded**. New `/api/chat/start` sessions are unaffected; the external HTTP contract did not change.

`Chat-Ask` runs the agent with `session: null`, which is stateless — the whole conversation is passed in on each call and `result.Messages` is appended back. That is deliberately the same shape the Semantic Kernel version had with a `ChatHistory`, and it is what lets the response be rebuilt by re-walking the conversation. The intermediate function-call and function-result messages are persisted too: they carry no text (so the response-building loop skips them), but a tool call without its result is an invalid conversation on the next turn.

### Configuration

All secrets come from Key Vault at startup (`VaultUri` env var + `DefaultAzureCredential`); in Development, `local.settings.json` and user secrets are layered on top. Key Vault secret names use `--` where the config key uses `:`.

| Config key | Key Vault secret | Provisioned by |
|---|---|---|
| `AzureOpenAi:Endpoint` | `AzureOpenAi--Endpoint` | `kv-secrets-openai.bicep` |
| `AzureOpenAi:Api-Key` | `AzureOpenAi--Api-Key` | `kv-secrets-openai.bicep` |
| `AzureOpenAi:Deployment` | `AzureOpenAi--Deployment` | `kv-secrets-openai.bicep` |
| `AzureOpenAi:Reasoning-Effort` | `AzureOpenAi--Reasoning-Effort` | `kv-secrets-openai.bicep` |
| `TheMovieDb:Api-Key` | `TheMovieDb--Api-Key` | `key-vault.bicep` |
| `OpenMovieDb:Api-Key` | `OpenMovieDb--Api-Key` | **not in Bicep — must be added manually** |
| `ConnectionStrings:Cosmos` | `ConnectionStrings--Cosmos` | `kv-secrets-cosmosdb.bicep` |
| `APPLICATIONINSIGHTS-CONNECTION-STRING` | same | `kv-secrets-app-insights.bicep` |

`OpenAi--Api-Key` (the direct OpenAI.com key) is still provisioned by `key-vault.bicep` but is no longer read by the app.

The account lives in **westus**, not the resource group's westus2, which offers no OpenAI models. Model choice is constrained by per-subscription quota, not just availability — `az cognitiveservices usage list -l westus` shows which SKUs have a non-zero limit, and Batch SKUs are not usable for these interactive calls.

### Microsoft Foundry (formerly the Azure OpenAI resource)

The Cognitive Services account was upgraded in place from `kind: 'OpenAI'` to `kind: 'AIServices'` with `allowProjectManagement: true` (`infrastructure/ai-foundry.bicep`). The resource name, custom subdomain, `openai.azure.com` endpoint, API keys and existing deployments all survive the upgrade, so no application code changed. It is reversible by setting `kind` back to `OpenAI` after deleting any projects and non-OpenAI deployments.

**Trap:** on an `AIServices` account, `properties.endpoint` returns the generic `https://<name>.cognitiveservices.azure.com/` FQDN, but `AzureOpenAIClient` appends `/openai/deployments/<deployment>/chat/completions`, which is served only on `openai.azure.com`. `kv-secrets-openai.bicep` therefore builds the endpoint from `customSubDomainName` instead of reading `properties.endpoint` — reverting that silently breaks every `Chat-Ask` call on the next `main.bicep` deployment. (This survived the Agent Framework migration unchanged: the URL is built by the Azure SDK, not by the agent layer.)

Do not set `disableLocalAuth: true` (as the Microsoft upgrade doc's sample does). The function app authenticates with an API key from Key Vault.

**Why the upgrade was worth it:** the `OpenAI` kind can only deploy OpenAI models, and this subscription has **zero** quota for `gpt-4.1-nano` and `gpt-5-nano`, while `gpt-4.1-mini` is in a deprecating state and refuses new deployments. The `AIServices` kind additionally unlocks the `AIServices.*` quota buckets (grok, gpt-oss, Claude, DeepSeek, Mistral), which is the only route to anything cheaper or faster than `gpt-5-mini`.

### Model selection

`gpt-5-4-mini` (model `gpt-5.4-mini`) on **DataZoneStandard**, because this subscription has 0 GlobalStandard quota for it and 200K TPM on DataZoneStandard. `gpt-5-mini` stays deployed as a fallback — it holds the largest quota pool of any chat model here (500K TPM GlobalStandard) and costs nothing while idle.

Measured head-to-head on this app's own system prompt and tool set (end-to-end, three benchmark queries):

| Deployment | E2E | ~$/1k turns | Quota | Notes |
|---|---|---|---|---|
| `gpt-5-4-mini` | 29.8s | $2.16 | 200K TPM | chosen; best results |
| `gpt-5-mini` (default effort) | 113.2s | $3.55 | 500K TPM | previous default |
| `gpt-5-mini` + `minimal` effort | 67.3s | $0.53 | 500K TPM | cheapest, but returned a wrong film |
| `grok-4-1-fast` | — | $0.48 | 50K TPM | **429s** under real load |
| `gpt-oss-120b` | — | $0.47 | 5000K TPM | schema-valid but incoherent output |

The dominant cost was hidden reasoning tokens, not the per-token rate: `gpt-5-mini` spent **1344 of its 1587 completion tokens per turn on reasoning**. `gpt-5.4-mini` emits none at default effort.

Those absolute latencies drift between sessions — the same unchanged code measured 29.8s in the morning and 33.3s the same evening. Treat the table as a *relative* comparison, and re-baseline in the same session before concluding anything has regressed.

`AzureOpenAi:Reasoning-Effort` is applied in `Chat-Ask` through `ChatOptions.RawRepresentationFactory`, setting the OpenAI SDK's `ChatCompletionOptions.ReasoningEffortLevel` (gated behind `#pragma warning disable OPENAI001`). Leave it `default` (or empty) to omit the parameter. Valid values are model-family-specific — `minimal` works on `gpt-5-mini`, `none` is **rejected** on `gpt-5.4-mini` through the API version in use, and non-reasoning models such as `grok-4-1-fast-non-reasoning` reject the parameter outright. A wrong value fails every `Chat-Ask` with HTTP 400.

It deliberately does **not** use `ChatOptions.Reasoning`: that takes a `ReasoningEffort` enum limited to `None`/`Low`/`Medium`/`High`/`ExtraHigh`, which cannot express `minimal`. Dropping to the provider options preserves the arbitrary-string passthrough this setting needs.

`dynamicThrottlingEnabled` cannot be set on these deployments — the control plane rejects it for `DataZoneStandard`. Headroom comes from capacity instead. Anthropic models (`claude-haiku-4-5`) additionally require `ModelProviderData` (industry, organization name, country code) that the `az cognitiveservices` CLI does not expose, so deploying one needs the portal or a raw ARM call.

A missing `OpenMovieDb:Api-Key` fails at DI resolution of `OpenMovieDbAgent`, which surfaces as a failure on every `Chat-Ask` call, not at startup.

### Observability

All three OTel signals export to Azure Monitor from `Program.cs`: `WithTracing`, `WithMetrics` and `WithLogging`, each with its own `AddAzureMonitor*Exporter`. Every function and repository method opens a span named `movie-tracker-func.*` via the injected `Tracer`; keep that convention when adding operations.

**One name does all the wiring.** `serviceName` (`movie-tracker-backend`) is the OTel service name, the app's `Meter` name in `Telemetry.cs`, *and* the `sourceName` passed to `.UseOpenTelemetry(...)` on the `IChatClient`. `OpenTelemetryChatClient` builds both its ActivitySource and its Meter from that one string, so the single `AddSource(serviceName)` / `AddMeter(serviceName)` pair covers the model spans, the framework's `gen_ai.*` histograms and this app's own instruments. Omit the `sourceName` argument and both silently become `Experimental.Microsoft.Extensions.AI`, matching no registration.

**Traces are sampled; metrics are not.** That distinction decides where to look. `host.json` enables adaptive sampling with only `Request` excluded, so `dependencies` — where every span lands — is sampled: a spot check found **5 surviving `chat` spans against 107 real completion calls**. Spans are for *inspecting one request*; anything you need to **count, total or alert on** must come from metrics.

What is emitted:

| Signal | Source | Use it for |
|---|---|---|
| `chat <model>`, `orchestrate_tools`, `execute_tool <name>` spans | framework, free | inspecting a single turn; `execute_tool` carries `ActivityStatusCode.Error` when a tool throws |
| `gen_ai.client.token.usage`, `gen_ai.client.operation.duration` | framework, free | token totals and model latency — unsampled |
| `gen_ai.client.tool.duration` / `.invocations` | `InstrumentedAIFunction` | per-tool latency and failure rate, dimensioned by tool name + outcome |
| `MovieTracker.Backend.ToolCalls` log category | `InstrumentedAIFunction` | **which tool the model chose and what it passed** — the only way to debug tool selection |
| `chat.turn.tool_calls`, `chat.context.retained_ratio` | `Function.cs` | is the iteration cap biting; is compaction still saving anything |
| `gen_ai.usage.cost_usd` | `Telemetry.RecordCost` | spend — **only if** the price keys below are configured |

**Three traps, all verified against the shipped assemblies rather than the docs:**

- **Worker logging took two independent fixes, and only the pair works.** `.WithLogging(builder => { })` in `ConfigureServices` was empty — no exporter — so no `MovieTracker.*` category had ever reached App Insights, silently swallowing the `LogCritical("{@ex}", ex)` in both HTTP catch blocks. Adding the exporter *there* still produced nothing. Logging is now registered on the logging builder (`logging.AddOpenTelemetry(...)` in `ConfigureLogging`) **and** the exporter sets **`EnableTraceBasedLogsSampler = false`**. That second flag is the non-obvious one: the exporter defaults to dropping any log record whose correlated trace was sampled out, and `host.json` samples most spans away, so every line written *inside* a function invocation vanished. Diagnosed by elimination — a warning on the same category emitted at startup with `Activity.Current == null` arrived, while the identical category logged inside `Chat-Ask` did not. Also set `IncludeFormattedMessage = true`, without which messages render as the raw template with `{Placeholders}` intact. The worker's `ILogger` output never reaches the `func start` console either way, so the console cannot confirm any of this — query App Insights.
- **Do not add `.UseOpenTelemetry()` on the agent as well.** `AIAgentBuilder.UseOpenTelemetry` wires an `OpenTelemetryAgent` that also auto-instruments the inner chat client, so stacking it on the chat-client registration records `gen_ai.client.token.usage` **twice per call** — measured 6 measurements for 2 calls instead of 4, inflating every token and cost figure by 50%. Agent-level OTel *alone* has the same problem for the same reason. Chat-client level only.
- **`AIAgentBuilder.Use(...)` function-invocation middleware never runs.** It compiles, registers and type-checks, and the callback is simply never invoked on Microsoft.Agents.AI 1.15.0 — measured zero invocations across a turn in which the tool definitely executed, with and without a service provider. `FunctionInvokingChatClient.FunctionInvoker` is likewise never consulted. The iteration cap is therefore `MaximumIterationsPerRequest` set through `.UseFunctionInvocation(...)` on the chat client (verified: cap 3 yields 4 `chat` spans), and per-tool telemetry comes from `InstrumentedAIFunction`, a `DelegatingAIFunction` that sits directly in the invocation path and cannot be bypassed.

`LogUsage` logs `input / cachedInput / output / reasoning / total` per turn and puts the same numbers on the current span as `gen_ai.usage.*` attributes — including `cache_read.input_tokens`, which the framework's histogram cannot express because it splits input/output only. **`cachedInput` is the one to watch:** non-zero means the static system prompt, tool schemas and JSON schema are hitting Azure OpenAI's automatic prompt cache. Measured in production at **2,176 cached of ~2,700 input tokens (75–82%)** on the agent calls; the first call of a conversation always misses because it populates the cache, and the small `ChatPlanner` prompts never cache because they are under the 1,024-token minimum. A persistent zero on the *large* calls means something started varying that prefix and the discount is gone. Prompt caching lowers cost but **not** TPM — quota is estimated before the cache lookup — so only compaction relieves the 200K TPM ceiling.

Two optional configuration keys, neither provisioned by Bicep:

| Config key | Effect |
|---|---|
| `Telemetry:Enable-Sensitive-Data` | Defaults to **true**, which is the long-standing behaviour: prompts and completions appear in traces. Set `false` to stop sending user questions and model answers to App Insights. |
| `AzureOpenAi:Price-Input-Per-1M`, `-Output-`, `-Cached-Input-` | Enables `gen_ai.usage.cost_usd`. Deliberately has **no defaults** — a hardcoded rate that drifts produces a confident, wrong cost dashboard. Cached input is charged at its own rate and subtracted from the input count, which matters here because ~80% of input tokens are cache reads. |

**Debugging a wrong answer starts with `MovieTracker.Backend.ToolCalls`.** Metrics tell you `DiscoverMovies` ran twice; only this tells you one ran with a cast filter and the other with a crew filter, which is the difference between right and wrong. The framework's `execute_tool` spans carry arguments too but are sampled away, so they cannot be relied on. Nearly every tool-selection bug found so far was invisible until the arguments were logged — `job=Actor` returning nothing, a raw `"show me the trailer for that one"` reaching a tool that cannot see the conversation, a runtime constraint the model passed correctly that TMDb then ignored. It is gated on `Telemetry:Enable-Sensitive-Data` because arguments contain user-supplied names and titles:

```kusto
traces | where timestamp > ago(30m)
| extend cat = tostring(customDimensions.CategoryName)
| where cat == 'MovieTracker.Backend.ToolCalls'
| project timestamp, message | order by timestamp asc
```

Worker logs do not appear in `func start` console output; read them in App Insights. Custom metrics land in the `customMetrics` table, but to be *alertable* in Metrics explorer they are published under the hardcoded namespace `azure.applicationinsights` scoped to the Application Insights resource, not the linked Log Analytics workspace — and a newly emitted metric can take 10–15 minutes to appear in metric definitions. Keep metric dimensions to low-arity values (tool name, outcome, model); Azure Monitor caps a metric at 5,000 time series per 24h and replaces all dimension values with `Maximum values reached` past that, so chat ids and movie ids belong on spans, never on metrics.
