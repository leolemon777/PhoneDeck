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

        TargetEnvelopeValidator.Validate(
            command.ProtocolVersion,
            command.RequestId,
            command.SessionId,
            command.TargetComputerId,
            computerId);

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

}
