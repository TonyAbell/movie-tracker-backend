# Optimizing a .NET Microsoft Agent Framework App (Microsoft.Agents.AI 1.15.0): Implementation Guide

## TL;DR
- **The single biggest misconception to kill first: none of the "conversation state" mechanisms (`AgentSession`, `ChatHistoryProvider`, `ChatOptions.ConversationId`, or the Responses API's server-side state) reduce *billed input tokens* on their own — every one of them still puts the full prior conversation into the model's input window on each turn.** OpenAI's own "Migrate to the Responses API" guide is explicit: "Even when using previous_response_id, all previous input tokens for responses in the chain are billed as input tokens in the API." Only two levers actually cut billed input tokens: **(a) trimming/summarizing** history, and **(b) automatic prompt caching**, which *discounts* (does not remove) the constant prefix. Caching cuts *cost*, not *TPM* — so it does nothing for your 200K DataZoneStandard throttling ceiling; only trimming does.
- Your fastest, lowest-risk wins are app-side and never touch the framework: **(1)** run the funny-fact pre-call **concurrently** with the main agent call (`Task.WhenAll`, not Workflows) to remove ~2 LLM round trips from the critical path; **(2)** stop re-hydrating the entire TMDb history every turn by **caching the hydrated view-model per message** in Cosmos; **(3)** make the prompt **cache-friendly** (static system prompt + 27 tool schemas + JSON schema first, byte-identical) and add a **rolling-window + summary reducer** to bound token growth.
- **Verified rename that bit the prior guide:** 1.15.0 ships `AgentResponse`/`AgentResponseUpdate` and `AgentSession` (the old `AgentRunResponse`/`AgentThread` names were renamed); the `RunAsync` parameter is now `session:`, not `thread:`. The auto-generated `/dotnet/api` pages still show the old names — treat them as stale. Streaming over SSE is **incoherent with your strict-JSON + full-hydration contract** and not worth adopting; A2A/AG-UI/OpenAI-compatible hosting adapters and the Durable extension are all poor fits here.

## Key Findings

### The token-efficiency question, answered precisely (Question A1)
For each state mechanism, the crucial question is whether it reduces *billed input tokens* or merely client-side payload:

| Mechanism | Verified in 1.15.0? | Reduces billed input tokens? | What it actually does |
|---|---|---|---|
| `AgentSession` + `SerializeSessionAsync`/`DeserializeSessionAsync` | **Yes** (namespace `Microsoft.Agents.AI`; renamed from `AgentThread`) | **No** | Persists conversation state (incl. full history) across requests/restarts. Full history is still replayed into the model input. Moves *where* state lives, not what's billed. |
| `ChatClientAgentOptions.ChatHistoryProvider` | **Yes** (property + `ChatHistoryProviderFactory`; `CosmosChatHistoryProvider` ships in `Microsoft.Agents.AI.CosmosNoSql`, **preview**) | **No** (by itself) | Framework loads history before each call and persists after. Same replay. **But** it's the correct hook to attach a *reducer* (trimming/summarization), which is what reduces tokens. |
| `AIContextProvider` | **Yes** (base class in `Microsoft.Agents.AI`) | **Depends** | Injects/overrides context messages, tools, instructions before invocation. Can *reduce* tokens if you use it to inject a compact summary instead of raw history — or *increase* them (RAG/memory). |
| `ChatOptions.ConversationId` | **Yes** (used for service-managed history) | **No** | Signals that the *service* holds history (Responses API / Foundry). The service still injects all prior turns into the input window. |
| Responses API (`OpenAIResponseClientExtensions.AsAIAgent`, `previous_response_id`) | **Yes** (extension confirmed in `Microsoft.Agents.AI.OpenAI`) | **No** | Server-side conversation state removes history from *your request payload*, but per OpenAI's migration guide "all previous input tokens for responses in the chain are billed as input tokens." Savings come only from prompt caching of the now-identical prefix. |

**Bottom line for A1:** switching state mechanisms is a *code-hygiene and payload* change, not a token-cost change. If you want fewer billed tokens you must trim/summarize; if you want cheaper billed tokens you must make the prefix cacheable. (A Microsoft Q&A thread on `previous_response_id` confirms the mechanism: Azure "reconstitutes the full conversation context server-side and injects all prior turns into the model's input window… counted as input tokens on every follow-up request," with savings arising only because the identical prefix "is very likely to qualify for automatic prompt caching.")

### Prompt caching on Azure OpenAI (Question A2)
- **Eligibility:** automatic, no code change, for prompts ≥ **1,024 tokens**; the **first 1,024 tokens must be byte-identical** across requests; matching then extends in 128-token increments. Per Microsoft Learn's "Prompt caching with Azure OpenAI in Microsoft Foundry Models": "After the first 1,024 tokens, cache hits occur for every 128 additional identical tokens. A single character difference in the first 1,024 tokens results in a cache miss, which is characterized by a cached_tokens value of 0." Routing uses a hash of roughly the first 256 tokens.
- **What's cacheable:** the structured-output JSON schema acts as a prefix to the system message and is cacheable; the tools array and the messages array both count toward the 1,024-token minimum and are cacheable. Your large static system prompt + 27 tool schemas + strict JSON schema is an ideal, large constant prefix.
- **Discount:** per Redress Compliance's 2026 Azure OpenAI pricing analysis, "Cached input prices at 10 percent of the input rate on the GPT-5 tiers… a 90 percent discount. GPT-4.1 and o4-mini cache at 25 percent of input… The 50 percent figure still in circulation is the GPT-4o number." Confirm the exact rate for `gpt-5.4-mini` on the Azure pricing page — this model name is newer than most indexed sources, so generalize from the GPT-5 family and verify. Microsoft Learn: "cache reads are billed at a discount on input token pricing for Standard deployment types and up to 100% discount on input tokens for Provisioned deployment types."
- **What breaks caching:** anything that changes the prefix — appending tool-call/tool-result messages *at the front*, reordering tools, injecting a per-request timestamp or session id near the top, or changing the JSON schema. Appending new turns at the *end* is fine and preserves the cached prefix; that is exactly the shape you want.
- **TTL:** the two sources disagree and both matter. OpenAI's own "Prompt Caching in the API" states caches "are typically cleared after 5-10 minutes of inactivity and are always removed within one hour of the cache's last use." Microsoft's Azure Foundry docs state caches are cleared within **24 hours**. Treat the practical eviction window as *minutes of inactivity* for a low-traffic chat app; don't count on long-lived cache survival between infrequent turns. In-memory caching is compatible with all data-residency regions; extended caching temporarily stores KV tensors on GPU machines (a residency consideration — see Caveats).
- **⚠️ Critical for your 200K TPM DataZoneStandard deployment:** **cached tokens still count against TPM.** Per Microsoft Learn's quota docs, "TPM rate limits are based on the maximum number of tokens that are estimated to be processed by a request at the time the request is received. It isn't the same as the token count used for billing, which is computed after all processing is completed." The estimate is computed from the prompt + `max_tokens` *before* any cache lookup, so prompt caching lowers your bill but gives you **zero additional TPM headroom**. Only trimming/summarizing reduces TPM pressure. On gpt-5.6+ Microsoft now bills cache *writes* in addition to discounted reads ("Models before gpt-5.6 don't charge extra to write to the cache. On gpt-5.6 and later models, cache writes are billed in addition to discounted cache reads"); for `gpt-5.4-mini` (pre-5.6) cache writes are almost certainly free, but verify.

### Trimming/summarization that preserves follow-ups (Question A3)
The framework ships an `InMemoryChatHistoryProvider` that accepts a reducer, e.g. `MessageCountingChatReducer(20)`, configured via `InMemoryChatHistoryProviderOptions { ChatReducer = ... }`. A naive count/token trim will break "which of those had the highest rating?" because that follow-up depends on the *entities* from the prior turn, not the prose. The robust pattern: keep the last N raw turns **plus** a rolling structured summary that always carries forward the last movie-list result (ids + names + any ratings already fetched). Inject that summary via an `AIContextProvider` so it's always present regardless of how aggressively raw turns are trimmed. Note the open question raised in repo discussion #4443: persisting a *session* alone grows unbounded because it holds the entire history and "there doesn't seem to be a built-in way to reduce or trim the history within the session" — so pair a persistent provider with an explicit reducer rather than relying on session serialization to stay small.

### Stop re-hydrating the entire history (Question A4)
This is pure app-side and one of the best value/risk items. Today every movie id in every past assistant message is re-fetched from TMDb on every call (memory-cached per instance, so it's cold on every new Functions instance and grows O(conversation length)). Fix: **persist the hydrated `movieList` view-model alongside each assistant message in Cosmos** when you first build it. On subsequent turns, read the stored view-model back rather than re-walking TMDb. You still return the full message list the contract requires, but hydration cost becomes O(new turn) instead of O(history). This also removes a per-instance memory cache that doesn't survive scale-out.

### 27 flat tools (Question B5)
A single flat list of 27 tools sent on every call hurts two ways: selection accuracy (more near-duplicate schemas to disambiguate) and cost/cache pressure (larger tool array — though as a *constant* prefix it caches well). Options, best first for your app:
- **Prune/consolidate.** Ten date helpers is a strong smell — most date math should be done in C#, not exposed as LLM tools. Collapsing the 7–10 date/planner helpers into 1–2 higher-level tools is the highest-leverage change and keeps everything in one agent.
- **Agents-as-tools (`AIAgent.AsAIFunction()`).** Confirmed: extension method in namespace `Microsoft.Agents.AI` (class `AIAgentExtensions`, source `dotnet/src/Microsoft.Agents.AI/AgentExtensions.cs`), returns a `Microsoft.Extensions.AI.AIFunction`; the inner agent's Name/Description become the tool name/description and it runs its own tool-calling loop internally via `agent.RunAsync`. Good for grouping (e.g., a "RatingsAgent" that owns the ratings/trailer tools) so the top agent sees a few coarse tools. Cost: each delegated call is a nested LLM round trip — more latency and more total tokens, so only worth it if pruning alone can't get tool count down. (Exact optional-parameter list not fully confirmed against source; the confirmed minimum form is `agent.AsAIFunction()`.)
- **Handoff/group-chat orchestration** (`AgentWorkflowBuilder.BuildHandoff`/handoff patterns): overkill for a single-domain movie Q&A — see "Don't bother."

### Concurrent funny-fact (Question B6)
The funny-fact currently runs **serially before** the main call: LLM #1 (entity detection) → Wikipedia/Wikidata fetch → LLM #2 (write fact), then the main agent call. Those ~2 extra round trips sit on the critical path for no reason — the funny fact is independent of the main answer. Run them concurrently with `Task.WhenAll`. **What do Workflows (`BuildConcurrent`) buy over `Task.WhenAll`? Nothing here — be blunt.** `AgentWorkflowBuilder.BuildConcurrent(IEnumerable<AIAgent> agents, Func<IList<List<ChatMessage>>, List<ChatMessage>>? aggregator)` fans one input out to multiple agents and aggregates; it adds a superstep runtime, event streaming (`AgentResponseUpdateEvent`), and checkpointing you don't need for two independent tasks inside a single request/response Function. Its aggregator signature also fights your two-different-shapes output (a fact string + a structured movie list). Use plain `Task.WhenAll`. **⚠️ TPM note:** firing both LLM calls simultaneously makes your token usage bursty — two concurrent calls against the 200K TPM ceiling with per-second/RPM quantization (RPM is set proportionally to TPM) raises 429 risk under load; add retry-with-jitter (see B7).

### Middleware for retries / tool-call validation / anti-hallucination (Question B7)
Confirmed surface in 1.15.0:
- `agent.AsBuilder().Use(...).Build()` returns a **new** agent — you must assign it or the middleware never runs.
- **Function-calling middleware** signature: `async ValueTask<object?> Mw(AIAgent agent, FunctionInvocationContext context, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next, CancellationToken ct)`. `FunctionInvocationContext` exposes `Function.Name` and `Arguments`, is scoped to a single tool call, and wraps each call independently within a turn. This is the correct home for your hand-rolled defenses: validate/sanitize tool arguments before execution, cap tool-call counts per turn, and **guard against model-invented IDs** by validating ids against TMDb before the call proceeds (short-circuit by returning an error result instead of calling `next`). It only fires for `FunctionInvokingChatClient`-based tools (your local functions qualify; hosted/OpenAPI tools may not — a known gap per repo issue #2960).
- Retries belong in **IChatClient middleware** (`AsBuilder().UseFunctionInvocation(...)` / a retry decorator) or via `FunctionInvokingChatClient` `IncludeDetailedErrors`, so transient 429s from the DataZoneStandard cap get retried with backoff.

### A better client API — streaming, delta route, hosting adapters (Questions C8–C10)
- **Streaming (`RunStreamingAsync` → `IAsyncEnumerable<AgentResponseUpdate>`): incoherent with your current contract.** Your response is a *strict JSON structured output* (`MovieList[]`, `FunnyFact`) that must be fully materialized before the hydration step can attach `movieList` view-models. The framework's own structured-output guidance for streaming confirms this shape: you collect all updates and call `ToAgentResponseAsync()` / `Deserialize<T>()` at the *end* — i.e., you don't get usable partial structured output mid-stream. You also can't hydrate a half-emitted movie list. Additionally, Azure Functions isolated worker **cannot stream** with the default `HttpRequestData` model (per Microsoft Learn, "If you use HttpRequestData, the body of the HTTP request can't be a stream… consider using the ASP.NET Core integration model instead"), and per Microsoft Learn "230 seconds is the maximum amount of time that an HTTP triggered function can take to respond to a request… because of the default idle timeout of Azure Load Balancer." SSE is a poor fit for Functions generally. Streaming is only coherent for a plain-text side-channel (e.g., streaming the funny-fact text), which is marginal. **Don't stream the structured payload.**
- **Delta-only v2 route: worth it, additive.** Returning only the latest turn instead of replaying the full hydrated transcript is a real win for payload and for the re-hydration problem. Add `POST /api/chat/{chatId}/ask/v2` returning `{ funnyFact, turn: { role, text, movieList? } }` (just the new assistant turn, hydrated once). Keep the existing route returning the full transcript for the existing frontend. This pairs naturally with the hydration-cache fix.
- **Hosting adapters (A2A, OpenAI-compatible, AG-UI) and the Durable extension: poor fit — say so plainly.** You have a fixed HTTP contract and a specific frontend. A2A exposes your agent to *other agents* (`Microsoft.Agents.AI.Hosting.A2A.AspNetCore`); OpenAI-compatible endpoints let *generic OpenAI SDK clients* call it (`/v1/chat/completions`, `/v1/responses`); AG-UI is for a generic agent-UI protocol. None has a consumer in your system, and all are net-new surface (several still preview, e.g. AG-UI hosting at `1.0.0-preview…`). The Durable Azure Functions extension targets long-running/multi-hour orchestrations needing checkpointing and exactly-once semantics — your calls are 5–10s, far under the 230s limit. Adopting it adds a storage backend and complexity for zero benefit.

## Ranked recommendations — (est. token/latency saving) ÷ (implementation risk)

| # | Recommendation | Est. saving | Risk | Breaks contract? | Notes |
|---|---|---|---|---|---|
| 1 | **Concurrent funny-fact** (`Task.WhenAll`, remove ~2 LLM round trips from critical path) | High latency (likely the largest single latency cut) | Low | No | Watch bursty TPM/RPM; add retry |
| 2 | **Cache hydrated `movieList` per message in Cosmos** (hydration O(new turn) not O(history)) | High latency + TMDb/RU | Low | No | Pure app-side |
| 3 | **Cache-friendly prompt ordering** (static system prompt + tools + JSON schema first, byte-identical) | High cost (~90% off the constant prefix on GPT-5 tiers) | Low | No | No TPM relief |
| 4 | **Rolling-window + summary reducer** via `ChatHistoryProvider`/`AIContextProvider` | High cost **and** TPM | Medium | No (internal) | Must preserve follow-up entities |
| 5 | **Prune/consolidate the 27 tools** (esp. the date helpers) | Medium cost + accuracy | Low–Med | No | Do C#-side date math |
| 6 | **Function-calling middleware** for id validation + retries | Quality + resilience | Low–Med | No | Replaces hand-rolled defenses |
| 7 | **Additive `/ask/v2` delta route** | Medium payload | Medium | No (additive) | New route only |
| 8 | **Move to Responses API for server-side state** | Payload only; cost only via caching | High | No (internal) | Preview/residency risk; **no billed-token or TPM reduction** |

## Concrete C# for the top 3

> API names below verified against the shipped 1.15.0 surface (`AgentResponse`, `AgentSession`, `session:` parameter, `AsBuilder().Use`, `FunctionInvocationContext`, `RunStreamingAsync`). Where a member could not be fully confirmed it is flagged inline.

### 1. Run the funny-fact concurrently with the main agent call
```csharp
// BEFORE: funnyFact computed serially (2 LLM round trips) THEN agent.RunAsync.
// AFTER: both run concurrently. Note the response type is AgentResponse (NOT AgentRunResponse) in 1.15.0.

public async Task<ChatAskResult> AskAsync(
    string chatId,
    IList<ChatMessage> history,          // Microsoft.Extensions.AI.ChatMessage
    string userText,
    CancellationToken ct)
{
    var messages = new List<ChatMessage>(history) { new(ChatRole.User, userText) };

    // Kick off the independent funny-fact pipeline (LLM -> wiki/wikidata -> LLM) without awaiting.
    Task<string> funnyFactTask = _chatPlanner.GenerateEnhancedFunnyFactAsync(userText, ct);

    // Main agent call runs at the same time.
    Task<AgentResponse> answerTask = _agent.RunAsync(messages, session: _session, cancellationToken: ct);

    await Task.WhenAll(funnyFactTask, answerTask).ConfigureAwait(false);

    AgentResponse answer = await answerTask;      // .Text, .Messages available
    string funnyFact      = await funnyFactTask;

    // ...deserialize structured output, hydrate, persist as today...
    return BuildResult(funnyFact, answer);
}
```
Do **not** wrap this in `AgentWorkflowBuilder.BuildConcurrent` — it adds a superstep/checkpointing runtime with no payoff for two independent tasks, and its aggregator shape doesn't match your two different output types.

### 2. Cache the hydrated movie view-model instead of re-walking TMDb every turn
```csharp
// Persist the hydrated view-model with each assistant message so future turns
// return the full contract-required list WITHOUT re-fetching all of history from TMDb.

public sealed record StoredAssistantTurn(
    string Role,
    string Text,
    IReadOnlyList<MovieViewModel>? MovieList);   // hydrated ONCE, then stored

// When producing a new assistant turn:
var hydrated = await _tmdb.HydrateAsync(newMovieIds, ct);   // only the NEW ids
var turn = new StoredAssistantTurn("assistant", answer.Text, hydrated);
await _cosmos.AppendTurnAsync(chatId, turn, ct);

// When building the contract response, read stored view-models back — no TMDb fan-out:
IReadOnlyList<StoredAssistantTurn> transcript = await _cosmos.LoadTranscriptAsync(chatId, ct);
var messages = transcript.Select(t => new ClientMessage(t.Role, t.Text, t.MovieList)).ToList();
// hydration cost is now O(new turn), not O(entire history)
```
Optionally add a TMDb-fact TTL if movie metadata can change; a per-instance `IDistributedMemoryCache` is fine as an L1 in front of Cosmos but does not survive scale-out (that's why the source of truth should be Cosmos).

### 3. Cache-friendly prefix + history reducer + id-validation middleware
```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// (a) Keep the large STATIC content first and byte-identical: instructions + tools + JSON schema.
//     Put anything per-request (timestamps, chatId) at the END so it never disturbs the cached prefix.
var options = new ChatClientAgentOptions
{
    Name = "MovieAgent",
    ChatOptions = new ChatOptions
    {
        Instructions   = StaticSystemPrompt,                    // unchanged across requests
        Tools          = _tools,                                // stable order => stable prefix
        ResponseFormat = ChatResponseFormat.ForJsonSchema<MovieAnswer>() // schema is part of the cacheable prefix
    },
    // (b) Bound token growth. InMemory provider + a reducer; pair with a summary context provider
    //     so follow-ups ("which of those had the highest rating?") keep the prior movie-list entities.
    ChatHistoryProvider = new InMemoryChatHistoryProvider(
        new InMemoryChatHistoryProviderOptions { ChatReducer = new MessageCountingChatReducer(20) }),
    AIContextProviders = [ new RollingMovieSummaryProvider() ]  // injects last movie-list ids+names+ratings
};

AIAgent baseAgent = _chatClient.AsAIAgent(options);   // .AsAIAgent extension, Microsoft.Agents.AI(.OpenAI)

// (c) Function-calling middleware: reject model-invented TMDb ids before the tool runs.
async ValueTask<object?> ValidateIds(
    AIAgent agent,
    FunctionInvocationContext context,
    Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
    CancellationToken ct)
{
    if (context.Function.Name is "GetMovieById" or "GetRating"
        && context.Arguments.TryGetValue("movieId", out var raw)
        && !await _tmdb.ExistsAsync(Convert.ToInt32(raw), ct))
    {
        // short-circuit: do NOT call next(); hand the model a correctable error
        return $"Error: movieId {raw} does not exist. Do not invent ids; call a search tool first.";
    }
    return await next(context, ct);
}

AIAgent agent = baseAgent
    .AsBuilder()
    .Use(ValidateIds)          // function-calling middleware; assign the returned agent!
    .Build();
```
> Verification flags: `InMemoryChatHistoryProvider`, `InMemoryChatHistoryProviderOptions.ChatReducer`, and `MessageCountingChatReducer` appear in official 1.x docs but confirm the exact constructor/property names against the `Microsoft.Agents.AI` assembly you have restored; `CosmosChatHistoryProvider` (if you prefer Cosmos-backed history over your existing manual Cosmos persistence) is in `Microsoft.Agents.AI.CosmosNoSql` and is **preview**. `ChatResponseFormat.ForJsonSchema<T>()` is the MEAI structured-output helper; you may keep your existing `AIJsonSchemaTransformOptions { DisallowAdditionalProperties, RequireAllProperties }` schema instead.

## Don't bother (features that look applicable but aren't)
- **Workflows `BuildConcurrent` for the funny-fact.** Plain `Task.WhenAll` does the same fan-out with none of the superstep/checkpoint/event-stream machinery, and the `BuildConcurrent` aggregator (`Func<IList<List<ChatMessage>>, List<ChatMessage>>`) doesn't fit your two-different-output-shapes case. Zero benefit here.
- **Multi-agent handoff / group-chat orchestration.** Designed for multi-domain routing (sales/support/billing). Your app is single-domain movie Q&A; a handoff router just adds an extra routing LLM call and latency.
- **A2A, OpenAI-compatible, and AG-UI hosting adapters.** No consumer in your system; you have a fixed HTTP contract and a bespoke frontend. These expose agents to *other agents* or *generic clients* you don't have. Net-new surface, several still preview.
- **Durable Task / Durable Functions extension.** For long-running (minutes-to-hours), checkpointed, exactly-once orchestrations. Your requests are 5–10s, far under the 230s HTTP limit. Adds a storage backend and operational weight for nothing.
- **Background responses (`AllowBackgroundResponses`, `ContinuationToken` polling).** Only relevant for long-running generations that risk the request timeout. Not your profile.
- **Streaming SSE for the structured payload.** Incompatible with strict-JSON structured output + full hydration, and awkward on Functions isolated worker. Skip (a plain-text funny-fact stream is the only coherent variant and is marginal).
- **Switching to the Responses API purely to "hold state server-side."** It does **not** reduce billed input tokens or TPM; it only shrinks your request payload. Adopt it only if you specifically want server-managed threads *and* you've confirmed Azure availability + `store:true` residency for your deployment — otherwise the ChatHistoryProvider path keeps you on the well-trodden Chat Completions surface you already use.

## Preview / unstable / constraint flags
- **`AgentResponse` vs `AgentRunResponse`:** 1.15.0 ships **`AgentResponse`/`AgentResponseUpdate`** (renamed per repo issue #2899; reflected in the changelog — "AgentRunResponse and AgentRunResponseUpdate were renamed to AgentResponse and AgentResponseUpdate" — and the current Learn "Running Agents" .NET page). The auto-generated `/dotnet/api` pages (e.g., `DelegatingAIAgent.RunAsync`) still show the old `AgentRunResponse`/`AgentThread` names — **stale, do not trust**. Confidence: high.
- **`session:` parameter:** `RunAsync`'s `thread` parameter was renamed to `session:` (type `AgentSession`). Per a Microsoft community-hub migration note, "The thread parameter is now session (type AgentSession). If you were using named arguments, this is a compile error." Confidence: high.
- **`SerializeSessionAsync`/`DeserializeSessionAsync`:** exist on `AIAgent` (serialize to/from `JsonElement`). There was an open request (repo issue #3725) to make serialize fully async because a synchronous `SerializeSession` variant existed — confirm which overload your build exposes. Confidence: medium-high.
- **OpenAI SDK compatibility (directly relevant to your pinned stack):** repo issue #4380 documented a runtime `MissingMethodException` (`ResponsesClient.get_Model()` not found) when `AsAIAgent()` on a Responses client ran against **OpenAI SDK 2.9.0**; the issue is **closed**. Root cause (per the issue thread): "Microsoft.Extensions.AI was compiled against OpenAI version 2.8.0, while your project is directly referencing version 2.9.0… remove the explicit reference and allow the correct version to be resolved transitively." Microsoft.Agents.AI.OpenAI 1.5.0 already pins **`OpenAI (>= 2.10.0)`**, so version 1.15.0 almost certainly requires ≥ 2.10.0 — which **matches your pinned OpenAI SDK 2.10.0**, so the Responses path should load. **Do not pin an older explicit `OpenAI` reference**, or you reintroduce the mismatch. `Microsoft.Agents.AI.OpenAI` depends on the base `OpenAI` SDK (+ `Microsoft.Extensions.AI.OpenAI`), **not** on `Azure.AI.OpenAI`; no evidence of a known Azure.AI.OpenAI 2.9.0-beta.1 ↔ OpenAI 2.10.0 conflict was found. Verify the exact 1.15.0 dependency block on the NuGet page. Confidence: medium-high (exact 1.15.0 dependency block not directly fetched).
- **`Microsoft.Agents.AI.CosmosNoSql` is preview** (versions like `1.13.0-preview…`), not GA. If you adopt `CosmosChatHistoryProvider`/`CosmosCheckpointStore`, treat as preview and pin deliberately; it also has security notes (stored history is accepted as-is → indirect prompt-injection risk if the store is compromised; use `MessageTtlSeconds` for retention).
- **Responses API on Azure:** `previous_response_id` bills all prior tokens; the persistent **Conversations API** (server `conversation_id`) reportedly rolled out via the Foundry v1 REST API (~April 2026) but users report `404`s on some resources — treat as not-yet-reliable. `store:true` persists responses server-side; on **DataZoneStandard**, data at rest stays in your geography and processing stays within the data zone, but **extended** prompt caching temporarily stores KV tensors on GPU machines — a residency nuance to check with your compliance owner. In-memory caching is residency-safe.
- **`gpt-5.4-mini` specifics:** the exact model name and its caching discount/cache-write behavior are newer than most indexed sources. Generalize from the GPT-5 family (~90% cached-input discount; cache writes free pre-gpt-5.6) and **verify the exact rate and cache-write policy on the Azure pricing page** before quoting a number.
- **200K TPM DataZoneStandard:** nothing recommended here *newly constrains* the deployment, but note explicitly: **prompt caching does not reduce TPM consumption** (TPM is estimated pre-processing), and **concurrent calls (funny-fact + main) are burstier** against the ceiling — add retry-with-jitter for 429s and consider a token-bucket limiter if you approach the cap.

## Recommendations (staged)
1. **Ship this week (low risk, no contract change):** funny-fact `Task.WhenAll` (#1) + hydrated-view-model caching in Cosmos (#2). Instrument first — see benchmarking note below.
2. **Next (cost + TPM):** cache-friendly prefix ordering (#3), then the rolling-window + summary reducer (#4). Confirm `cached_tokens > 0` in the usage response after #3; confirm mean input tokens/turn drops and stops growing with conversation length after #4.
3. **Then (quality + resilience):** consolidate the 27 tools starting with the date helpers (#5) and add function-calling middleware for id validation + retry (#6).
4. **Optional (additive API):** ship `/ask/v2` delta route (#7) once #2 is in place.
5. **Only if a concrete need appears:** Responses API server-side state (#8) — and only after confirming Azure availability and residency for your deployment.

**Benchmarking thresholds that would change these calls:** measure with a **same-session A/B against unchanged code** (your end-to-end latency drifts 10–15% between sessions, so cross-session comparisons are meaningless). Track: p50/p95 end-to-end latency, mean billed input tokens/turn, `cached_tokens`/`prompt_tokens` ratio, TMDb call count/turn, and 429 rate. If after #3 `cached_tokens` stays 0, your prefix isn't byte-stable — fix ordering before doing anything else. If mean input tokens/turn still grows linearly after #4, the reducer/summary isn't engaging. If the 429 rate rises after #1, add the token-bucket limiter before rolling out further concurrency.

## Caveats
- The exact 1.15.0 NuGet dependency block was inferred (from 1.5.0, which already requires `OpenAI ≥ 2.10.0`) rather than fetched directly — verify on the package page. Your pinned OpenAI 2.10.0 satisfies the constraint that fixed issue #4380.
- Several member names in the reducer/history-provider area (`InMemoryChatHistoryProviderOptions.ChatReducer`, `MessageCountingChatReducer`) and the streaming-aggregation helper (`ToAgentResponseAsync()` vs the older `ToAgentRunResponseAsync()`) should be confirmed against your restored assembly, since docs and blogs straddle the rename boundary.
- Prompt-cache TTL sources conflict: OpenAI's API docs say 5–10 min inactivity / removed within 1 hour, while Azure Foundry docs say within 24 hours. For a low-traffic chat app, plan for minutes-scale eviction, not hours.
- `gpt-5.4-mini` behavior (caching discount, cache-write charges) is generalized from the GPT-5 family; confirm on the Azure pricing page.
- All token/latency figures here are directional; the guide deliberately prescribes *what to measure* rather than promising hard percentages, given the 10–15% inter-session drift.