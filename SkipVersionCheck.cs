using System.IO;
using System.Text;

using Microsoft.Xna.Framework;

using Terraria;
using Terraria.ID;
using Terraria.Net.Sockets;
using TerrariaApi.Server;

using TShockAPI;

namespace SkipVersionCheck;

/// <summary>
/// Allows any Terraria client within a compatible release range to connect
/// by bypassing the built-in version check and manually handling the
/// connection handshake. Provides protocol translation (NetModule filtering,
/// item filtering, journey mode fix) for cross-version play.
/// </summary>
[ApiVersion(2, 1)]
public class SkipVersionCheck : TerrariaPlugin
{
    // Friendly labels for logging.
    private static readonly Dictionary<int, string> KnownVersions = new()
    {
        { 315, "v1.4.5.0" },
        { 316, "v1.4.5.3" },
        { 317, "v1.4.5.5" },
        { 318, "v1.4.5.5" },
        { 319, "v1.4.5.6" },
    };

    // Max item IDs per release version (for outgoing packet filtering).
    private static readonly Dictionary<int, int> MaxItems = new()
    {
        { 315, 6145 },
        { 316, 6145 },
        { 317, 6145 },
        { 318, 6145 },
        { 319, 6147 },   // +2 items: Music Box (Rainbow Boulder)=6145, Music Box (Silence)=6146
    };

    // Protocol version thresholds for packet format changes.
    // Spawn packet (12) added numberOfDeathsPVE, numberOfDeathsPVP, team
    // fields starting in release 316 (v1.4.5.3).
    private const int SpawnPacketV2Release = 316;

    // Track each client's release number. -1 = same as server, 0 = not connected.
    private readonly int[] _clientVersions = new int[Main.maxPlayers + 1];

    // The server's max item ID.
    private int _serverMaxItemId;

    // Plugin config.
    private PluginConfig _config = new();

    // Whether MonoMod hooks were successfully registered.
    private bool _monoModAvailable;

    // Singleton instance for NetModuleHandler to access.
    public static SkipVersionCheck? Instance { get; private set; }

    public override string Name => "SkipVersionCheck";
    public override string Author => "Jgran";
    public override string Description =>
        "Allows compatible Terraria clients to connect regardless of exact patch version, " +
        "with full protocol translation for cross-version play.";
    public override Version Version => new(2, 14, 0);

    public SkipVersionCheck(Main game) : base(game)
    {
        Order = 1;
        Instance = this;
    }

    public override void Initialize()
    {
        _config = PluginConfig.Load();

        // Hook NetManager for outgoing NetModule packet filtering (requires MonoMod)
        try
        {
            On.Terraria.Net.NetManager.Broadcast_NetPacket_int += NetModuleHandler.OnBroadcast;
            On.Terraria.Net.NetManager.SendToClient += NetModuleHandler.OnSendToClient;
            _monoModAvailable = true;
        }
        catch (Exception ex) when (ex is TypeLoadException or MissingMethodException or FileNotFoundException or FileLoadException)
        {
            _monoModAvailable = false;
            TShock.Log.ConsoleWarn(
                "[SkipVersionCheck] MonoMod hooks unavailable — outgoing NetModule " +
                $"packet filtering is DISABLED. Reason: {ex.GetType().Name}: {ex.Message}");
        }

        // Hook incoming packets (run first to intercept before TShock)
        ServerApi.Hooks.NetGetData.Register(this, OnGetData, int.MinValue);
        // Late handler runs AFTER TShock's handlers — blocks vanilla for cross-version PlayerInfo
        ServerApi.Hooks.NetGetData.Register(this, OnGetDataLate, int.MaxValue);
        ServerApi.Hooks.GamePostInitialize.Register(this, OnPostInitialize);
        ServerApi.Hooks.ServerLeave.Register(this, OnLeave);

        // Outgoing packet translation (always active) + debug logging
        ServerApi.Hooks.NetSendData.Register(this, OnSendData, int.MinValue);

        if (_config.DebugLogging)
        {
            // Diagnostic: log ServerJoin (fires when CC2 arrives)
            ServerApi.Hooks.ServerJoin.Register(this, OnServerJoin, int.MinValue);
        }

        // Register reload command
        Commands.ChatCommands.Add(new Command("skipversioncheck.admin", ReloadCommand,
            "svcreload")
        {
            HelpText = "Reloads the SkipVersionCheck configuration."
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_monoModAvailable)
            {
                On.Terraria.Net.NetManager.Broadcast_NetPacket_int -= NetModuleHandler.OnBroadcast;
                On.Terraria.Net.NetManager.SendToClient -= NetModuleHandler.OnSendToClient;
            }

            ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
            ServerApi.Hooks.NetGetData.Deregister(this, OnGetDataLate);
            ServerApi.Hooks.GamePostInitialize.Deregister(this, OnPostInitialize);
            ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
            ServerApi.Hooks.NetSendData.Deregister(this, OnSendData);
            ServerApi.Hooks.ServerJoin.Deregister(this, OnServerJoin);

            Commands.ChatCommands.RemoveAll(c =>
                c.Names.Contains("svcreload"));
        }
        base.Dispose(disposing);
    }

