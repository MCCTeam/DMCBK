using DMCBK.Core;
using Umpk.Client;
using Umpk.Client.Events;
using Umpk.Text;

namespace DMCBK.Samples.ClientGuide;

// The observer owns its event registrations. Dispose it before disposing the client.
internal sealed class ClientObserver : IDisposable
{
    private readonly Client _client;

    public ClientObserver(Client client)
    {
        _client = client;
        client.StatusChanged += OnStatus;
        client.Game.Chat.MessageReceived += OnChat;
    }

    private static void OnStatus(object? sender, ClientStatusChangedEventArgs change)
        => Console.WriteLine($"Status: {change.Previous} -> {change.Current}");

    private void OnChat(object? sender, ChatMessageReceived message)
        => Console.WriteLine($"Chat: {message.Message.ToPlainText(_client.Translations)}");

    public void Dispose()
    {
        _client.StatusChanged -= OnStatus;
        _client.Game.Chat.MessageReceived -= OnChat;
    }
}
