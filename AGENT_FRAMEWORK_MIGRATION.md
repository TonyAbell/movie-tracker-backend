# AGENT_FRAMEWORK_MIGRATION.md — Migrating a .NET App from Microsoft Semantic Kernel to Microsoft Agent Framework

> **Purpose:** Reference/instruction document for an AI coding agent (Claude Code) to perform a real Semantic Kernel → Microsoft Agent Framework migration in a .NET (possibly F#/.NET 10) codebase. Verified against Microsoft docs, the `microsoft/agent-framework` repo, NuGet, and Microsoft DevBlogs current to **July 2026**. Agent Framework .NET latest stable: **`Microsoft.Agents.AI` 1.15.0** (confirmed on NuGet Gallery). Preview-only and fast-changing areas are flagged inline.

## TL;DR
- **Microsoft Agent Framework (MAF) is the GA successor to both Semantic Kernel (SK) and AutoGen.** It reached **1.0 GA on April 3, 2026** ("we're thrilled to announce that Microsoft Agent Framework has reached version 1.0 for both .NET and Python. This is the production-ready release: stable APIs, and a commitment to long-term support" — Shawn Henry, MAF devblog). Microsoft calls it "Semantic Kernel v2.0 (it's built by the same team!)" and commits to supporting **SK v1.x with critical bug and security fixes for at least one year after MAF GA** — so migrate deliberately, not in a panic.
- **The migration is mostly mechanical:** `Kernel` + `ChatCompletionAgent` collapse into a single `AIAgent` created via `chatClient.AsAIAgent(...)`; `[KernelFunction]` plugins become `AIFunctionFactory.Create(...)` tools; `AgentGroupChat`/SK orchestration becomes graph-based **Workflows**; options move from `PromptExecutionSettings`/`KernelArguments` to `ChatOptions`. **Critical:** a post-preview rename means you must emit the **new** names — `AgentSession` (not `AgentThread`), `CreateSessionAsync()` (not `GetNewThread()`), and the `session` parameter (not `thread`).
- **MAF and SK coexist in one solution, so migrate incrementally file-by-file.** Known gaps to plan around: SK **Process Framework is discontinued** (use Workflows), prompt-template formats (**Handlebars/Liquid/YAML** prompts) are **not ported**, and **F# works but needs care** around attributes, records, and async interop.

## Key Findings
1. **Status & official guidance.** MAF unifies SK's enterprise foundations with AutoGen's orchestration, layered on `Microsoft.Extensions.AI` (MEAI). GA 1.0 shipped **April 3, 2026** (RC on Feb 19, 2026; public preview Oct 2025). Microsoft's stance: new agent projects start on MAF; stable SK production agents may migrate lazily; all new agentic investment goes to MAF first; AutoGen enters maintenance mode.
2. **A rename affects every line of state-management code you generate.** The conversation-state type was renamed `AgentThread` → `AgentSession`, creation `GetNewThread()` → `CreateSessionAsync()`, the `RunAsync` parameter `thread` → `session`, and serialization moved onto the agent (`SerializeSessionAsync`/`DeserializeSessionAsync`). Microsoft's auto-generated `/dotnet/api/...` reference pages and many blogs still show the **old** names — the conceptual docs and migration guide show the new ones. Always emit the new names.
3. **Package remap is clean:** `Microsoft.SemanticKernel.*` → `Microsoft.Agents.AI.*` + `Microsoft.Extensions.AI`.
4. **Interop is first-class:** existing `[KernelFunction]` methods work as tools with zero changes; SK `KernelFunction` instances can be adapted; SK and MAF packages install side by side.
5. **Gaps to design around:** Process Framework not carried forward; prompt-template languages not ported; several hosting/declarative/DevUI features preview-only.

## Details

### 1. What MAF is and how it relates to SK / AutoGen
Microsoft Agent Framework is an open-source (MIT) SDK and runtime for building agents and multi-agent workflows in .NET, Python, and Go. Per the official overview it "combines AutoGen's simple agent abstractions with Semantic Kernel's enterprise features — session-based state management, type safety, middleware, telemetry — and adds graph-based workflows for explicit multi-agent orchestration," and is "the direct successor … the next generation of both Semantic Kernel and AutoGen," built by the same teams on top of MEAI's `IChatClient`.

**Timeline:** introduced October 2025 (public preview) → **Release Candidate February 19, 2026** (API surface stabilized, v1.0 features complete) → **GA 1.0 April 3, 2026** with a long-term-support commitment.

**Support policy** (official "Semantic Kernel and Microsoft Agent Framework" devblog): *"Think of Microsoft Agent Framework as Semantic Kernel v2.0 (it's built by the same team!) … we will continue to support Semantic Kernel v1.x for the foreseeable future. We will continue to address critical bugs, security issues and we'll take some existing Semantic Kernel features to GA … and for at least one year after Microsoft Agent Framework leaves Preview and is Generally Available."* Community migration guidance corroborates: *"SK agent abstractions are supported and will receive critical bug fixes for at least one year after Agent Framework GA."*

**When to migrate:**
- **New projects →** start on MAF.
- **Stable SK agents in production →** migrate opportunistically; you have ≥1 year of SK v1.x support. Trigger a full migration when you need MAF-only features (MCP, handoff/magentic orchestration, checkpointing/durability, human-in-the-loop).
- **Complex SK apps** relying on Process Framework, prompt templates, or deep vector-store/RAG pipelines → stage the move and keep SK where MAF still has gaps.

### 2. NuGet package mapping

| Semantic Kernel package | Agent Framework replacement | Notes |
|---|---|---|
| `Microsoft.SemanticKernel` | `Microsoft.Agents.AI` (1.15.0) + `Microsoft.Extensions.AI` (10.4.1) | Core agent abstraction `AIAgent`; message/content types come from MEAI. |
| `Microsoft.SemanticKernel.Agents.Core` | `Microsoft.Agents.AI` | `ChatClientAgent` / base `AIAgent`. |
| `Microsoft.SemanticKernel.Agents.OpenAI` | `Microsoft.Agents.AI.OpenAI` | Extensions on `OpenAIClient` / `AzureOpenAIClient`. |
| `Microsoft.SemanticKernel.Agents.AzureAI` | `Microsoft.Agents.AI.Foundry` (+ `Azure.AI.Projects`, `Azure.AI.Agents.Persistent`) | Azure AI Foundry / persistent agents. |
| `Microsoft.SemanticKernel.Connectors.OpenAI` / `.AzureOpenAI` | `Azure.AI.OpenAI` / `OpenAI` surfaced via `IChatClient` (`.AsIChatClient()`) | Provider SDKs consumed through MEAI. |
| SK orchestration (`Microsoft.SemanticKernel.Agents.Orchestration`, `.Runtime.InProcess`) | `Microsoft.Agents.AI.Workflows` | Graph workflows + orchestration builders. |
| (declarative / YAML) | `Microsoft.Agents.AI.Declarative`, `Microsoft.Agents.AI.Workflows.Declarative` | **Preview.** |
| hosting / DI | `Microsoft.Agents.AI.Hosting` (+ `...Hosting.A2A`, `...Hosting.A2A.AspNetCore`) | `AddAIAgent`. |

**Install (typical):** `dotnet add package Microsoft.Agents.AI`. Provider packages currently ship `--prerelease` variants alongside the stable 1.x core — **pin explicit versions**. Packages target **.NET 8.0 / .NET Standard 2.0 / .NET Framework 4.7.2+** (so .NET 10 is fine). MAF moved from the MEAI 9.x preview to the **stable `Microsoft.Extensions.AI` 10.4.1**.

First-party providers at GA (exactly seven, per the 1.0 blog): **Microsoft Foundry, Azure OpenAI, OpenAI, Anthropic Claude, Amazon Bedrock, Google Gemini, and Ollama.**

### 3. Core concept mapping

| Semantic Kernel | Agent Framework | Notes |
|---|---|---|
| `Kernel` | *(gone)* | No kernel required; agent is built directly from a chat client. |
| `ChatCompletionAgent`, `AzureAIAgent`, `OpenAIAssistantAgent` | `ChatClientAgent` / base `AIAgent` | One agent type over any `IChatClient`. |
| `[KernelFunction]` + `KernelPlugin` + `Kernel.Plugins.Add` | `AIFunctionFactory.Create(method)` passed to `tools:` | Attribute not required; `[Description]` optional. |
| `AgentThread` / `ChatHistory` | **`AgentSession`** (renamed from `AgentThread`) | `await agent.CreateSessionAsync()`. |
| `AgentGroupChat` / SK orchestration | Workflows + orchestration builders | `AgentWorkflowBuilder`, `WorkflowBuilder`. |
| `KernelArguments` + `PromptExecutionSettings` | `ChatOptions` (+ `ChatClientAgentRunOptions`) | `MaxTokens` → `MaxOutputTokens`. |
| Function invocation filters / `IFunctionInvocationFilter` | Agent middleware / function middleware | `.AsBuilder().Use(...)`, `FunctionInvocationContext`. |
| OpenTelemetry via SK | `.UseOpenTelemetry()` in the `IChatClient` pipeline | GenAI semantic conventions emitted automatically. |
| DI: `AddKernel()` + keyed `Agent` | `AddAIAgent(...)` / `AddKeyedSingleton<AIAgent>` | From `Microsoft.Agents.AI.Hosting`. |
| `agent.InvokeAsync` / `InvokeStreamingAsync` | `agent.RunAsync` / `RunStreamingAsync` | Returns `AgentRunResponse` / `IAsyncEnumerable<AgentRunResponseUpdate>`. |
| Memory / vector connectors | `AIContextProvider` + Mem0 / Redis / Neo4j / Foundry memory | SK vector stores can be reused via adapter (see §4). |
| MCP | `ModelContextProtocol` SDK; `McpClient.ListToolsAsync()` → tools; `McpServerTool` to expose | First-class. |
| Human-in-the-loop / checkpointing | Built into Workflows + Durable extension | Durable hosting is **preview**. |

**Invocation return types (authoritative, from the API reference):** `RunAsync(...)` returns `Task<AgentRunResponse>` — text in `.Text` / `.ToString()`, all messages (tool calls, function results, reasoning, final) in `.Messages`. `RunStreamingAsync(...)` returns `IAsyncEnumerable<AgentRunResponseUpdate>`. Conceptual docs sometimes write `AgentResponse` / `AgentResponseUpdate` in prose; the real type names are **`AgentRunResponse` / `AgentRunResponseUpdate`**.

**Session serialization:** `JsonElement s = await agent.SerializeSessionAsync(session);` and `AgentSession restored = await agent.DeserializeSessionAsync(s);` — both on the **agent** (renamed from the old thread-based `thread.SerializeAsync()` / `agent.DeserializeThreadAsync()`). You **must use the same agent instance** for serialize/deserialize, because the agent attaches behaviors to the session.

### 4. Interop / incremental migration
- **Existing `[KernelFunction]` plugins transfer with zero changes** — MAF consumes the same methods as tools. To register in MAF, drop the plugin/kernel wrapper and pass the method to `AIFunctionFactory.Create(...)`.
- **SK and MAF packages install together and coexist** in the same project; migrate file-by-file. A common intermediate `.csproj` keeps `Microsoft.SemanticKernel` for plugins/kernel config while adding `Microsoft.Agents.AI.OpenAI` / `Microsoft.Agents.AI.Workflows`.
- **SK `KernelFunction` instances** (including prompt functions and vector-store `create_search_function` results) can be adapted to MAF tools. Python exposes a documented `.as_agent_framework_tool(kernel=...)` (requires `semantic-kernel` ≥ 1.38); in .NET, wrap the underlying method via `AIFunctionFactory.Create`. This lets you keep SK vector-store/RAG infrastructure while running MAF agents.
- **Architectural layering:** `Microsoft.Extensions.AI (IChatClient)` → `Semantic Kernel (plugins/memory/filters)` → `Microsoft Agent Framework (agents + workflows)` → provider SDKs (Azure OpenAI / OpenAI / Anthropic / Ollama / …). SK becomes a foundation layer you can keep calling through the shared `IChatClient`.

### 5. Before/after C# code

**(a) Simple chat agent**
```csharp
// SK (before)
Kernel kernel = Kernel.CreateBuilder()
    .AddAzureOpenAIChatCompletion(deployment, endpoint, new DefaultAzureCredential())
    .Build();
ChatCompletionAgent agent = new() { Instructions = "You are helpful.", Kernel = kernel };

// MAF (after)
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;

AIAgent agent = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deployment)
    .AsAIAgent(instructions: "You are helpful.", name: "Helper");

Console.WriteLine(await agent.RunAsync("What is the largest city in France?"));
```

**(b) Agent with tools**
```csharp
// SK
public class WeatherPlugin {
    [KernelFunction, Description("Gets weather")]
    public string GetWeather([Description("City")] string city) => $"{city}: sunny";
}
kernel.Plugins.AddFromType<WeatherPlugin>();

// MAF
using System.ComponentModel;
using Microsoft.Extensions.AI;

[Description("Gets weather")]
static string GetWeather([Description("City")] string city) => $"{city}: sunny";

AIAgent agent = chatClient.AsAIAgent(
    instructions: "You help with weather.",
    tools: [AIFunctionFactory.Create(GetWeather)]);
```

**(c) Providers (Azure OpenAI vs OpenAI vs Azure AI Foundry persistent agents)**
```csharp
// OpenAI
AIAgent a = new OpenAIClient(apiKey).GetChatClient("gpt-4o-mini").AsAIAgent(instructions: "...");

// Azure OpenAI + managed identity
AIAgent b = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deployment).AsAIAgent(instructions: "...");

// Azure AI Foundry (named, versioned, server-side persistent agent)
AIProjectClient proj = new(new Uri(foundryEndpoint), new DefaultAzureCredential());
AIAgent c = await proj.CreateAIAgentAsync(name: "FoundryAgent", model: deployment, instructions: "...");
// retrieve existing: await proj.GetAIAgentAsync(agentId)  /  proj.AsAIAgent(agentRecord)
// lower-level control: proj.GetPersistentAgentsClient()  (Azure.AI.Agents.Persistent)
```
> `RunAsync()`, `RunStreamingAsync()`, tools, and sessions behave identically whether the agent is local or Foundry-hosted; only the client construction differs.

**(d) Sessions / multi-turn state**
```csharp
AgentSession session = await agent.CreateSessionAsync();
Console.WriteLine(await agent.RunAsync("Hi, I'm Alex.", session));
Console.WriteLine(await agent.RunAsync("What's my name?", session)); // remembers "Alex"

// persist across restarts (same agent instance required to deserialize)
JsonElement saved = await agent.SerializeSessionAsync(session);
AgentSession resumed = await agent.DeserializeSessionAsync(saved);
```

**(e) Streaming**
```csharp
await foreach (AgentRunResponseUpdate update in agent.RunStreamingAsync("Tell a joke", session))
    Console.Write(update); // update is ToString()-friendly
```

**(f) Structured output**
```csharp
using Microsoft.Extensions.AI;

JsonElement schema = AIJsonUtilities.CreateJsonSchema(typeof(PersonInfo));
ChatOptions opts = new() {
    ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "PersonInfo", "A person") };
AIAgent agent = chatClient.CreateAIAgent(new ChatClientAgentOptions { ChatOptions = opts });
// or the generic helper: ChatResponseFormat.ForJsonSchema<PersonInfo>()
```
> **Known bug (agent-framework issue #2874):** `ResponseFormat = ForJsonSchema<T[]>` for arrays/lists returns HTTP 400 (`schema must be type: "object"`). The typed `RunAsync<T>()` path handles lists correctly. **Workaround:** wrap the array in an object type (e.g. `record MovieList(Movie[] Movies)`).

**(g) Multi-agent orchestration (Workflows)**
```csharp
using Microsoft.Agents.AI.Workflows;

// Sequential (pipeline)
Workflow seq = AgentWorkflowBuilder.BuildSequential(writer, reviewer);

// Concurrent (fan-out / fan-in)
Workflow conc = AgentWorkflowBuilder.BuildConcurrent(physicist, chemist);

// Handoff (dynamic routing) — replaces AgentGroupChat + custom selection
Workflow handoff = AgentWorkflowBuilder.CreateHandoffBuilderWith(triage)
    .WithHandoffs(triage, [travel, vision, general])
    .WithHandoff(travel, triage)
    .WithHandoff(vision, triage)
    .WithHandoff(general, triage)
    .Build();

await using StreamingRun run = await InProcessExecution.RunStreamingAsync(seq, "input");
await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
```
Per the GA blog, **sequential, concurrent, handoff, group chat, and Magentic-One are all stable in .NET**, and "all patterns support streaming, checkpointing, human-in-the-loop approvals, and pause/resume." (One older Medium post wrongly claims Magentic is C#-unsupported — disregard; official docs and the orchestration-1.0 announcement confirm .NET support.) You can also compose agents as tools: `agentB.AsAIFunction()` passed into agent A's `tools:`. For custom graph topologies, drop to the raw `WorkflowBuilder` (executors + edges + superstep BSP execution model).

**(h) ASP.NET Core hosting + DI**
```csharp
// Program.cs
IChatClient chatClient = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deployment).AsIChatClient();
builder.Services.AddSingleton(chatClient);

// Register as keyed hosted agent (Microsoft.Agents.AI.Hosting)
builder.AddAIAgent("writer", instructions: "You write short stories (<= 300 words).");
// equivalent manual form:
// builder.Services.AddKeyedSingleton<AIAgent>("weather",
//     (sp,_) => chatClient.AsAIAgent(instructions: "...", name: "weather"));

var app = builder.Build();
// Optionally expose over the A2A protocol:
// builder.Services.AddA2AServer(); app.MapA2AServer();
app.Run();
```
`AddAIAgent` registers the agent as a **keyed service** (first-class like a `DbContext`/`HttpClient`). Hosting libraries act as protocol adapters (A2A, OpenAI-compatible, AG-UI), keeping your agent implementation protocol-agnostic.

### 6. Azure-specific concerns
- **Auth:** `DefaultAzureCredential` for dev; **in production prefer `ManagedIdentityCredential`** — the docs explicitly warn `DefaultAzureCredential` "requires careful consideration in production … to avoid latency issues, unintended credential probing, and potential security risks from fallback mechanisms."
- **Azure AI Foundry:** `AIProjectClient.CreateAIAgentAsync(...)` creates a named, versioned, server-side persistent agent (definitions immutable after creation; `Agents.DeleteAgentAsync(name)` to remove). Retrieve with `GetAIAgentAsync` / `AsAIAgent(agentRecord)`. Lower-level control via `AIProjectClient.GetPersistentAgentsClient()` from `Azure.AI.Agents.Persistent`.
- **Hosted MCP:** Foundry agents can attach a hosted `MCPToolDefinition(serverLabel, serverUrl)` with an allow-list and approval workflow.
- **Local MCP:** `await using var mcpClient = await McpClient.CreateAsync(new StdioClientTransport(...));` → `var tools = await mcpClient.ListToolsAsync();` → pass into `AsAIAgent(tools: [...])`. Use `await using` (async disposal) or you leak the server process/socket in long-running services.
- **Deployment:** package as ASP.NET Core / container → **Azure Container Apps**; or use the **Durable extension** for **Azure Functions** (persist sessions, checkpoint orchestration/workflow progress, HITL, scale-to-zero on Flex Consumption). Durable also supports bring-your-own-compute/self-hosted workers (`ConfigureDurableWorkflows`). The repo ships `dotnet/samples/04-hosting/DurableAgents/AzureFunctions` covering single-agent, chaining, concurrency, conditionals, HITL, and reliable streaming.
- **Observability (Azure Monitor / App Insights):**
  ```csharp
  builder.Services.AddOpenTelemetry()
      .UseAzureMonitor()  // reads APPLICATIONINSIGHTS_CONNECTION_STRING
      .WithTracing(t => t
          .AddSource("Microsoft.Extensions.AI*").AddSource("OpenAI*")
          .AddSource("Experimental.OpenAI*").AddSource("Azure.AI.OpenAI*"))
      .WithMetrics(m => m
          .AddMeter("Microsoft.Extensions.AI*").AddMeter("OpenAI*"));
  ```
  Emits GenAI semantic conventions: `gen_ai.agent.name` / `gen_ai.agent.id` per span, `gen_ai.usage.input_tokens` / `output_tokens`, and `execute_tool` spans. App Insights has an **Agents (Preview)** view. Microsoft also ships an `opentelemetry-distro-dotnet` distro with `UseMicrosoftOpenTelemetry(o => { o.Exporters = ExportTarget.AzureMonitor; ... })` that auto-captures Agent Framework activity sources. Full prompt capture requires an explicit sensitive-data (`EnableSensitiveData`) flag.

### 7. Known gaps, breaking changes, and not-yet-supported
- **SK Process Framework is discontinued.** Microsoft (semantic-kernel Discussion #12270): *"We are not moving forward with the development of SK's Process Framework … please instead look at Microsoft Agent Framework Workflows … Orchestration patterns (group chat, magentic, handoff, etc.) are built on top of workflows APIs."* No automatic converter — re-model `KernelProcess` steps/edges as `WorkflowBuilder` executors/edges.
- **Prompt template formats not ported.** SK's **Handlebars / Liquid / YAML** prompt templates and "agent-from-YAML-template" have no direct MAF equivalent (open discussion #1090). **Workaround:** render templates yourself (or keep SK for template rendering) and feed the resulting string as `instructions`. SK **planners** (Handlebars/Stepwise) are deprecated — rely on modern models' native tool-calling instead.
- **API rename churn (breaking):** `AgentThread` → `AgentSession`, `GetNewThread()` → `CreateSessionAsync()`, `RunAsync` param `thread` → `session`, and serialize/deserialize moved onto the agent. If you used **named arguments** (`RunAsync(..., thread: t)`), that is now a **compile error**. Microsoft's `/dotnet/api/...` reference pages lag and still show old names in places — trust the conceptual docs + migration guide.
- **Structured-output arrays bug** — see §5(f).
- **Preview-only (functional, APIs may evolve):** Declarative (YAML) tooling in .NET, Durable/hosting extensions, DevUI, Foundry hosted-agent integration, AG-UI adapters, Skills, and the GitHub Copilot / Claude Code harness.
- **`.GetChatClient()` vs `IChatClient` gotcha:** `OpenAIClient` / `AzureOpenAIClient`.`GetChatClient()` returns the **OpenAI SDK `ChatClient`**, not MEAI `IChatClient`. For the generic `CreateAIAgent` / `AsAIAgent` on `IChatClient`, insert **`.AsIChatClient()`**; the provider packages (`Microsoft.Agents.AI.OpenAI`) also supply overloads that accept the native client directly. A mismatch produces compile error **CS1929** (notably under the .NET 10 SDK).

### 8. Step-by-step migration sequence (ordered, for an AI coding agent)
1. **Inventory.** Grep for: `Microsoft.SemanticKernel`, `Kernel`, `ChatCompletionAgent`, `AzureAIAgent`, `OpenAIAssistantAgent`, `[KernelFunction]`, `AgentGroupChat`, `KernelArguments`, `PromptExecutionSettings`, `IFunctionInvocationFilter`, `KernelProcess`, `ChatHistory`, `AgentThread`. Classify each file by pattern. **Verify:** produce a mapping table; nothing unclassified.
2. **Add packages side-by-side.** Add `Microsoft.Agents.AI`, `Microsoft.Extensions.AI`, and provider packages; **keep SK installed**. **Verify:** solution still builds.
3. **Migrate provider/client setup.** Replace kernel-builder chat-completion registration with a provider client → `.AsAIAgent(...)` (insert `.AsIChatClient()` where needed). **Verify:** one agent returns a response.
4. **Migrate tools.** Remove `[KernelFunction]` + plugin/kernel registration; pass methods via `AIFunctionFactory.Create(...)` in `tools:`; keep `[Description]`. **Verify:** a prompt that requires a tool triggers the tool call.
5. **Migrate state.** Replace `ChatHistory`/`AgentThread` with `AgentSession` via `CreateSessionAsync()`; update `RunAsync`/`RunStreamingAsync` to pass `session`; replace serialization with `SerializeSessionAsync`/`DeserializeSessionAsync`. **Verify:** multi-turn memory + serialize→deserialize round-trip.
6. **Migrate options.** `PromptExecutionSettings`/`KernelArguments` → `ChatOptions`/`ChatClientAgentRunOptions`; `MaxTokens` → `MaxOutputTokens`. **Verify:** token/temperature limits take effect.
7. **Migrate filters → middleware.** Convert `IFunctionInvocationFilter` to agent/function middleware (`.AsBuilder().Use(...)`, `FunctionInvocationContext`). **Verify:** middleware fires around runs and function calls.
8. **Migrate orchestration.** Replace `AgentGroupChat`/SK orchestration with `AgentWorkflowBuilder` (sequential/concurrent/handoff/group chat/magentic) or raw `WorkflowBuilder`. **Verify:** ordering/fan-in matches the SK version on the same inputs.
9. **Migrate DI/hosting.** Replace `AddKernel()` + keyed `Agent` with `AddAIAgent(...)` / `AddKeyedSingleton<AIAgent>`; wire OpenTelemetry. **Verify:** DI resolves the agent; telemetry flows.
10. **Handle gaps.** Re-model Process Framework as Workflows; externalize prompt templates (render → `instructions`). **Verify:** each gap has a working workaround or a documented deferral.
11. **Remove SK** packages/usings from fully migrated files once parity is verified. Keep SK **only** where a gap remains. **Verify:** build clean; no stale `Microsoft.SemanticKernel` usings in migrated files.
12. **Full regression** (build + entire test suite) before merge.

### 9. Testing strategy for behavioral parity
- **Golden-transcript tests:** capture SK inputs/outputs before migration; replay against MAF. For nondeterministic text, assert on **tool-call sequences**, **structured-output schema conformance**, and **semantic similarity** (Azure AI Evaluation SDK `SimilarityEvaluator`, target mean **≥ 3.5 / 5**) rather than exact strings.
- **Tool-invocation assertions:** verify the same tools are called with the same arguments (middleware/telemetry spans make this observable).
- **Session/state tests:** multi-turn memory retention; serialize → deserialize round-trip on the **same agent instance**.
- **Structured-output tests:** validate JSON against schema; **specifically test array/list outputs** (known bug in §5f).
- **Orchestration tests:** deterministic patterns (sequential/concurrent) assert ordering and fan-in aggregation; run SK vs MAF side by side with identical inputs.
- **Observability checks:** confirm `gen_ai.*` spans and token metrics reach App Insights and that per-agent segmentation works.

### 10. F# considerations (relevant if the codebase is F#/.NET 10)
MAF is a standard .NET library callable from F#; NuGet even documents F# Interactive `#r "nuget: Microsoft.Agents.AI, ..."` usage. Caveats:
- **Tools via `AIFunctionFactory.Create`** generate JSON Schema from parameter types. Per practitioner guidance: *"Simple types (string, int, double, bool) and records with simple properties work well. Complex nested types can work but produce more elaborate schemas."* Prefer explicit `name` and `[<Description>]` for reliability.
- **Attributes:** use `[<Description("...")>]` on parameters. `[KernelFunction]` is **not needed at all** in MAF, which actually simplifies F# tool authoring (no attribute plumbing).
- **Async interop:** MAF returns `Task` / `IAsyncEnumerable`. Bridge with `Async.AwaitTask` / the `task { }` computation expression, and handle streaming via `IAsyncEnumerable`/`TaskSeq`.
- **Records / discriminated unions:** F# records map cleanly to structured-output schemas; **DUs have no clean JSON-schema representation** — avoid them at the tool/structured-output boundary or supply a custom `JsonConverter`.
- No F#-specific MAF package exists; consume the C# API directly. **Validate schema generation** for any record/collection used as a tool parameter or structured-output type before relying on it.

## Recommendations
1. **Start new agent code on MAF now; migrate existing SK agents in stages.** Because SK v1.x is supported ≥1 year post-GA (April 2026 baseline), avoid a big-bang. Trigger full migration of a module when it needs MAF-only features (MCP, handoff/magentic orchestration, checkpointing/durability, HITL).
2. **Follow the ordered sequence** in §8 (packages → single agent → tools → state → options → filters/middleware → orchestration → DI/hosting → gaps → remove SK), and **verify build + targeted tests at each step** before proceeding.
3. **Keep SK deliberately** where MAF has gaps: Process Framework workloads, prompt-template-heavy prompts, and mature vector-store/RAG pipelines — bridge via the shared `IChatClient` and `AIFunctionFactory` tool interop rather than rewriting them.
4. **Pin versions** (`Microsoft.Agents.AI` 1.15.0, `Microsoft.Extensions.AI` 10.4.1, and known-good provider prereleases) and treat any `*.Declarative` / hosting / durable / DevUI preview package as changeable.
5. **Always emit the new API names** (`AgentSession`, `CreateSessionAsync`, `session`, `SerializeSessionAsync`/`DeserializeSessionAsync`) even though some Microsoft `/dotnet/api` reference pages still show the old `AgentThread`/`GetNewThread` names.
6. **Benchmarks/thresholds that change the plan:** if similarity parity < **3.5/5** or tool-call sequences diverge, **stop** and fix prompt/tool wiring before removing SK. If a module depends on Process Framework or prompt templates with no viable workaround, **defer** that module and keep it on SK until a workaround exists.

## Caveats
- MAF is young: smaller community and fewer answered edge-case questions than SK — factor in for tricky scenarios.
- Some code in blogs and in Microsoft's **auto-generated `/dotnet/api` reference pages predates the `AgentSession` rename**; cross-check against the conceptual docs and the SK→MAF migration guide, not the API-reference pages.
- Preview features (durable hosting, declarative YAML in .NET, DevUI, Skills, harness) may change before their own GA.
- The exact release number where the `thread` → `session` rename landed is **not pinned to a single changelog line**; evidence points to the 1.0 GA window (April 2026) and it is confirmed present through 1.13–1.15.
- Provider packages ship prerelease variants alongside the stable core; version skew can cause CS1929 with `.GetChatClient()` vs `IChatClient` (fix with `.AsIChatClient()`).
- "Magentic not supported in C#" claims found in some community posts are **outdated** — Magentic-One is stable in .NET as of 1.0.

## Sources
- Agent Framework Overview (C#): https://learn.microsoft.com/en-us/agent-framework/overview/?pivots=programming-language-csharp
- Semantic Kernel → Agent Framework Migration Guide: https://learn.microsoft.com/en-us/agent-framework/migration-guide/from-semantic-kernel/
- MAF 1.0 GA announcement: https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/
- Release Candidate announcement: https://devblogs.microsoft.com/foundry/microsoft-agent-framework-reaches-release-candidate/
- SK & MAF support policy: https://devblogs.microsoft.com/agent-framework/semantic-kernel-and-microsoft-agent-framework/
- .NET Blog — Building Blocks for AI Part 3 (sessions, serialize, `AsAIAgent`): https://devblogs.microsoft.com/dotnet/microsoft-agent-framework-building-blocks-for-ai-part-3/
- `AgentSession` API reference: https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.agentsession
- Session conceptual doc: https://learn.microsoft.com/en-us/agent-framework/agents/conversations/session
- Workflows overview / orchestrations / Magentic: https://learn.microsoft.com/en-us/agent-framework/workflows/ · /orchestrations/ · /orchestrations/magentic
- Orchestration patterns reach 1.0: https://devblogs.microsoft.com/agent-framework/agent-frameworks-orchestration-patterns-reach-1-0/
- Hosting (ASP.NET Core DI, A2A): https://learn.microsoft.com/en-us/agent-framework/get-started/hosting · https://learn.microsoft.com/en-us/agent-framework/hosting/agent-to-agent
- Local MCP tools / Hosted MCP tools: https://learn.microsoft.com/en-us/agent-framework/agents/tools/local-mcp-tools · /hosted-mcp-tools
- Structured output: https://learn.microsoft.com/en-us/agent-framework/agents/structured-outputs · Array bug: https://github.com/microsoft/agent-framework/issues/2874
- Durable extension (Azure Functions): https://learn.microsoft.com/en-us/agent-framework/integrations/durable-extension · Samples: https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/04-hosting/DurableAgents/AzureFunctions
- Observability (App Service tutorial): https://learn.microsoft.com/en-us/azure/app-service/tutorial-ai-agent-monitoring-dotnet · OpenTelemetry distro: https://github.com/microsoft/opentelemetry-distro-dotnet
- Azure AI Foundry (persistent agents): https://learn.microsoft.com/en-us/agent-framework/user-guide/agents/agent-types/azure-ai-foundry-agent · https://learn.microsoft.com/en-us/dotnet/api/overview/azure/ai.projects-readme
- Process Framework discontinued: https://github.com/microsoft/semantic-kernel/discussions/12270
- Prompt template gap discussion: https://github.com/microsoft/agent-framework/discussions/1090
- Multi-turn / thread(session) migration (SK support page): https://learn.microsoft.com/en-us/semantic-kernel/support/migration/agent-framework-rc-migration-guide
- NuGet (Microsoft.Agents.AI 1.15.0): https://www.nuget.org/packages/Microsoft.Agents.AI/
- GitHub repo: https://github.com/microsoft/agent-framework