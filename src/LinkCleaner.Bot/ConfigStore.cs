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
    public const string PingAuthorKey = "ping_author";
    public const string GlobalEnabledKey = "global_enabled";
    public const string GlobalParamsKey = "global_params";

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
                CREATE TABLE IF NOT EXISTS deleted_defaults (
                    guild_id INTEGER NOT NULL, name TEXT NOT NULL,
                    PRIMARY KEY (guild_id, name));
                CREATE TABLE IF NOT EXISTS ping_optouts (
                    guild_id INTEGER NOT NULL, user_id INTEGER NOT NULL,
                    PRIMARY KEY (guild_id, user_id));
                """);
        }
    }

    public GuildConfig Get(ulong guildId) => _cache.GetOrAdd(guildId, Load);

    // ---- Sites -------------------------------------------------------------------------

    /// <param name="customises">True for admin edits (built-in updates then leave the site alone). Enable/disable and reset pass false.</param>
    public void SaveSite(ulong guildId, SiteRule rule, bool customises = true) => Mutate(guildId, c =>
    {
        var stored = customises ? rule with { Customised = true } : rule;
        Exec(c, "INSERT OR REPLACE INTO sites (guild_id, name, json) VALUES ($g, $n, $j)",
            ("$g", (long)guildId), ("$n", rule.Name), ("$j", JsonSerializer.Serialize(stored)));
        Exec(c, "DELETE FROM deleted_defaults WHERE guild_id = $g AND name = $n", ("$g", (long)guildId), ("$n", rule.Name));
    });

    public void DeleteSite(ulong guildId, string name) => Mutate(guildId, c =>
    {
        Exec(c, "DELETE FROM sites WHERE guild_id = $g AND name = $n", ("$g", (long)guildId), ("$n", name));
        // Remember deleted built-ins so a later default update doesn't bring them back.
        if (DefaultSites.Create().Any(s => s.Name == name))
            Exec(c, "INSERT OR IGNORE INTO deleted_defaults (guild_id, name) VALUES ($g, $n)", ("$g", (long)guildId), ("$n", name));
    });

    /// <summary>
    /// Applies a validated import in one transaction. Sites are merged by name (the file wins), or with
    /// <paramref name="replace"/> the server's sites become exactly the file's. Server-wide settings always come from the file.
    /// </summary>
    public void ImportBundle(ulong guildId, ConfigBundle bundle, bool replace) => Mutate(guildId, c =>
    {
        var g = (long)guildId;
        using var tx = c.BeginTransaction();

        if (replace)
        {
            Exec(c, "DELETE FROM sites WHERE guild_id = $g", ("$g", g));
            Exec(c, "DELETE FROM deleted_defaults WHERE guild_id = $g", ("$g", g));
        }

        foreach (var site in bundle.Sites)
        {
            Exec(c, "INSERT OR REPLACE INTO sites (guild_id, name, json) VALUES ($g, $n, $j)",
                ("$g", g), ("$n", site.Name), ("$j", JsonSerializer.Serialize(site)));
            Exec(c, "DELETE FROM deleted_defaults WHERE guild_id = $g AND name = $n", ("$g", g), ("$n", site.Name));
        }

        if (replace)
            foreach (var def in DefaultSites.Create().Where(d => bundle.Sites.All(s => s.Name != d.Name)))
                Exec(c, "INSERT OR IGNORE INTO deleted_defaults (guild_id, name) VALUES ($g, $n)", ("$g", g), ("$n", def.Name));

        void Put(string key, string value) => Exec(c, "INSERT OR REPLACE INTO settings (guild_id, key, value) VALUES ($g, $k, $v)",
            ("$g", g), ("$k", key), ("$v", value));
        Put(InfoLinkKey, bundle.InfoLink);
        Put(MirrorPrefixKey, bundle.MirrorPrefix);
        Put(PingAuthorKey, bundle.PingAuthor ? "1" : "0");
        Put(GlobalEnabledKey, bundle.Global.Enabled ? "1" : "0");
        Put(GlobalParamsKey, JsonSerializer.Serialize(bundle.Global.StripParams));

        tx.Commit();
    });

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

    public void AddPingOptOut(ulong guildId, ulong userId) => Mutate(guildId, c =>
        Exec(c, "INSERT OR IGNORE INTO ping_optouts (guild_id, user_id) VALUES ($g, $u)",
            ("$g", (long)guildId), ("$u", (long)userId)));

    public void RemovePingOptOut(ulong guildId, ulong userId) => Mutate(guildId, c =>
        Exec(c, "DELETE FROM ping_optouts WHERE guild_id = $g AND user_id = $u",
            ("$g", (long)guildId), ("$u", (long)userId)));

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

    public const string DefaultsRevisionKey = "defaults_revision";

    /// <summary>
    /// Copies the default sites into a guild the first time it is seen, and rolls later default
    /// revisions out to it (once per revision). Deleted built-ins stay deleted; edited sites are untouched.
    /// </summary>
    private static void EnsureSeeded(SqliteConnection c, ulong guildId)
    {
        var g = (long)guildId;
        var seeded = Scalar(c, "SELECT 1 FROM settings WHERE guild_id = $g AND key = 'seeded'", ("$g", g)) is not null;
        var revision = Scalar(c, "SELECT value FROM settings WHERE guild_id = $g AND key = $k", ("$g", g), ("$k", DefaultsRevisionKey))
            is string text && int.TryParse(text, out var parsed) ? parsed : 0;
        if (seeded && revision >= DefaultSites.Revision)
            return;

        using var tx = c.BeginTransaction();
        if (!seeded)
            foreach (var rule in DefaultSites.Create())
                Exec(c, "INSERT OR IGNORE INTO sites (guild_id, name, json) VALUES ($g, $n, $j)",
                    ("$g", g), ("$n", rule.Name), ("$j", JsonSerializer.Serialize(rule)));
        else
            ApplyDefaultUpdates(c, g, legacy: revision == 0);

        Exec(c, "INSERT OR REPLACE INTO settings (guild_id, key, value) VALUES ($g, 'seeded', '1')", ("$g", g));
        Exec(c, "INSERT OR REPLACE INTO settings (guild_id, key, value) VALUES ($g, $k, $v)",
            ("$g", g), ("$k", DefaultsRevisionKey), ("$v", DefaultSites.Revision.ToString()));
        tx.Commit();
    }

    /// <param name="legacy">The guild was seeded before revisions existed, so a missing original built-in was deleted by an admin.</param>
    private static void ApplyDefaultUpdates(SqliteConnection c, long g, bool legacy)
    {
        var stored = Query(c, "SELECT json FROM sites WHERE guild_id = $g", r => r.GetString(0), ("$g", g))
            .Select(j => JsonSerializer.Deserialize<SiteRule>(j)!)
            .ToDictionary(s => s.Name);
        var deleted = Query(c, "SELECT name FROM deleted_defaults WHERE guild_id = $g", r => r.GetString(0), ("$g", g)).ToHashSet();

        void Save(SiteRule rule) => Exec(c, "INSERT OR REPLACE INTO sites (guild_id, name, json) VALUES ($g, $n, $j)",
            ("$g", g), ("$n", rule.Name), ("$j", JsonSerializer.Serialize(rule)));

        foreach (var def in DefaultSites.Create())
        {
            if (!stored.TryGetValue(def.Name, out var current))
            {
                if (legacy && DefaultSites.OriginalNames.Contains(def.Name))
                    Exec(c, "INSERT OR IGNORE INTO deleted_defaults (guild_id, name) VALUES ($g, $n)", ("$g", g), ("$n", def.Name));
                else if (!deleted.Contains(def.Name))
                    Save(def);
                continue;
            }

            if (current.Customised)
                continue;

            // A row stored before revisions existed counts as unedited only if it is a known earlier default.
            if (current.DefaultRevision == 0 && !MatchesEarlierDefault(current, def))
            {
                Save(current with { Customised = true });
                continue;
            }

            Save(def with { Enabled = current.Enabled });
        }
    }

    /// <summary>Revision 1 equals today's definition of the original sites, except YouTube then lacked "is".</summary>
    private static bool MatchesEarlierDefault(SiteRule stored, SiteRule current)
    {
        static bool Same(SiteRule a, SiteRule b) =>
            a.Hosts.SequenceEqual(b.Hosts) && a.StripParams.SequenceEqual(b.StripParams) &&
            a.StripPathPatterns.SequenceEqual(b.StripPathPatterns) &&
            a.MirrorHost == b.MirrorHost && a.MirrorPathPattern == b.MirrorPathPattern;

        return Same(stored, current) ||
               Same(stored, current with { StripParams = current.StripParams.Where(p => p != "is").ToList() });
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

            var optOuts = Query(c, "SELECT user_id FROM ping_optouts WHERE guild_id = $g",
                r => (ulong)r.GetInt64(0), ("$g", g)).ToHashSet();

            return new GuildConfig(
                sites,
                settings.GetValueOrDefault(InfoLinkKey) ?? DefaultInfoLink,
                settings.GetValueOrDefault(MirrorPrefixKey) ?? DefaultMirrorPrefix,
                settings.TryGetValue(ModLogKey, out var modlog) && ulong.TryParse(modlog, out var modlogId) ? modlogId : null,
                filters.Where(f => f.Kind == IncludeKind).Select(f => f.Id).ToHashSet(),
                filters.Where(f => f.Kind == ExcludeKind).Select(f => f.Id).ToHashSet(),
                roles,
                // Pinging the poster is on unless an admin turned it off.
                PingAuthor: settings.GetValueOrDefault(PingAuthorKey) != "0",
                PingOptOuts: optOuts,
                Global: LoadGlobal(settings));
        }
    }

    /// <summary>Unset settings mean the default (on, utm_*), so existing servers get it without any migration.</summary>
    private static GlobalRule LoadGlobal(Dictionary<string, string> settings)
    {
        var enabled = settings.GetValueOrDefault(GlobalEnabledKey) != "0";
        var parameters = GlobalRule.Default.StripParams;
        if (settings.TryGetValue(GlobalParamsKey, out var json))
        {
            try { parameters = JsonSerializer.Deserialize<List<string>>(json) ?? parameters; }
            catch (JsonException) { /* corrupt value: fall back to the default */ }
        }
        return new GlobalRule(enabled, parameters);
    }

    /// <summary>Replaces the server-wide strip list; null restores the default.</summary>
    public void SetGlobalParams(ulong guildId, IReadOnlyList<string>? parameters) =>
        SetSetting(guildId, GlobalParamsKey, parameters is null ? null : JsonSerializer.Serialize(parameters));

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
