using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Set PropertyNamingPolicy to null to disable camel casing and use PascalCase.
        options.JsonSerializerOptions.PropertyNamingPolicy = null;

        // Alternatively, you can explicitly set it to JsonNamingPolicy.CamelCase
        // if you want camel casing, but setting to null or a custom policy
        // will prevent the default camel casing.
        // options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; 
    });

builder.Build().Run();
