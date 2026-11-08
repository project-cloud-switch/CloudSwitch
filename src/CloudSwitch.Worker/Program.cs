using CloudSwitch.Worker;
using CloudSwitch.Worker.Consumers;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddCloudSwitchWorker(useDeterministicAdapters: true);

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<DeploymentRequestedConsumer>();

    x.UsingInMemory((context, cfg) =>
    {
        cfg.ConfigureEndpoints(context);
    });
});

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("CloudSwitch Worker (Deployer) started. Listening for DeploymentRequested messages...");

await host.RunAsync();

public partial class Program { }
