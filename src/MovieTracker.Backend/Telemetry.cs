using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MovieTracker.Backend;

/// <summary>
/// The instruments the framework does not give us.
///
/// The Meter is named <see cref="SourceName"/>, which is also the <c>sourceName</c> handed to
/// <c>UseOpenTelemetry</c> on the chat client in <c>Program.cs</c>. That is deliberate and
/// load-bearing: <c>OpenTelemetryChatClient</c> builds its ActivitySource *and* its Meter from that
/// one string, so a single <c>AddSource(serviceName)</c> / <c>AddMeter(serviceName)</c> pair covers
/// the model spans, the framework's own gen_ai histograms, and everything defined here. Pass no
/// sourceName and both silently become "Experimental.Microsoft.Extensions.AI" instead - and the
/// registrations in Program.cs would then match nothing.
///
/// Scope: <b>metrics only</b>. Microsoft.Extensions.AI 10.6.0 already emits every span this app
/// needs - <c>orchestrate_tools</c>, one <c>chat</c> per model round trip, and one
/// <c>execute_tool &lt;name&gt;</c> per tool call carrying ActivityStatusCode.Error when the tool
/// throws. Verified by listening to every ActivitySource across a real tool-calling turn. Do not add
/// hand-rolled <c>execute_tool</c> spans on top; they nest inside the framework's own and double the
/// trace.
///
/// What the framework does <i>not</i> emit is any tool-level <b>metric</b>. Its Meter carries only
/// gen_ai.client.token.usage, gen_ai.client.operation.duration and the two streaming-chunk
/// histograms. Spans are sampled, so "which tools are slow" and "which tools are failing" cannot be
/// answered from them reliably - a spot check found 5 surviving spans against 107 real model calls.
/// That is the gap the tool instruments below fill.
///
/// Cardinality is deliberately tiny. Azure Monitor caps a custom metric at 5,000 time series per 24h
/// and 10 dimension keys, so dimensions here are bounded low-arity sets only - tool name (23),
/// outcome (2), model (1-2). Never add chatId, movie id, or user text as a dimension; those belong
/// on spans and logs, which are not aggregated into time series.
/// </summary>
internal static class Telemetry
{
    /// <summary>Shared by the ActivitySource, the Meter, and UseOpenTelemetry's sourceName.</summary>
    public const string SourceName = "movie-tracker-backend";

    public const string ServiceVersion = "1.0.0";

    private static readonly Meter Meter = new(SourceName, ServiceVersion);

    /// <summary>
    /// Per-tool wall clock. Named to match the shape the GenAI conventions use for the client
    /// histograms rather than inventing a scheme; the .NET function-invocation metric name is
    /// unspecified, so this is ours and may collide with a future framework metric of the same name.
    /// </summary>
    public static readonly Histogram<double> ToolDuration = Meter.CreateHistogram<double>(
        "gen_ai.client.tool.duration",
        unit: "s",
        description: "Wall-clock duration of a single tool invocation.");

    /// <summary>Split by outcome so a tool that starts throwing shows up without needing traces.</summary>
    public static readonly Counter<long> ToolInvocations = Meter.CreateCounter<long>(
        "gen_ai.client.tool.invocations",
        unit: "{invocation}",
        description: "Tool invocations, dimensioned by tool name and outcome.");

    /// <summary>
    /// Tool calls made in a single turn. The iteration cap is enforced inside
    /// FunctionInvokingChatClient, which offers no callback when it trips, so this is how you tell
    /// that it is tripping: a distribution pressed up against MaxToolIterations means real queries
    /// are outgrowing the budget and getting cut off mid-answer.
    /// </summary>
    public static readonly Histogram<int> ToolCallsPerTurn = Meter.CreateHistogram<int>(
        "chat.turn.tool_calls",
        unit: "{call}",
        description: "Tool calls issued while answering one user turn.");

    /// <summary>
    /// How much of the stored conversation compaction actually sent. Recorded as a ratio so it is
    /// comparable across conversation lengths; a value trending toward 1.0 means compaction has
    /// stopped saving anything.
    /// </summary>
    public static readonly Histogram<double> ContextRetainedRatio = Meter.CreateHistogram<double>(
        "chat.context.retained_ratio",
        unit: "1",
        description: "Messages sent to the model divided by messages stored for the session.");

    /// <summary>
    /// There is no standard cost metric in the GenAI conventions, so this is derived from token
    /// counts and a price table. The table is configuration-only and has no defaults on purpose:
    /// per-1M rates for a given deployment change without notice and a wrong hardcoded number
    /// produces a confident, wrong cost dashboard. Unconfigured, nothing is emitted.
    /// </summary>
    public static readonly Counter<double> CostUsd = Meter.CreateCounter<double>(
        "gen_ai.usage.cost_usd",
        unit: "USD",
        description: "Estimated spend, derived from token counts and configured per-1M rates.");

    /// <summary>
    /// Records a completed tool invocation. <paramref name="elapsed"/> is passed in rather than
    /// measured here so the caller can time the delegate itself.
    /// </summary>
    public static void RecordTool(string toolName, TimeSpan elapsed, bool succeeded)
    {
        var tool = new KeyValuePair<string, object?>("gen_ai.tool.name", toolName);
        var outcome = new KeyValuePair<string, object?>("outcome", succeeded ? "ok" : "error");

        ToolDuration.Record(elapsed.TotalSeconds, tool, outcome);
        ToolInvocations.Add(1, tool, outcome);
    }

