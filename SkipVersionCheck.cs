using System.IO;
using System.Text;

using Terraria;
using Terraria.ID;
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
    // Accept any client with release >= this value.
    private const int MinSupportedRelease = 269;

    // Friendly labels for logging.
    private static readonly Dictionary<int, string> KnownVersions = new()
    {
        { 269, "v1.4.4" },
        { 270, "v1.4.4.1" },
        { 271, "v1.4.4.2" },
        { 272, "v1.4.4.3" },
        { 273, "v1.4.4.4" },
        { 274, "v1.4.4.5" },
        { 275, "v1.4.4.6" },
        { 276, "v1.4.4.7" },
        { 277, "v1.4.4.8" },
        { 278, "v1.4.4.8.1" },
        { 279, "v1.4.4.9" },
        { 315, "v1.4.5.0" },
        { 316, "v1.4.5.3" },
        { 317, "v1.4.5.5" },
        { 318, "v1.4.5.5" },
    };

    // Max item IDs per release version (for outgoing packet filtering).
    private static readonly Dictionary<int, int> MaxItems = new()
    {
        { 269, 5453 },
        { 270, 5453 },
        { 271, 5453 },
        { 272, 5453 },
        { 273, 5453 },
        { 274, 5456 },
        { 275, 5456 },
        { 276, 5456 },
        { 277, 5456 },
        { 278, 5456 },
        { 279, 5456 },
        { 315, 6145 },
        { 316, 6145 },
        { 317, 6145 },
        { 318, 6145 },
    };

    // Track each client's release number. -1 = same as server, 0 = not connected.
    private readonly int[] _clientVersions = new int[Main.maxPlayers + 1];

    // The server's max item ID.
    private int _serverMaxItemId;

    // Singleton instance for NetModuleHandler to access.
    public static SkipVersionCheck? Instance { get; private set; }

    public override string Name => "SkipVersionCheck";
    public override string Author => "Jgran";
    public override string Description =>
        "Allows compatible Terraria clients to connect regardless of exact patch version, " +
        "with full protocol translation for cross-version play.";
    public override Version Version => new(2, 7, 0);

    public SkipVersionCheck(Main game) : base(game)
    {
        Order = -1;
        Instance = this;
    }

    public override void Initialize()
    {
        // Hook NetManager for outgoing NetModule packet filtering
        On.Terraria.Net.NetManager.Broadcast_NetPacket_int += NetModuleHandler.OnBroadcast;
        On.Terraria.Net.NetManager.SendToClient += NetModuleHandler.OnSendToClient;

        // Hook incoming packets (run first to intercept before TShock)
        ServerApi.Hooks.NetGetData.Register(this, OnGetData, int.MinValue);
        ServerApi.Hooks.GamePostInitialize.Register(this, OnPostInitialize);
        ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            On.Terraria.Net.NetManager.Broadcast_NetPacket_int -= NetModuleHandler.OnBroadcast;
            On.Terraria.Net.NetManager.SendToClient -= NetModuleHandler.OnSendToClient;

            ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
            ServerApi.Hooks.GamePostInitialize.Deregister(this, OnPostInitialize);
            ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
        }
        base.Dispose(disposing);
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

        TShock.Log.ConsoleInfo(sb.ToString());
    }

    private void OnLeave(LeaveEventArgs args)
    {
        if (args.Who >= 0 && args.Who < _clientVersions.Length)
        {
            _clientVersions[args.Who] = 0;
        }
    }

    // ───────── Public accessors for NetModuleHandler ─────────

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

        switch (args.MsgID)
        {
            case PacketTypes.ConnectRequest:
                HandleConnectRequest(args);
                break;

            case PacketTypes.PlayerInfo:
                HandlePlayerInfo(args);
                break;

            case PacketTypes.ItemDrop:
                HandleIncomingItemDrop(args);
                break;
        }
    }

    /// <summary>
    /// Bypass the version check for cross-version clients by manually handling
    /// the connection handshake. We replicate what vanilla Terraria does:
    /// 1. Set connection state to 1
    /// 2. Construct a proper ConnectRequest packet with the server's version
    ///    using PacketFactory and copy it into the buffer
    /// 3. Send password request or continue-connecting packet
    /// 4. Mark packet as handled so vanilla doesn't reject the version
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
            return;
        }

        string label = KnownVersions.TryGetValue(clientRelease, out string? ver)
            ? ver : $"release {clientRelease}";

        TShock.Log.ConsoleInfo(
            $"[SkipVersionCheck] Cross-version client (index {playerIndex}) " +
            $"{clientVersion} ({label}) connecting to server {Main.curRelease}.");

        // --- Step 1: Set connection state ---
        Netplay.Clients[playerIndex].State = 1;

        // --- Step 2: Construct a ConnectRequest with the server's version ---
        // Use PacketFactory to build a proper packet matching the Crossplay approach.
        byte[] connectRequest = new PacketFactory()
            .SetType(1) // PacketTypes.ConnectRequest
            .PackString($"Terraria{Main.curRelease}")
            .GetByteData();

        // Copy the rewritten packet into the buffer at the packet header start
        Buffer.BlockCopy(connectRequest, 0, args.Msg.readBuffer, args.Index - 3, connectRequest.Length);

        TShock.Log.ConsoleInfo(
            $"[SkipVersionCheck] Rewrote packet buffer: {clientVersion} => Terraria{Main.curRelease} " +
            $"({connectRequest.Length} bytes at offset {args.Index - 3}).");

        // --- Step 3: Send continue-connecting ---
        if (Netplay.ServerPassword != null && Netplay.ServerPassword.Length > 0)
        {
            NetMessage.SendData(37, playerIndex);
            TShock.Log.ConsoleInfo(
                $"[SkipVersionCheck] Password required. Sent password request to client {playerIndex}.");
        }
        else
        {
            NetMessage.SendData(3, playerIndex);
            TShock.Log.ConsoleInfo(
                $"[SkipVersionCheck] No password. Sent ContinueConnecting to client {playerIndex}.");
        }

        // --- Step 4: Bypass vanilla processing ---
        args.Handled = true;
    }

    /// <summary>
    /// Handle PlayerInfo packet for cross-version compatibility.
    /// Fixes journey mode flag so cross-version clients match the server's game mode.
    /// </summary>
    private void HandlePlayerInfo(GetDataEventArgs args)
    {
        int who = args.Msg.whoAmI;
        if (!IsCrossVersionClient(who))
            return;

        int flagsIndex = args.Index + args.Length - 1;
        if (flagsIndex < 0 || flagsIndex >= args.Msg.readBuffer.Length)
            return;

        ref byte gameModeFlags = ref args.Msg.readBuffer[flagsIndex];

        if (Main.GameModeInfo.IsJourneyMode)
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
            TShock.Log.ConsoleDebug(
                $"[SkipVersionCheck] Filtered unsupported item {itemType} from client {who} (ItemDrop)");
            args.Msg.readBuffer[typeOffset] = 0;
            args.Msg.readBuffer[typeOffset + 1] = 0;
        }
    }

    // ───────────────────── Helpers ─────────────────────

    private bool IsCrossVersionClient(int playerIndex)
    {
        if (playerIndex < 0 || playerIndex >= _clientVersions.Length)
            return false;

        int ver = _clientVersions[playerIndex];
        return ver > 0 && ver != Main.curRelease;
    }
}
