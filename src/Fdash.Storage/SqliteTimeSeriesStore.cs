using Fdash.Core;
using Microsoft.Data.Sqlite;

namespace Fdash.Storage;

/// <summary>
/// SQLite-basierter Zeitreihen-Speicher mit manuellem Roll-up (Plan §5.5).
/// Vier Tiers als eigene Tabellen; Aggregat avg/min/max. WITHOUT ROWID
/// fuer kompakten zusammengesetzten Primaerschluessel.
/// </summary>
public sealed class SqliteTimeSeriesStore : ITimeSeriesStore {
    private readonly string connString;

    // (Tabelle, Bucket-Sekunden, Retention-Sekunden)  (§5.5)
    private static readonly (string Table, long Bucket, long Retention)[] Tiers = new[] {
        ("samples_raw",     5L,    6L * 3600),
        ("samples_min",     60L,   7L * 86400),
        ("samples_quarter", 900L,  90L * 86400),
        ("samples_hour",    3600L, long.MaxValue)
    };

    public SqliteTimeSeriesStore(StorageOptions options) {
        connString = new SqliteConnectionStringBuilder {
            DataSource = options.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct) {
        using(SqliteConnection conn = open()) {
            await conn.OpenAsync(ct);
            foreach((string table, long _, long __) in Tiers) {
                await exec(conn, $@"
                    CREATE TABLE IF NOT EXISTS {table} (
                        save_id TEXT NOT NULL,
                        metric  TEXT NOT NULL,
                        labels  TEXT NOT NULL,
                        ts      INTEGER NOT NULL,
                        value   REAL NOT NULL,
                        vmin    REAL NOT NULL DEFAULT 0,
                        vmax    REAL NOT NULL DEFAULT 0,
                        PRIMARY KEY (save_id, metric, labels, ts)
                    ) WITHOUT ROWID;", ct);
            }
            await exec(conn, "PRAGMA journal_mode=WAL;", ct);
        }
    }

    public async Task WriteAsync(IReadOnlyCollection<Sample> samples, CancellationToken ct) {
        if(samples.Count == 0) return;
        using(SqliteConnection conn = open()) {
            await conn.OpenAsync(ct);
            using(SqliteTransaction tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct)) {
                using(SqliteCommand cmd = conn.CreateCommand()) {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT OR REPLACE INTO samples_raw
                        (save_id, metric, labels, ts, value, vmin, vmax)
                        VALUES ($s, $m, $l, $t, $v, $v, $v);";
                    SqliteParameter ps = cmd.Parameters.Add("$s", SqliteType.Text);
                    SqliteParameter pm = cmd.Parameters.Add("$m", SqliteType.Text);
                    SqliteParameter pl = cmd.Parameters.Add("$l", SqliteType.Text);
                    SqliteParameter pt = cmd.Parameters.Add("$t", SqliteType.Integer);
                    SqliteParameter pv = cmd.Parameters.Add("$v", SqliteType.Real);
                    foreach(Sample s in samples) {
                        ps.Value = s.SaveId; pm.Value = s.Metric; pl.Value = s.Labels;
                        pt.Value = s.Ts; pv.Value = s.Value;
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                }
                await tx.CommitAsync(ct);
            }
        }
    }

    public async Task<IReadOnlyList<Sample>> QueryAsync(string saveId, string metric, string? labels,
        long fromTs, long toTs, Resolution resolution, CancellationToken ct) {
        string table = tableFor(resolution);
        List<Sample> result = new List<Sample>();
        using(SqliteConnection conn = open()) {
            await conn.OpenAsync(ct);
            using(SqliteCommand cmd = conn.CreateCommand()) {
                cmd.CommandText = $@"SELECT save_id, metric, labels, ts, value FROM {table}
                    WHERE save_id=$s AND metric=$m AND ts BETWEEN $from AND $to
                    {(labels != null ? "AND labels=$l" : "")}
                    ORDER BY ts;";
                cmd.Parameters.AddWithValue("$s", saveId);
                cmd.Parameters.AddWithValue("$m", metric);
                cmd.Parameters.AddWithValue("$from", fromTs);
                cmd.Parameters.AddWithValue("$to", toTs);
                if(labels != null) cmd.Parameters.AddWithValue("$l", labels);
                using(SqliteDataReader r = (SqliteDataReader)await cmd.ExecuteReaderAsync(ct)) {
                    while(await r.ReadAsync(ct)) {
                        result.Add(new Sample(r.GetString(0), r.GetString(1), r.GetString(2),
                            r.GetInt64(3), r.GetDouble(4)));
                    }
                }
            }
        }
        return result;
    }

    public async Task RollupAsync(CancellationToken ct) {
        // Jede grobe Stufe aus der jeweils feineren aggregieren (§5.5).
        using(SqliteConnection conn = open()) {
            await conn.OpenAsync(ct);
            for(int i = 1; i < Tiers.Length; i++) {
                string src = Tiers[i - 1].Table;
                string dst = Tiers[i].Table;
                long bucket = Tiers[i].Bucket;
                await exec(conn, $@"
                    INSERT OR REPLACE INTO {dst} (save_id, metric, labels, ts, value, vmin, vmax)
                    SELECT save_id, metric, labels, (ts / {bucket}) * {bucket} AS b,
                           AVG(value), MIN(vmin), MAX(vmax)
                    FROM {src}
                    GROUP BY save_id, metric, labels, b;", ct);
            }
        }
    }

    public async Task PruneAsync(CancellationToken ct) {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using(SqliteConnection conn = open()) {
            await conn.OpenAsync(ct);
            foreach((string table, long _, long retention) in Tiers) {
                if(retention == long.MaxValue) continue;
                await exec(conn, $"DELETE FROM {table} WHERE ts < {now - retention};", ct);
            }
        }
    }

    private static string tableFor(Resolution r) {
        switch(r) {
            case Resolution.Raw: return "samples_raw";
            case Resolution.Minute: return "samples_min";
            case Resolution.Quarter: return "samples_quarter";
            case Resolution.Hour: return "samples_hour";
        }
        return "samples_raw";
    }

    private SqliteConnection open() => new SqliteConnection(connString);

    private static async Task exec(SqliteConnection conn, string sql, CancellationToken ct) {
        using(SqliteCommand cmd = conn.CreateCommand()) {
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
