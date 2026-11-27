namespace LinkCleaner.Bot;

/// <summary>
/// Size limits shared by the slash commands and config import, so anything a command accepts
/// also survives an export and re-import.
/// </summary>
public static class Limits
{
    /// <summary>Discord's cap on one message.</summary>
    public const int MessageLength = 2000;

    public const int MirrorPrefixLength = 16;
    public const int RegexLength = 200;
    public const int HostsPerSite = 50;
    public const int PathPatternsPerSite = 20;

    /// <summary>For each parameter list: a site's stripped and tidy params, the server-wide list, generic utm values.</summary>
    public const int EntriesPerList = 200;

    /// <summary>Cuts text to fit in one Discord message.</summary>
    public static string FitMessage(string text) =>
        text.Length <= MessageLength ? text : text[..(MessageLength - 1)] + "…";
}
