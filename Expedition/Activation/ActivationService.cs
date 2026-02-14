using System.Buffers.Binary;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Expedition.Activation;

/// <summary>
/// Activation key validation service.
/// Keys are HMAC-SHA256 signed by the Discord bot using a shared secret.
/// The plugin validates the signature locally and checks a remote revocation list on startup.
/// </summary>
public static class ActivationService
{
    private const byte KeyVersion = 0x01;
    private const int PayloadSize = 34;   // 1 + 16 + 8 + 8 + 1
    private const int SignatureSize = 32;  // HMAC-SHA256
    private const int TotalSize = PayloadSize + SignatureSize; // 66 bytes
    private const string KeyPrefix = "EXP-";

    // Revocation list hosted on a public GitHub Gist (fetched once on startup).
    private const string RevocationListUrl =
        "https://gist.githubusercontent.com/hocng015/ede003ec8621f64fa67a0a63e5b445da/raw/expedition_revoked_keys.json";
    private const int MaxRevocationResponseBytes = 1024 * 64; // 64 KB max response
    private static readonly TimeSpan RevocationFetchTimeout = TimeSpan.FromSeconds(5);
    private static readonly HttpClient _httpClient = new() { Timeout = RevocationFetchTimeout };

    // Shared secret stored as two XOR'd arrays to prevent trivial extraction.
    // Actual secret = _secretA XOR _secretB, computed at runtime.
    private static readonly byte[] _secretA = { 0xF5, 0x6F, 0xC9, 0x37, 0x42, 0x4A, 0x4B, 0xFE, 0xD3, 0x65, 0x4F, 0x4A, 0x69, 0x44, 0xE8, 0x3A, 0x98, 0x2F, 0x49, 0x13, 0xAA, 0x44, 0x23, 0x0A, 0xC0, 0x24, 0xA3, 0xF2, 0x87, 0x8A, 0x66, 0x5A };
    private static readonly byte[] _secretB = { 0x06, 0xD5, 0xCC, 0x82, 0x41, 0x63, 0x6C, 0xBD, 0x8D, 0x3E, 0x67, 0xFA, 0x28, 0xCD, 0xE6, 0xDF, 0xC9, 0x71, 0x73, 0xB4, 0x57, 0x68, 0xF6, 0xDA, 0xEC, 0x67, 0x62, 0x3A, 0x92, 0xF2, 0xE8, 0xA0 };

    public static bool IsActivated { get; private set; }
    public static ActivationInfo? Info { get; private set; }

    /// <summary>
    /// Called once during plugin initialization to validate a previously stored key.
    /// </summary>
    public static void Initialize(Configuration config)
    {
        IsActivated = false;
        Info = null;

        if (!string.IsNullOrEmpty(config.ActivationKey))
        {
            var result = ValidateKey(config.ActivationKey);
            if (result.IsValid)
            {
                // Key is cryptographically valid. Check the online revocation list.
                if (CheckRevocationList(result.Info!.KeyId))
                {
                    DalamudApi.Log.Warning(
                        $"Activation key has been revoked. Key ID: {result.Info.KeyId.ToString()[..8]}...");

                    // Clear the revoked key so the user sees the activation prompt
                    config.ActivationKey = string.Empty;
                    config.Save();
                    return;
                }

                IsActivated = true;
                Info = result.Info;
                DalamudApi.Log.Information($"Activation key valid. Key ID: {Info!.KeyId.ToString()[..8]}..., " +
                    $"Expires: {(Info.IsLifetime ? "Never" : Info.ExpiresAt.ToString("yyyy-MM-dd HH:mm UTC"))}");
            }
            else
            {
                DalamudApi.Log.Warning($"Stored activation key invalid: {result.ErrorMessage}");
            }
        }
    }

    /// <summary>
    /// Validates and activates a new key. Persists to config on success.
    /// </summary>
    public static ActivationResult Activate(string rawKey, Configuration config)
    {
        var result = ValidateKey(rawKey);
        if (result.IsValid)
        {
            config.ActivationKey = rawKey;
            config.Save();
            IsActivated = true;
            Info = result.Info;
            DalamudApi.Log.Information($"Plugin activated. Key ID: {Info!.KeyId.ToString()[..8]}...");
        }
        return result;
    }

    /// <summary>
    /// Removes the stored activation key and deactivates the plugin.
    /// </summary>
    public static void Deactivate(Configuration config)
    {
        config.ActivationKey = string.Empty;
        config.Save();
        IsActivated = false;
        Info = null;
        DalamudApi.Log.Information("Plugin deactivated.");
    }

