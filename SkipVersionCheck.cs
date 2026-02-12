using System.IO;
using System.Text;

using Terraria;
using Terraria.ID;
using Terraria.Localization;
using TerrariaApi.Server;

using TShockAPI;

namespace SkipVersionCheck;

/// <summary>
/// Allows any Terraria client within a compatible release range to connect
/// by bypassing the built-in version check entirely, and provides basic
/// protocol translation (item filtering) so that clients on nearby patch
/// versions can play without desync.
/// </summary>
[ApiVersion(2, 1)]
public class SkipVersionCheck : TerrariaPlugin
{
    // Accept any client with release >= this value.
    private const int MinSupportedRelease = 279;

    // Friendly labels for logging.
    private static readonly Dictionary<int, string> KnownVersions = new()
    {
        { 279, "v1.4.4.9" },
        { 315, "v1.4.5.0" },
        { 316, "v1.4.5.3" },
        { 317, "v1.4.5.5" },
        { 318, "v1.4.5.5" },
    };

    // Track each client's release number. -1 = same as server, 0 = not connected.
    private readonly int[] _clientVersions = new int[Main.maxPlayers + 1];

    // The server's max item ID (items with ID >= this are unknown to the server).
    private int _serverMaxItemId;

    public override string Name => "SkipVersionCheck";
    public override string Author => "Jgran";
    public override string Description =>
        "Allows compatible Terraria clients to connect regardless of exact patch version, " +
        "with basic protocol translation for cross-version play.";
    public override Version Version => new(2, 1, 0);

    public SkipVersionCheck(Main game) : base(game)
    {
        // Run before all other plugins so we intercept the packet first.
        Order = int.MaxValue;
    }

