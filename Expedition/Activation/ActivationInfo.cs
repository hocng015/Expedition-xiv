namespace Expedition.Activation;

/// <summary>
/// Parsed information from a validated activation key.
/// </summary>
public sealed class ActivationInfo
{
    public Guid KeyId { get; init; }
    public DateTime IssuedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public byte Flags { get; init; }

    /// <summary>True if the key never expires (ExpiresAt == Unix epoch).</summary>
    public bool IsLifetime => ExpiresAt == DateTime.UnixEpoch;
}
