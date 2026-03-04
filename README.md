# Expedition

## About

This is a **personal hobby project** developed for non-profit purposes. The source code is open and available under the [GPL-3.0 license](LICENSE) for anyone who is interested in learning from, contributing to, or building upon it.

## Authentication

Expedition includes an activation key system. This is **not** a monetization mechanism. The authentication layer exists solely to prevent unauthorized distribution of access to the software without proper consent. Anyone who wishes to host the bot backend themselves is free to do so — the server-side validation ensures that whoever operates the backend retains control over how access to their hosted instance is granted.

In short: the source is open, but distributing access to a running instance should be at the discretion of whoever is hosting it.

## Building

Requires .NET 10 and Dalamud SDK 14.0.1.

```
dotnet restore
dotnet build --configuration Release
```

## License

GPL-3.0 — see [LICENSE](LICENSE) for details.