    public override void Initialize()
    {
        ServerApi.Hooks.NetGetData.Register(this, OnGetData, int.MaxValue);
        ServerApi.Hooks.NetSendData.Register(this, OnSendData);
        ServerApi.Hooks.GamePostInitialize.Register(this, OnPostInitialize);
        ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
            ServerApi.Hooks.NetSendData.Deregister(this, OnSendData);
            ServerApi.Hooks.GamePostInitialize.Deregister(this, OnPostInitialize);
            ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
        }
        base.Dispose(disposing);
    }

    private void OnPostInitialize(EventArgs args)
    {
        _serverMaxItemId = ItemID.Count;
        TShock.Log.ConsoleInfo(
            $"[SkipVersionCheck] Active — Server curRelease: {Main.curRelease}, " +
            $"versionNumber: {Main.versionNumber}, maxItemId: {_serverMaxItemId}. " +
            $"Accepting any client with release >= {MinSupportedRelease}.");
    }

    private void OnLeave(LeaveEventArgs args)
    {
        if (args.Who >= 0 && args.Who < _clientVersions.Length)
        {
            _clientVersions[args.Who] = 0;
        }
    }

    // ───────────────────── Incoming packets (client → server) ─────────────────────

    private void OnGetData(GetDataEventArgs args)
    {
        if (args.Handled)
            return;

        switch (args.MsgID)
        {
            case PacketTypes.ConnectRequest:
                HandleConnectRequest(args);
                break;

            case PacketTypes.ItemDrop:
                HandleIncomingItemDrop(args);
                break;
        }
    }

    /// <summary>
    /// Bypass the version check entirely for supported clients.
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
            return;

        string releaseStr = clientVersion.Substring(8);
        if (!int.TryParse(releaseStr, out int clientRelease))
            return;

        // Below minimum — let vanilla reject it.
        if (clientRelease < MinSupportedRelease)
        {
            TShock.Log.ConsoleInfo(
                $"[SkipVersionCheck] Client (index {args.Msg.whoAmI}) version " +
                $"{clientVersion} (release {clientRelease}) is below minimum {MinSupportedRelease}. Rejecting.");
            return;
        }

        // Store client version.
        int playerIndex = args.Msg.whoAmI;
        _clientVersions[playerIndex] = clientRelease;

        // If version matches server, let vanilla handle it normally.
        if (clientRelease == Main.curRelease)
        {
            _clientVersions[playerIndex] = -1; // same as server
            TShock.Log.ConsoleInfo(
                $"[SkipVersionCheck] Client (index {playerIndex}) version " +
                $"{clientVersion} matches server. Passing through.");
            return;
        }

        // --- BYPASS the version check entirely ---
        string label = KnownVersions.TryGetValue(clientRelease, out string? ver)
            ? ver : $"release {clientRelease}";

        TShock.Log.ConsoleInfo(
            $"[SkipVersionCheck] Bypassing version check for client (index {playerIndex}) " +
            $"{clientVersion} ({label}). Server curRelease={Main.curRelease}.");

        if (Netplay.ServerPassword != null && Netplay.ServerPassword.Length > 0)
        {
            Netplay.Clients[playerIndex].State = 1;
            NetMessage.SendData(37, playerIndex);
            TShock.Log.ConsoleInfo(
                $"[SkipVersionCheck] Password required. Sent password request to client {playerIndex}.");
        }
        else
        {
            Netplay.Clients[playerIndex].State = 1;
            NetMessage.SendData(3, playerIndex);
            TShock.Log.ConsoleInfo(
                $"[SkipVersionCheck] No password. Sent ContinueConnecting to client {playerIndex}.");
        }

        args.Handled = true;
    }



    /// <summary>
    /// Filter dropped items with IDs the server doesn't recognize.
    /// Packet 21 layout: [short id] [single x] [single y] [single vx] [single vy]
    ///                    [short stacks] [byte prefix] [byte nodelay] [short type]
    /// </summary>
    private void HandleIncomingItemDrop(GetDataEventArgs args)
    {
        int who = args.Msg.whoAmI;
        if (!IsCrossVersionClient(who))
            return;

        // Need at least 22 bytes: 2 + 4 + 4 + 4 + 4 + 2 + 1 + 1 + 2
        if (args.Length < 22)
            return;

        int offset = args.Index;
        // Item type is at offset +20 (2+4+4+4+4+2+1+1)
        int typeOffset = offset + 20;
        short itemType = BitConverter.ToInt16(args.Msg.readBuffer, typeOffset);

        if (itemType >= _serverMaxItemId)
        {
            TShock.Log.ConsoleDebug(
                $"[SkipVersionCheck] Filtered unsupported item {itemType} from client {who} (ItemDrop)");
            args.Msg.readBuffer[typeOffset] = 0;
            args.Msg.readBuffer[typeOffset + 1] = 0;
        }
    }



    // ───────────────────── Outgoing packets (server → client) ─────────────────────

    /// <summary>
    /// Intercept outgoing packets to filter unsupported items being sent to
    /// clients that are on an older version than the server.
    /// </summary>
    private void OnSendData(SendDataEventArgs args)
    {
        if (args.Handled)
            return;

        // For broadcast packets (remoteClient == -1), we can't easily filter
        // per-client. For targeted packets, check if the target is a cross-version
        // client on an older version that might not know about newer items.
        // 
        // The NetSendData hook fires before the packet is serialized, so we only have
        // access to the high-level parameters (number, number2, etc.), not the raw
        // byte buffer. For item packets, the item type is typically in one of the
        // number parameters, but the exact mapping depends on how NetMessage.SendData
        // serializes each packet type.
        //
        // Since the server is on v1.4.5.3 (older) and clients may be on v1.4.5.5
        // (newer), the server won't send items the client doesn't know about because
        // the server simply doesn't have those items. So outgoing filtering is only
        // needed if the SERVER is on the newer version, which would be unusual.
        //
        // For now, we primarily rely on incoming filtering (client → server).
    }

    // ───────────────────── Helpers ─────────────────────

    /// <summary>
    /// Check if a player is a cross-version client (different version from server).
    /// </summary>
    private bool IsCrossVersionClient(int playerIndex)
    {
        if (playerIndex < 0 || playerIndex >= _clientVersions.Length)
            return false;

        int ver = _clientVersions[playerIndex];
        // 0 = not connected, -1 = same as server
        return ver > 0 && ver != Main.curRelease;
    }
}
