using System.Collections.Concurrent;
using System.Text.Json;
using LinkCleaner.Core;
using Microsoft.Data.Sqlite;

namespace LinkCleaner.Bot;

/// <summary>
/// SQLite-backed per-guild configuration. Stores settings only, never message content.
/// Reads come from an in-memory snapshot that is rebuilt after every write.
/// </summary>
public sealed class ConfigStore
{
    public const string DefaultInfoLink =
        "https://medium.com/@ian-darwin/you-are-sharing-urls-with-tracking-links-please-stop-502c6f54895";

    public const string DefaultMirrorPrefix = "🪞";

    public const string InfoLinkKey = "info_link";
    public const string MirrorPrefixKey = "mirror_prefix";
    public const string ModLogKey = "modlog_channel";

    public const string IncludeKind = "include";
    public const string ExcludeKind = "exclude";

    private readonly string _connectionString;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<ulong, GuildConfig> _cache = new();

    public ConfigStore(string databasePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

        lock (_gate)
        {
            using var c = Open();
            Exec(c, """
                CREATE TABLE IF NOT EXISTS sites (
                    guild_id INTEGER NOT NULL, name TEXT NOT NULL, json TEXT NOT NULL,
                    PRIMARY KEY (guild_id, name));
                CREATE TABLE IF NOT EXISTS settings (
                    guild_id INTEGER NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL,
                    PRIMARY KEY (guild_id, key));
                CREATE TABLE IF NOT EXISTS channel_filters (
                    guild_id INTEGER NOT NULL, channel_id INTEGER NOT NULL, kind TEXT NOT NULL,
                    PRIMARY KEY (guild_id, channel_id));
                CREATE TABLE IF NOT EXISTS exempt_roles (
                    guild_id INTEGER NOT NULL, role_id INTEGER NOT NULL,
                    PRIMARY KEY (guild_id, role_id));
                """);
        }
    }

    public GuildConfig Get(ulong guildId) => _cache.GetOrAdd(guildId, Load);

    // ---- Sites -------------------------------------------------------------------------

    public void SaveSite(ulong guildId, SiteRule rule) => Mutate(guildId, c =>
        Exec(c, "INSERT OR REPLACE INTO sites (guild_id, name, json) VALUES ($g, $n, $j)",
            ("$g", (long)guildId), ("$n", rule.Name), ("$j", JsonSerializer.Serialize(rule))));

    public void DeleteSite(ulong guildId, string name) => Mutate(guildId, c =>
        Exec(c, "DELETE FROM sites WHERE guild_id = $g AND name = $n", ("$g", (long)guildId), ("$n", name)));

    // ---- Settings ----------------------------------------------------------------------

    /// <summary>Sets a setting; null removes it so the default applies again.</summary>
    public void SetSetting(ulong guildId, string key, string? value) => Mutate(guildId, c =>
    {
        if (value is null)
            Exec(c, "DELETE FROM settings WHERE guild_id = $g AND key = $k", ("$g", (long)guildId), ("$k", key));
        else
            Exec(c, "INSERT OR REPLACE INTO settings (guild_id, key, value) VALUES ($g, $k, $v)",
                ("$g", (long)guildId), ("$k", key), ("$v", value));
    });

    // ---- Channels and roles ------------------------------------------------------------

    public void SetChannelFilter(ulong guildId, ulong channelId, string kind) => Mutate(guildId, c =>
        Exec(c, "INSERT OR REPLACE INTO channel_filters (guild_id, channel_id, kind) VALUES ($g, $c, $k)",
            ("$g", (long)guildId), ("$c", (long)channelId), ("$k", kind)));

    public void RemoveChannelFilter(ulong guildId, ulong channelId) => Mutate(guildId, c =>
        Exec(c, "DELETE FROM channel_filters WHERE guild_id = $g AND channel_id = $c",
            ("$g", (long)guildId), ("$c", (long)channelId)));

    public void ClearChannelFilters(ulong guildId) => Mutate(guildId, c =>
        Exec(c, "DELETE FROM channel_filters WHERE guild_id = $g", ("$g", (long)guildId)));

    public void AddExemptRole(ulong guildId, ulong roleId) => Mutate(guildId, c =>
        Exec(c, "INSERT OR IGNORE INTO exempt_roles (guild_id, role_id) VALUES ($g, $r)",
            ("$g", (long)guildId), ("$r", (long)roleId)));

    public void RemoveExemptRole(ulong guildId, ulong roleId) => Mutate(guildId, c =>
        Exec(c, "DELETE FROM exempt_roles WHERE guild_id = $g AND role_id = $r",
            ("$g", (long)guildId), ("$r", (long)roleId)));

    // ---- Internals ---------------------------------------------------------------------

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Mutate(ulong guildId, Action<SqliteConnection> write)
    {
        lock (_gate)
        {
            using var c = Open();
            EnsureSeeded(c, guildId);
            write(c);
            _cache.TryRemove(guildId, out _);
        }
    }

    /// <summary>Copies the default sites into a guild the first time it is seen (never again, so deletions stick).</summary>
    private static void EnsureSeeded(SqliteConnection c, ulong guildId)
    {
        var seeded = Scalar(c, "SELECT 1 FROM settings WHERE guild_id = $g AND key = 'seeded'", ("$g", (long)guildId));
        if (seeded is not null)
            return;

        using var tx = c.BeginTransaction();
        foreach (var rule in DefaultSites.Create())
            Exec(c, "INSERT OR IGNORE INTO sites (guild_id, name, json) VALUES ($g, $n, $j)",
                ("$g", (long)guildId), ("$n", rule.Name), ("$j", JsonSerializer.Serialize(rule)));
        Exec(c, "INSERT OR REPLACE INTO settings (guild_id, key, value) VALUES ($g, 'seeded', '1')", ("$g", (long)guildId));
        tx.Commit();
    }

    private GuildConfig Load(ulong guildId)
    {
        lock (_gate)
        {
            using var c = Open();
            EnsureSeeded(c, guildId);
            var g = (long)guildId;

            var sites = Query(c, "SELECT json FROM sites WHERE guild_id = $g ORDER BY name", r => r.GetString(0), ("$g", g))
                .Select(j => JsonSerializer.Deserialize<SiteRule>(j)!)
                .ToList();

            var settings = Query(c, "SELECT key, value FROM settings WHERE guild_id = $g",
                    r => (Key: r.GetString(0), Value: r.GetString(1)), ("$g", g))
                .ToDictionary(x => x.Key, x => x.Value);

            var filters = Query(c, "SELECT channel_id, kind FROM channel_filters WHERE guild_id = $g",
                r => (Id: (ulong)r.GetInt64(0), Kind: r.GetString(1)), ("$g", g)).ToList();

            var roles = Query(c, "SELECT role_id FROM exempt_roles WHERE guild_id = $g",
                r => (ulong)r.GetInt64(0), ("$g", g)).ToHashSet();

            return new GuildConfig(
                sites,
                settings.GetValueOrDefault(InfoLinkKey) ?? DefaultInfoLink,
                settings.GetValueOrDefault(MirrorPrefixKey) ?? DefaultMirrorPrefix,
                settings.TryGetValue(ModLogKey, out var modlog) && ulong.TryParse(modlog, out var modlogId) ? modlogId : null,
                filters.Where(f => f.Kind == IncludeKind).Select(f => f.Id).ToHashSet(),
                filters.Where(f => f.Kind == ExcludeKind).Select(f => f.Id).ToHashSet(),
                roles);
        }
    }

    private static void Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    private static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map,
        params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
            rows.Add(map(reader));
        return rows;
    }
}
