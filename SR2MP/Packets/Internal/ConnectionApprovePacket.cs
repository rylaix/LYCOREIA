using Il2CppMonomiPark.SlimeRancher.Economy;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Packets.Internal;

internal sealed class ConnectionApprovePacket : IPacket
{
    public bool InitialJoin;

    public string PlayerId;
    public (string ID, string Username)[] OtherPlayers;

    public Dictionary<byte, int> CurrencyBalances = new();
    public Dictionary<byte, ulong> CurrencyRevisions = new();
    public bool AllowCheats;

    public List<ModNetData> NetData;
    // Monotonic host sync generation shared with InitialActors. The optional
    // trailing field keeps packets from older mod builds readable.
    public long SyncId;
    // Shared cross-peer diagnostics key. Optional for compatibility with older hosts.
    public string HostSessionId = string.Empty;

    public PacketType Type => PacketType.ConnectionApprove;
    public PacketReliability Reliability => PacketReliability.Reliable;
    public NetworkChannel Channel => NetworkChannel.Important;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedBool(InitialJoin);
        writer.WritePackedBool(AllowCheats);

        writer.WriteStringWithoutSize(PlayerId);

        writer.WritePackedUInt((uint)OtherPlayers.Length);

        for (var i = 0; i < OtherPlayers.Length; i++)
        {
            var (id, username) = OtherPlayers[i];
            writer.WriteStringWithoutSize(id);
            writer.WriteString(username);
        }

        writer.WriteDictionary(CurrencyBalances, PacketWriterDels.Byte, PacketWriterDels.PackedInt);
        writer.WriteDictionary(CurrencyRevisions, PacketWriterDels.Byte, PacketWriterDels.ULong);

        writer.WriteList(NetData, PacketWriterDels.NetObject<ModNetData>.Writer);
        if (SyncId > 0 || !string.IsNullOrWhiteSpace(HostSessionId))
            writer.WriteLong(SyncId);
        if (!string.IsNullOrWhiteSpace(HostSessionId))
            writer.WriteString(HostSessionId);
    }

    public void Deserialise(PacketReader reader)
    {
        InitialJoin = reader.ReadPackedBool();
        AllowCheats = reader.ReadPackedBool();

        PlayerId = reader.ReadPooledStringOfSize(16)!;

        var count = reader.ReadPackedUInt();
        OtherPlayers = new (string ID, string Username)[count];

        for (var i = 0; i < count; i++)
            OtherPlayers[i] = (reader.ReadPooledStringOfSize(16), reader.ReadPooledString())!;

        CurrencyBalances = reader.ReadDictionary(PacketReaderDels.Byte, PacketReaderDels.PackedInt)
                           ?? new Dictionary<byte, int>();
        CurrencyRevisions = reader.ReadDictionary(PacketReaderDels.Byte, PacketReaderDels.ULong)
                            ?? new Dictionary<byte, ulong>();

        NetData = reader.ReadList(PacketReaderDels.NetObject<ModNetData>.Reader)!;
        if (reader.BytesRemaining >= sizeof(long))
            SyncId = reader.ReadLong();
        if (reader.BytesRemaining > 0)
            HostSessionId = reader.ReadPooledString() ?? string.Empty;
    }

    internal static Dictionary<byte, int> CaptureCurrencyBalances()
    {
        var balances = new Dictionary<byte, int>();
        var entries = GameContext.Instance?.LookupDirector?._currencyList?._currencies;
        var playerState = SceneContext.Instance?.PlayerState;
        if (entries == null || playerState == null)
            return balances;

        foreach (var entry in entries)
        {
            if (entry == null)
                continue;

            var currency = entry.Cast<ICurrency>();
            if (currency.PersistenceId is < byte.MinValue or > byte.MaxValue)
                continue;

            var currencyId = (byte)currency.PersistenceId;
            balances[currencyId] = playerState.GetCurrency(currency);
        }

        return balances;
    }

    internal static Dictionary<byte, ulong> CaptureCurrencyRevisions(IEnumerable<byte> currencyTypes)
        => NetworkCurrencyManager.CreateHostRevisionSnapshot(currencyTypes);

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new ConnectionApprovePacket
        {
            InitialJoin = true,
            PlayerId = "PLAYER_123456789",
            OtherPlayers = new[] { ("PLAYER_456789012", "Guest") },
            CurrencyBalances = new Dictionary<byte, int>
            {
                [1] = 1234,
                [2] = 56,
                [7] = 89
            },
            CurrencyRevisions = new Dictionary<byte, ulong>
            {
                [1] = 0,
                [2] = 15,
                [7] = 0xFEDCBA9876543210UL
            },
            AllowCheats = false,
            NetData = new List<ModNetData> { new() { ModId = 0x12345678, NetId = 3 } },
            SyncId = 42,
            HostSessionId = "PLAYER_HOST_SESSION"
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new ConnectionApprovePacket();
        actual.Deserialise(reader);

        if (reader.BytesRemaining != 0
            || actual.InitialJoin != expected.InitialJoin
            || actual.PlayerId != expected.PlayerId
            || !actual.OtherPlayers.SequenceEqual(expected.OtherPlayers)
            || actual.CurrencyBalances.Count != expected.CurrencyBalances.Count
            || !expected.CurrencyBalances.All(balance =>
                actual.CurrencyBalances.TryGetValue(balance.Key, out var amount) && amount == balance.Value)
            || actual.CurrencyRevisions.Count != expected.CurrencyRevisions.Count
            || !expected.CurrencyRevisions.All(revision =>
                actual.CurrencyRevisions.TryGetValue(revision.Key, out var value) && value == revision.Value)
            || actual.AllowCheats != expected.AllowCheats
            || actual.SyncId != expected.SyncId
            || actual.HostSessionId != expected.HostSessionId
            || actual.NetData.Count != expected.NetData.Count
            || actual.NetData[0].ModId != expected.NetData[0].ModId
            || actual.NetData[0].NetId != expected.NetData[0].NetId)
            return false;

        // A pre-generation host omits the optional trailing sync id.
        expected.SyncId = 0;
        expected.HostSessionId = string.Empty;
        using var legacyWriter = PacketWriter.Borrow();
        expected.Serialise(legacyWriter);
        using var legacyReader = PacketReader.Borrow(legacyWriter.ToSpan().ToArray());
        var legacy = new ConnectionApprovePacket();
        legacy.Deserialise(legacyReader);
        return legacyReader.BytesRemaining == 0 && legacy.SyncId == 0;
    }
}

internal struct ModNetData : INetObject
{
    public uint ModId;
    public byte NetId;

    public readonly void Serialise(PacketWriter writer)
    {
        writer.WriteUInt(ModId);
        writer.WriteByte(NetId);
    }

    public void Deserialise(PacketReader reader)
    {
        ModId = reader.ReadUInt();
        NetId = reader.ReadByte();
    }
}
