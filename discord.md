## Note: This is vibe coded plugin but it worked.

A TShock plugin that allows Terraria clients on different patch versions to connect to your server, with basic protocol translation to prevent desync.

## Features

- **Version check bypass** — completely bypasses Terraria's built-in version check so clients on different patch versions can connect
- **Per-client version tracking** — tracks each connected client's release number
- **Item filtering** — silently filters out item IDs that the server doesn't recognize, preventing crashes and desync from newer-version clients
- **Future-proof** — accepts any client with release  (v1.4.5.0+), no need to update for each new Terraria patch

## How It Works

When a client connects, Terraria sends a version string (e.g. for v1.4.5.3). If it doesn't exactly match the server's version, the connection is rejected.

This plugin intercepts the `ConnectRequest` packet and, if the client's version is within the supported range, **handles the connection handshake itself** — bypassing Terraria's version check entirely and manually advancing the connection state.

For cross-version clients, the plugin also:
- Filters `PlayerSlot` and `ItemDrop` packets to replace unknown item IDs with empty (0)
- Tracks client versions to enable version-aware packet handling

## Supported Versions
|  v1.4.5.0   |
|  v1.4.5.3   |
|  v1.4.5.5+ |

Future versions are automatically accepted — no plugin update required.

## Installation

1. Build the plugin: `dotnet build -c Release`
2. Copy `bin/Release/net9.0/SkipVersionCheck.dll` into your TShock `ServerPlugins` folder.
3. Restart the server.

https://github.com/jgranserver/SkipVersionCheck/releases