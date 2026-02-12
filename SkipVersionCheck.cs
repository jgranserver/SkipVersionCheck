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
/// connection handshake. Also provides basic protocol translation
/// (item filtering, journey mode fix) for cross-version play.
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
    public override Version Version => new(2, 6, 0);

    public SkipVersionCheck(Main game) : base(game)
    {
        Order = -1;
    }

    public override void Initialize()
    {
        // Run first to intercept ConnectRequest before TShock rejects it.
        ServerApi.Hooks.NetGetData.Register(this, OnGetData, int.MinValue);
        ServerApi.Hooks.GamePostInitialize.Register(this, OnPostInitialize);
        ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
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
    /// 2. Fire the ServerConnect hook (so TShock initializes SSC, bans, etc.)
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
            $"[SkipVersionCheck] Bypassing version check for client (index {playerIndex}) " +
            $"{clientVersion} ({label}). Server curRelease={Main.curRelease}.");

        // --- Replicate vanilla's ConnectRequest handling ---

        // Step 1: Set connection state
        Netplay.Clients[playerIndex].State = 1;

        // Note: We can't easily invoke ServerApi.Hooks.ServerConnect because
        // ConnectEventArgs.Who is read-only in this TShock version.
        // TShock's ServerJoin and other hooks will still fire during the
        // subsequent connection flow (PlayerInfo, etc.).

        // Step 3: Send password request or continue-connecting
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

        // Step 4: Prevent vanilla from processing (it would reject the version)
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
