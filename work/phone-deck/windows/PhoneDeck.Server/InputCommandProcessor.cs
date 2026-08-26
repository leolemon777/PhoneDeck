internal sealed record InputExecutionResult(bool Duplicate, string Message);

internal static class InputCommandProcessor
{
    internal static InputExecutionResult Execute(InputCommand command, string computerId)
    {
        if (string.IsNullOrWhiteSpace(command.Action))
        {
            throw new ArgumentException("缺少 action");
        }
        if (command.Text is { Length: > 4096 })
        {
            throw new ArgumentException("输入数据过大");
        }

        if (command.ProtocolVersion is > 2)
        {
            throw new ArgumentException("不支持的 protocolVersion");
        }
        if (command.ProtocolVersion == 2)
        {
            ValidateV2Envelope(command, computerId);
        }

        if (string.Equals(command.Action, "keyChord", StringComparison.Ordinal))
        {
            if (command.ProtocolVersion != 2)
            {
                throw new ArgumentException("keyChord 必须使用协议 v2");
            }
            var duplicate = KeyboardInput.ExecuteKeyChordOnce(
                command.Keys,
                command.HoldMs,
                command.RequestId,
                out var description);
            return new InputExecutionResult(duplicate, $"已发送 {description}");
        }

        var fixedDuplicate = KeyboardInput.ExecuteOnce(
            command.Action,
            command.Text,
            command.RequestId);
        return new InputExecutionResult(fixedDuplicate, $"已执行 {command.Action}");
    }

    private static void ValidateV2Envelope(InputCommand command, string computerId)
    {
        if (string.IsNullOrWhiteSpace(command.RequestId)
            || command.RequestId.Trim().Length > 128)
        {
            throw new ArgumentException("无效的 requestId");
        }
        if (string.IsNullOrWhiteSpace(command.SessionId)
            || command.SessionId.Trim().Length > 128
            || !Guid.TryParse(command.SessionId, out _))
        {
            throw new ArgumentException("无效的 sessionId");
        }
        if (string.IsNullOrWhiteSpace(command.TargetComputerId))
        {
            throw new ArgumentException("缺少 targetComputerId");
        }
        if (!string.Equals(command.TargetComputerId.Trim(), computerId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("请求目标不是当前电脑");
        }
    }
}
