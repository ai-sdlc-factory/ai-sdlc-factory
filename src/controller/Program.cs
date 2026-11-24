using AiSdlcFactory.Controller;

// `validate-project <file|->`: check a .factory/project.json and exit, without starting the worker or needing its settings.
if (args is [ProjectCheckCommand.Name, .. var checkArguments])
{
    return ProjectCheckCommand.Run(checkArguments, Console.In, Console.Out);
}

var builder = Host.CreateApplicationBuilder(args);
var settings = WorkerSettings.FromConfiguration(builder.Configuration);

builder.Services.AddFactoryController(settings);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(25));

var app = builder.Build();

// Who may steer the factory on each git host, said once at start: an open rule ("anyone who can comment") is easy to forget on a public repository.
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AiSdlcFactory.Controller");
foreach (var host in settings.Hosts)
{
    var rules = host.Commands ?? HostCommandRules.Open;
    log.LogInformation("Git host {Host} ({Type}): {Rule}{Exceptions}", host.Name, host.Type, rules.Default.Describe(),
        rules.Repositories.Count > 0 ? $"; {rules.Repositories.Count} repositories have a rule of their own" : "");
}

await app.RunAsync();

return 0;
