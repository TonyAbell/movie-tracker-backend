using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

var serviceName = "movie-tracker-backend";
var serviceVersion = "1.0.0";

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
    .ConfigureLogging(logging =>
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
    })
    .ConfigureServices((context, services) =>
    {
        services.AddDistributedMemoryCache();
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
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
                    options.Retry.MaxRetryAttempts = 1;
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
        services.AddHttpClient<WikipediaSearchAgent>();
        services.AddScoped<WikipediaSearchAgent>();
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
            return azureOpenAiClient
                .GetChatClient(azureOpenAiDeployment)
                .AsIChatClient()
                .AsBuilder()
                // Replaces the Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive
                // AppContext switch: emits GenAI spans and includes prompts and completions in them.
                .UseOpenTelemetry(loggerFactory, serviceName, options => options.EnableSensitiveData = true)
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

            List<AITool> tools =
            [
                .. theMovieDbFunctions.CreateTools(),
                .. DateTimeKernelFunctions.CreateTools(),
                .. chatPlanner.CreateTools(),
            ];

            // ChatClientAgent decorates the chat client with automatic function invocation by default,
            // which is what ToolCallBehavior.AutoInvokeKernelFunctions used to switch on.
            return chatClient.AsAIAgent(
                new ChatClientAgentOptions
                {
                    Name = serviceName,
                    ChatOptions = new ChatOptions { Tools = tools },
                },
                serviceProvider.GetRequiredService<ILoggerFactory>(),
                serviceProvider);
        });
        services.AddOpenTelemetry()
                 .WithTracing((builder) =>
                 {
                     // serviceName also covers the model spans: it is the source name passed to
                     // UseOpenTelemetry on the chat client above.
                     builder.AddSource(serviceName)
                            .AddSource("Microsoft.Extensions.AI*")
                            .AddSource("Microsoft.Agents.AI*")
                            .AddSource("Azure.AI.OpenAI*")
                            .AddSource("OpenAI*")
                            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                            .AddService(serviceName: serviceName, serviceVersion: serviceVersion))
                            .AddAspNetCoreInstrumentation()
                            .AddHttpClientInstrumentation()
                            .AddAzureMonitorTraceExporter(options => options.ConnectionString = context.Configuration["APPLICATIONINSIGHTS-CONNECTION-STRING"]);
                     //.AddAzureMonitorTraceExporter(configure =>
                     //{
                     //    configure.ConnectionString = context.Configuration["APPLICATIONINSIGHTS-CONNECTION-STRING"];
                     //});
                 })
               .WithLogging(builder =>
               {

               })
               .UseFunctionsWorkerDefaults();
               //.UseAzureMonitor(configure =>
               //{
               //    configure.ConnectionString = context.Configuration["APPLICATIONINSIGHTS-CONNECTION-STRING"];
               //});
        })
       
    .Build();

host.Run();
