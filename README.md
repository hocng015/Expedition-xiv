<div align="center">
  <img src="Expedition/Images/icon.png" alt="Expedition" width="128" />
  <h1>Expedition</h1>
</div>

## About

This is a **personal hobby project** developed for non-profit purposes. The source code is open and available under the [GPL-3.0 license](LICENSE) for anyone who is interested in learning from, contributing to, or building upon it.

## Authentication & Server Communication

Expedition includes an activation key system. This is **not** a monetization mechanism. The authentication layer exists solely to prevent unauthorized distribution of access to the software without proper consent. Anyone who wishes to host the bot backend themselves is free to do so — the server-side validation ensures that whoever operates the backend retains control over how access to their hosted instance is granted.

In short: the source is open, but distributing access to a running instance should be at the discretion of whoever is hosting it.

### What the plugin sends to the activation server

When you activate or refresh your session, the plugin contacts `https://expedition-tsukio.duckdns.org` with the following:

| Request | Data sent | Purpose |
|---------|-----------|---------|
| **POST `/api/validate`** | Activation key (`EXP-...`), machine ID, plugin version | Initial key validation — returns a signed session token |
| **POST `/api/refresh`** | Session token (`EST-...`), machine ID | Token refresh (every 4 hours) — returns a new session token |

**Machine ID** — A SHA-256 hash derived from your Windows machine name, machine GUID, and OS username (`Activation/MachineFingerprint.cs`). This is a one-way hash; the raw values are never sent to the server. It is used to bind one activation key to one device.

**Session tokens** — Ed25519-signed tokens issued by the server. The plugin verifies the signature locally using the server's embedded public key (`Activation/SessionTokenVerifier.cs`). Tokens expire and are auto-refreshed. A 24-hour grace period allows offline use after the last successful server contact.

**Stored locally** — Your activation key, session token, and machine ID are saved in plaintext in Dalamud's plugin configuration directory (standard Dalamud config path). No additional encryption is applied beyond Windows user-level file permissions.

### Third-party API calls

The plugin also contacts these **public, read-only APIs** for game data — no activation key or personal data is sent with these requests:

| Service | URL | Purpose |
|---------|-----|---------|
| [Universalis](https://universalis.app/) | `universalis.app/api/v2` | Market board price and sale history |
| [Garland Tools](https://www.garlandtools.org/) | `garlandtools.org/db/doc/...` | Item drop sources, mob locations, NPC vendor data |
| Expedition Bot | `/api/insights?dc=...` | Pre-computed market insight rankings (no auth required) |

### Self-hosting

All server-side validation is designed so that anyone can host their own backend. If you choose to self-host, you control the activation keys and who has access. The source code for the activation flow is fully readable in the `Activation/` directory.

## Building

Requires .NET 10 and Dalamud SDK 14.0.1.

```
dotnet restore
dotnet build --configuration Release
```

## License

GPL-3.0 — see [LICENSE](LICENSE) for details.
