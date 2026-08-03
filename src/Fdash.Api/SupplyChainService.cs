using System.Text.Json;
using Fdash.Analysis;
using Fdash.Collector;
using Fdash.Core;
using Fdash.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Fdash.Api;

public sealed class SupplyChainDto {
    public int Id { get; set; }
    public string TargetItem { get; set; } = "";
    public string RecipeName { get; set; } = "";
    public string? Name { get; set; }
    public string Surface { get; set; } = "nauvis";
    public double? TargetPerMin { get; set; }
    public List<SupplyNodeDto> Nodes { get; set; } = new();
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public sealed class SupplyNodeDto {
    public int? Id { get; set; }
    public string ItemName { get; set; } = "";
    public string ItemType { get; set; } = "item";
    public double AmountPerCraft { get; set; }
    public string Source { get; set; } = "local";
    public string? ChildRecipe { get; set; }
    public List<SupplyNodeDto> Children { get; set; } = new();
}

public sealed class TrainAvailabilityDto {
    public Dictionary<string, TrainItemInfo> Provides { get; set; } = new();
    public Dictionary<string, TrainItemInfo> Requests { get; set; } = new();
}

public sealed class TrainItemInfo {
    public string ItemType { get; set; } = "item";
    public int StationCount { get; set; } = 1;
}

public sealed class RecipeOptionDto {
    public string Name { get; set; } = "";
    public double Energy { get; set; }
    public string? Category { get; set; }
    public bool AllowProductivity { get; set; }
    public bool IsResearched { get; set; }
    public bool IsProducing { get; set; }
    public int MachineCount { get; set; }
    public List<RecipeIoDto> Ingredients { get; set; } = new();
    public List<RecipeIoDto> Products { get; set; } = new();
}

public sealed class RecipeIoDto {
    public string Name { get; set; } = "";
    public string Type { get; set; } = "item";
    public double Amount { get; set; }
}

public sealed class SupplyChainService : IHostedService {
    private readonly string connStr;
    private readonly PrototypeExporter proto;
    private readonly ISnapshotBus bus;

    // In-memory train cache, refreshed on every stations snapshot
    private readonly Dictionary<string, TrainAvailabilityDto> trainCache = new();

