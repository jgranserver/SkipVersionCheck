using System.Runtime.CompilerServices;

using Terraria;
using Terraria.Net;

using TShockAPI;

namespace SkipVersionCheck;

/// <summary>
/// Hooks into Terraria's NetManager to filter outgoing NetModule packets
/// sent to cross-version clients. This prevents the server from sending
/// items/data that the client's version doesn't understand.
/// Ported from the Crossplay plugin by Moneylover3246.
/// </summary>
internal static class NetModuleHandler
{
    /// <summary>
    /// Replaces the default NetManager.Broadcast to filter per-client.
    /// Instead of broadcasting to all, we check each client individually.
    /// </summary>
    internal static void OnBroadcast(
        On.Terraria.Net.NetManager.orig_Broadcast_NetPacket_int orig,
        NetManager self,
        NetPacket packet,
        int ignoreClient)
    {
        for (int i = 0; i < Main.maxPlayers; i++)
        {
            if (i != ignoreClient && Netplay.Clients[i].IsConnected())
            {
                if (!IsInvalidNetPacket(packet, i))
                {
                    self.SendData(Netplay.Clients[i].Socket, packet);
                }
            }
        }
    }

    /// <summary>
    /// Wraps NetManager.SendToClient to filter packets for specific clients.
    /// </summary>
    internal static void OnSendToClient(
        On.Terraria.Net.NetManager.orig_SendToClient orig,
        NetManager self,
        NetPacket packet,
        int playerId)
    {
        if (!IsInvalidNetPacket(packet, playerId))
        {
            orig(self, packet, playerId);
        }
    }

    /// <summary>
    /// Check if a NetPacket contains data that's incompatible with the
    /// target client's version. Currently checks for unsupported item IDs
    /// in CreativeUnlocksPlayerReport packets (NetModule ID 5).
    /// </summary>
    private static bool IsInvalidNetPacket(NetPacket packet, int playerId)
    {
        // Only filter for cross-version clients
        int clientVersion = SkipVersionCheck.Instance?.GetClientVersion(playerId) ?? 0;
        if (clientVersion <= 0)
            return false; // same version or not tracked

        switch (packet.Id)
        {
            case 5: // CreativeUnlocksPlayerReport — contains item net IDs
                {
                    if (packet.Buffer.Data.Length < 5)
                        return false;

                    var itemNetID = Unsafe.As<byte, short>(ref packet.Buffer.Data[3]);

                    int maxItems = SkipVersionCheck.Instance?.GetMaxItemsForVersion(clientVersion) ?? int.MaxValue;
                    if (itemNetID > maxItems)
                    {
                        TShock.Log.ConsoleDebug(
                            $"[SkipVersionCheck] Filtered NetModule packet (ID={packet.Id}) " +
                            $"with item {itemNetID} for client {playerId} (max={maxItems})");
                        return true;
                    }
                }
                break;
        }
        return false;
    }
}
