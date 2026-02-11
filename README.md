# SkipVersionCheck

A TShock plugin that allows any Terraria client within the **1.4.5.x** family to connect, regardless of exact patch version.

## How It Works

When a client connects, Terraria sends a version string (e.g. `"Terraria316"` for v1.4.5.3). If it doesn't exactly match the server's version, the connection is rejected.

This plugin intercepts the connection packet and, if the client's version falls within the 1.4.5.x release range, rewrites it to match the server — so v1.4.5.0 clients can join a v1.4.5.3 server (and vice versa).

## Supported Versions

Any Terraria release in the **1.4.5.x** family, including:

| Release | Version   |
|---------|-----------|
| 315     | v1.4.5.0  |
| 316     | v1.4.5.3  |

Clients outside this range are rejected normally.

## Installation

1. Build the plugin: `dotnet build -c Release`
2. Copy `bin/Release/net9.0/SkipVersionCheck.dll` into your TShock `ServerPlugins` folder.
3. Restart the server.

## Requirements

- TShock 6.0.0+ (for Terraria 1.4.5.x)
- .NET 9.0
