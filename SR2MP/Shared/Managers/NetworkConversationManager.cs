using Il2CppMonomiPark.SlimeRancher.Dialogue.CommStation;
using SR2MP.Packets.Loading;
using SR2MP.Packets.World;

namespace SR2MP.Shared.Managers;

internal static class NetworkConversationManager
{
    private static Dictionary<string, FixedConversation>? conversationsByName;

    private static Dictionary<string, FixedConversation> ConversationsByName
    {
        get
        {
            conversationsByName ??= new Dictionary<string, FixedConversation>();
            foreach (var conversation in Resources.FindObjectsOfTypeAll<FixedConversation>())
            {
                if (conversation != null && !string.IsNullOrWhiteSpace(conversation.name))
                    conversationsByName[conversation.name] = conversation;
            }

            return conversationsByName;
        }
    }

    private static FixedConversation? FindConversation(string name)
        => ConversationsByName.TryGetValue(name, out var conversation) ? conversation : null;
    
    internal static void SendConversationPlayed(string conversationName)
        => Main.SendToAllOrServer(new ConversationPlayedPacket { ConversationName = conversationName });
    
    internal static void ApplyConversationPlayed(string conversationName)
    {
        var conversation = FindConversation(conversationName);
        if (conversation?.HasBeenPlayed() != false)
            return;

        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            conversation.RecordPlayed();
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }
    }

    internal static void ReplacePlayedConversations(List<string?> conversationNames)
    {
        var table = SceneContext.Instance.eventDirector._model.table;
        table.Remove("convoPlayed");
        table.Remove("convoRancherPlayed");

        var playedNames = new HashSet<string>(conversationNames.Where(name => !string.IsNullOrEmpty(name))!);
        var wasHandlingPacket = HandlingPacket;
        HandlingPacket = true;
        try
        {
            foreach (var conversation in ConversationsByName.Values)
            {
                if (playedNames.Contains(conversation.name) && !conversation.HasBeenPlayed())
                    conversation.RecordPlayed();
            }
        }
        finally
        {
            HandlingPacket = wasHandlingPacket;
        }

        SceneContext.Instance.eventDirector._model.NotifyParticipants();
    }
    
    internal static List<string?> GetPlayedConversations()
    {
        var names = new List<string?>();

        foreach (var conversation in ConversationsByName.Values)
            if (conversation.HasBeenPlayed())
                names.Add(conversation.name);

        return names;
    }
}
