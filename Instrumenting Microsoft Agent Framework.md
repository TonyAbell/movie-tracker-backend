# Instrumenting Microsoft Agent Framework (.NET) with OpenTelemetry → Azure Monitor / Application Insights

## TL;DR
- Microsoft Agent Framework (MAF), which reached GA 1.0 on April 2, 2026 as the convergence of AutoGen and Semantic Kernel into a single supported platform, emits OpenTelemetry GenAI traces, metrics, and logs out of the box via `Microsoft.Extensions.AI`'s `OpenTelemetryChatClient` and the agent-level `OpenTelemetryAgent`; you opt in with `.UseOpenTelemetry(...)` on the chat client and `.WithOpenTelemetry(...)`/`.AsBuilder().UseOpenTelemetry(...)` on the agent, then register the ActivitySource/Meter names `Experimental.Microsoft.Extensions.AI` and `Experimental.Microsoft.Agents.AI` on your TracerProvider/MeterProvider.
- Token usage is captured automatically as the histogram `gen_ai.client.token.usage` (split by `gen_ai.token.type`) and as span attributes `gen_ai.usage.input_tokens`/`gen_ai.usage.output_tokens`; export to App Insights with `UseAzureMonitor()` (the distro) for greenfield Azure apps or `AddAzureMonitor*Exporter` (raw exporters) when you need manual control.
- Two production gotchas dominate: the GenAI semantic conventions are still "Development"/experimental (attribute renames like `gen_ai.system`→`gen_ai.provider.name` land via `OTEL_SEMCONV_STABILITY_OPT_IN`), and custom OTel metrics only reach the alertable Azure Monitor metric store under the hardcoded namespace `azure.applicationinsights` — traces are sampled, metrics are not.

## Key Findings

### What the framework gives you for free
- MAF is built on the `Microsoft.Extensions.AI` (MEAI) middleware pipeline for `IChatClient`. The `OpenTelemetryChatClient` is a `DelegatingChatClient` that implements the OpenTelemetry GenAI Semantic Conventions (documented as v1.37, "still experimental and subject to change").
- Auto-captured signals: chat-completion spans (`chat <model>`), agent-invocation spans (`invoke_agent <agent_name>`), tool spans (`execute_tool <function_name>`), plus the histograms `gen_ai.client.operation.duration` (seconds) and `gen_ai.client.token.usage` (tokens).
- Default source/meter names: `Experimental.Microsoft.Extensions.AI` (chat-client level) and `Experimental.Microsoft.Agents.AI` (agent level). The same string is used for both the ActivitySource and the Meter. You MUST register these with `AddSource`/`AddMeter` or spans/metrics are silently dropped.
- Sensitive-data capture (prompts, completions, tool args/results) is off by default; enable with `EnableSensitiveData = true` or the env var `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`.

### Semantic conventions state (2026)
- As of mid-2026 the GenAI semantic conventions are still in "Development" status. Per John Hodge's "The state of the OpenTelemetry GenAI semantic conventions (July 2026)": main-repo semconv v1.42.0 (June 12, 2026) deprecated and moved all `gen_ai.*` content, v1.43.0 (July 3) ships none, and "As of July 17, 2026, no GenAI-specific span, event, metric, or attribute in the dedicated repository is marked Stable; the GenAI conventions remain Development." The content moved to `open-telemetry/semantic-conventions-genai` — per the semantic-conventions release notes (#3696): "All gen_ai.* attributes, metrics, events, and spans previously defined under model/gen-ai/, model/openai/, and model/mcp/ … are deprecated in this repository and have moved to the OpenTelemetry GenAI semantic conventions repository."
- Canonical token metrics/attributes: `gen_ai.client.token.usage` (histogram, split by `gen_ai.token.type` = input/output), `gen_ai.client.operation.duration`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`, `gen_ai.operation.name`, `gen_ai.request.model`, `gen_ai.response.model`, `gen_ai.response.finish_reasons`, agent attributes `gen_ai.agent.id`/`gen_ai.agent.name`, tool attribute `gen_ai.tool.name`.
- Rename: per the OpenTelemetry semantic-conventions (as surfaced in spring-ai issue #6668), "The attribute gen_ai.system was renamed to gen_ai.provider.name in semantic-conventions v1.37.0, the old name is deprecated" (v1.37.0 dates to August 2025). Managed via `OTEL_SEMCONV_STABILITY_OPT_IN=gen_ai_latest_experimental`. Reasoning tokens: `gen_ai.usage.reasoning.output_tokens` (should be included within output_tokens); cached tokens surface in provider `AdditionalProperties`.

## Details

### 1. Package set (mid-2026, .NET)
- `Microsoft.Agents.AI` — GA, latest stable 1.6.1 (5/14/2026); targets net8.0/netstandard2.0/net472.
- `Microsoft.Agents.AI.OpenAI` — same 1.x GA train.
- `Microsoft.Extensions.AI` — latest 10.8.3 (Extensions 10.x train); MAF 1.6.1 depends on MEAI ≥ 10.5.1.
- Azure Monitor: `Azure.Monitor.OpenTelemetry.AspNetCore` (distro, latest 1.5.0) or `Azure.Monitor.OpenTelemetry.Exporter` (raw exporter).
- Core OTel: `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.AspNetCore`.

### 2. Enabling instrumentation on the client and agent
```csharp
const string SourceName = "MyApp.Agents"; // choose one; register the SAME name on the providers