    /// <summary>
    /// Converts a turn's usage into a cost measurement. Cached input is billed at a steep discount,
    /// so it is subtracted from the full input count and charged at its own rate - treating it as
    /// ordinary input is the difference between a useful number and a meaningless one on this app,
    /// where roughly 80% of input tokens are cache reads.
    /// </summary>
    public static void RecordCost(string model, UsageDetails usage, ModelPricing pricing)
    {
        var cachedInput = usage.CachedInputTokenCount ?? 0;
        var freshInput = Math.Max(0, (usage.InputTokenCount ?? 0) - cachedInput);

        var cost = freshInput / 1_000_000d * pricing.InputPerMillion
                 + cachedInput / 1_000_000d * (pricing.CachedInputPerMillion ?? pricing.InputPerMillion)
                 + (usage.OutputTokenCount ?? 0) / 1_000_000d * pricing.OutputPerMillion;

        if (cost > 0)
        {
            CostUsd.Add(cost, new KeyValuePair<string, object?>("gen_ai.request.model", model));
        }
    }
}

/// <summary>
/// Adds duration and success/failure metrics to a tool, leaving everything the model sees untouched.
///
/// Wrapping the <see cref="AIFunction"/> is what makes this reliable. The obvious alternatives do not
/// work on Microsoft.Agents.AI 1.15.0: <c>AIAgentBuilder.Use(...)</c> function-invocation middleware
/// compiles and registers but is never invoked, and <c>FunctionInvokingChatClient.FunctionInvoker</c>
/// is likewise never consulted - both verified against a real tool-calling turn. A
/// <see cref="DelegatingAIFunction"/> sits directly in the invocation path, so it cannot be bypassed.
///
/// <see cref="DelegatingAIFunction"/> forwards Name, Description and JsonSchema to the inner
/// function, so the tool schema the model is shown is byte-identical to the unwrapped one.
/// Deliberately emits no Activity: the framework already opens an
/// <c>execute_tool &lt;name&gt;</c> span around this call.
/// </summary>
internal sealed class InstrumentedAIFunction(
    AIFunction inner,
    ILogger? logger = null,
    bool logArguments = false) : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            Telemetry.RecordTool(Name, stopwatch.Elapsed, succeeded: true);
            LogCall(arguments, stopwatch.Elapsed, error: null);
            return result;
        }
        catch (Exception ex)
        {
            // Rethrown untouched. The framework catches it, records it on its own execute_tool span,
            // and hands the model a correctable error - which several tools here rely on.
            Telemetry.RecordTool(Name, stopwatch.Elapsed, succeeded: false);
            LogCall(arguments, stopwatch.Elapsed, ex);
            throw;
        }
    }

    /// <summary>
    /// Which tool the model chose and what it passed.
    /// <para>
    /// The metrics above give counts and durations but not arguments, and the framework's
    /// <c>execute_tool</c> spans - which do carry them - are sampled away: host.json enables adaptive
    /// sampling and a spot check found 5 surviving spans against 107 real calls. So when an answer is
    /// wrong there is otherwise no way to see *why*, and tool-selection bugs live almost entirely in
    /// the arguments: a director filtered as cast, a job filter applied to a question that wanted
    /// acting credits, a genre the model decided to apply itself instead of passing down.
    /// </para>
    /// <para>
    /// Gated on <c>Telemetry:Enable-Sensitive-Data</c>, because arguments contain the names and titles
    /// the user asked about. Names only when that is off.
    /// </para>
    /// </summary>
    private void LogCall(AIFunctionArguments arguments, TimeSpan elapsed, Exception? error)
    {
        if (logger is null || !logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var rendered = logArguments ? Render(arguments) : "(arguments not logged)";

        if (error is null)
        {
            logger.LogInformation("tool {ToolName}({ToolArguments}) ok in {ElapsedMs}ms",
                Name, rendered, (int)elapsed.TotalMilliseconds);
        }
        else
        {
            logger.LogWarning("tool {ToolName}({ToolArguments}) FAILED in {ElapsedMs}ms: {Error}",
                Name, rendered, (int)elapsed.TotalMilliseconds, error.Message);
        }
    }

    private const int MaxLoggedArgumentChars = 120;

    private static string Render(AIFunctionArguments arguments)
    {
        if (arguments is null || arguments.Count == 0) return string.Empty;

        return string.Join(", ", arguments
            .Where(argument => argument.Value is not null)
            .Select(argument =>
            {
                var value = argument.Value!.ToString() ?? string.Empty;
                if (value.Length > MaxLoggedArgumentChars)
                {
                    value = value[..MaxLoggedArgumentChars] + "...";
                }
                return $"{argument.Key}={value}";
            }));
    }
}

/// <summary>
/// Per-1M-token rates for the deployed model, read from configuration. Null when unconfigured, in
/// which case no cost metric is emitted at all.
/// </summary>
/// <param name="InputPerMillion">USD per 1M uncached input tokens.</param>
/// <param name="OutputPerMillion">USD per 1M output tokens.</param>
/// <param name="CachedInputPerMillion">
/// USD per 1M cached input tokens. Falls back to the uncached rate when absent, which understates
/// the discount rather than overstating it.
/// </param>
internal sealed record ModelPricing(
    double InputPerMillion,
    double OutputPerMillion,
    double? CachedInputPerMillion);
