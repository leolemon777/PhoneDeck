namespace PhoneDeck.MacReceiver;

internal static class PhoneDeckDataDirectory
{
    internal const string EnvironmentVariable = "PHONEDECK_DATA_DIR";

    internal static string Get()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Resolve(configured, userProfile);
    }

    internal static string Resolve(string? configured, string userProfile)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configured.Trim()));
        }
        return Path.Combine(userProfile, "Library", "Application Support", "PhoneDeck");
    }
}