    private void ReloadCommand(CommandArgs args)
    {
        _config = PluginConfig.Load();
        args.Player.SendSuccessMessage(
            "[SkipVersionCheck] Configuration reloaded. " +
            $"DebugLogging={_config.DebugLogging}, MinSupportedRelease={_config.MinSupportedRelease}");
    }

    private void OnPostInitialize(EventArgs args)
    {
        _serverMaxItemId = ItemID.Count;

        StringBuilder sb = new StringBuilder()
            .Append("[SkipVersionCheck] Active — ")
            .Append($"Server curRelease: {Main.curRelease}, ")
            .Append($"versionNumber: {Main.versionNumber}, ")
            .Append($"maxItemId: {_serverMaxItemId}\n")
            .Append("[SkipVersionCheck] Whitelisted versions: ")
            .Append(string.Join(", ", KnownVersions.Values));

        if (_config.DebugLogging)
            sb.Append("\n[SkipVersionCheck] Debug logging is ENABLED.");

        TShock.Log.ConsoleInfo(sb.ToString());
    }

    private void OnLeave(LeaveEventArgs args)
    {
        if (args.Who >= 0 && args.Who < _clientVersions.Length)
        {
            int wasVersion = _clientVersions[args.Who];
            _clientVersions[args.Who] = 0;
            if (wasVersion > 0 && _config.DebugLogging)
            {
                TShock.Log.ConsoleInfo(
                    $"[SkipVersionCheck] Cross-version client {args.Who} " +
                    $"(release {wasVersion}) disconnected.");
            }
        }
    }

    // ───────────── Debug-only hooks ─────────────

    /// <summary>
    /// Diagnostic: logs when ContinueConnecting2(8) fires InvokeServerJoin.
    /// Only registered when DebugLogging is enabled.
    /// </summary>
    private void OnServerJoin(JoinEventArgs args)
    {
        int who = args.Who;
        if (who >= 0 && who < 256 && IsCrossVersionClient(who))
        {
            TShock.Log.ConsoleInfo(
                $"[SkipVersionCheck] DEBUG ServerJoin: client={who}, " +
                $"state={Netplay.Clients[who].State}, " +
                $"handled={args.Handled}");
        }
    }