    /// <summary>
    /// Checks if the current key has expired. Called periodically from the framework update.
    /// </summary>
    public static void CheckExpiration()
    {
        if (!IsActivated || Info == null) return;
        if (Info.IsLifetime) return;

        if (DateTime.UtcNow > Info.ExpiresAt)
        {
            IsActivated = false;
            Info = null;
            DalamudApi.Log.Warning("Activation key has expired.");
            DalamudApi.ChatGui.PrintError("[Expedition] Your activation key has expired. Please obtain a new key.");
        }
    }

    /// <summary>
    /// Periodically re-checks the revocation list and deactivates if the key has been revoked.
    /// Called from the framework update loop on a timer (e.g. every 10 minutes).
    /// Runs the HTTP fetch on a background thread so it never blocks the game.
    /// </summary>
    private static volatile bool _revocationCheckInProgress;

    public static void CheckRevocationPeriodic()
    {
        if (!IsActivated || Info == null) return;
        if (_revocationCheckInProgress) return;

        _revocationCheckInProgress = true;
        var keyId = Info.KeyId;

        Task.Run(async () =>
        {
            try
            {
                var isRevoked = await CheckRevocationListAsync(keyId);
                if (isRevoked)
                {
                    DalamudApi.Log.Warning(
                        $"Periodic revocation check: key {keyId.ToString()[..8]}... has been REVOKED.");
                    IsActivated = false;
                    Info = null;

                    // Clear the stored key so it won't pass validation on next startup either
                    var config = Expedition.Config;
                    if (config != null)
                    {
                        config.ActivationKey = string.Empty;
                        config.Save();
                    }

                    DalamudApi.ChatGui.PrintError(
                        "[Expedition] Your activation key has been revoked. The plugin has been deactivated.");
                }
                else
                {
                    DalamudApi.Log.Information("Periodic revocation check: key not revoked.");
                }
            }
            catch (Exception ex)
            {
                // Fail-open: if the check fails, don't lock the user out
                DalamudApi.Log.Warning($"Periodic revocation check failed: {ex.Message}");
            }
            finally
            {
                _revocationCheckInProgress = false;
            }
        });
    }

    /// <summary>
    /// Async version of revocation list check for use in background tasks.
    /// </summary>
    private static async Task<bool> CheckRevocationListAsync(Guid keyId)
    {
        var revokedIds = await FetchRevocationListAsync();
        return IsKeyInRevocationList(keyId, revokedIds);
    }

    /// <summary>
    /// Fetches and parses the revocation list JSON from the Gist.
    /// </summary>
    private static async Task<List<string>> FetchRevocationListAsync()
    {
        var response = await _httpClient.GetAsync(RevocationListUrl);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaxRevocationResponseBytes)
            throw new InvalidOperationException("Revocation list response too large.");

        var json = await response.Content.ReadAsStringAsync();
        if (json.Length > MaxRevocationResponseBytes)
            throw new InvalidOperationException("Revocation list response too large.");

