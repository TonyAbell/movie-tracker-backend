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
        services.AddHttpClient("HttpClient")
                .AddStandardResilienceHandler();

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
        services.AddScoped<Kernel>(serviceProvider =>
        {



            var apiKey = context.Configuration["OpenAi:Api-Key"];
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new Exception("OpenAIKey is missing from configuration");
            }
            OpenAIConfig openAIOptions = new()
            {
                TextGenerationType = OpenAIConfig.TextGenerationTypes.Chat,
                TextModel = "gpt-4o",
                APIKey = apiKey
            };

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
            kernelBuilder.AddOpenAIChatCompletion(openAIOptions.TextModel, openAIOptions.APIKey, httpClient: httpClient);
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