    /// <summary>
    /// Outgoing packet handler: translates packets for cross-version clients.
    /// Also logs outgoing packets in debug mode.
    /// </summary>
    private void OnSendData(SendDataEventArgs args)
    {
        if (args.Handled)
            return;

        // Debug logging for cross-version clients in connection phase
        if (_config.DebugLogging)
        {
            int target = args.remoteClient;
            if (target >= 0 && target < 256)
            {
                var clientState = Netplay.Clients[target].State;
                if (clientState < 10 && IsCrossVersionClient(target))
                {
                    TShock.Log.ConsoleInfo(
                        $"[SkipVersionCheck] DEBUG SEND pkt={args.MsgId}({(int)args.MsgId}) " +
                        $"to={target} state={clientState}");
                }
            }
        }

        // Translate outgoing packets for cross-version clients
        switch (args.MsgId)
        {
            case PacketTypes.PlayerSpawn:
                HandleOutgoingSpawnPacket(args);
                break;
        }
    }

    // ───────────── Public accessors for NetModuleHandler ─────────────

    /// <summary>Get the stored version for a connected client.</summary>
    public int GetClientVersion(int playerIndex)
    {
        if (playerIndex < 0 || playerIndex >= _clientVersions.Length)
            return 0;
        return _clientVersions[playerIndex];
    }

    /// <summary>Get the max item ID that a client version supports.</summary>
    public int GetMaxItemsForVersion(int clientVersion)
    {
        return MaxItems.TryGetValue(clientVersion, out int max) ? max : _serverMaxItemId;
    }

    // ───────────────────── Incoming packets ─────────────────────

    private void OnGetData(GetDataEventArgs args)
    {
        if (args.Handled)
            return;

        // Debug: log all packets from clients in connection phase
        if (_config.DebugLogging)
        {
            int debugWho = args.Msg.whoAmI;
            if (debugWho >= 0 && debugWho < 256)
            {
                var clientState = Netplay.Clients[debugWho].State;
                if (clientState < 10)
                {
                    TShock.Log.ConsoleInfo(
                        $"[SkipVersionCheck] DEBUG RECV pkt={args.MsgID}({(int)args.MsgID}) " +
                        $"client={debugWho} state={clientState}");
                }
            }
        }

        switch (args.MsgID)
        {
            case PacketTypes.ConnectRequest:
                HandleConnectRequest(args);
                break;

            case PacketTypes.PlayerInfo:
                HandlePlayerInfo(args);
                break;

            case PacketTypes.PlayerSpawn:
                HandleIncomingSpawnPacket(args);
                break;

            case PacketTypes.ItemDrop:
                HandleIncomingItemDrop(args);
                break;
        }
    }

    /// <summary>
    /// Late handler (int.MaxValue priority) — runs AFTER TShock's handlers.
    /// For cross-version clients, blocks vanilla's case 4 (PlayerInfo) from running
    /// and sends WorldInfo to unblock the client's handshake.
    /// </summary>
    private void OnGetDataLate(GetDataEventArgs args)
    {
        if (args.Handled)
            return;

        if (args.MsgID != PacketTypes.PlayerInfo)
            return;

        int who = args.Msg.whoAmI;
        if (!IsCrossVersionClient(who))
            return;

        // Block vanilla's case 4 from running — it doesn't handle cross-version
        // clients correctly since we bypassed case 1 (ConnectRequest).
        args.Handled = true;

        // Parse and apply all PlayerInfo fields (appearance + extraAccessory)
        // for this cross-version client. TShock's HandlePlayerInfo can't parse
        // cross-version packets (ReceivedInfo stays False), so we do it here.
        ApplyPlayerInfoFields(who, args.Msg.readBuffer, args.Index, args.Length);

        if (_config.DebugLogging)
        {
            string nameForLog = Main.player[who]?.name ?? "(null)";
            TShock.Log.ConsoleInfo(
                $"[SkipVersionCheck] DEBUG OnGetDataLate: blocked vanilla case 4, " +
                $"client={who}, name='{nameForLog}', state={Netplay.Clients[who].State}");
        }

        // The v1.4.5.5 client waits for WorldInfo after PlayerInfo before sending
        // any more packets. Advance state and send WorldInfo to unblock the client.
        if (Netplay.Clients[who].State == 1)
        {
            Netplay.Clients[who].State = 2;
            NetMessage.SendData((int)PacketTypes.WorldInfo, who);

            if (_config.DebugLogging)
            {
                TShock.Log.ConsoleInfo(
                    $"[SkipVersionCheck] DEBUG Sent WorldInfo(7) to client {who}, " +
                    $"state now={Netplay.Clients[who].State}");
            }
        }
    }

