internal static class PhoneDeckDataDirectory
{
    internal const string EnvironmentVariable = "PHONEDECK_DATA_DIR";

    internal static string Get()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            var adjacent = Path.Combine(AppContext.BaseDirectory, "data");
            if (Directory.Exists(adjacent))
            {
                return Path.GetFullPath(adjacent);
            }
        }
        return Resolve(
            configured,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    internal static string Resolve(string? configured, string localApplicationData)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configured.Trim()));
        }
        return Path.Combine(localApplicationData, "PhoneDeck");
    }
}