// Chat-client level
var chatClient = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deploymentName)
    .AsIChatClient()
    .AsBuilder()
    .UseOpenTelemetry(sourceName: SourceName, configure: c => c.EnableSensitiveData = false) // prod-safe
    .Build();

// Agent level
var agent = new ChatClientAgent(
        chatClient,
        name: "WeatherAdvisor",
        instructions: "You are a helpful weather assistant.",
        tools: [AIFunctionFactory.Create(GetWeatherAsync)])
    .WithOpenTelemetry(sourceName: SourceName, configure: c => c.EnableSensitiveData = false);
```
Note on duplication: if you enable OTel on both the chat client and the agent with sensitive data, prompts/responses appear in both spans. Choose one layer if you want to avoid duplication.

If you omit `sourceName`, the framework defaults to `Experimental.Microsoft.Extensions.AI` (chat) and `Experimental.Microsoft.Agents.AI` (agent) — register those exact strings instead.

**How the layers nest:** the top-level `invoke_agent <name>` span (agent invocation, from `OpenTelemetryAgent`) contains one or more `chat <model>` child spans (each `GetResponseAsync`/streaming call, from `OpenTelemetryChatClient`), which in turn parent `execute_tool <function>` child spans for each function/tool call resolved by `FunctionInvokingChatClient`. Workflows wrap this with their own `workflow.run` → `executor.process` → `message.send` spans. Internally, `OpenTelemetryAgent` resolves a single source name and forwards through an inner `OpenTelemetryChatClient`, so agent and chat spans share one ActivitySource.

### 3. Wiring the OTel pipeline (ASP.NET Core / Container Apps, distro path)
```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenTelemetry()
    .UseAzureMonitor(o =>
    {
        // reads APPLICATIONINSIGHTS_CONNECTION_STRING automatically; or set o.ConnectionString
        o.SamplingRatio = 1.0f; // fixed-percentage; lower in prod
    })
    .ConfigureResource(r => r.AddService(
        serviceName: "my-agent-service",
        serviceVersion: "1.4.2",
        serviceInstanceId: Environment.MachineName))
    .WithTracing(t => t
        .AddSource("MyApp.Agents")
        .AddSource("Experimental.Microsoft.Extensions.AI")
        .AddSource("Experimental.Microsoft.Agents.AI")
        .AddSource("Azure.AI.OpenAI*")
        .AddSource("OpenAI*"))
    .WithMetrics(m => m
        .AddMeter("MyApp.Agents")
        .AddMeter("Experimental.Microsoft.Extensions.AI")
        .AddMeter("Experimental.Microsoft.Agents.AI"));
```
`UseAzureMonitor()` sets up traces, logs, and metrics to Application Insights in one call and includes AspNetCore/HTTP instrumentation and Live Metrics. Do not also add `OpenTelemetry.Instrumentation.AspNetCore` manually or you may get missing/duplicate request telemetry.

**Raw-exporter path (manual control / multi-backend):**
```csharp
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resourceBuilder)
    .AddSource("MyApp.Agents")
    .AddSource("Experimental.Microsoft.Extensions.AI")
    .AddSource("Experimental.Microsoft.Agents.AI")
    .AddAzureMonitorTraceExporter(o => o.ConnectionString = connString)
    .Build();

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resourceBuilder)
    .AddMeter("MyApp.Agents")
    .AddMeter("Experimental.Microsoft.Extensions.AI")
    .AddMeter("Experimental.Microsoft.Agents.AI")
    .AddAzureMonitorMetricExporter(o => o.ConnectionString = connString)
    .Build();