    /// <summary>
    /// Bypass the version check for cross-version clients.
    ///
    /// Execution order in HookManager.InvokeNetGetData for ConnectRequest:
    ///   1. InvokeServerConnect fires → TShock.OnConnect creates TSPlayer
    ///   2. NetGetData.Invoke fires → THIS handler runs
    ///   3. If args.Handled, vanilla processing is skipped
    ///
    /// We skip vanilla (which would reject the version) and manually set
    /// the connection state + send ContinueConnecting. TShock's own
    /// HandleConnecting (on packet 8) handles password prompts and login.
    /// We do NOT create a TSPlayer — TShock already did in step 1.
    /// </summary>
    private void HandleConnectRequest(GetDataEventArgs args)
    {
        string clientVersion;
        using (var reader = new BinaryReader(
            new MemoryStream(args.Msg.readBuffer, args.Index, args.Length)))
        {
            clientVersion = reader.ReadString();
        }

        if (!clientVersion.StartsWith("Terraria"))
        {
           args.Handled = true;
            return;
        }
        string releaseStr = clientVersion.Substring(8);
        if (!int.TryParse(releaseStr, out int clientRelease))
            return;

        // Below minimum — let vanilla reject it.
        if (clientRelease < _config.MinSupportedRelease)
        {
            if (_config.DebugLogging)
            {
                TShock.Log.ConsoleInfo(
                    $"[SkipVersionCheck] Client (index {args.Msg.whoAmI}) version " +
                    $"{clientVersion} (release {clientRelease}) below minimum {_config.MinSupportedRelease}.");
            }
            return;
        }

        // Store client version.
        int playerIndex = args.Msg.whoAmI;
        _clientVersions[playerIndex] = clientRelease;

        // If version matches server, let vanilla handle it normally.
        if (clientRelease == Main.curRelease)
        {
            _clientVersions[playerIndex] = -1; // same as server
            return;
        }

        string label = KnownVersions.TryGetValue(clientRelease, out string? ver)
            ? ver : $"release {clientRelease}";

        TShock.Log.ConsoleInfo(
            $"[SkipVersionCheck] Cross-version client (index {playerIndex}) " +
            $"{clientVersion} ({label}) connecting to server {Main.curRelease}.");

        // TShock's OnConnect already created TSPlayer via ServerConnect hook.
        // Set connection state manually (vanilla would have done this).
        Netplay.Clients[playerIndex].State = 1;

        // Send ContinueConnecting with the correct player slot index.
        NetMessage.SendData((int)PacketTypes.ContinueConnecting, playerIndex, -1,
            null, playerIndex);

        if (_config.DebugLogging)
        {
            TShock.Log.ConsoleInfo(
                $"[SkipVersionCheck] DEBUG Sent ContinueConnecting(3) to client {playerIndex}, " +
                $"state={Netplay.Clients[playerIndex].State}");
        }

        // Skip vanilla processing (which would reject the version mismatch).
        args.Handled = true;
    }

