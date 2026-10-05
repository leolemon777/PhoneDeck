internal static class TargetEnvelopeValidator
{
    internal static void Validate(
        int? protocolVersion,
        string? requestId,
        string? sessionId,
        string? targetComputerId,
        string computerId)
    {
        ValidateProtocolAndTarget(protocolVersion, targetComputerId, computerId);
        if (protocolVersion != 2)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(requestId)
            || requestId.Trim().Length > 128)
        {
            throw new ArgumentException("无效的 requestId");
        }
        if (string.IsNullOrWhiteSpace(sessionId)
            || sessionId.Trim().Length > 128
            || !Guid.TryParse(sessionId, out _))
        {
            throw new ArgumentException("无效的 sessionId");
        }
    }

    internal static void ValidateProtocolAndTarget(
        int? protocolVersion,
        string? targetComputerId,
        string computerId)
    {
        if (protocolVersion is > 2)
        {
            throw new ArgumentException("不支持的 protocolVersion");
        }
        if (protocolVersion != 2)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(targetComputerId))
        {
            throw new ArgumentException("缺少 targetComputerId");
        }
        if (!string.Equals(targetComputerId.Trim(), computerId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("请求目标不是当前电脑");
        }
    }
}
