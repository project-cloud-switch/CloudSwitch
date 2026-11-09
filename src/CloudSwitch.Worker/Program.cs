using CloudSwitch.Worker;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCloudSwitchWorker(builder.Configuration, builder.Environment);
using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("CloudSwitch Worker simulation is using PostgreSQL and RabbitMQ.");
await host.RunAsync();
