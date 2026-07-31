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
using Microsoft.SemanticKernel;
using Microsoft.KernelMemory;
using MovieTracker.Backend;
using MovieTracker.Backend.Prompts;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using MovieTracker.Backend.Agents;

var serviceName = "movie-tracker-backend";
var serviceVersion = "1.0.0";

AppContext.SetSwitch("Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnosticsSensitive", true);

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
        // This named client is used only by the Semantic Kernel chat completion service.
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
        services.AddScoped<Kernel>(serviceProvider =>
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

            var kernelBuilder = Kernel.CreateBuilder();
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            kernelBuilder.Services.AddLogging(builder =>
            {
                builder.AddOpenTelemetry(options =>
                {
                    options.SetResourceBuilder(ResourceBuilder.CreateDefault());
                    options.AddAzureMonitorLogExporter(options => options.ConnectionString = context.Configuration["APPLICATIONINSIGHTS-CONNECTION-STRING"]);
                    options.IncludeFormattedMessage = true;
                    options.IncludeScopes = true;
                });
                builder.AddFilter("Microsoft", LogLevel.Warning);
                builder.AddFilter("Microsoft.SemanticKernel", LogLevel.Information);
                builder.SetMinimumLevel(LogLevel.Information);
            });
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            kernelBuilder.Services.AddSingleton(configuration);
            IHttpClientFactory httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("HttpClient");
            kernelBuilder.AddAzureOpenAIChatCompletion(
                deploymentName: azureOpenAiDeployment,
                endpoint: azureOpenAiEndpoint,
                apiKey: azureOpenAiApiKey,
                httpClient: httpClient);
            kernelBuilder.Plugins.AddFromType<TheMovieDBKernelFunctions>();
            kernelBuilder.Plugins.AddFromType<DateTimeKernelFunctions>();

            //kernelBuilder.Plugins.AddFromType<ChatPlanner>();
            var kernel = kernelBuilder.Build();
            return kernel;

        });
        services.AddOpenTelemetry()
                 .WithTracing((builder) =>
                 {
                     builder.AddSource(serviceName)
                            .AddSource("Microsoft.SemanticKernel*")
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
