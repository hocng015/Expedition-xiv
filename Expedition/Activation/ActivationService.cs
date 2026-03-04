using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Expedition.Activation;

/// <summary>
/// Activation service with server-side key validation.
/// Keys are validated by the Expedition Bot server which holds the HMAC secret.
/// The plugin only holds the server's Ed25519 public key to verify session tokens.
/// </summary>
public static class ActivationService
{
    // TODO: Replace with your Railway deployment URL
    private const string ServerBaseUrl = "https://expedition-bot-production.up.railway.app";

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static bool IsActivated { get; private set; }
    public static ActivationInfo? Info { get; private set; }
    public static SessionToken? CurrentToken { get; private set; }

    /// <summary>Tracks when we last successfully contacted the server.</summary>
    private static DateTime _lastServerContact = DateTime.MinValue;

    /// <summary>Prevents concurrent refresh attempts.</summary>
    private static volatile bool _refreshInProgress;

    // Legacy migration: timestamp when legacy grace period started
    private static DateTime _legacyGraceStart = DateTime.MinValue;
    private const int LegacyGraceDays = 30;

    /// <summary>
    /// Called once during plugin initialization to restore cached session state.
    /// </summary>
    public static void Initialize(Configuration config)
    {
        IsActivated = false;
        Info = null;
        CurrentToken = null;

        // Try cached session token first
        if (!string.IsNullOrEmpty(config.SessionToken))
        {
            var token = SessionTokenVerifier.Verify(config.SessionToken);
            if (token != null)
            {
                CurrentToken = token;

                if (!token.IsExpired)
                {
                    // Valid cached token — activate immediately
                    ActivateFromToken(token);
                    DalamudApi.Log.Information(
                        $"Session token valid. Key ID: {token.KeyId.ToString()[..8]}..., " +
                        $"Expires: {token.ExpiresAt:yyyy-MM-dd HH:mm UTC}");
                    return;
                }

                // Token expired but we might be in grace period
                if (token.IsInGracePeriod(_lastServerContact))
                {
                    ActivateFromToken(token);
                    DalamudApi.Log.Warning("Session token expired but within grace period. Will refresh soon.");
                    return;
                }

                DalamudApi.Log.Warning("Cached session token expired and outside grace period.");
            }
            else
            {
                DalamudApi.Log.Warning("Cached session token failed verification.");
            }
        }

        // Legacy migration: if user has an old EXP- key but no session token,
        // allow it temporarily while they get a session token
        if (!string.IsNullOrEmpty(config.ActivationKey) && config.ActivationKey.StartsWith("EXP-"))
        {
            if (_legacyGraceStart == DateTime.MinValue)
                _legacyGraceStart = DateTime.UtcNow;

            if ((DateTime.UtcNow - _legacyGraceStart).TotalDays < LegacyGraceDays)
            {
                IsActivated = true;
                DalamudApi.Log.Warning(
                    "Legacy key detected. Operating in migration grace period. " +
                    "Please connect to the internet to obtain a session token.");

                // Try to exchange the legacy key for a session token in the background
                var legacyKey = config.ActivationKey;
                Task.Run(async () =>
                {
                    try
                    {
                        var result = await ValidateWithServer(legacyKey);
                        if (result.IsValid && result.Token != null)
                        {
                            config.SessionToken = result.TokenString;
                            config.Save();
                            CurrentToken = result.Token;
                            ActivateFromToken(result.Token);
                            _lastServerContact = DateTime.UtcNow;
                            DalamudApi.Log.Information("Legacy key exchanged for session token successfully.");
                        }
                    }
                    catch (Exception ex)
                    {
                        DalamudApi.Log.Warning($"Background legacy key exchange failed: {ex.Message}");
                    }
                });
                return;
            }

            DalamudApi.Log.Warning("Legacy grace period expired. Please activate with an internet connection.");
        }
    }

    /// <summary>
    /// Validates a key with the server and activates. Call from UI/command.
    /// </summary>
    public static async Task<ActivationResult> ActivateAsync(string rawKey, Configuration config)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return ActivationResult.Failure("Key is empty.");

        if (!rawKey.StartsWith("EXP-", StringComparison.OrdinalIgnoreCase))
            return ActivationResult.Failure("Invalid key format (must start with EXP-).");

