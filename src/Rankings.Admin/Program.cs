using Microsoft.AspNetCore.HostFiltering;
using Rankings.Admin;

// The local admin dashboard: http://localhost:5199, for this machine only. It edits a local
// rankings.db (default: rankings.db in the folder it starts in) and syncs it with R2 on request.
// It is never deployed: the Worker publishes web/public only and the daily job does not reference it.
if (LocalOnly.RunningOnCi())
{
    Console.Error.WriteLine("The admin dashboard only runs on your own machine, not on CI or the host.");
    return 1;
}

var databasePath = Path.GetFullPath(ArgumentAfter("--db") ?? "rankings.db");
var port = int.TryParse(ArgumentAfter("--port"), out var chosenPort) ? chosenPort : 5199;

var builder = WebApplication.CreateBuilder();
// Loopback only, whatever ASPNETCORE_URLS or settings say: other machines cannot connect.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port));
builder.Services.Configure<HostFilteringOptions>(options => options.AllowedHosts = LocalOnly.AllowedHosts);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

var store = new AdminStore(databasePath);
var sync = R2Sync.FromEnvironment(databasePath);

// A web page elsewhere could aim the browser at localhost (DNS rebinding) or post to it: the Host
// header must name this machine, and every change must carry the header only this page sends.
app.UseHostFiltering();
app.Use(async (context, next) =>
{
    if (LocalOnly.Refusal(context.Connection.RemoteIpAddress, context.Request.Method,
            context.Request.Headers[LocalOnly.ChangeHeader]) is { } refusal)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = refusal });
        return;
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (AdminException error)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new { error = error.Message });
    }
});

var api = app.MapGroup("/api");
api.MapGet("/status", () => new { databasePath, databaseExists = store.DatabaseExists, sync = sync.Status() });

api.MapGet("/unassigned-teams", () => store.UnassignedTeams());
api.MapGet("/teams", () => store.Teams());
api.MapPost("/teams", (NewTeam request) =>
    new { teamId = store.CreateTeam(request.League, request.Name, request.ShortName, request.CreatedOn, request.ProviderKey) });
api.MapPost("/teams/{teamId:int}/display", (int teamId, TeamDisplay request) =>
{
    store.SetDisplay(teamId, request.ValidFrom, request.Name, request.ShortName, request.Logo);
    return Results.NoContent();
});
api.MapPost("/teams/{teamId:int}/league", (int teamId, TeamLeague request) =>
    new { fullRecalculationNeeded = store.SetLeague(teamId, request.League) });
api.MapPost("/teams/{teamId:int}/merge", (int teamId, TeamMerge request) =>
    new { fullRecalculationNeeded = store.MergeTeams(teamId, request.IntoTeamId) });
api.MapPost("/teams/{teamId:int}/status", (int teamId, TeamStatus request) =>
{
    store.SetStatus(teamId, request.Status, request.EffectiveOn, request.Note);
    return Results.NoContent();
});
api.MapPost("/teams/status", (TeamStatuses request) =>
{
    store.SetStatuses(request.TeamIds, request.Status, request.EffectiveOn, request.Note);
    return Results.NoContent();
});
api.MapPost("/provider-teams/link", (ProviderLink request) =>
{
    store.LinkTeam(request.ProviderKey, request.TargetId);
    return Results.NoContent();
});

api.MapGet("/players", (string? search) => store.FindPlayers(search ?? ""));
api.MapPost("/provider-players/link", (ProviderLink request) =>
{
    store.LinkPlayer(request.ProviderKey, request.TargetId);
    return Results.NoContent();
});

api.MapGet("/seasons", () => store.Seasons());
api.MapPost("/seasons", (AdminSeason request) =>
{
    store.SetSeason(request.StartsOn, request.Name);
    return Results.NoContent();
});
api.MapDelete("/seasons/{startsOn}", (string startsOn) =>
{
    store.DeleteSeason(startsOn);
    return Results.NoContent();
});

api.MapGet("/regions", () => store.Regions());
api.MapPost("/regions/{regionId}/entry-level", (string regionId, EntryLevel request) =>
{
    store.SetEntryLevel(regionId, request.Level);
    return Results.NoContent();
});

api.MapGet("/skipped-maps", () => store.SkippedMaps());
api.MapGet("/rankings", (string? date) => store.Rankings(string.IsNullOrEmpty(date) ? null : date));

api.MapPost("/sync/pull", async (PullRequest request, CancellationToken cancellationToken) =>
{
    await sync.PullAsync(request.Overwrite, cancellationToken);
    return Results.NoContent();
});
api.MapPost("/sync/push", async (CancellationToken cancellationToken) =>
{
    await sync.PushAsync(cancellationToken);
    return Results.NoContent();
});

Console.WriteLine($"Admin dashboard on http://localhost:{port}, editing {databasePath}.");
app.Run();
return 0;

string? ArgumentAfter(string name)
{
    var position = Array.IndexOf(args, name);
    return position >= 0 && position + 1 < args.Length ? args[position + 1] : null;
}

record NewTeam(string League, string Name, string? ShortName, string CreatedOn, string? ProviderKey);
record TeamDisplay(string ValidFrom, string Name, string? ShortName, string? Logo);
record TeamStatus(string Status, string EffectiveOn, string? Note);
record TeamStatuses(int[] TeamIds, string Status, string EffectiveOn, string? Note);
record TeamLeague(string League);
record TeamMerge(int IntoTeamId);
record ProviderLink(string ProviderKey, int TargetId);
record EntryLevel(double? Level);
record PullRequest(bool Overwrite);
