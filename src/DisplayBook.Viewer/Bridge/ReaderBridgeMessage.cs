using System.Text.Json;

namespace DisplayBook.Viewer.Bridge;

public sealed record ReaderBridgeMessage(string Type, JsonElement Payload);

public sealed class ReaderMessageEventArgs(ReaderBridgeMessage message) : EventArgs
{
	public ReaderBridgeMessage Message { get; } = message;
}