        try
        {
            var result = await ValidateWithServer(rawKey);
            if (result.IsValid && result.Token != null)
            {
                config.ActivationKey = rawKey;
                config.SessionToken = result.TokenString;
                config.MachineId = MachineFingerprint.Get();
                config.Save();

                CurrentToken = result.Token;
                _lastServerContact = DateTime.UtcNow;
                ActivateFromToken(result.Token);

                DalamudApi.Log.Information($"Plugin activated. Key ID: {result.Token.KeyId.ToString()[..8]}...");
                return ActivationResult.Success(Info!);
            }

            return ActivationResult.Failure(result.ErrorMessage);
        }
        catch (HttpRequestException ex)
        {
            DalamudApi.Log.Warning($"Server validation failed (network): {ex.Message}");
            return ActivationResult.Failure("Could not reach activation server. Please check your internet connection and try again.");
        }
        catch (TaskCanceledException)
        {
            return ActivationResult.Failure("Activation server timed out. Please try again.");
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error($"Unexpected activation error: {ex}");
            return ActivationResult.Failure($"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Synchronous activation wrapper for backwards compatibility with command handler.
    /// </summary>
    public static ActivationResult Activate(string rawKey, Configuration config)
    {
        try
        {
            return Task.Run(() => ActivateAsync(rawKey, config)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return ActivationResult.Failure($"Activation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the stored activation and deactivates the plugin.
    /// </summary>
    public static void Deactivate(Configuration config)
    {
        config.ActivationKey = string.Empty;
        config.SessionToken = string.Empty;
        config.Save();
        IsActivated = false;
        Info = null;
        CurrentToken = null;
        DalamudApi.Log.Information("Plugin deactivated.");
    }

    /// <summary>
    /// Checks if the current session has expired. Called periodically (every 60s).
    /// </summary>
    public static void CheckExpiration()
    {
        if (!IsActivated || CurrentToken == null) return;

        if (CurrentToken.IsExpired && !CurrentToken.IsInGracePeriod(_lastServerContact))
        {
            IsActivated = false;
            Info = null;
            CurrentToken = null;
            DalamudApi.Log.Warning("Session token has expired.");
            DalamudApi.ChatGui.PrintError(
                "[Expedition] Your session has expired. Please connect to the internet to refresh.");
        }
    }

    /// <summary>
    /// Refreshes the session token with the server. Called every 4 hours.
    /// Runs the HTTP call on a background thread.
    /// </summary>
    public static void RefreshToken(Configuration config)
    {
        if (!IsActivated || CurrentToken == null) return;
        if (!CurrentToken.NeedsRefresh) return;
        if (_refreshInProgress) return;

        _refreshInProgress = true;
        var tokenStr = config.SessionToken;
        var machineId = MachineFingerprint.Get();

        Task.Run(async () =>
        {
            try
            {
                var body = JsonSerializer.Serialize(new { token = tokenStr, machine_id = machineId });
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"{ServerBaseUrl}/api/refresh", content);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<ServerResponse>(json, _jsonOpts);

                    if (!string.IsNullOrEmpty(data?.Token))
                    {
                        var newToken = SessionTokenVerifier.Verify(data.Token);
                        if (newToken != null)
                        {
                            config.SessionToken = data.Token;
                            config.Save();
                            CurrentToken = newToken;
                            _lastServerContact = DateTime.UtcNow;
                            ActivateFromToken(newToken);
                            DalamudApi.Log.Information("Session token refreshed.");
                            return;
                        }
                    }
                }

                // Handle revocation/expiration from server
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<ServerResponse>(json, _jsonOpts);
                    var error = data?.Error ?? "Key revoked or expired";

                    DalamudApi.Log.Warning($"Token refresh denied: {error}");
                    IsActivated = false;
                    Info = null;
                    CurrentToken = null;
                    config.SessionToken = string.Empty;
                    config.ActivationKey = string.Empty;
                    config.Save();
                    DalamudApi.ChatGui.PrintError($"[Expedition] {error}. Plugin deactivated.");
                    return;
                }

                // Handle machine limit exceeded (key is being used on another device)
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<ServerResponse>(json, _jsonOpts);
                    var error = data?.Error ?? "Machine limit exceeded";

                    DalamudApi.Log.Warning($"Token refresh denied (machine limit): {error}");
                    IsActivated = false;
                    Info = null;
                    CurrentToken = null;
                    config.SessionToken = string.Empty;
                    config.Save();
                    DalamudApi.ChatGui.PrintError(
                        "[Expedition] This key is already in use on another device. " +
                        "Each key may only be used on one machine. Plugin deactivated.");
                    return;
                }

                // Other errors — fail-open, keep current token
                DalamudApi.Log.Warning($"Token refresh failed (HTTP {(int)response.StatusCode}). Keeping current token.");
            }
            catch (Exception ex)
            {
                // Fail-open: if server unreachable, keep the current token
                DalamudApi.Log.Warning($"Token refresh failed: {ex.Message}");
            }
            finally
            {
                _refreshInProgress = false;
            }
        });
    }

    // ── Private helpers ──

    private static void ActivateFromToken(SessionToken token)
    {
        IsActivated = true;
        Info = new ActivationInfo
        {
            KeyId = token.KeyId,
            IssuedAt = token.IssuedAt,
            ExpiresAt = token.ExpiresAt,
            Flags = token.Flags,
        };
    }

    private static async Task<ServerValidationResult> ValidateWithServer(string rawKey)
    {
        var machineId = MachineFingerprint.Get();
        var pluginVersion = typeof(Expedition).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

        var body = JsonSerializer.Serialize(new
        {
            key = rawKey,
            machine_id = machineId,
            plugin_version = pluginVersion,
        });

        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync($"{ServerBaseUrl}/api/validate", content);
        var json = await response.Content.ReadAsStringAsync();
        var data = JsonSerializer.Deserialize<ServerResponse>(json, _jsonOpts);

        if (response.IsSuccessStatusCode && !string.IsNullOrEmpty(data?.Token))
        {
            var token = SessionTokenVerifier.Verify(data.Token);
            if (token != null)
            {
                return new ServerValidationResult
                {
                    IsValid = true,
                    Token = token,
                    TokenString = data.Token,
                };
            }

            return new ServerValidationResult
            {
                IsValid = false,
                ErrorMessage = "Server returned an invalid token.",
            };
        }

        // Map HTTP status codes to user-friendly messages
        var error = data?.Error ?? "Unknown server error";
        return new ServerValidationResult
        {
            IsValid = false,
            ErrorMessage = error,
        };
    }

    private sealed class ServerResponse
    {
        public string? Token { get; set; }
        public string? Error { get; set; }
    }

    private sealed class ServerValidationResult
    {
        public bool IsValid { get; init; }
        public string ErrorMessage { get; init; } = string.Empty;
        public SessionToken? Token { get; init; }
        public string TokenString { get; init; } = string.Empty;
    }
}
