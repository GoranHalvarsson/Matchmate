var builder = DistributedApplication.CreateBuilder(args);
var root = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
var store = Path.Combine(root, ".local", "replay");
var footballStore = Path.Combine(root, ".local", "football-full-v1");
builder.AddProject<Projects.Matchmate_Worker>("worker")
    .WithEnvironment("LocalReplay__SourceDirectory", Path.Combine(root, "prototypes", "first-experience", "assets", "tracking"))
    .WithEnvironment("LocalReplay__StoreDirectory", store)
    .WithEnvironment("SharedFootball__StoreDirectory", footballStore)
    .WithEnvironment("SharedFootball__PossessionsPath", Path.Combine(root, "prototypes", "first-experience", "assets", "tracking", "1874553-completed-events.json"))
    .WithEnvironment("SharedFootball__BrentfordExport", Path.Combine(root, ".local", "shared-inputs", "brentford-full-v5", "manifest.json"))
    .WithEnvironment("SharedFootball__LiverpoolExport", Path.Combine(root, "output", "brentford-liverpool-generated", "dataset-v2", "manifest.json"))
    .WithEnvironment("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
var api = builder.AddProject<Projects.Matchmate_Api>("api")
    .WithEnvironment("LocalReplay__StoreDirectory", store)
    .WithEnvironment("SharedFootball__StoreDirectory", footballStore)
    .WithEnvironment("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
builder.AddProject<Projects.Matchmate_Web>("web", launchProfileName: "https").WithReference(api)
    .WithEndpoint("https", endpoint => { endpoint.Port = 7410; endpoint.TargetPort = 7410; endpoint.IsProxied = false; })
    .WithEndpoint("http", endpoint => { endpoint.Port = 5410; endpoint.TargetPort = 5410; endpoint.IsProxied = false; })
    .WithExternalHttpEndpoints().WithEnvironment("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
builder.Build().Run();
