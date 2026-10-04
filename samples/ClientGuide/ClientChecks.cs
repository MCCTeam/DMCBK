using DMCBK.Core;
using DMCBK.Testing;
using Umpk.Client;
using Umpk.Protocol.Java;
using Umpk.Protocol.Java.Codecs;
using Umpk.Protocol.Java.Packets;
using Umpk.Text;

namespace DMCBK.Samples.ClientGuide;

internal static class ClientChecks
{
    public static async Task RunAsync()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Client.Game.Chat.MessageReceived += (_, message) =>
            received.TrySetResult(message.Message.ToPlainText(host.Client.Translations));

        var local = await host.Client.Commands.DispatchAsync("set lesson=client-guide", host.Token);
        Require(local.IsSuccess, "The local set command failed.");
        Console.WriteLine("PASS: local command before connection");

        await host.RunSessionAsync(async session =>
        {
            Require(host.Client.Status == ClientStatus.Playing, "The client did not reach play.");
            Console.WriteLine("PASS: handshake, login, configuration and play");

            await session.SendAsync(new ClientboundSystemChatPacket(Component.Text("Hello, GuideBot"), false));
            Require(await received.Task.WaitAsync(host.Token) == "Hello, GuideBot", "Incoming chat changed.");
            Console.WriteLine("PASS: incoming chat event");

            await session.SendAsync(new ClientboundSetHealthPacket(15, 17, 5));
            Require(await host.WaitForAsync(() =>
                host.Client.Game.Player.GetStatusAsync(host.Token).GetAwaiter().GetResult().Health == 15),
                "The player snapshot did not update.");
            PlayerStatus status = await host.Client.Game.Player.GetStatusAsync(host.Token);
            Require(status.Health == 15 && status.Food == 17, "The player snapshot is incorrect.");
            Require(!status.HasSpawned, "This minimal fake server does not place a player.");
            Console.WriteLine("PASS: health snapshot and placement distinction");

            PlayerInventorySnapshot inventory = await host.Client.Game.Inventory.GetPlayerInventoryAsync(host.Token);
            Require(inventory.Slots.Count == 46 && inventory.Slots.All(item => item.IsEmpty),
                "The minimal server should have the default empty inventory.");
            Require((await host.Client.Game.Entities.NearbyAsync(16, host.Token)).Count == 0,
                "The minimal server should have no tracked entities.");
            Console.WriteLine("PASS: inventory and entity snapshots");

            await host.Client.Game.Chat.SendAsync("Hello from GuideBot", host.Token);
            int expected = ChatWireId(host);
            while (true)
            {
                var frame = await session.NextFrameAsync();
                if (frame.WireId != expected) continue;
                Require(ReadFirstString(frame.Payload) == "Hello from GuideBot", "Outgoing chat changed.");
                break;
            }
            Console.WriteLine("PASS: outgoing chat packet");
        });

        Require(host.Client.Status == ClientStatus.Disconnected, "The client did not stop.");
        Console.WriteLine("PASS: clean shutdown");
    }

    private static int ChatWireId(PluginTestHost host)
    {
        if (!host.Version.Protocol.TryGetRegistry(ProtocolPhase.Play, PacketFlow.Serverbound, out var registry))
            throw new InvalidOperationException("The play registry is missing.");
        return registry.Packets.Single(pair => pair.Type.Id.ToString() == "minecraft:chat").WireId;
    }

    private static string ReadFirstString(byte[] payload)
    {
        var reader = new PacketReader(payload);
        return reader.ReadString();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