    /// <summary>
    /// Handle PlayerInfo packet for cross-version compatibility.
    /// Fixes journey mode flag so cross-version clients match the server's game mode.
    /// Also extracts extraAccessory (Demon Heart) from the packet since TShock's
    /// HandlePlayerInfo can't parse cross-version packets (ReceivedInfo stays False).
    /// </summary>
    private void HandlePlayerInfo(GetDataEventArgs args)
    {
        int who = args.Msg.whoAmI;
        if (!IsCrossVersionClient(who))
            return;

        // --- Journey mode fix (existing logic) ---
        int flagsIndex = args.Index + args.Length - 1;
        if (flagsIndex < 0 || flagsIndex >= args.Msg.readBuffer.Length)
            return;

        ref byte gameModeFlags = ref args.Msg.readBuffer[flagsIndex];

        if (Main.GameMode == GameModeID.Creative)
        {
            if ((gameModeFlags & 8) != 8)
            {
                TShock.Log.ConsoleInfo(
                    $"[SkipVersionCheck] Enabled journey mode flag for cross-version client {who}");
                gameModeFlags |= 8;
                if (Main.ServerSideCharacter)
                {
                    NetMessage.SendData(4, who, -1, null, who);
                }
            }
        }
        else
        {
            if ((gameModeFlags & 8) == 8)
            {
                TShock.Log.ConsoleInfo(
                    $"[SkipVersionCheck] Disabled journey mode flag for cross-version client {who}");
                gameModeFlags &= 247;
            }
        }

        // Apply all PlayerInfo fields (appearance + extraAccessory).
        // Also runs in OnGetDataLate, but we call it here too because
        // HandlePlayerInfo fires first (int.MinValue) and sets up flags
        // before TShock's handler runs at priority 0.
        ApplyPlayerInfoFields(who, args.Msg.readBuffer, args.Index, args.Length);

        // WorldInfo send and vanilla blocking is handled in OnGetDataLate
        // (int.MaxValue priority) after TShock finishes processing PlayerInfo.
    }

    /// <summary>
    /// Filter dropped items with IDs the server doesn't recognize.
    /// </summary>
    private void HandleIncomingItemDrop(GetDataEventArgs args)
    {
        int who = args.Msg.whoAmI;
        if (!IsCrossVersionClient(who))
            return;

        if (args.Length < 22)
            return;

        int offset = args.Index;
        int typeOffset = offset + 20;
        short itemType = BitConverter.ToInt16(args.Msg.readBuffer, typeOffset);

        if (itemType >= _serverMaxItemId)
        {
            if (_config.DebugLogging)
            {
                TShock.Log.ConsoleInfo(
                    $"[SkipVersionCheck] DEBUG Filtered unsupported item {itemType} from client {who}");
            }
            args.Msg.readBuffer[typeOffset] = 0;
            args.Msg.readBuffer[typeOffset + 1] = 0;
        }
    }

    // ───────────── Outgoing packet translation ─────────────

