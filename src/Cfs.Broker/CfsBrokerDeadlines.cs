namespace Cfs.Broker;

/// <summary>One policy for direct-owner and forwarded broker request deadlines.</summary>
public sealed class CfsBrokerDeadlinePolicy
{
    public CfsBrokerDeadlinePolicy(
        TimeSpan framing,
        TimeSpan standardHandler,
        TimeSpan compressionHandler,
        TimeSpan responseWrite,
        TimeSpan standardClient,
        TimeSpan compressionClient)
    {
        Framing = RequirePositive(framing, nameof(framing));
        StandardHandler = RequirePositive(standardHandler, nameof(standardHandler));
        CompressionHandler = RequirePositive(compressionHandler, nameof(compressionHandler));
        ResponseWrite = RequirePositive(responseWrite, nameof(responseWrite));
        StandardClient = RequirePositive(standardClient, nameof(standardClient));
        CompressionClient = RequirePositive(compressionClient, nameof(compressionClient));
        if (StandardClient <= StandardHandler + ResponseWrite)
            throw new ArgumentException("The standard client deadline must include a response margin beyond the handler deadline.", nameof(standardClient));
        if (CompressionClient <= CompressionHandler + ResponseWrite)
            throw new ArgumentException("The compression client deadline must include a response margin beyond the handler deadline.", nameof(compressionClient));
    }

    public static CfsBrokerDeadlinePolicy Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromHours(2),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(10),
        TimeSpan.FromHours(2) + TimeSpan.FromSeconds(10));

    public TimeSpan Framing { get; }
    public TimeSpan StandardHandler { get; }
    public TimeSpan CompressionHandler { get; }
    public TimeSpan ResponseWrite { get; }
    public TimeSpan StandardClient { get; }
    public TimeSpan CompressionClient { get; }

    public TimeSpan HandlerFor(string? command) => IsCompression(command) ? CompressionHandler : StandardHandler;
    public TimeSpan ClientFor(string? command) => IsCompression(command) ? CompressionClient : StandardClient;

    public BrokerResponse TimeoutResponse(string? command) => new(
        CfsBrokerProtocol.CurrentVersion,
        false,
        "request-timeout",
        IsCompression(command)
            ? "Folder compression exceeded its bounded processing deadline. The source folder is unchanged; inspect the CFS diagnostic log before retrying."
            : "The broker request exceeded its bounded processing deadline. Try the action again or inspect the CFS diagnostic log.",
        BrokerProcessId: Environment.ProcessId);

    private static TimeSpan RequirePositive(TimeSpan value, string name) =>
        value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(name, "Broker deadlines must be positive.");

    private static bool IsCompression(string? command) =>
        string.Equals(command, "compress", StringComparison.OrdinalIgnoreCase);
}
