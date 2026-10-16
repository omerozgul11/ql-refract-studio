using System.Text.Json;
using Microsoft.Data.Sqlite;
using RefractionStudio.Domain;

namespace RefractionStudio.Persistence;

/// <summary>A recently used project.</summary>
public sealed record RecentProject(string Path, string Name, DateTimeOffset OpenedAt, bool Pinned);

/// <summary>A reusable spread / instrument profile (channel roles and layout).</summary>
/// <param name="Id">Identifier.</param>
/// <param name="Name">Name, e.g. "QuakeLogic 24-ch refraction spread".</param>
/// <param name="Description">Description.</param>
/// <param name="ChannelCount">Number of channels.</param>
/// <param name="Spacing">Default geophone interval (m).</param>
/// <param name="Direction">Channel direction.</param>
/// <param name="Channels">Channel roles (positions are relative: XOverride is an offset from the first receiver).</param>
public sealed record SpreadProfile(Guid Id, string Name, string? Description, int ChannelCount, double Spacing, ChannelDirection Direction, ImmutableArray<ChannelAssignment> Channels)
{
    /// <summary>Built-in profile of a 24-channel spread plus a reference (shot) geophone on channel 25.</summary>
    public static SpreadProfile QuakeLogic24PlusReference { get; } = new(
        Guid.Parse("5c1b33e0-0a77-4a5e-9a32-6f2f6b3a1d24"),
        "QuakeLogic 24-ch spread + shot geophone",
        "Channels 1–24 receivers in increasing x, channel 25 reference geophone at the shot.",
        25, 2.0, ChannelDirection.Increasing,
        [.. Enumerable.Range(1, 24).Select(c => new ChannelAssignment(c, ChannelRole.Receiver)), new ChannelAssignment(25, ChannelRole.Reference)]);

    /// <summary>Built-in profile of a plain 48-channel spread.</summary>
    public static SpreadProfile Plain48 { get; } = new(
        Guid.Parse("a3f6a3c4-8fd2-4bb3-9b47-3c0d0c48a048"),
        "48-ch spread",
        "Channels 1–48 receivers in increasing x.",
        48, 2.0, ChannelDirection.Increasing,
        [.. Enumerable.Range(1, 48).Select(c => new ChannelAssignment(c, ChannelRole.Receiver))]);
}

/// <summary>A saved report template (branding and section choices).</summary>
public sealed record ReportTemplate(Guid Id, string Name, CompanyBranding Branding, ImmutableArray<ReportSection> Sections);

/// <summary>
/// Per-user SQLite catalogue: recent projects, spread profiles and report templates. Never stores
/// project data; deleting it loses only conveniences.
/// </summary>
public sealed class AppCatalog
{
    private const int MaxRecent = 20;
    private readonly string _connectionString;

    public AppCatalog(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS recent_projects (path TEXT PRIMARY KEY, name TEXT NOT NULL, opened_at TEXT NOT NULL, pinned INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS spread_profiles (id TEXT PRIMARY KEY, name TEXT NOT NULL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS report_templates (id TEXT PRIMARY KEY, name TEXT NOT NULL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);
            INSERT INTO schema_info (version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_info);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public async Task AddRecentAsync(string path, string name, DateTimeOffset when, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO recent_projects (path, name, opened_at, pinned) VALUES ($p, $n, $t, 0)
            ON CONFLICT(path) DO UPDATE SET name = excluded.name, opened_at = excluded.opened_at;
            DELETE FROM recent_projects WHERE pinned = 0 AND path NOT IN (SELECT path FROM recent_projects ORDER BY pinned DESC, opened_at DESC LIMIT $max);
            """;
        cmd.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$t", when.ToString("O"));
        cmd.Parameters.AddWithValue("$max", MaxRecent);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecentProject>> RecentAsync(CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, name, opened_at, pinned FROM recent_projects ORDER BY pinned DESC, opened_at DESC";
        var list = new List<RecentProject>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new RecentProject(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2), System.Globalization.CultureInfo.InvariantCulture), r.GetInt32(3) != 0));
        return list;
    }

    public async Task SetPinnedAsync(string path, bool pinned, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE recent_projects SET pinned = $v WHERE path = $p";
        cmd.Parameters.AddWithValue("$v", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveRecentAsync(string path, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM recent_projects WHERE path = $p";
        cmd.Parameters.AddWithValue("$p", Path.GetFullPath(path));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public Task SaveSpreadProfileAsync(SpreadProfile profile, CancellationToken ct = default)
        => UpsertAsync("spread_profiles", profile.Id, profile.Name, JsonSerializer.Serialize(profile, DomainJson.Compact), ct);

    public async Task<IReadOnlyList<SpreadProfile>> SpreadProfilesAsync(CancellationToken ct = default)
    {
        var stored = await ListAsync<SpreadProfile>("spread_profiles", ct).ConfigureAwait(false);
        var builtIn = new[] { SpreadProfile.QuakeLogic24PlusReference, SpreadProfile.Plain48 }.Where(b => stored.All(s => s.Id != b.Id));
        return [.. builtIn, .. stored];
    }

    public Task DeleteSpreadProfileAsync(Guid id, CancellationToken ct = default) => DeleteAsync("spread_profiles", id, ct);

    public Task SaveReportTemplateAsync(ReportTemplate template, CancellationToken ct = default)
        => UpsertAsync("report_templates", template.Id, template.Name, JsonSerializer.Serialize(template, DomainJson.Compact), ct);

    public Task<IReadOnlyList<ReportTemplate>> ReportTemplatesAsync(CancellationToken ct = default) => ListAsync<ReportTemplate>("report_templates", ct);

    public Task DeleteReportTemplateAsync(Guid id, CancellationToken ct = default) => DeleteAsync("report_templates", id, ct);

    private async Task UpsertAsync(string table, Guid id, string name, string json, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO {table} (id, name, json) VALUES ($i, $n, $j) ON CONFLICT(id) DO UPDATE SET name = excluded.name, json = excluded.json";
        cmd.Parameters.AddWithValue("$i", id.ToString("N"));
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$j", json);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> ListAsync<T>(string table, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT json FROM {table} ORDER BY name";
        var list = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            try
            {
                if (JsonSerializer.Deserialize<T>(r.GetString(0), DomainJson.Options) is { } item) list.Add(item);
            }
            catch (JsonException)
            {
                // A damaged entry is skipped; the catalogue only holds conveniences.
            }
        }
        return list;
    }

    private async Task DeleteAsync(string table, Guid id, CancellationToken ct)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE id = $i";
        cmd.Parameters.AddWithValue("$i", id.ToString("N"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