```

**Distro vs raw exporter:**
- Use the **distro** (`UseAzureMonitor()`) for greenfield Azure apps — one call, Live Metrics, resource detectors, statsbeat. This is the recommended path for ASP.NET Core.
- Use the **raw exporters** (`AddAzureMonitorTraceExporter` / `AddAzureMonitorMetricExporter` / `AddAzureMonitorLogExporter`) when you want full manual control of the SDK, non-ASP.NET hosts, or multi-backend fan-out (e.g., also OTLP to Grafana). You can combine either with manual `AddSource`/`AddMeter`.

**Credentials:** the distro reads the connection string from `APPLICATIONINSIGHTS_CONNECTION_STRING`. For Entra ID / managed identity on the ingestion path, set `o.Credential = new ManagedIdentityCredential()` (prefer a specific credential over `DefaultAzureCredential` in production to avoid probing latency).

### 4. Token capture specifics
- Non-streaming: `ChatResponse.Usage` is a `UsageDetails` with `InputTokenCount`, `OutputTokenCount`, `TotalTokenCount`, plus `AdditionalProperties` (provider extras such as reasoning/cached token counts — e.g. Azure OpenAI surfaces a `ReasoningTokenCount`). The `OpenTelemetryChatClient` maps these to `gen_ai.usage.*` attributes and the `gen_ai.client.token.usage` histogram automatically — you do not hand-roll this.
- Streaming: usage arrives on the final `ChatResponseUpdate` as a `UsageContent` in its `Contents`. Per the GenAI spec, `token.usage` is emitted only when the upstream provider returns usage (e.g., OpenAI `stream_options.include_usage=true`); when absent, "no observation" is recorded rather than 0. Forward the include-usage option if you need streaming token accounting.
- Cost tracking: there is no standard cost metric. Emit a custom `gen_ai.usage.cost_usd`-style metric derived from token counts × a pricing table you own, keyed by model. Bucket boundaries for token histograms follow powers-of-four up to ~67M tokens per spec.

```csharp
var meter = new Meter("MyApp.Agents");
var costCounter = meter.CreateCounter<double>("gen_ai.usage.cost_usd", unit: "USD");

static readonly Dictionary<string, (double In, double Out)> PricePer1M = new()
{
    ["gpt-4o"]      = (2.50, 10.00),
    ["gpt-4o-mini"] = (0.15, 0.60),
};

void RecordCost(string model, UsageDetails u)
{
    if (u is null || !PricePer1M.TryGetValue(model, out var p)) return;
    var cost = ((u.InputTokenCount ?? 0) / 1_000_000d) * p.In
             + ((u.OutputTokenCount ?? 0) / 1_000_000d) * p.Out;
    costCounter.Add(cost, new KeyValuePair<string, object?>("gen_ai.request.model", model));
}
```
Manual reading of usage (e.g., to feed the cost counter) for a non-streaming call:
```csharp
var response = await agent.RunAsync("What's the weather in Lisbon?");
RecordCost(deploymentName, response.Usage);
```

### 5. Other useful agent metrics
- Emitted automatically: operation duration (`gen_ai.client.operation.duration`), token usage, finish reasons, per-tool `execute_tool` spans, per-agent `invoke_agent` spans.
- Require manual instrumentation: time-to-first-token (streaming), retry counts, 429/rate-limit events, context-window utilization, conversation-turn counts, per-workflow-step durations, cost. Use `System.Diagnostics.Metrics.Meter` counters/histograms and custom `Activity` spans/tags.
- Workflows (`Microsoft.Agents.AI.Workflows`) emit extra spans when enabled via `WithOpenTelemetry` on the WorkflowBuilder: `workflow.build`, `workflow.run`, `executor.process {executor_id}`, `edge_group.process {edge_group_type}`, `message.send`, with attributes like `workflow.id`, `executor.id`, `message.type`. `WorkflowTelemetryOptions` flags (`EnableSensitiveData`, `DisableWorkflowBuild`, `DisableWorkflowRun`, `DisableExecutorProcess`, `DisableEdgeGroupProcess`, `DisableMessageSend`) toggle these. Note the Python vs .NET divergence: Python emits `workflow.session`/`workflow_invoke`, .NET emits `workflow.run`.

Example manual span + 429 counter:
```csharp
static readonly ActivitySource Source = new("MyApp.Agents");
static readonly Meter Meter = new("MyApp.Agents");
static readonly Counter<long> RateLimitHits = Meter.CreateCounter<long>("agent.rate_limit.count");

