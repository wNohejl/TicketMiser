using TicketMiser.Data;
using TicketMiser.Ingestion;
using TicketMiser.Observability;
using TicketMiser.Reliability;

// Standalone worker host. The web app can host the same schedule in-process for a personal
// deployment; this entry point exists so ingestion can be run and scaled on its own.
var builder = Host.CreateApplicationBuilder(args);

// Local overrides, last so they win. Provider keys live here because this file is gitignored
// and the environment-named ones are not.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

builder.Services.AddTicketMiserData(builder.Configuration);
builder.Services.AddTicketMiserIngestion(builder.Configuration);
builder.Services.AddTicketMiserReliability(builder.Configuration);
builder.Services.AddTicketMiserObservability(builder.Configuration);

builder.Services.AddTicketMiserIngestionScheduler();
builder.Services.AddTicketMiserReliabilityEvaluator();

var host = builder.Build();

await using (var scope = host.Services.CreateAsyncScope())
{
    var initialiser = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initialiser.InitialiseAsync();
}

await host.RunAsync();
