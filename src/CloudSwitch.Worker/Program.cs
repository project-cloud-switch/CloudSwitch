using CloudSwitch.Worker;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CloudSwitch.Persistence.Diagnostics;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddSafeConsole();
builder.Services.AddCloudSwitchWorker(builder.Configuration, builder.Environment);
using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("CloudSwitch Worker simulation is using PostgreSQL and RabbitMQ.");
await host.RunAsync();
