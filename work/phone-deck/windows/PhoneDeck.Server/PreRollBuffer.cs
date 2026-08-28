/// <summary>每会话独立的 PCM pre-roll 环形缓冲：按序写入、按序一次性取空。
/// 超容量时丢弃最旧数据（正常流程由水位线在写满前主动放行，覆盖路径仅作兜底）。
/// 纯逻辑类，便于单元测试验证“不重复、不乱序、不丢失”。</summary>
internal sealed class PreRollBuffer
{
    private readonly object sync = new();
    private readonly byte[] store;
    private int writeIndex;
    private int storedBytes;

    internal PreRollBuffer(int capacityBytes)
    {
        if (capacityBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        }
        store = new byte[capacityBytes];
    }

    internal int CapacityBytes => store.Length;

    internal int StoredBytes
    {
        get
        {
            lock (sync)
            {
                return storedBytes;
            }
        }
    }

    internal void Write(byte[] buffer, int offset, int count)
    {
        if (count <= 0)
        {
            return;
        }
        lock (sync)
        {
            if (count >= store.Length)
            {
                // 只保留最新的整段。
                Array.Copy(buffer, offset + count - store.Length,
                    store, 0, store.Length);
                writeIndex = 0;
                storedBytes = store.Length;
                return;
            }
            var first = Math.Min(count, store.Length - writeIndex);
            Array.Copy(buffer, offset, store, writeIndex, first);
            if (count > first)
            {
                Array.Copy(buffer, offset + first, store, 0, count - first);
            }
            writeIndex = (writeIndex + count) % store.Length;
            storedBytes = Math.Min(storedBytes + count, store.Length);
        }
    }

    /// <summary>原子地按写入顺序取走全部已缓冲数据并清空缓冲。</summary>
    internal byte[] TakeAll()
    {
        lock (sync)
        {
            var result = new byte[storedBytes];
            var start = (writeIndex - storedBytes + store.Length) % store.Length;
            var first = Math.Min(storedBytes, store.Length - start);
            Array.Copy(store, start, result, 0, first);
            if (storedBytes > first)
            {
                Array.Copy(store, 0, result, first, storedBytes - first);
            }
            storedBytes = 0;
            writeIndex = 0;
            return result;
        }
    }
}