    /// <summary>
    /// Translates outgoing spawn packet (12) for cross-version clients.
    /// v1.4.5.5 (release 318) added numberOfDeathsPVE, numberOfDeathsPVP, and team
    /// fields that older clients don't understand. We strip these fields
    /// and send the packet in the old format.
    /// </summary>
    private void HandleOutgoingSpawnPacket(SendDataEventArgs args)
    {
        // number = player index, number2 = spawn context
        int playerIndex = args.number;
        if (playerIndex < 0 || playerIndex >= Main.maxPlayers)
            return;

        Player player = Main.player[playerIndex];
        if (player == null)
            return;

        // Build the old-format spawn packet (without death counters + team)
        byte[] oldPacket = new PacketFactory()
            .SetType(12)
            .PackByte((byte)playerIndex)
            .PackInt16((short)player.SpawnX)
            .PackInt16((short)player.SpawnY)
            .PackInt32(player.respawnTimer)
            .PackByte((byte)args.number2) // spawnContext
            .GetByteData();

        // Determine which clients need the translated packet
        int remoteClient = args.remoteClient;
        int ignoreClient = args.ignoreClient;
        bool anyCrossVersion = false;

        if (remoteClient >= 0 && remoteClient < 256)
        {
            // Targeted send to a single client
            if (NeedsSpawnTranslation(remoteClient))
            {
                SendRawPacket(remoteClient, oldPacket);
                args.Handled = true;
                anyCrossVersion = true;
            }
        }
        else
        {
            // Broadcast: check if any connected cross-version client needs translation
            for (int i = 0; i < Main.maxPlayers; i++)
            {
                if (i == ignoreClient || !Netplay.Clients[i].IsConnected())
                    continue;

                if (NeedsSpawnTranslation(i))
                {
                    SendRawPacket(i, oldPacket);
                    anyCrossVersion = true;
                }
            }

            // If we sent to any cross-version clients, we need to handle this carefully.
            // We can't set Handled = true because native clients still need the normal packet.
            // The native clients will get the normal packet through default processing.
            // But cross-version clients will get a duplicate if we don't suppress.
            // Solution: Only suppress if ALL connected targets are cross-version.
            if (anyCrossVersion)
            {
                bool allCrossVersion = true;
                for (int i = 0; i < Main.maxPlayers; i++)
                {
                    if (i == ignoreClient || !Netplay.Clients[i].IsConnected())
                        continue;
                    if (!NeedsSpawnTranslation(i))
                    {
                        allCrossVersion = false;
                        break;
                    }
                }
                if (allCrossVersion)
                    args.Handled = true;
            }
        }

        if (anyCrossVersion && _config.DebugLogging)
        {
            TShock.Log.ConsoleInfo(
                $"[SkipVersionCheck] Translated outgoing spawn packet for player {playerIndex}, " +
                $"handled={args.Handled}");
        }
    }