using var activity = Source.StartActivity("retrieve-context", ActivityKind.Internal);
activity?.SetTag("gen_ai.agent.name", "WeatherAdvisor");
try { /* work */ }
catch (RequestFailedException ex) when (ex.Status == 429)
{
    RateLimitHits.Add(1, new KeyValuePair<string, object?>("gen_ai.request.model", model));
    activity?.SetStatus(ActivityStatusCode.Error, "429");
    throw;
}
```

### 6. Sampling
- The Azure Monitor distro applies its own `ApplicationInsightsSampler` by default. Two modes: fixed-percentage (`SamplingRatio` 0.0–1.0) and rate-limited (`TracesPerSecond`, default 5/sec in the equivalent SDK 3.x). Live Metrics requires the Azure Monitor sampler.
- Metrics are never sampled; only traces (spans) are. Logs tied to unsampled traces are dropped by default (trace-based logs sampler), which you can disable (`EnableTraceBasedLogsSampler=false`). Because sampling reduces trace accuracy, alert on OTel metrics (unaffected by sampling), not on trace counts. Recommended starting point for high volume: 5% (0.05).

### 7. Custom metrics in Azure Monitor — the namespace and cardinality caveats
Custom OTel metrics land in the `customMetrics` Log Analytics table, but to appear in the Metrics explorer / be alertable they are published under the **hardcoded namespace `azure.applicationinsights`** — the OTel Meter name is NOT used as the metric namespace. A metric visible in `customMetrics` via KQL may still be missing from metric definitions if you scope to the wrong resource: use the Application Insights resource (not the linked Log Analytics workspace) with namespace `azure.applicationinsights`.

Watch cardinality. Per Microsoft Learn ("Metrics in Application Insights"): "Each metric can only have up to 5,000 time series within 24 hours. Once this limit is reached, Azure Monitor replaces all dimension values of that metric point with the constant Maximum values reached." There are also subscription-wide ceilings — per "Custom metrics in Azure Monitor": "Azure Monitor currently sets a limit of 10 dimension keys per metric, and a limit of 50,000 total active time series per region in a subscription (within a 12 hour period)." Keep metric dimensions to model, agent name, and operation; put high-cardinality ids (tenant/user/conversation) on spans/logs instead.

### 8. KQL queries
Spans land in `dependencies`, incoming requests in `requests`, logs in `traces`, metrics in `customMetrics`, and events in `customEvents`. GenAI attributes are in `customDimensions`.

```kusto
// Total tokens by model over time
dependencies
| where timestamp > ago(24h)
| extend model = tostring(customDimensions["gen_ai.request.model"]),
         inTok = toint(customDimensions["gen_ai.usage.input_tokens"]),
         outTok = toint(customDimensions["gen_ai.usage.output_tokens"])
| where isnotempty(model)
| summarize input=sum(inTok), output=sum(outTok) by model, bin(timestamp, 1h)
| render timechart

// Cost per agent (derived in-query; or read the custom cost metric)
dependencies
| extend agent = tostring(customDimensions["gen_ai.agent.name"]),
         model = tostring(customDimensions["gen_ai.request.model"]),
         inTok = todouble(customDimensions["gen_ai.usage.input_tokens"]),
         outTok = todouble(customDimensions["gen_ai.usage.output_tokens"])
| where isnotempty(agent)
| extend costUsd = iff(model == "gpt-4o", inTok/1e6*2.5 + outTok/1e6*10.0, 0.0)
| summarize cost=sum(costUsd) by agent
| order by cost desc

// p95 latency of model calls
dependencies
| where customDimensions has "gen_ai.operation.name"
| summarize p95=percentile(duration, 95) by tostring(customDimensions["gen_ai.request.model"])

// Failure rate by tool
dependencies
| extend tool = tostring(customDimensions["gen_ai.tool.name"]),
         op = tostring(customDimensions["gen_ai.operation.name"])
| where op == "execute_tool"
| summarize total=count(), failures=countif(success == false) by tool
| extend failureRate = 1.0 * failures / total

// Token usage per conversation (if you tag a conversation id)
dependencies
| extend conv = tostring(customDimensions["conversation.id"]),
         outTok = toint(customDimensions["gen_ai.usage.output_tokens"])
| summarize tokens=sum(outTok) by conv
| top 20 by tokens desc

