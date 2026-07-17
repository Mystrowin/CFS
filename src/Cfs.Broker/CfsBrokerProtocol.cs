using System.Buffers.Binary;
using System.Text.Json;

namespace Cfs.Broker;

public sealed record BrokerRequest(int Version, string Command, string? ArchivePath = null, string? SourcePath = null, string? TargetPath = null);

public sealed record BrokerResponse(
    int Version,
    bool Success,
    string? ErrorCode = null,
    string? Message = null,
    string? CanonicalArchiveKey = null,
    string? MountPath = null,
    string? OutputPath = null,
    string? Warning = null,
    string? PersistenceState = null,
    bool IsDirty = false,
    long DirtyGeneration = 0,
    long CommittedGeneration = 0,
    DateTimeOffset? LastCommitUtc = null,
    string? LastCommitError = null,
    int SessionCount = 0,
    int CreatedSessionCount = 0,
    int BrokerProcessId = 0);

public sealed class BrokerProtocolException : Exception
{
    public BrokerProtocolException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException) => ErrorCode = errorCode;

    public string ErrorCode { get; }
}

public static class CfsBrokerProtocol
{
    public const int CurrentVersion = 1;
    public const int MaximumPayloadBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length == 0 || payload.Length > MaximumPayloadBytes)
            throw new BrokerProtocolException("payload-too-large", $"Broker messages must contain 1 to {MaximumPayloadBytes} bytes.");

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static Task<BrokerRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken = default) =>
        ReadAsync<BrokerRequest>(stream, cancellationToken);

    public static Task<BrokerResponse> ReadResponseAsync(Stream stream, CancellationToken cancellationToken = default) =>
        ReadAsync<BrokerResponse>(stream, cancellationToken);

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, prefix, "malformed-frame", "The broker message length prefix is incomplete.", cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > MaximumPayloadBytes)
            throw new BrokerProtocolException("payload-too-large", $"Broker message length {length} is outside the 1 to {MaximumPayloadBytes} byte limit.");

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, "malformed-frame", "The broker message payload is incomplete.", cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions)
                ?? throw new BrokerProtocolException("malformed-request", "The broker message was empty JSON.");
        }
        catch (JsonException ex)
        {
            throw new BrokerProtocolException("malformed-request", "The broker message is not valid JSON.", ex);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, string errorCode, string message, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new BrokerProtocolException(errorCode, message);
            offset += read;
        }
    }
}