    /// <summary>
    /// Handles incoming spawn packets from cross-version clients.
    /// Older clients send a shorter packet without numberOfDeathsPVE,
    /// numberOfDeathsPVP, and team. We pad the buffer so vanilla can
    /// process it normally.
    /// </summary>
    private void HandleIncomingSpawnPacket(GetDataEventArgs args)
    {
        int who = args.Msg.whoAmI;
        if (!NeedsSpawnTranslation(who))
            return;

        // Old format: [byte playerid][short SpawnX][short SpawnY][int respawnTimer][byte context]
        //           = 1 + 2 + 2 + 4 + 1 = 10 bytes payload
        // New format: [byte playerid][short SpawnX][short SpawnY][int respawnTimer]
        //             [short deathsPVE][short deathsPVP][byte team][byte context]
        //           = 1 + 2 + 2 + 4 + 2 + 2 + 1 + 1 = 15 bytes payload
        int payloadLen = args.Length - 1; // subtract msg type byte
        int expectedOldLen = 10;
        int expectedNewLen = 15;

        if (payloadLen >= expectedNewLen)
            return; // Already new format, let vanilla handle it

        if (payloadLen < expectedOldLen)
            return; // Malformed packet, let vanilla reject it

        try
        {
            int offset = args.Index;

            // Read the old format fields
            byte playerId = args.Msg.readBuffer[offset];
            short spawnX = BitConverter.ToInt16(args.Msg.readBuffer, offset + 1);
            short spawnY = BitConverter.ToInt16(args.Msg.readBuffer, offset + 3);
            int respawnTimer = BitConverter.ToInt32(args.Msg.readBuffer, offset + 5);
            byte spawnContext = args.Msg.readBuffer[offset + 9];

            // Read the player's current team (fallback to 0)
            byte team = 0;
            if (who >= 0 && who < Main.maxPlayers && Main.player[who] != null)
                team = (byte)Main.player[who].team;

            // Rewrite the buffer in new format:
            // [byte playerid][short SpawnX][short SpawnY][int respawnTimer]
            // [short deathsPVE=0][short deathsPVP=0][byte team][byte context]
            args.Msg.readBuffer[offset] = playerId;
            BitConverter.GetBytes(spawnX).CopyTo(args.Msg.readBuffer, offset + 1);
            BitConverter.GetBytes(spawnY).CopyTo(args.Msg.readBuffer, offset + 3);
            BitConverter.GetBytes(respawnTimer).CopyTo(args.Msg.readBuffer, offset + 5);
            BitConverter.GetBytes((short)0).CopyTo(args.Msg.readBuffer, offset + 9);  // numberOfDeathsPVE
            BitConverter.GetBytes((short)0).CopyTo(args.Msg.readBuffer, offset + 11); // numberOfDeathsPVP
            args.Msg.readBuffer[offset + 13] = team;        // team
            args.Msg.readBuffer[offset + 14] = spawnContext; // context

            if (_config.DebugLogging)
            {
                TShock.Log.ConsoleInfo(
                    $"[SkipVersionCheck] Padded incoming spawn packet from client {who}: " +
                    $"spawn=({spawnX},{spawnY}), respawnTimer={respawnTimer}, " +
                    $"team={team}, context={spawnContext}");
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError(
                $"[SkipVersionCheck] Error translating incoming spawn packet from client {who}: {ex.Message}");
        }
    }

    // ───────────── Cross-version PlayerInfo parser ─────────────

    /// <summary>
    /// Parses and applies all fields from a cross-version PlayerInfo (packet 4)
    /// buffer to Main.player[who]. This covers appearance fields (skinVariant,
    /// hair, hairDye, colors, voiceVariant, voicePitchOffset), name, and
    /// extraAccessory (Demon Heart). Required because TShock's HandlePlayerInfo
    /// can't parse cross-version packets, and KeepPlayerAppearance depends on
    /// the client's appearance data being populated on TPlayer.
    ///
    /// v1.4.5.5 PlayerInfo layout after the message type byte:
    ///   byte   playerId
    ///   byte   skinVariant
    ///   byte   voiceVariant      (added in v1.4.5.5)
    ///   float  voicePitchOffset  (added in v1.4.5.5)
    ///   byte   hair
    ///   string name              (7-bit length-prefixed)
    ///   byte   hairDye
    ///   ushort hideVisualFlags
    ///   byte   hideMisc
    ///   Color  hairColor         (3 bytes: R, G, B)
    ///   Color  skinColor         (3 bytes)
    ///   Color  eyeColor          (3 bytes)
    ///   Color  shirtColor        (3 bytes)
    ///   Color  underShirtColor   (3 bytes)
    ///   Color  pantsColor        (3 bytes)
    ///   Color  shoeColor         (3 bytes)
    ///   byte   extraFlags        (bit 2 = extraAccessory / Demon Heart)
    ///   ...remaining fields (difficulty, torches, etc.)
    /// </summary>
    private void ApplyPlayerInfoFields(int who, byte[] readBuffer, int index, int length)
    {
        if (who < 0 || who >= Main.maxPlayers || Main.player[who] == null)
            return;

        try
        {
            using var ms = new MemoryStream(readBuffer, index, length - 1);
            using var br = new BinaryReader(ms);

            Player player = Main.player[who];

            br.ReadByte();                           // playerId (skip)
            byte skinVariant    = br.ReadByte();     // skinVariant
            byte voiceVariant   = br.ReadByte();     // voiceVariant
            float voicePitch    = br.ReadSingle();   // voicePitchOffset
            byte hair           = br.ReadByte();     // hair
            string name         = br.ReadString();   // name (7-bit length-prefixed)
            byte hairDye        = br.ReadByte();     // hairDye
            ushort hideFlags    = br.ReadUInt16();   // hideVisualFlags
            byte hideMisc       = br.ReadByte();     // hideMisc

            // 7 colors × 3 bytes each (R, G, B)
            Color hairColor       = ReadColor(br);
            Color skinColor       = ReadColor(br);
            Color eyeColor        = ReadColor(br);
            Color shirtColor      = ReadColor(br);
            Color underShirtColor = ReadColor(br);
            Color pantsColor      = ReadColor(br);
            Color shoeColor       = ReadColor(br);

            byte extraFlags = br.ReadByte();
            bool extraAccessory = (extraFlags & 4) != 0; // bit 2

            // --- Apply appearance fields ---
            player.skinVariant      = skinVariant;
            player.voiceVariant     = voiceVariant;
            player.voicePitchOffset = voicePitch;
            player.hair             = hair;
            player.hairDye         = hairDye;
            player.hairColor       = hairColor;
            player.skinColor       = skinColor;
            player.eyeColor        = eyeColor;
            player.shirtColor      = shirtColor;
            player.underShirtColor = underShirtColor;
            player.pantsColor      = pantsColor;
            player.shoeColor       = shoeColor;

            // Apply name if not already set
            if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(player.name))
            {
                player.name = name;
                TShock.Log.ConsoleInfo(
                    $"[SkipVersionCheck] Fixed blank player name -> '{name}' for client {who}");
            }

            // Apply hideVisibleAccessory flags
            for (int i = 0; i < player.hideVisibleAccessory.Length && i < 16; i++)
            {
                player.hideVisibleAccessory[i] = (hideFlags & (1 << i)) != 0;
            }

            // Apply hideMisc flags
            player.hideMisc = hideMisc;

            // Demon Heart is permanent — only allow false → true
            bool serverExtraAccessory = player.extraAccessory;
            if (extraAccessory && !serverExtraAccessory)
            {
                player.extraAccessory = true;
                TShock.Log.ConsoleInfo(
                    $"[SkipVersionCheck] Demon Heart extra slot ACTIVATED for client {who}");
            }

            if (_config.DebugLogging)
            {
                TShock.Log.ConsoleInfo(
                    $"[SkipVersionCheck] PlayerInfo applied: client={who} ('{name}') " +
                    $"skin={skinVariant} hair={hair} hairDye={hairDye} " +
                    $"voice={voiceVariant} pitch={voicePitch:F2} " +
                    $"extraFlags=0x{extraFlags:X2} extraAccessory={extraAccessory}");
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError(
                $"[SkipVersionCheck] Error parsing PlayerInfo for client {who}: {ex.Message}");
        }
    }

    /// <summary>Reads 3 bytes (R, G, B) as a Color from the stream.</summary>
    private static Color ReadColor(BinaryReader br)
    {
        byte r = br.ReadByte();
        byte g = br.ReadByte();
        byte b = br.ReadByte();
        return new Color(r, g, b);
    }

    // ───────────────────── Helpers ─────────────────────

    /// <summary>Check if a client is a cross-version client.</summary>
    private bool IsCrossVersionClient(int playerIndex)
    {
        if (playerIndex < 0 || playerIndex >= _clientVersions.Length)
            return false;

        int ver = _clientVersions[playerIndex];
        return ver > 0 && ver != Main.curRelease;
    }

    /// <summary>
    /// Check if a client needs spawn packet translation.
    /// Returns true for cross-version clients with a release below the
    /// threshold where the new spawn packet format was introduced.
    /// </summary>
    private bool NeedsSpawnTranslation(int playerIndex)
    {
        if (playerIndex < 0 || playerIndex >= _clientVersions.Length)
            return false;

        int ver = _clientVersions[playerIndex];
        return ver > 0 && ver < SpawnPacketV2Release;
    }

    /// <summary>Send raw packet bytes to a specific client.</summary>
    private static void SendRawPacket(int clientIndex, byte[] data)
    {
        if (clientIndex < 0 || clientIndex >= 256)
            return;

        var client = Netplay.Clients[clientIndex];
        if (client?.Socket == null || !client.IsConnected())
            return;

        try
        {
            client.Socket.AsyncSend(
                data, 0, data.Length,
                new SocketSendCallback(client.ServerWriteCallBack));
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError(
                $"[SkipVersionCheck] Error sending raw packet to client {clientIndex}: {ex.Message}");
        }
    }
}
