namespace Expedition.Activation;

/// <summary>
/// Result of a key validation attempt.
/// </summary>
public sealed class ActivationResult
{
    public bool IsValid { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public ActivationInfo? Info { get; init; }

    public static ActivationResult Success(ActivationInfo info) => new() { IsValid = true, Info = info };
    public static ActivationResult Failure(string message) => new() { IsValid = false, ErrorMessage = message };
}
