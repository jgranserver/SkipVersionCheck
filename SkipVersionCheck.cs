using System.IO;
using System.Text;

using Terraria;
using Terraria.ID;
using TerrariaApi.Server;

using TShockAPI;

namespace SkipVersionCheck;

/// <summary>
/// Allows any Terraria client within a compatible release range to connect
/// by rewriting the version string in the packet buffer so TShock handles the
/// connection normally (SSC, auth, state all initialize correctly).
/// Also provides basic protocol translation (item filtering, journey mode fix)
/// so that clients on nearby patch versions can play without desync.
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
    public override Version Version => new(2, 5, 0);

    public SkipVersionCheck(Main game) : base(game)
    {
        Order = -1;
    }

    public override void Initialize()
    {
        // Use int.MinValue priority so our handler runs FIRST in the NetGetData chain.
        // In TerrariaApi, handlers are sorted by priority in ascending order, so
        // int.MinValue runs before all other handlers (including TShock's).
        // This ensures we rewrite the version in the buffer BEFORE TShock reads it.
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
    /// Rewrite the version string in the payload portion of the buffer so that
    /// vanilla Terraria and TShock see a matching version.
    /// The payload at args.Index starts with a BinaryWriter-encoded string:
    ///   [7-bit encoded length][UTF-8 string bytes]
    /// Since all version strings are "Terraria" + 3 digits (11 chars), the length
    /// prefix is always 1 byte (0x0B) and the total payload size never changes.
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

        // If version matches server, nothing to do.
        if (clientRelease == Main.curRelease)
        {
            _clientVersions[playerIndex] = -1; // same as server
            return;
        }

        // --- Rewrite the version string directly in the buffer ---
        string serverVersion = "Terraria" + Main.curRelease;
        byte[] newStringBytes = Encoding.UTF8.GetBytes(serverVersion);

        int offset = args.Index;

        // Overwrite the 7-bit length prefix (1 byte for strings < 128 chars)
        args.Msg.readBuffer[offset] = (byte)newStringBytes.Length;

        // Overwrite the string content
        Buffer.BlockCopy(newStringBytes, 0, args.Msg.readBuffer, offset + 1, newStringBytes.Length);

        string label = KnownVersions.TryGetValue(clientRelease, out string? ver)
            ? ver : $"release {clientRelease}";

        TShock.Log.ConsoleInfo(
            $"[SkipVersionCheck] Rewrote client (index {playerIndex}) version " +
            $"from {clientVersion} ({label}) to {serverVersion}. " +
            $"TShock will handle connect normally.");

        // Do NOT set args.Handled — let TShock/vanilla process the rewritten packet.
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

        // The game mode flags byte is at the end of the PlayerInfo packet payload.
        // Bit 3 (value 8) = journey mode flag.
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
                gameModeFlags &= 247; // clear bit 3
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
