# SkipVersionCheck

A TShock plugin that allows Terraria clients on different patch versions to connect to your server, with basic protocol translation to prevent desync.

## Features

- **Version check bypass** — completely bypasses Terraria's built-in version check so clients on different patch versions can connect
- **Per-client version tracking** — tracks each connected client's release number
- **Item filtering** — silently filters out item IDs that the server doesn't recognize, preventing crashes and desync from newer-version clients
- **Future-proof** — accepts any client with release ≥ 269 (v1.4.4+), no need to update for each new Terraria patch

## How It Works

When a client connects, Terraria sends a version string (e.g. `"Terraria316"` for v1.4.5.3). If it doesn't exactly match the server's version, the connection is rejected.

This plugin intercepts the `ConnectRequest` packet and, if the client's version is within the supported range, **handles the connection handshake itself** — bypassing Terraria's version check entirely and manually advancing the connection state.

For cross-version clients, the plugin also:
- Filters `PlayerSlot` and `ItemDrop` packets to replace unknown item IDs with empty (0)
- Tracks client versions to enable version-aware packet handling
- Fixes Journey mode sync for cross-version clients

## Supported Versions

Any Terraria release ≥ 269, including:

| Release | Version   |
|---------|-----------|
| 269     | v1.4.4    |
| 279     | v1.4.4.9  |
| 315     | v1.4.5.0  |
| 316     | v1.4.5.3  |
| 317     | v1.4.5.5  |
| 318+    | v1.4.5.5+ |

Future versions are automatically accepted — no plugin update required.

## Installation

1. Build the plugin: `dotnet build -c Release`
2. Copy `bin/Release/net9.0/SkipVersionCheck.dll` into your TShock `ServerPlugins` folder.
3. Restart the server.

## Requirements

- TShock 5.x / 6.0.0+ (for Terraria 1.4.4+)
- .NET 9.0

## Limitations

- **Protocol differences**: While the plugin bypasses the version check and filters items, it cannot translate fundamental protocol changes between major Terraria versions. It works best between minor patch versions (e.g. 1.4.5.3 ↔ 1.4.5.5) where the packet format is identical.
- **New items**: Items that only exist in a newer version will be silently dropped when sent to the server. Players won't lose them client-side, but the server won't process them.
