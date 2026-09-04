namespace PhoneDeck.MacReceiver;

internal sealed record DictationCommand(
    int? ProtocolVersion,
    string? SessionId,
    string? RequestId,
    string? TargetComputerId,
    string? Mode);
