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
    if (HostStartupNotes.OpenCommandsOnGitHub(host) is { } warning)
    {
        log.LogWarning("{Warning}", warning);
    }
    // Who may start a run by a label is easy to forget until a ticket is not picked up; said once at start, when there is a rule.
    if (host.Start is { IsOpen: false } start)
    {
        log.LogInformation("Git host {Host} ({Type}): {Rule}{Exceptions}", host.Name, host.Type, start.Default.Describe(),
            start.Repositories.Count > 0 ? $"; {start.Repositories.Count} repositories have a rule of their own" : "");
    }
}

// A model of its own for some roles is easy to forget until an answer changes; said once at start, with the roles in order.
if (settings.RoleModels.Count > 0)
{
    log.LogInformation("Models: {Roles} use their own (FACTORY_ROLE_MODELS); every other role uses {Default}",
        string.Join(", ", settings.RoleModels.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} = {pair.Value}")),
        settings.Model ?? "the model its role file or opencode.json names");
}

await app.RunAsync();

return 0;
