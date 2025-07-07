using eShop.AppHost;
using ProcfilerOnline.Aspire;
using DistributedApplicationBuilderExtensions = ProcfilerOnline.Aspire.DistributedApplicationBuilderExtensions;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddForwardedHeaders();

var ext = Environment.OSVersion.Platform.ToString().Contains("win", StringComparison.CurrentCultureIgnoreCase)
    ? ".exe"
    : string.Empty;

var procfilerExePath = $@"..\..\..\workspace\Procfiler\src\dotnet\ProcfilerOnline\bin\Release\net9.0\ProcfilerOnline{ext}";

var redis = builder.AddRedis("redis");
var rabbitMq = builder.AddRabbitMQ("eventbus")
    .WithLifetime(ContainerLifetime.Persistent);
var postgres = builder.AddPostgres("postgres")
    .WithImage("ankane/pgvector")
    .WithImageTag("latest")
    .WithLifetime(ContainerLifetime.Persistent);

var catalogDb = postgres.AddDatabase("catalogdb");
var identityDb = postgres.AddDatabase("identitydb");
var orderDb = postgres.AddDatabase("orderingdb");
var webhooksDb = postgres.AddDatabase("webhooksdb");

var launchProfileName = ShouldUseHttpForEndpoints() ? "http" : "https";

// Services
void ConfigureSettings(DistributedApplicationBuilderExtensions.ProcfilerSettings settings, string regex)
{
    settings.BootstrapServers = "localhost:9092";
    settings.TopicName = "my-topic";
    settings.TargetMethodsRegex = settings.MethodsFilterRegex = regex;
}

var identityApi = builder
    .AddLocalProcfilerExecutable<Projects.Identity_API>(
        "identity-api",
        procfilerExePath,
        settings =>
        {
            ConfigureSettings(settings, @"eShop\.Identity\.API");
        }
    )
    .WithExternalHttpEndpoints()
    .WaitFor(identityDb)
    .WithReference(identityDb);

var identityEndpoint = identityApi.GetEndpoint(launchProfileName);

var basketApi = builder
    .AddLocalProcfilerExecutable<Projects.Basket_API>(
        "basket-api",
        procfilerExePath,
        settings => ConfigureSettings(settings, @"eShop\.Basket\.API")
    )
    .WithReference(redis)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithEnvironment("Identity__Url", identityEndpoint);
redis.WithParentRelationship(basketApi);

var catalogApi = builder
    .AddLocalProcfilerExecutable<Projects.Catalog_API>(
        "catalog-api",
        procfilerExePath,
        settings => ConfigureSettings(settings, @"eShop\.Catalog\.API")
    )
    .WithReference(rabbitMq)
    .WaitFor(rabbitMq)
    .WithReference(catalogDb);

var orderingApi = builder
    .AddLocalProcfilerExecutable<Projects.Ordering_API>(
        "ordering-api",
        procfilerExePath,
        settings => ConfigureSettings(settings, @"eShop\.Ordering\.API")
    )
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithReference(orderDb).WaitFor(orderDb)
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Identity__Url", identityEndpoint);

builder.
    AddLocalProcfilerExecutable<Projects.OrderProcessor>(
        "order-processor",
        procfilerExePath,
        settings => ConfigureSettings(settings, @"eShop\.OrderProcessor\.API")
    )
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithReference(orderDb)
    .WaitFor(orderingApi); // wait for the orderingApi to be ready because that contains the EF migrations

builder.AddProject<Projects.PaymentProcessor>("payment-processor")
    .WithReference(rabbitMq).WaitFor(rabbitMq);

var webHooksApi = builder.AddProject<Projects.Webhooks_API>("webhooks-api")
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithReference(webhooksDb)
    .WithEnvironment("Identity__Url", identityEndpoint);

// Reverse proxies
builder.AddProject<Projects.Mobile_Bff_Shopping>("mobile-bff")
    .WithReference(catalogApi)
    .WithReference(orderingApi)
    .WithReference(basketApi)
    .WithReference(identityApi);

// Apps
var webhooksClient = builder.AddProject<Projects.WebhookClient>("webhooksclient", launchProfileName)
    .WithReference(webHooksApi)
    .WithEnvironment("IdentityUrl", identityEndpoint);

var webApp = builder.AddProject<Projects.WebApp>("webapp", launchProfileName)
    .WithExternalHttpEndpoints()
    .WithUrls(c => c.Urls.ForEach(u => u.DisplayText = $"Online Store ({u.Endpoint?.EndpointName})"))
    .WithReference(basketApi)
    .WithReference(catalogApi)
    .WithReference(orderingApi)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithEnvironment("IdentityUrl", identityEndpoint);

// set to true if you want to use OpenAI
//bool useOpenAI = false;
// if (useOpenAI)
// {
//     builder.AddOpenAI(catalogApi, webApp);
// }
//
// bool useOllama = false;
// if (useOllama)
// {
//     builder.AddOllama(catalogApi, webApp);
// }

// Wire up the callback urls (self referencing)
webApp.WithEnvironment("CallBackUrl", webApp.GetEndpoint(launchProfileName));
webhooksClient.WithEnvironment("CallBackUrl", webhooksClient.GetEndpoint(launchProfileName));

// Identity has a reference to all of the apps for callback urls, this is a cyclic reference
identityApi.WithEnvironment("BasketApiClient", basketApi.GetEndpoint("http"))
           .WithEnvironment("OrderingApiClient", orderingApi.GetEndpoint("http"))
           .WithEnvironment("WebhooksApiClient", webHooksApi.GetEndpoint("http"))
           .WithEnvironment("WebhooksWebClient", webhooksClient.GetEndpoint(launchProfileName))
           .WithEnvironment("WebAppClient", webApp.GetEndpoint(launchProfileName));

builder.Build().Run();

// For test use only.
// Looks for an environment variable that forces the use of HTTP for all the endpoints. We
// are doing this for ease of running the Playwright tests in CI.
static bool ShouldUseHttpForEndpoints()
{
    const string EnvVarName = "ESHOP_USE_HTTP_ENDPOINTS";
    var envValue = Environment.GetEnvironmentVariable(EnvVarName);

    // Attempt to parse the environment variable value; return true if it's exactly "1".
    return int.TryParse(envValue, out int result) && result == 1;
}
