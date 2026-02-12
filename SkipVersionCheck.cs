using System.IO;
using System.Text;

using Terraria;
using Terraria.ID;
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
    public override Version Version => new(2, 2, 0);

    public SkipVersionCheck(Main game) : base(game)
    {
        // Run before all other plugins so we intercept the packet first.
        Order = int.MaxValue;
    }

    public override void Initialize()
    {
        ServerApi.Hooks.NetGetData.Register(this, OnGetData, int.MaxValue);
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

            case PacketTypes.ItemDrop:
                HandleIncomingItemDrop(args);
                break;
        }
    }

    /// <summary>
    /// Rewrite the client's version string in the packet buffer to match the
    /// server's curRelease, then let TShock handle the connect normally.
    /// This ensures TShock fully initializes the player (SSC, auth, state).
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

        // --- REWRITE the version in the buffer to match the server ---
        // This lets TShock's connect handler run normally (SSC init, auth, etc.)
        // instead of us bypassing it with args.Handled = true.
        string serverVersion = "Terraria" + Main.curRelease;
        byte[] serverVersionBytes = Encoding.UTF8.GetBytes(serverVersion);

        int offset = args.Index;
        // BinaryWriter string format: [7-bit encoded length][string bytes]
        // For strings < 128 chars, length is 1 byte.
        args.Msg.readBuffer[offset] = (byte)serverVersionBytes.Length;
        Buffer.BlockCopy(serverVersionBytes, 0, args.Msg.readBuffer, offset + 1, serverVersionBytes.Length);

        string label = KnownVersions.TryGetValue(clientRelease, out string? ver)
            ? ver : $"release {clientRelease}";

        TShock.Log.ConsoleInfo(
            $"[SkipVersionCheck] Rewrote client (index {playerIndex}) version " +
            $"from {clientVersion} ({label}) to {serverVersion}. " +
            $"TShock will handle connect normally.");

        // Do NOT set args.Handled — let TShock process the rewritten packet.
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
