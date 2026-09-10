internal sealed class UpdateFileTransaction(string install, string stage, string backup)
{
    private readonly List<string> changed = [];
    internal void Apply()
    {
        Directory.CreateDirectory(backup);
        foreach (var name in UpdatePackage.Names.Where(n => n.EndsWith(".exe", StringComparison.Ordinal)))
        {
            var target = Path.Combine(install, name);
            if (File.Exists(target)) File.Copy(target, Path.Combine(backup, name), true);
            else File.Delete(Path.Combine(backup, name));
            File.Copy(Path.Combine(stage, name), target + ".new", true);
            ReplaceWithRetry(() => File.Move(target + ".new", target, true));
            changed.Add(name);
        }
    }
    internal void Rollback()
    {
        foreach (var name in changed.AsEnumerable().Reverse())
        {
            var old = Path.Combine(backup, name);
            var target = Path.Combine(install, name);
            if (File.Exists(old)) ReplaceWithRetry(() => File.Copy(old, target, true));
            else File.Delete(target);
        }
    }
    private static void ReplaceWithRetry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(250); }
        }
    }
}