// Sanity check that agent spans are flowing
dependencies
| where timestamp > ago(1h)
| where customDimensions has "gen_ai.agent.name"
| project timestamp, name, customDimensions
| take 20
```

**Metric alerts:** create the rule scoped to the Application Insights resource, namespace `azure.applicationinsights`, then pick your custom metric (e.g. `gen_ai.usage.cost_usd` or `gen_ai.client.token.usage`). It can take 10–15 minutes for a newly emitted metric to appear in metric definitions. Alert on metrics (not sampled) rather than trace counts.

### 9. App Insights / Foundry GenAI UI
- Azure Monitor has an **Agents (preview)** view and App Service an **AI (preview) → Agents** tab that roll up per-agent calls, tokens, and error rate — they group on `gen_ai.agent.name` and read `gen_ai.usage.*`. Drill-downs include "View Traces with Agent Runs" and "View Traces with Gen AI Errors," and you can sort by "Most tokens used." Foundry's observability (GA March 2026, some dashboard views preview) reads the same OTel data from the connected Application Insights resource, so the same traces render in Foundry, App Insights, Grafana, or a local dashboard.
- On App Service, disable the codeless agent (`ApplicationInsightsAgent_EXTENSION_VERSION=disabled`) when instrumenting in code so it doesn't compete for the same activity sources.

### 10. F# notes
- `ActivitySource`/`Activity` interop: `Activity` methods return the object for chaining; in F# use `activity.SetTag("k", box v)` and remember `StartActivity` can return `null` — model as `match Option.ofObj (source.StartActivity "op") with`. Use `use` bindings for the `IDisposable` activity scope (equivalent of C# `using`).
- Tuples/options: MEAI `UsageDetails` token counts are nullable `int?`; in F# these surface as `Nullable<int>` — convert with `Option.ofNullable`. Configuration callbacks (`Action<OpenTelemetryChatClient>`) can be passed as F# lambdas `(fun c -> c.EnableSensitiveData <- false)`.
- The `AddOpenTelemetry().WithTracing(...).WithMetrics(...)` builder chain works identically from F#; there is no computation-expression wrapper, so use method chaining or intermediate `let` bindings.

## Recommendations
1. **Start**: add `.UseOpenTelemetry()`/`.WithOpenTelemetry()` on one layer (chat client OR agent), wire `UseAzureMonitor()` with the three source/meter registrations, and verify spans appear in `dependencies` with `gen_ai.*` in `customDimensions`. Keep `EnableSensitiveData=false`. Benchmark to change: if the Agents tab is empty, confirm both `.UseAzureMonitor()` and the `AddSource`/`AddMeter` names are present.
2. **Token/cost**: rely on the built-in `gen_ai.client.token.usage` histogram for tokens; add a custom cost counter keyed only by model. Alert on the metric (not traces).
3. **Sampling**: set `SamplingRatio=1.0` while validating; move to 0.05–0.1 (or rate-limited) in production once volume grows. Re-confirm token/cost dashboards read from metrics so sampling doesn't skew them. Benchmark to change: if failures/performance panes look inaccurate, raise the ratio; if ingestion cost spikes, lower it.
4. **Local dev**: export OTLP to the Aspire Dashboard (`docker run --rm -it -d -p 18888:18888 -p 4317:18889 --name aspire-dashboard mcr.microsoft.com/dotnet/aspire-dashboard:latest`) via `AddOtlpExporter` to `http://localhost:4317` instead of Azure Monitor; switch by environment/config.
5. **Change triggers**: if you upgrade MEAI/MAF and dashboards break on `gen_ai.provider.name` vs `gen_ai.system`, set `OTEL_SEMCONV_STABILITY_OPT_IN=gen_ai_latest_experimental` and inspect a real exported span before rolling out.

## Caveats
- GenAI semconv is experimental ("Development") with no Stable elements as of mid-July 2026; attribute names and the MEAI/MAF telemetry output may change. Pin versions and inspect exported spans.
- MEAI `UseOpenTelemetry` implements span attributes but (per open MAF issue #3637) does not emit the spec's `gen_ai.client.inference.operation.details` ActivityEvents — if you depend on structured message events, add them manually.
- The .NET function-invocation *metric* name is unconfirmed; `agent_framework.function.invocation.duration` is documented for Python. Verify against `FunctionInvokingChatClient` before alerting on a .NET metric; tool timing is reliably available via `execute_tool` spans.
- PII: only enable sensitive-data capture in dev/test; in prod redact at the SDK (`EnableSensitiveData=false`) and optionally at an OTel Collector (redaction/transform processors). Sensitive data includes prompts, responses, function arguments, and results.
- Azure Functions: use `Microsoft.Azure.Functions.Worker.OpenTelemetry` + `AddOpenTelemetry().UseFunctionsWorkerDefaults().UseAzureMonitor()`; avoid running the distro in the worker if the host already exports, to prevent duplicate telemetry. Enable log scopes explicitly (`builder.Logging.AddOpenTelemetry(b => b.IncludeScopes = true)`).