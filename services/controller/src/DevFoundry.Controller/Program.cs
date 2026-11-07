using DevFoundry.Controller;

var builder = Host.CreateApplicationBuilder(args);
var settings = WorkerSettings.FromConfiguration(builder.Configuration);

builder.Services.AddFactoryController(settings);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(25));

await builder.Build().RunAsync();
