using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Configuration;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Azure.Cosmos;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MovieTracker.Backend;
using MovieTracker.Backend.Prompts;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using MovieTracker.Backend.Agents;
using Azure.AI.OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using TMDbLib.Client;

// One string for the OTel service name, the ActivitySource, the Meter, and the sourceName handed to
// UseOpenTelemetry below. OpenTelemetryChatClient derives both its ActivitySource and its Meter from
// that sourceName, so this single value is what makes one AddSource/AddMeter pair cover the model
// spans, the framework's gen_ai histograms, and this app's own instruments.
var serviceName = Telemetry.SourceName;
var serviceVersion = Telemetry.ServiceVersion;

// FunctionInvokingChatClient allows 40 tool-calling iterations per request by default. At a few
// seconds per round trip that exhausts the 120s HttpClient budget, and then the platform's ~230s
// trigger limit, long before it gives up - the caller sees a timeout rather than an answer. A real
// query needs far fewer: resolve a person, discover, fetch details, sometimes ratings.
const int MaxToolIterations = 12;

var host = new HostBuilder()
    .ConfigureAppConfiguration((context, config) =>
    {
        var vaultUri = Environment.GetEnvironmentVariable("VaultUri");
        if (String.IsNullOrEmpty(vaultUri))
        {
            throw new ConfigurationErrorsException("Missing VaultUri");
        }
        var keyVaultEndpoint = new Uri(vaultUri);
        config.AddAzureKeyVault(keyVaultEndpoint, new DefaultAzureCredential());
        if (context.HostingEnvironment.IsDevelopment())
        {
            config.AddJsonFile("local.settings.json");
            config.AddUserSecrets<Program>();
        }
        config.Build();

    })
    .ConfigureFunctionsWebApplication()
    .ConfigureLogging((context, logging) =>
    {
        logging.Services.Configure<LoggerFilterOptions>(options =>
        {
            LoggerFilterRule defaultRule = options.Rules.FirstOrDefault(rule => rule.ProviderName
                == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
            if (defaultRule is not null)
            {
                options.Rules.Remove(defaultRule);
            }
        });

        // Worker ILogger output -> Azure Monitor.
        //
        // Registered on the logging builder, NOT via AddOpenTelemetry().WithLogging(...) in
        // ConfigureServices. That distinction is the whole fix and it is not cosmetic: with the
        // exporter attached through WithLogging, application categories never reached App Insights at
        // all - polled for eight minutes across several runs and got nothing from MovieTracker.* while
        // Microsoft.AspNetCore.* from the same process arrived fine. Registered here, a processor on
        // this pipeline sees MovieTracker.Backend.Functions.Function at Information/Warning/Error and
        // MovieTracker.Backend.ChatSessionRepository at Critical, which is the path that carries
        // production exceptions.
        //
        // Note the worker's ILogger output does not reach the `func start` console either way, so the
        // console is not a way to check this - query App Insights.
        //
        // IncludeFormattedMessage is required or messages render as the raw template with
        // {Placeholders} intact. Scopes carry the Functions invocation id, which is what ties a log
        // line back to its request.
        logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(serviceName: serviceName, serviceVersion: serviceVersion));
            options.AddAzureMonitorLogExporter(exporter =>
            {
                exporter.ConnectionString = context.Configuration["APPLICATIONINSIGHTS-CONNECTION-STRING"];

                // The second half of the fix, and the non-obvious one. The exporter defaults to a
                // trace-based logs sampler: a log record correlated to a trace that got sampled OUT is
                // dropped on the floor. host.json enables adaptive sampling with only Request excluded,
                // so most spans are discarded - and with them went every log line written inside a
                // function invocation, which is all of the interesting ones.
                //
                // Proven rather than guessed: a warning on the MovieTracker category emitted at startup
                // with Activity.Current == null arrives in App Insights; the identical category logged
                // inside Chat-Ask does not. Same logger, same level, same exporter - only the ambient
                // Activity differs. Turning this off decouples log retention from trace sampling, which
                // is what you want for exception logs: an error is worth keeping even when its trace
                // was not.
                exporter.EnableTraceBasedLogsSampler = false;
            });
        });
    })
    .ConfigureServices((context, services) =>
    {
        services.AddDistributedMemoryCache();
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Prompts and completions in traces. Defaults to true, which is what this app has always done
        // and what makes the traces worth reading, but it is now a switch rather than a recompile: it
        // puts the user's questions and the model's answers into App Insights, so set it false if the
        // deployment ever handles data that should not land in telemetry.
        var enableSensitiveTelemetry =
            context.Configuration.GetValue<bool?>("Telemetry:Enable-Sensitive-Data") ?? true;

        // This named client is used only by the Azure OpenAI chat client behind the agent.
        // The standard resilience handler defaults to a 30s total / 10s per-attempt timeout,
        // which a reasoning model comfortably exceeds, so the budget is widened here.
        // Constraint: SamplingDuration must be >= 2x AttemptTimeout or options validation throws.
        services.AddHttpClient("HttpClient")
                .AddStandardResilienceHandler(options =>
                {
                    // A single /ask makes several LLM round trips and the platform kills HTTP
                    // triggers at ~230s, so per-call budgets stay well inside that ceiling.
                    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
                    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
                    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
                    // The deployment is DataZoneStandard at 200K TPM and Chat-Ask now issues the
                    // funny-fact completion and the main agent call at the same time, so bursts are
                    // sharper than they were when the two ran back to back. The default handler already
                    // treats 429 as transient and honours Retry-After; only the attempt budget needed
                    // widening. TotalRequestTimeout still caps how long the retries can run.
                    options.Retry.MaxRetryAttempts = 3;
                });

        services.AddSingleton(TracerProvider.Default.GetTracer(serviceName, serviceVersion));
        services.AddSingleton<CosmosClient>(serviceProvider =>
        {
            IHttpClientFactory httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
            string connectionString = context.Configuration.GetConnectionString("Cosmos");
            if (String.IsNullOrEmpty(connectionString))
            {
                throw new ConfigurationErrorsException("Missing Cosmos Connection String");
            }
            var cosmosClientOptions = new CosmosClientOptions
            {
                HttpClientFactory = httpClientFactory.CreateClient,
                Serializer = new CosmosSystemTextJsonSerializer()
            };
            return new CosmosClient(connectionString, cosmosClientOptions);
        });
        services.AddScoped<ChatSessionRepository>();

        // One TMDbClient for the process, instead of the fourteen that were being constructed inline -
        // one inside every tool method, two in TrailerAgent, two more per HTTP function. Each
        // `new TMDbClient(apiKey)` brings its own connection pool and re-fetches TMDb's API
        // configuration on first use, so nothing was reused across the several TMDb calls a single turn
        // makes. Singleton rather than scoped: it holds no per-request state, and hydration already
        // drives it concurrently under Task.WhenAll. This is also now the one place a default language
        // or region could be set.
        services.AddSingleton(_ =>
        {
            var theMovieDbApiKey = context.Configuration["TheMovieDb:Api-Key"];
            if (string.IsNullOrEmpty(theMovieDbApiKey))
            {
                throw new ConfigurationErrorsException("Missing TheMovieDb:Api-Key");
            }
            return new TMDbClient(theMovieDbApiKey);
        });

        // The User-Agent is not decoration. Wikimedia's API etiquette requires a descriptive one and
        // enforces it: measured, both en.wikipedia.org/api/rest_v1 and query.wikidata.org answer
        // 403 Forbidden to a request without it, and 200 to the identical request with it.
        // AddHttpClient<T>() sets none, so every Wikipedia summary and every SPARQL query this app has
        // ever made was rejected. SearchWikipedia returned null, the confidence score was always 0, and
        // the funny fact fell through to the ungrounded fallback every single time - which is the real
        // reason it was inventing biography. The entity-type bug fixed earlier was real but secondary;
        // this is what made the grounded path unreachable for movies as well as people.
        services.AddHttpClient<WikipediaSearchAgent>(httpClient =>
        {
            httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(serviceName, serviceVersion));
            httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("(+https://github.com/TonyAbell/movie-tracker-backend)"));

            // Wikidata's public endpoint is slow under load and this races the main model call, so it
            // must never be the thing that holds a turn open.
            httpClient.Timeout = TimeSpan.FromSeconds(15);
        });
        // No AddScoped<WikipediaSearchAgent>() here. AddHttpClient<T> already registers T, and adding a
        // second descriptor for the same type shadows it - last registration wins, so the agent was
        // being activated with an HttpClient that had none of the configuration above. The typed-client
        // registration is the one that must resolve, so this is the only registration it gets.
        services.AddScoped<OpenMovieDbAgent>();
        services.AddScoped<TrailerAgent>();
        // The raw model connection. Registered separately from the agent because the funny-fact and
        // trailer-title prompts in ChatPlanner/TrailerAgent must run WITHOUT the agent's tool set and
        // JSON response format - the same reason they resolved IChatCompletionService directly off
        // the kernel before this migration.
        services.AddScoped<IChatClient>(serviceProvider =>
        {
            var azureOpenAiEndpoint = context.Configuration["AzureOpenAi:Endpoint"];
            if (string.IsNullOrEmpty(azureOpenAiEndpoint))
            {
                throw new ConfigurationErrorsException("Missing AzureOpenAi:Endpoint");
            }
            var azureOpenAiApiKey = context.Configuration["AzureOpenAi:Api-Key"];
            if (string.IsNullOrEmpty(azureOpenAiApiKey))
            {
                throw new ConfigurationErrorsException("Missing AzureOpenAi:Api-Key");
            }
            var azureOpenAiDeployment = context.Configuration["AzureOpenAi:Deployment"];
            if (string.IsNullOrEmpty(azureOpenAiDeployment))
            {
                throw new ConfigurationErrorsException("Missing AzureOpenAi:Deployment");
            }

            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            IHttpClientFactory httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("HttpClient");

            // Routing the Azure SDK pipeline through the named HttpClient is what preserves the
            // widened resilience timeouts configured above; AddAzureOpenAIChatCompletion took the
            // HttpClient directly.
            var clientOptions = new AzureOpenAIClientOptions
            {
                Transport = new HttpClientPipelineTransport(httpClient)
            };
            var azureOpenAiClient = new AzureOpenAIClient(
                new Uri(azureOpenAiEndpoint),
                new ApiKeyCredential(azureOpenAiApiKey),
                clientOptions);

            // GetChatClient returns the OpenAI SDK's ChatClient, not an IChatClient - AsIChatClient
            // is the required adapter.
            // OTel is enabled HERE, on the chat client, and deliberately NOT on the agent as well.
            // Replaces the Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive
            // AppContext switch.
            // AIAgentBuilder.UseOpenTelemetry wires an OpenTelemetryAgent whose autoWireChatClient
            // also instruments the inner chat client, so stacking the two records
            // gen_ai.client.token.usage twice per model call - measured 6 measurements for 2 calls
            // instead of 4, i.e. every token and cost figure inflated by 50%. Agent-level OTel on its
            // own has the same problem for the same reason.
            return azureOpenAiClient
                .GetChatClient(azureOpenAiDeployment)
                .AsIChatClient()
                .AsBuilder()
                // Registered explicitly so MaximumIterationsPerRequest can be set. ChatClientAgent
                // adds function invocation itself when the chat client has none, but gives no way to
                // configure it, and it defaults to 40 iterations - at a few seconds per round trip
                // that burns the 120s HttpClient budget and then the platform's ~230s trigger limit,
                // so the caller gets a timeout instead of an answer. A real query needs far fewer:
                // resolve a person, discover, fetch details, sometimes ratings.
                //
                // Registering it here does NOT produce a second function-invocation layer: the agent
                // detects the existing one and leaves it alone (verified - one orchestrate_tools span,
                // and the cap is honoured).
                .UseFunctionInvocation(loggerFactory, client =>
                    client.MaximumIterationsPerRequest = MaxToolIterations)
                .UseOpenTelemetry(loggerFactory, serviceName, options => options.EnableSensitiveData = enableSensitiveTelemetry)
                .Build(serviceProvider);
        });

        services.AddScoped<TheMovieDBKernelFunctions>();
        services.AddScoped<ChatPlanner>();

        // The agent replaces the Kernel. Semantic Kernel discovered tools by attribute at build time
        // (Plugins.AddFromType) and had ChatPlanner bolted on per request; the Agent Framework takes
        // one explicit tool list, so all three sources are unioned here instead.
        services.AddScoped<AIAgent>(serviceProvider =>
        {
            var chatClient = serviceProvider.GetRequiredService<IChatClient>();
            var theMovieDbFunctions = serviceProvider.GetRequiredService<TheMovieDBKernelFunctions>();
            var chatPlanner = serviceProvider.GetRequiredService<ChatPlanner>();

            // Wrapped for per-tool duration and failure metrics. The framework traces tool calls but
            // emits no tool metric, and traces are sampled - so without this there is no reliable way
            // to see that, say, OMDb has started timing out. InstrumentedAIFunction forwards name,
            // description and schema untouched, so the model sees exactly the same 20 tools.
            List<AITool> tools =
            [
                .. theMovieDbFunctions.CreateTools(),
                .. DateTimeKernelFunctions.CreateTools(),
                .. chatPlanner.CreateTools(),
            ];
            tools = [.. tools.Select(tool =>
                tool is AIFunction function ? new InstrumentedAIFunction(function) : tool)];

            // ChatClientAgent decorates the chat client with automatic function invocation by default,
            // which is what ToolCallBehavior.AutoInvokeKernelFunctions used to switch on. The
            // iteration cap it runs under is configured on the chat client above.
            //
            // There is deliberately no AIAgentBuilder.Use(...) middleware here. That is the documented
            // way to intercept tool calls and it does compile, but on Microsoft.Agents.AI 1.15.0 the
            // callback is simply never invoked - measured zero invocations across a turn in which the
            // tool definitely ran, with and without a service provider. The iteration cap that used to
            // live in it was therefore doing nothing at all; it is now MaximumIterationsPerRequest,
            // and the per-tool telemetry is in InstrumentedAIFunction. Both are verified to fire.
            return chatClient.AsAIAgent(
                new ChatClientAgentOptions
                {
                    Name = serviceName,
                    ChatOptions = new ChatOptions { Tools = tools },
                },
                serviceProvider.GetRequiredService<ILoggerFactory>(),
                serviceProvider);
        });
        var appInsightsConnectionString = context.Configuration["APPLICATIONINSIGHTS-CONNECTION-STRING"];
        var telemetryResource = ResourceBuilder.CreateDefault()
            .AddService(serviceName: serviceName, serviceVersion: serviceVersion);

        services.AddOpenTelemetry()
                 .WithTracing((builder) =>
                 {
                     // serviceName covers this app's own spans (the Tracer used in Function.cs and
                     // ChatSessionRepository) AND every span the framework emits - `chat`,
                     // `orchestrate_tools`, and one `execute_tool <name>` per tool call - because it is
                     // the sourceName passed to UseOpenTelemetry on the chat client, and that string
                     // names the framework's ActivitySource too.
                     //
                     // The Microsoft.Extensions.AI*/Microsoft.Agents.AI* entries carry nothing while an
                     // explicit sourceName is set; they are kept only so telemetry survives if that
                     // argument is ever dropped and the sources revert to their Experimental.* defaults.
                     builder.AddSource(serviceName)
                            .AddSource("Microsoft.Extensions.AI*")
                            .AddSource("Microsoft.Agents.AI*")
                            .AddSource("Azure.AI.OpenAI*")
                            .AddSource("OpenAI*")
                            .SetResourceBuilder(telemetryResource)
                            .AddAspNetCoreInstrumentation()
                            .AddHttpClientInstrumentation()
                            .AddAzureMonitorTraceExporter(options => options.ConnectionString = appInsightsConnectionString);
                 })
                 // Traces are sampled; metrics are not. That distinction is the whole reason this
                 // pipeline exists. OpenTelemetryChatClient has always emitted
                 // gen_ai.client.token.usage and gen_ai.client.operation.duration off the same Meter
                 // name as its ActivitySource, but with no MeterProvider and no AddMeter they went
                 // nowhere - leaving sampled `chat` spans as the only record of token usage, which is
                 // useless for totals (a spot check found 5 surviving spans against 107 real calls).
                 // Anything that needs to be *counted* rather than *inspected* - tokens, cost, tool
                 // failure rates - has to be read from here, and alerts should be built on these.
                 .WithMetrics((builder) =>
                 {
                     builder.AddMeter(serviceName)
                            .SetResourceBuilder(telemetryResource)
                            .AddHttpClientInstrumentation()
                            .AddAzureMonitorMetricExporter(options => options.ConnectionString = appInsightsConnectionString);
                 })
                 // Logs are NOT configured here. The empty .WithLogging(builder => { }) that used to
                 // sit at this spot had no exporter, which is why no MovieTracker.* category has ever
                 // appeared in App Insights and why every LogCritical in the HTTP catch blocks was
                 // silently swallowed. Adding the exporter here does not fix it either - see the
                 // logging.AddOpenTelemetry(...) call in ConfigureLogging above, which does.
                 .UseFunctionsWorkerDefaults();
        })

    .Build();

host.Run();
