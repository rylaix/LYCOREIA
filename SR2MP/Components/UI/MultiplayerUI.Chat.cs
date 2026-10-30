using Il2CppInterop.Runtime.Attributes;
using SR2MP.Packets;

namespace SR2MP.Components.UI;

/// <summary>Chat data (messages, sending). Drawn by <see cref="ChatHud"/>.</summary>
internal sealed partial class MultiplayerUI
{
    private const int MaxStoredChatMessages = 100;

    private bool chatHidden = true;
    private bool internalChatToggle;
    private readonly List<ChatMessage> chatMessages = new();
    private readonly Queue<Action> pendingMessageRegistrations = new();
    private readonly HashSet<string> processedMessageIds = new();

    internal sealed class ChatMessage
    {
        public string message = string.Empty;
        public string playerName = string.Empty;
        public long time;
        public string messageId = string.Empty;
        public bool isSystemMessage;
        public byte systemMessageType;
        public bool isOwn;
    }

    /// <summary>Bumped whenever messages are added or cleared.</summary>
    internal int ChatRevision { [HideFromIl2Cpp] get; private set; }

    internal List<ChatMessage> ChatMessages { [HideFromIl2Cpp] get => chatMessages; }

    internal bool ChatHidden => chatHidden;

    internal bool IsChatAllowed => state is not (MenuState.DisconnectedMainMenu or MenuState.Hidden);

    public void RegisterChatMessage(string message, string playerName, string messageId)
        => RegisterMessageInternal(message, playerName, messageId, false, 0);

    public void RegisterSystemMessage(string message, string messageId, byte type)
        => RegisterMessageInternal(message, "SYSTEM", messageId, true, type);

    private void RegisterMessageInternal(string message, string displayName, string messageId, bool isSystem, byte systemType)
    {
        if (processedMessageIds.Contains(messageId)) return;

        pendingMessageRegistrations.Enqueue(() =>
        {
            if (!processedMessageIds.Add(messageId))
                return;

            chatMessages.Add(new ChatMessage
            {
                message = (message ?? string.Empty).Trim(),
                playerName = displayName,
                time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                messageId = messageId,
                isSystemMessage = isSystem,
                systemMessageType = systemType,
                isOwn = !isSystem && displayName == Main.Username && messageId.StartsWith(Main.Username, StringComparison.Ordinal)
            });

            if (chatMessages.Count > MaxStoredChatMessages)
                chatMessages.RemoveRange(0, chatMessages.Count - MaxStoredChatMessages);
            ChatRevision++;

            if (processedMessageIds.Count <= 1000) return;

            // Snapshot to avoid mutating the set while iterating
            var toRemove = processedMessageIds.Take(500).ToList();
            foreach (var id in toRemove)
                processedMessageIds.Remove(id);
        });
    }

    private void ProcessPendingMessages()
    {
        while (pendingMessageRegistrations.TryDequeue(out var registration))
            registration?.Invoke();
    }

    internal void SendChatMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        message = message.Trim();
        if (message.Length > MaxChatMessageLength)
            message = message[..MaxChatMessageLength];

        var messageId = $"{Main.Username}_{message.GetHashCode()}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        RegisterChatMessage(message, Main.Username, messageId);

        Main.SendToAllOrServer(new ChatMessagePacket
        {
            Message = message,
            Username = Main.Username,
            MessageID = messageId,
            MessageType = 0
        });
    }

    public void ClearChatMessages()
    {
        chatMessages.Clear();
        processedMessageIds.Clear();
        pendingMessageRegistrations.Clear();
        ChatRevision++;
    }

    public void ClearAndWelcome()
    {
        ClearChatMessages();
        RegisterSystemMessage(
            $"Welcome to {BuildInfo.Name} {BuildInfo.Version}! Press Enter to chat, F5 to hide the chat.",
            $"SYSTEM_WELCOME_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            SystemMessageNormal
        );
    }

    internal static void SetGameInputEnabled(bool enabled)
    {
        try
        {
            if (enabled)
                EnableInput();
            else
                DisableInput();
        }
        catch (Exception ex)
        {
            SrLogger.LogWarning($"Could not {(enabled ? "enable" : "disable")} game input for chat: {ex.Message}");
        }
    }
}
