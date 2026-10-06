namespace PhoneDeck.Desktop;

internal sealed record Transcript(string ResultId, string SourceComputerId, string SessionId, string Text, DateTimeOffset CreatedUtc, long Cursor = 0);

internal sealed class TranscriptStore(string computerId, TimeProvider? clock = null)
{
    private readonly object gate = new();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly List<(Transcript Value, string Owner)> entries = new();
    private long cursor;
    internal Transcript Add(string clientId, Transcript value, bool imported = false)
    {
        if (!Guid.TryParse(value.ResultId, out _) || !Guid.TryParse(value.SessionId, out _) || !Guid.TryParse(value.SourceComputerId, out _)
            || string.IsNullOrWhiteSpace(value.Text) || value.Text.Length > 4096 || value.Text.Contains('\0')) throw new ArgumentException("同步结果格式无效");
        if (!imported && value.SourceComputerId != computerId) throw new ArgumentException("来源电脑不匹配");
        lock (gate)
        {
            Prune();
            var existing = entries.FirstOrDefault(x => x.Value.ResultId == value.ResultId && x.Owner == clientId);
            if (existing.Value is not null)
            {
                if (existing.Value.Text != value.Text || existing.Value.SourceComputerId != value.SourceComputerId || existing.Value.SessionId != value.SessionId)
                    throw new ArgumentException("同一结果编号不能对应不同内容");
                return existing.Value;
            }
            var normalized = value with { CreatedUtc = clock.GetUtcNow(), Cursor = ++cursor };
            entries.Add((normalized, clientId));
            if (entries.Count > 100) entries.RemoveAt(0);
            return normalized;
        }
    }
    internal object ForPhone(string clientId, long after)
    {
        lock (gate)
        {
            Prune();
            return new { ok = true, computerId, cursor, results = entries.Where(x => x.Owner == clientId && x.Value.Cursor > after && x.Value.SourceComputerId == computerId).Select(x => x.Value).ToArray() };
        }
    }
    internal Transcript[] LocalHistory() { lock (gate) { Prune(); return entries.Select(x => x.Value).Reverse().ToArray(); } }
    internal void Clear() { lock (gate) entries.Clear(); }
    private void Prune() => entries.RemoveAll(x => clock.GetUtcNow() - x.Value.CreatedUtc > TimeSpan.FromMinutes(30));
}
