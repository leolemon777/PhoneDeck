using System.Runtime.InteropServices;

internal sealed record ReceiverIdentity(
    string ComputerId,
    string DisplayName,
    string Platform,
    string Architecture)
{
    internal static ReceiverIdentity LoadOrCreate()
    {
        var directory = PhoneDeckDataDirectory.Get();
        var path = Path.Combine(directory, "computer-id.txt");
        string? computerId = null;
        try
        {
            if (File.Exists(path))
            {
                var saved = File.ReadAllText(path).Trim();
                if (Guid.TryParse(saved, out _))
                {
                    computerId = saved;
                }
            }
            if (computerId is null)
            {
                Directory.CreateDirectory(directory);
                computerId = Guid.NewGuid().ToString();
                var temporaryPath = path + ".tmp";
                File.WriteAllText(temporaryPath, computerId);
                File.Move(temporaryPath, path, overwrite: true);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"保存电脑身份失败，本次使用临时身份：{exception.Message}");
            computerId ??= Guid.NewGuid().ToString();
        }

        return new ReceiverIdentity(
            computerId,
            Environment.MachineName,
            "windows",
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
    }
}
