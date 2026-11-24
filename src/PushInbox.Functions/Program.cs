using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PushInbox.Functions;

var builder = FunctionsApplication.CreateBuilder(args);
builder.ConfigureFunctionsWebApplication();
builder.Services.AddApplicationInsightsTelemetryWorkerService();

static string Setting(string name) =>
    Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"App setting {name} is missing.");

var credential = new DefaultAzureCredential();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(_ => new CosmosClient(Setting("COSMOS_ENDPOINT"), credential));
builder.Services.AddSingleton(sp => new Store(sp.GetRequiredService<CosmosClient>(), Setting("COSMOS_DATABASE")));
builder.Services.AddSingleton(_ => new ServiceBusClient(Setting("ServiceBus__fullyQualifiedNamespace"), credential));
builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(Setting("DISPATCH_QUEUE")));
builder.Services.AddSingleton(sp => new NotificationHubRest(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("anh"), Setting("NOTIFICATIONHUB_CONNECTION"), Setting("NOTIFICATIONHUB_NAME")));
builder.Services.AddSingleton(sp => new DataverseClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("dataverse"), Environment.GetEnvironmentVariable("DATAVERSE_URL")));

builder.Build().Run();