    public SupplyChainService(IOptions<StorageOptions> storageOpts, PrototypeExporter proto, ISnapshotBus bus) {
        connStr = new SqliteConnectionStringBuilder {
            DataSource = storageOpts.Value.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        this.proto = proto;
        this.bus = bus;
    }

    public async Task StartAsync(CancellationToken ct) {
        await InitializeAsync();
        bus.OnSnapshot += OnBusSnapshot;
    }

    public Task StopAsync(CancellationToken ct) {
        bus.OnSnapshot -= OnBusSnapshot;
        return Task.CompletedTask;
    }

    private async Task OnBusSnapshot(Snapshot s) {
        if (s.Job.StartsWith("stations@")) {
            string surface = s.Job[(s.Job.IndexOf('@') + 1)..];
            try { RefreshTrainCache(surface, s.Payload); } catch { }
        }
        await Task.CompletedTask;
    }

    public async Task InitializeAsync() {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS supply_chains (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                target_item     TEXT NOT NULL,
                recipe_name     TEXT NOT NULL,
                name            TEXT,
                surface         TEXT NOT NULL DEFAULT 'nauvis',
                target_per_min  REAL,
                created_at      TEXT NOT NULL DEFAULT (datetime('now')),
                updated_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS supply_chain_nodes (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                chain_id        INTEGER NOT NULL REFERENCES supply_chains(id) ON DELETE CASCADE,
                parent_node_id  INTEGER REFERENCES supply_chain_nodes(id) ON DELETE CASCADE,
                item_name       TEXT NOT NULL,
                item_type       TEXT NOT NULL DEFAULT 'item',
                amount_per_craft REAL NOT NULL,
                source          TEXT NOT NULL DEFAULT 'local',
                child_recipe    TEXT,
                sort_order      INTEGER NOT NULL DEFAULT 0,
                created_at      TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_sc_nodes_chain ON supply_chain_nodes(chain_id);
            CREATE INDEX IF NOT EXISTS idx_sc_nodes_parent ON supply_chain_nodes(parent_node_id);
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;";
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- Train cache ----

    public TrainAvailabilityDto GetTrainAvailability(string surface) {
        if (trainCache.TryGetValue(surface, out var a)) return a;
        return new TrainAvailabilityDto();
    }

    public void RefreshTrainCache(string surface, JsonElement stationsPayload) {
        var provides = new Dictionary<string, TrainItemInfo>(StringComparer.Ordinal);
        var requests = new Dictionary<string, TrainItemInfo>(StringComparer.Ordinal);

        if (stationsPayload.TryGetProperty("stations", out JsonElement stations)
            && stations.ValueKind == JsonValueKind.Object) {
            foreach (JsonProperty st in stations.EnumerateObject()) {
                var label = StationNames.Parse(st.Name);
                if (label.Item == null || !label.HasRole) continue;
                int cnt = 0;
                try { cnt = st.Value.GetProperty("stops").GetInt32(); } catch { }
                var info = new TrainItemInfo { ItemType = label.Type, StationCount = cnt };
                if (label.Provides) provides.TryAdd(label.Item, info);
                if (label.Requests) requests.TryAdd(label.Item, info);
            }
        }

        trainCache[surface] = new TrainAvailabilityDto { Provides = provides, Requests = requests };
    }

    // ---- Chains CRUD ----

    public async Task<List<SupplyChainDto>> ListAsync() {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, target_item, recipe_name, name, surface, target_per_min, created_at, updated_at FROM supply_chains ORDER BY updated_at DESC";
        var list = new List<SupplyChainDto>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) {
            list.Add(new SupplyChainDto {
                Id = r.GetInt32(0),
                TargetItem = r.GetString(1),
                RecipeName = r.GetString(2),
                Name = r.IsDBNull(3) ? null : r.GetString(3),
                Surface = r.GetString(4),
                TargetPerMin = r.IsDBNull(5) ? null : r.GetDouble(5),
                CreatedAt = r.GetString(6),
                UpdatedAt = r.GetString(7)
            });
        }
        return list;
    }

    public async Task<SupplyChainDto?> GetAsync(int id) {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, target_item, recipe_name, name, surface, target_per_min, created_at, updated_at FROM supply_chains WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        var chain = new SupplyChainDto {
            Id = r.GetInt32(0),
            TargetItem = r.GetString(1),
            RecipeName = r.GetString(2),
            Name = r.IsDBNull(3) ? null : r.GetString(3),
            Surface = r.GetString(4),
            TargetPerMin = r.IsDBNull(5) ? null : r.GetDouble(5),
            CreatedAt = r.GetString(6),
            UpdatedAt = r.GetString(7)
        };
        r.Close();

        var flat = await LoadNodesFlatAsync(conn, id);
        chain.Nodes = BuildTreeFromFlat(flat);
        return chain;
    }

    public async Task<SupplyChainDto> CreateAsync(string targetItem, string recipeName, string surface, double? targetPerMin) {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO supply_chains (target_item, recipe_name, surface, target_per_min, created_at, updated_at)
            VALUES ($t, $r, $s, $p, datetime('now'), datetime('now'));
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$t", targetItem);
        cmd.Parameters.AddWithValue("$r", recipeName);
        cmd.Parameters.AddWithValue("$s", surface);
        cmd.Parameters.AddWithValue("$p", targetPerMin != null ? targetPerMin.Value : DBNull.Value);
        long id = (long)(await cmd.ExecuteScalarAsync())!;
        return new SupplyChainDto {
            Id = (int)id,
            TargetItem = targetItem,
            RecipeName = recipeName,
            Surface = surface,
            TargetPerMin = targetPerMin,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            UpdatedAt = DateTime.UtcNow.ToString("o")
        };
    }

    public async Task<bool> UpdateAsync(int id, string? name, string? targetItem, string? recipeName, double? targetPerMin) {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        // Build dynamic SET
        var sets = new List<string> { "updated_at = datetime('now')" };
        var parms = new List<SqliteParameter>();
        parms.Add(new SqliteParameter("$id", id));

        if (name != null) { sets.Add("name = $n"); parms.Add(new SqliteParameter("$n", name)); }
        if (targetItem != null) { sets.Add("target_item = $ti"); parms.Add(new SqliteParameter("$ti", targetItem)); }
        if (recipeName != null) { sets.Add("recipe_name = $r"); parms.Add(new SqliteParameter("$r", recipeName)); }
        if (targetPerMin != null) { sets.Add("target_per_min = $p"); parms.Add(new SqliteParameter("$p", targetPerMin.Value)); }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE supply_chains SET {string.Join(", ", sets)} WHERE id = $id";
        cmd.Parameters.AddRange(parms.ToArray());
        int rows = await cmd.ExecuteNonQueryAsync();
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(int id) {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM supply_chain_nodes WHERE chain_id = $id; DELETE FROM supply_chains WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        int rows = await cmd.ExecuteNonQueryAsync();
        return rows > 0;
    }

    public async Task<bool> ReplaceNodesAsync(int chainId, List<SupplyNodeDto> nodes) {
        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM supply_chain_nodes WHERE chain_id = $cid";
        cmd.Parameters.AddWithValue("$cid", chainId);
        await cmd.ExecuteNonQueryAsync();

        await InsertNodesAsync(conn, chainId, null, nodes);

        cmd.CommandText = "UPDATE supply_chains SET updated_at = datetime('now') WHERE id = $cid";
        await cmd.ExecuteNonQueryAsync();

        await tx.CommitAsync();
        return true;
    }

    // ---- Recipe info ----

    public List<RecipeOptionDto> GetRecipesForItem(string itemName, string surface) {
        if (!proto.Loaded) return new List<RecipeOptionDto>();

        // Get productions for this surface from the bus
        JsonElement? productionJson = null;
        try {
            var snap = bus.Latest($"production@{surface}");
            if (snap != null) productionJson = snap.Payload;
        } catch { }

        JsonElement? assemblersJson = null;
        try {
            var snap = bus.Latest($"assemblers@{surface}");
            if (snap != null) assemblersJson = snap.Payload;
        } catch { }

        var producedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in proto.Recipes.Values) {
            foreach (var p in r.Products) {
                if (!producedBy.TryGetValue(p.Name, out var list)) {
                    list = new List<string>();
                    producedBy[p.Name] = list;
                }
                if (!list.Contains(r.Name)) list.Add(r.Name);
            }
        }

        if (!producedBy.TryGetValue(itemName, out var recipeNames)) return new List<RecipeOptionDto>();

        var result = new List<RecipeOptionDto>();
        foreach (var name in recipeNames) {
            if (!proto.Recipes.TryGetValue(name, out var rp)) continue;

            // Count machines running this recipe
            int machineCount = 0;
            if (assemblersJson is JsonElement asm) {
                foreach (JsonProperty g in asm.EnumerateObject()) {
                    if (g.Name == "by_item" && g.Value.ValueKind == JsonValueKind.Object) {
                        foreach (JsonProperty byItem in g.Value.EnumerateObject()) {
                            if (byItem.Value.TryGetProperty("recipes", out JsonElement recipes)
                                && recipes.ValueKind == JsonValueKind.Object) {
                                foreach (JsonProperty rec in recipes.EnumerateObject()) {
                                    if (rec.Name == name) machineCount += (int)rec.Value.GetDouble();
                                }
                            }
                        }
                    }
                }
            }

            bool isProd = false;
            if (productionJson is JsonElement prod && prod.TryGetProperty("items", out JsonElement items)
                && items.ValueKind == JsonValueKind.Array) {
                foreach (JsonElement it in items.EnumerateArray()) {
                    if (it.TryGetProperty("item", out JsonElement itm) && itm.GetString() == itemName
                        && it.TryGetProperty("produced_per_min", out JsonElement ppm) && ppm.GetDouble() > 0) {
                        isProd = true;
                        break;
                    }
                }
            }

            result.Add(new RecipeOptionDto {
                Name = rp.Name,
                Energy = rp.EnergyRequired,
                Category = rp.Category,
                AllowProductivity = rp.AllowProductivity,
                IsProducing = isProd,
                MachineCount = machineCount,
                Ingredients = rp.Ingredients.Select(i => new RecipeIoDto { Name = i.Name, Type = i.Type, Amount = i.Amount }).ToList(),
                Products = rp.Products.Select(p => new RecipeIoDto { Name = p.Name, Type = p.Type, Amount = p.Amount }).ToList()
            });
        }

        result.Sort((a, b) => {
            int c = b.MachineCount.CompareTo(a.MachineCount);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });

        return result;
    }

    // ---- Private helpers ----

    private async Task<List<(int? Id, int? ParentId, SupplyNodeDto Node)>> LoadNodesFlatAsync(SqliteConnection conn, int chainId) {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, parent_node_id, item_name, item_type, amount_per_craft, source, child_recipe, sort_order
            FROM supply_chain_nodes WHERE chain_id = $cid ORDER BY sort_order";
        cmd.Parameters.AddWithValue("$cid", chainId);
        var list = new List<(int?, int?, SupplyNodeDto)>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) {
            var node = new SupplyNodeDto {
                Id = r.GetInt32(0),
                ItemName = r.GetString(2),
                ItemType = r.GetString(3),
                AmountPerCraft = r.GetDouble(4),
                Source = r.GetString(5),
                ChildRecipe = r.IsDBNull(6) ? null : r.GetString(6),
                Children = new List<SupplyNodeDto>()
            };
            int? parentId = r.IsDBNull(1) ? null : r.GetInt32(1);
            list.Add((node.Id, parentId, node));
        }
        return list;
    }

    private List<SupplyNodeDto> BuildTreeFromFlat(List<(int? Id, int? ParentId, SupplyNodeDto Node)> flat) {
        var roots = new List<SupplyNodeDto>();
        var byId = new Dictionary<int, SupplyNodeDto>();
        foreach (var (id, parentId, node) in flat) {
            if (id.HasValue) byId[id.Value] = node;
            if (parentId == null) roots.Add(node);
        }
        foreach (var (id, parentId, node) in flat) {
            if (parentId.HasValue && byId.TryGetValue(parentId.Value, out var parent)) {
                parent.Children.Add(node);
            }
        }
        return roots;
    }

    private async Task InsertNodesAsync(SqliteConnection conn, int chainId, long? parentId, List<SupplyNodeDto> nodes) {
        for (int i = 0; i < nodes.Count; i++) {
            var n = nodes[i];
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO supply_chain_nodes (chain_id, parent_node_id, item_name, item_type, amount_per_craft, source, child_recipe, sort_order)
                VALUES ($cid, $pid, $n, $t, $a, $s, $r, $o);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$cid", chainId);
            cmd.Parameters.AddWithValue("$pid", parentId != null ? (object)parentId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$n", n.ItemName);
            cmd.Parameters.AddWithValue("$t", n.ItemType);
            cmd.Parameters.AddWithValue("$a", n.AmountPerCraft);
            cmd.Parameters.AddWithValue("$s", n.Source);
            cmd.Parameters.AddWithValue("$r", n.ChildRecipe != null ? (object)n.ChildRecipe : DBNull.Value);
            cmd.Parameters.AddWithValue("$o", i);
            long newId = (long)(await cmd.ExecuteScalarAsync())!;

            if (n.Children.Count > 0) {
                await InsertNodesAsync(conn, chainId, newId, n.Children);
            }
        }
    }
}
