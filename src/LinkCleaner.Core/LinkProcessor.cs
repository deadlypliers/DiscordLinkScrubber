using System.Text;
using System.Text.RegularExpressions;

namespace LinkCleaner.Core;

/// <summary>
/// Pure link-cleaning logic: message text and site rules in, results out.
/// No Discord, network or storage code lives here.
/// </summary>
public static partial class LinkProcessor
{
    // '|' is excluded so spoilered links (||url||) stop at the closing bars.
    [GeneratedRegex(@"https?://[^\s<>|]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    // Fenced blocks first, then inline code.
    [GeneratedRegex(@"```.*?```|`[^`\n]+`", RegexOptions.Singleline)]
    private static partial Regex CodeRegex();

    private const string TrailingJunk = ".,;:!?'\"*~_";

    /// <param name="global">Rules applied to every link, including sites not in <paramref name="sites"/>. Null means none.</param>
    /// <param name="enforcement">Whether cosmetic params are a reason to act on their own.</param>
    public static ProcessedMessage Process(string? content, IReadOnlyList<SiteRule> sites, GlobalRule? global = null,
        Enforcement enforcement = Enforcement.TrackingOnly)
    {
        if (string.IsNullOrEmpty(content))
            return new ProcessedMessage("", "", []);

        var codeRanges = CodeRegex().Matches(content).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
        var links = new List<LinkResult>();

        foreach (Match match in UrlRegex().Matches(content))
        {
            if (codeRanges.Any(r => match.Index >= r.Index && match.Index < r.End))
                continue;
            // <https://...> is Discord's "suppress embed" form: the user chose to leave it alone.
            if (match.Index > 0 && content[match.Index - 1] == '<')
                continue;

            var raw = TrimTrailing(match.Value);
            var result = Evaluate(raw, match.Index, sites, global is { Enabled: true } ? global : null, enforcement);
            if (result is not null)
                links.Add(result);
        }

        var sb = new StringBuilder();
        var pos = 0;
        foreach (var link in links.Where(l => l.TrackingRemoved))
        {
            sb.Append(content, pos, link.Start - pos).Append(link.Replacement);
            pos = link.Start + link.Length;
        }
        sb.Append(content, pos, content.Length - pos);

        return new ProcessedMessage(content, sb.ToString(), links);
    }

    /// <summary>
    /// A link is acted on only if something identifying is found: a listed site param, a global identifier,
    /// a utm param with a non-generic value, or a path tracker. Only then are cosmetic params removed too,
    /// unless <paramref name="enforcement"/> makes cosmetic params a reason to act as well.
    /// </summary>
    private static LinkResult? Evaluate(string url, int start, IReadOnlyList<SiteRule> sites, GlobalRule? global, Enforcement enforcement)
    {
        var strict = enforcement == Enforcement.IncludeCosmetic;
        Func<string, string, bool> Trigger(IReadOnlyList<string> sitePatterns, IReadOnlyList<string> siteTidy)
        {
            var identifying = Identifying(sitePatterns, global);
            if (!strict)
                return identifying;
            var cosmetic = Cosmetic(siteTidy, global);
            return (name, value) => identifying(name, value) || cosmetic(name, value);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var host = NormalizeHost(uri.Host);
        foreach (var site in sites)
        {
            if (!site.Enabled)
                continue;

            var isMirrorHost = !string.IsNullOrWhiteSpace(site.MirrorHost) && HostMatches(host, site.MirrorHost);
            if (!isMirrorHost && !site.Hosts.Any(h => HostMatches(host, h)))
                continue;

            var (cleaned, removed) = StripParams(url, Trigger(site.StripParams, site.TidyParams));
            var (noPath, pathRemoved) = StripPath(cleaned, site.StripPathPatterns);
            var identifying = removed || pathRemoved;
            if (identifying)
                noPath = StripParams(noPath, Cosmetic(site.TidyParams, global)).Url;
            var mirror = isMirrorHost ? null : BuildMirror(noPath, new Uri(noPath).AbsolutePath, site);
            return new LinkResult(url, start, site.Name, noPath, identifying, mirror);
        }

        // Not a listed site: only the global rule applies, and links without anything identifying are left alone.
        if (global is null)
            return null;
        var (globalCleaned, globalRemoved) = StripParams(url, Trigger([], []));
        if (!globalRemoved)
            return null;
        globalCleaned = StripParams(globalCleaned, Cosmetic([], global)).Url;
        return new LinkResult(url, start, GlobalRule.SiteName, globalCleaned, true, null);
    }

    private static Func<string, string, bool> Identifying(IReadOnlyList<string> sitePatterns, GlobalRule? global) =>
        (name, value) => sitePatterns.Any(p => ParamMatches(name, p)) ||
                         (global is not null &&
                          (global.StripParams.Any(p => ParamMatches(name, p)) ||
                           (GlobalRule.ValueGated.Any(p => ParamMatches(name, p)) && !global.IsGeneric(value))));

    private static Func<string, string, bool> Cosmetic(IReadOnlyList<string> sitePatterns, GlobalRule? global) =>
        (name, _) => sitePatterns.Any(p => ParamMatches(name, p)) ||
                     (global is not null && GlobalRule.ValueGated.Any(p => ParamMatches(name, p)));

    /// <summary>Removes regex matches from the path only; query and fragment keep their exact text.</summary>
    public static (string Url, bool Removed) StripPath(string url, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
            return (url, false);

        var authorityStart = url.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0)
            return (url, false);
        authorityStart += 3;
        var pathStart = url.IndexOf('/', authorityStart);
        if (pathStart < 0)
            return (url, false);
        var pathEnd = url.IndexOfAny(['?', '#'], pathStart);
        if (pathEnd < 0)
            pathEnd = url.Length;

        var path = url[pathStart..pathEnd];
        var changed = false;
        foreach (var pattern in patterns)
        {
            try
            {
                var next = Regex.Replace(path, pattern, "", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                if (next.Length == 0)
                    next = "/"; // never leave an empty path
                if (next != path)
                {
                    path = next;
                    changed = true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                // A bad or runaway pattern must never break link handling: skip it.
            }
        }

        return changed ? (url[..pathStart] + path + url[pathEnd..], true) : (url, false);
    }

    private static string? BuildMirror(string cleaned, string path, SiteRule site)
    {
        if (string.IsNullOrWhiteSpace(site.MirrorHost))
            return null;

        bool applies;
        try
        {
            applies = site.MirrorPathPattern is null
                ? path.Length > 1
                : Regex.IsMatch(path, site.MirrorPathPattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            applies = false; // a bad or runaway pattern (e.g. imported) must not break the message
        }
        if (!applies)
            return null;

        // Swap only the authority so the rest of the URL keeps its exact original encoding.
        var authorityStart = cleaned.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = cleaned.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
            authorityEnd = cleaned.Length;
        return "https://" + site.MirrorHost.Trim().ToLowerInvariant() + cleaned[authorityEnd..];
    }

    /// <summary>Removes matching query params while leaving every other character of the URL untouched.</summary>
    public static (string Url, bool Removed) StripParams(string url, IReadOnlyList<string> patterns) =>
        StripParams(url, (name, _) => patterns.Any(p => ParamMatches(name, p)));

    /// <summary>Same, but the caller decides per param from its decoded name and value.</summary>
    public static (string Url, bool Removed) StripParams(string url, Func<string, string, bool> shouldRemove)
    {
        var hash = url.IndexOf('#');
        var fragment = hash >= 0 ? url[hash..] : "";
        var rest = hash >= 0 ? url[..hash] : url;

        var q = rest.IndexOf('?');
        if (q < 0)
            return (url, false);

        var kept = new List<string>();
        var removed = 0;
        foreach (var part in rest[(q + 1)..].Split('&'))
        {
            var eq = part.IndexOf('=');
            var rawName = eq >= 0 ? part[..eq] : part;
            string name;
            try { name = Uri.UnescapeDataString(rawName); }
            catch (UriFormatException) { name = rawName; }

            var rawValue = eq >= 0 ? part[(eq + 1)..] : "";
            string value;
            try { value = Uri.UnescapeDataString(rawValue); }
            catch (UriFormatException) { value = rawValue; }

            if (part.Length > 0 && shouldRemove(name, value))
                removed++;
            else
                kept.Add(part);
        }

        if (removed == 0)
            return (url, false);

        var query = string.Join('&', kept.Where(k => k.Length > 0));
        return (rest[..q] + (query.Length > 0 ? "?" + query : "") + fragment, true);
    }

    private static bool ParamMatches(string name, string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.Length == 0)
            return false;
        return pattern.EndsWith('*')
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHost(string host)
    {
        host = host.Trim().ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>True if host equals the entry or is a subdomain of it (never a lookalike like "notyoutube.com").</summary>
    private static bool HostMatches(string host, string entry)
    {
        entry = NormalizeHost(entry);
        return entry.Length > 0 && (host == entry || host.EndsWith("." + entry, StringComparison.Ordinal));
    }

    private static string TrimTrailing(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (TrailingJunk.Contains(last) || Unbalanced(url, last))
                url = url[..^1];
            else
                break;
        }
        return url;
    }

    private static bool Unbalanced(string url, char last) => last switch
    {
        ')' => url.Count(c => c == ')') > url.Count(c => c == '('),
        ']' => url.Count(c => c == ']') > url.Count(c => c == '['),
        _ => false,
    };
}
