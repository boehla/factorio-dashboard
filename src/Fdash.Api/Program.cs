using Fdash.Collector;
using Fdash.Core;
using Fdash.Rcon;
using Fdash.Storage;
using Fdash.Api;

WebApplicationOptions webOpts = new() {
    ContentRootPath = AppContext.BaseDirectory,
    Args = args
};
WebApplicationBuilder builder = WebApplication.CreateBuilder(webOpts);

// Geheimnisse (vor allem das RCON-Passwort) gehoeren nicht ins Repo.
// appsettings.Local.json ist per .gitignore ausgeschlossen; alternativ geht
// jede Einstellung als Umgebungsvariable, z. B. Collector__RconPassword.
builder.Configuration
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json"), optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// --- Optionen ---
builder.Services.Configure<CollectorOptions>(builder.Configuration.GetSection("Collector"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<IconOptions>(builder.Configuration.GetSection("Icons"));

// --- Infrastruktur ---
builder.Services.AddSingleton(sp => {
    CollectorOptions o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CollectorOptions>>().Value;
    return new RconClient(new RconOptions { Host = o.Host, Port = o.RconPort, Password = o.RconPassword });
});
// GameLink waehlt den Transportweg zum Mod (Datei oder RCON) und ist zugleich
// der einzige schreibende Pfad ins Spiel.
builder.Services.AddSingleton<GameLink>();
builder.Services.AddSingleton<ISnapshotSource>(sp => sp.GetRequiredService<GameLink>());
builder.Services.AddSingleton<IGameControl>(sp => sp.GetRequiredService<GameLink>());
builder.Services.AddSingleton(sp => {
    StorageOptions o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value;
    return new SqliteTimeSeriesStore(o);
});
builder.Services.AddSingleton<ITimeSeriesStore>(sp => sp.GetRequiredService<SqliteTimeSeriesStore>());
builder.Services.AddSingleton<ISnapshotBus, SnapshotBus>();
builder.Services.AddSingleton<DiscoveryService>();
builder.Services.AddSingleton<PrototypeExporter>();
builder.Services.AddSingleton<StallDetector>();
builder.Services.AddSingleton<AutoResearchService>();
builder.Services.AddSingleton<IconService>();

// --- Hosted Services ---
builder.Services.AddHostedService<CollectorService>();
builder.Services.AddHostedService<RollupService>();
builder.Services.AddHostedService<SnapshotRelay>();

// --- Web ---
builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

WebApplication app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<DashboardHub>("/hub");

// --- REST (Plan §7) ---
app.MapGet("/api/snapshots", (ISnapshotBus bus) => Results.Json(bus.All()));

app.MapGet("/api/snapshots/{job}", (string job, ISnapshotBus bus) => {
    Snapshot? s = bus.Latest(job);
    return s == null ? Results.NotFound() : Results.Json(s);
});

app.MapGet("/api/series", async (string saveId, string metric, string? labels,
        long from, long to, string? resolution, ITimeSeriesStore store, CancellationToken ct) => {
    Resolution res = resolution switch {
        "minute" => Resolution.Minute,
        "quarter" => Resolution.Quarter,
        "hour" => Resolution.Hour,
        _ => Resolution.Raw
    };
    IReadOnlyList<Sample> data = await store.QueryAsync(saveId, metric, labels, from, to, res, ct);
    return Results.Json(data.Select(s => new { s.Ts, s.Value }));
});

// Auto-Research: Toggle, Audit, Preview (Plan §6)
app.MapGet("/api/research/audit", (AutoResearchService ar) => Results.Json(ar.Audit));
app.MapPost("/api/research/toggle", (bool enabled, AutoResearchService ar) => {
    ar.Enabled = enabled;
    return Results.Json(new { enabled = ar.Enabled });
});

// Item-Icons aus Base-Data + Mod-Zips (Plan §10.6, F12)
app.MapGet("/api/icon/{name}", (string name, IconService icons, PrototypeExporter proto) => {
    byte[]? png = icons.GetIcon(name, proto.IconStems);
    if(png == null || png.Length == 0) return Results.NotFound();
    return Results.File(png, "image/png");
});

// Statische Rezept-Struktur fuer den Item-Abhaengigkeitsgraphen (pro Save konstant).
app.MapGet("/api/graph/meta", (PrototypeExporter proto) => Results.Json(new {
    recipes = proto.Recipes.Values.Select(r => new {
        name = r.Name,
        energy = r.EnergyRequired,
        ingredients = r.Ingredients.Select(i => new { name = i.Name, type = i.Type, amount = i.Amount }),
        products = r.Products.Select(p => new { name = p.Name, type = p.Type, amount = p.Amount }),
    }),
    oreItems = proto.OreItems,
    fluidNames = proto.FluidNames
}));

// Health inkl. Transportweg und Mod-Zustand — der erste Anlaufpunkt, wenn im
// Dashboard nichts ankommt.
app.MapGet("/api/health", async (GameLink link, PrototypeExporter proto, CancellationToken ct) => {
    string? modStatus = null;
    try { modStatus = await link.ModStatusAsync(ct); } catch { /* RCON optional */ }
    return Results.Json(new {
        status = "ok",
        transport = link.Description,
        canWrite = link.Available,
        prototypesLoaded = proto.Loaded,
        mod = modStatus
    });
});

app.Run();