        return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
    }

    /// <summary>
    /// Checks if a key ID appears in the revocation list, accounting for the
    /// UUID/GUID endianness mismatch between Python and C#.
    ///
    /// Python's uuid.UUID(bytes=...) uses big-endian (RFC 4122) for all fields.
    /// C#'s new Guid(byte[]) uses mixed-endian (first 3 groups are little-endian).
    /// The same 16 raw bytes produce different string representations:
    ///   Python: d30ca0e2-a380-4342-a099-fce355de578f
    ///   C#:     e2a00cd3-80a3-4243-a099-fce355de578f
    ///
    /// We check both the C# format and the Python-equivalent format so the
    /// revocation works regardless of which format the gist contains.
    /// </summary>
    private static bool IsKeyInRevocationList(Guid keyId, List<string> revokedIds)
    {
        if (revokedIds.Count == 0) return false;

        // C# native format (what the plugin produces from Guid.ToString())
        var csharpFormat = keyId.ToString();
        if (revokedIds.Contains(csharpFormat)) return true;

        // Python-equivalent format: reverse the endianness of the first 3 groups
        // to match what Python's uuid.UUID(bytes=...) would produce from the same raw bytes.
        var bytes = keyId.ToByteArray(); // C# mixed-endian layout
        var pythonFormat = string.Format(
            "{0:x2}{1:x2}{2:x2}{3:x2}-{4:x2}{5:x2}-{6:x2}{7:x2}-{8:x2}{9:x2}-{10:x2}{11:x2}{12:x2}{13:x2}{14:x2}{15:x2}",
            bytes[3], bytes[2], bytes[1], bytes[0],  // Data1 reversed (LE → BE)
            bytes[5], bytes[4],                       // Data2 reversed
            bytes[7], bytes[6],                       // Data3 reversed
            bytes[8], bytes[9],                       // Data4 unchanged
            bytes[10], bytes[11], bytes[12], bytes[13], bytes[14], bytes[15]);

        return revokedIds.Contains(pythonFormat);
    }

    /// <summary>
    /// Fetches the revocation list from GitHub Gist and checks if the given key ID is revoked.
    /// Returns true if the key IS revoked. Returns false if not revoked or if the fetch fails
    /// (fail-open: offline users are not locked out).
    /// Called once during plugin initialization.
    /// </summary>
    private static bool CheckRevocationList(Guid keyId)
    {
        try
        {
            var task = Task.Run(async () =>
            {
                var revokedIds = await FetchRevocationListAsync();
                return IsKeyInRevocationList(keyId, revokedIds);
            });

            if (task.Wait(RevocationFetchTimeout))
            {
                var isRevoked = task.Result;
                DalamudApi.Log.Information(
                    $"Revocation list checked: {(isRevoked ? "KEY REVOKED" : "key not revoked")}.");
                return isRevoked;
            }

            DalamudApi.Log.Warning("Revocation list fetch timed out. Allowing key (fail-open).");
            return false;
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"Failed to check revocation list: {ex.Message}. Allowing key (fail-open).");
            return false;
        }
    }

    /// <summary>
    /// Validates an activation key string.
    /// Format: EXP-{base64url(version(1) | keyId(16) | issuedAt(8) | expiresAt(8) | flags(1) | hmacSha256(32))}
    /// </summary>
    private static ActivationResult ValidateKey(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return ActivationResult.Failure("Key is empty.");

        if (!rawKey.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
            return ActivationResult.Failure("Invalid key format (must start with EXP-).");

        var encoded = rawKey[KeyPrefix.Length..];

        // Base64url decode (add padding as needed)
        var padding = (4 - encoded.Length % 4) % 4;
        var padded = encoded + new string('=', padding);
        // Convert base64url to standard base64
        padded = padded.Replace('-', '+').Replace('_', '/');

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(padded);
        }
        catch
        {
            return ActivationResult.Failure("Invalid key encoding.");
        }

        if (raw.Length != TotalSize)
            return ActivationResult.Failure($"Invalid key length (expected {TotalSize}, got {raw.Length}).");

        var payload = raw.AsSpan(0, PayloadSize);
        var signature = raw.AsSpan(PayloadSize, SignatureSize);

        // Verify HMAC-SHA256 signature
        var secret = GetSecret();
        byte[] expectedSignature;
        try
        {
            using var hmac = new HMACSHA256(secret);
            expectedSignature = hmac.ComputeHash(payload.ToArray());
        }
        finally
        {
            // Clear secret from memory
            Array.Clear(secret, 0, secret.Length);
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, expectedSignature))
            return ActivationResult.Failure("Invalid key signature.");

        // Parse payload
        var version = payload[0];
        if (version != KeyVersion)
            return ActivationResult.Failure($"Unsupported key version ({version}).");

        var keyId = new Guid(payload.Slice(1, 16));
        var issuedAt = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(17, 8));
        var expiresAt = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(25, 8));
        var flags = payload[33];

        var issuedAtDt = DateTimeOffset.FromUnixTimeSeconds(issuedAt).UtcDateTime;
        var expiresAtDt = expiresAt == 0
            ? DateTime.UnixEpoch
            : DateTimeOffset.FromUnixTimeSeconds(expiresAt).UtcDateTime;

        // Check expiration
        if (expiresAt > 0 && DateTime.UtcNow > expiresAtDt)
            return ActivationResult.Failure($"Key expired on {expiresAtDt:yyyy-MM-dd HH:mm UTC}.");

        var info = new ActivationInfo
        {
            KeyId = keyId,
            IssuedAt = issuedAtDt,
            ExpiresAt = expiresAtDt,
            Flags = flags,
        };

        return ActivationResult.Success(info);
    }

    /// <summary>
    /// Recovers the shared secret by XOR'ing the two obfuscated halves.
    /// </summary>
    private static byte[] GetSecret()
    {
        var secret = new byte[32];
        for (var i = 0; i < 32; i++)
            secret[i] = (byte)(_secretA[i] ^ _secretB[i]);
        return secret;
    }
}
