using SR2MP.Packets.Utils;
using SR2MP.Shared.Managers;

namespace SR2MP.Packets.Shop;

internal sealed class ShopStateSnapshotPacket : IPacket
{
    public Guid SessionId;
    public ulong Revision;
    public ShopRuntimeState[] Runtimes = Array.Empty<ShopRuntimeState>();
    public ShopPurchaseSetState[] PurchaseSets = Array.Empty<ShopPurchaseSetState>();
    public ShopPendingSourceState[] PendingSpawns = Array.Empty<ShopPendingSourceState>();

    public PacketType Type => PacketType.ShopStateSnapshot;
    public PacketReliability Reliability => PacketReliability.ReliableOrdered;
    public NetworkChannel Channel => NetworkChannel.Economy;

    public void Serialise(PacketWriter writer)
    {
        writer.WriteGuid(SessionId);
        writer.WriteULong(Revision);
        writer.WriteArray(Runtimes, static (packetWriter, runtime) => runtime.Serialise(packetWriter));
        writer.WriteArray(PurchaseSets, static (packetWriter, set) => set.Serialise(packetWriter));
        writer.WriteArray(PendingSpawns, static (packetWriter, pending) => pending.Serialise(packetWriter));
    }

    public void Deserialise(PacketReader reader)
    {
        SessionId = reader.ReadGuid();
        Revision = reader.ReadULong();
        Runtimes = reader.ReadArray(static packetReader => ShopRuntimeState.Deserialise(packetReader))
                   ?? Array.Empty<ShopRuntimeState>();
        PurchaseSets = reader.ReadArray(static packetReader => ShopPurchaseSetState.Deserialise(packetReader))
                       ?? Array.Empty<ShopPurchaseSetState>();
        PendingSpawns = reader.ReadArray(static packetReader => ShopPendingSourceState.Deserialise(packetReader))
                        ?? Array.Empty<ShopPendingSourceState>();
    }

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new ShopStateSnapshotPacket
        {
            SessionId = Guid.Parse("f622f592-48bd-4520-a0ad-75f4d1905b3f"),
            Revision = 0x123456789ABCDEF0UL,
            Runtimes = new[]
            {
                new ShopRuntimeState
                {
                    ShopAssetTag = "shop-a",
                    IsAvailable = true,
                    HasNewStock = false,
                    HasGameWorldTime = true,
                    GameWorldTimeSeconds = 12345.5,
                    HasRealTime = false,
                    RealTimeSeconds = 0,
                    Categories = new[]
                    {
                        new ShopCategoryState
                        {
                            AnalyticsName = "daily",
                            LastSeedUsed = 41,
                            NextOverrideIndex = 3,
                            Items = new[]
                            {
                                new ShopItemStockState { AssetGuid = "item-a", IsSoldOut = true },
                                new ShopItemStockState { AssetGuid = "item-b", IsSoldOut = false }
                            }
                        }
                    }
                }
            },
            PurchaseSets = new[]
            {
                new ShopPurchaseSetState
                {
                    CategoryIdentifier = "category-a",
                    Seed = 9,
                    Purchases = new[]
                    {
                        new ShopPurchaseEntryState { ItemAssetGuid = "item-a", UnlockType = 2, Count = 1 }
                    }
                }
            },
            PendingSpawns = new[]
            {
                new ShopPendingSourceState { SourceId = "source-a", IdentifiableIds = new[] { 11, 23 } }
            }
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        var bytes = writer.ToSpan().ToArray();

        using var reader = PacketReader.Borrow(bytes);
        var actual = new ShopStateSnapshotPacket();
        actual.Deserialise(reader);

        return reader.BytesRemaining == 0 && SnapshotsEqual(expected, actual);
    }

    internal static bool VerifyRevisionOrdering()
    {
        var currentSession = Guid.Parse("72f2b6d0-8c1e-4eb0-9bcf-e7f4de4f9b28");
        var newSession = Guid.Parse("74b997eb-b1ab-49cb-a8a6-f01c5ffce3b5");
        return NetworkShopManager.ShouldAcceptSnapshot(currentSession, 7, currentSession, 8) &&
               !NetworkShopManager.ShouldAcceptSnapshot(currentSession, 8, currentSession, 8) &&
               !NetworkShopManager.ShouldAcceptSnapshot(currentSession, 8, currentSession, 7) &&
               NetworkShopManager.ShouldAcceptSnapshot(currentSession, 8, newSession, 1);
    }

    private static bool SnapshotsEqual(ShopStateSnapshotPacket left, ShopStateSnapshotPacket right)
    {
        if (left.SessionId != right.SessionId || left.Revision != right.Revision ||
            left.Runtimes.Length != right.Runtimes.Length ||
            left.PurchaseSets.Length != right.PurchaseSets.Length ||
            left.PendingSpawns.Length != right.PendingSpawns.Length)
            return false;

        for (var i = 0; i < left.Runtimes.Length; i++)
        {
            var a = left.Runtimes[i];
            var b = right.Runtimes[i];
            if (a.ShopAssetTag != b.ShopAssetTag || a.IsAvailable != b.IsAvailable ||
                a.HasNewStock != b.HasNewStock || a.HasGameWorldTime != b.HasGameWorldTime ||
                a.GameWorldTimeSeconds != b.GameWorldTimeSeconds || a.HasRealTime != b.HasRealTime ||
                a.RealTimeSeconds != b.RealTimeSeconds || a.Categories.Length != b.Categories.Length)
                return false;

            for (var categoryIndex = 0; categoryIndex < a.Categories.Length; categoryIndex++)
            {
                var categoryA = a.Categories[categoryIndex];
                var categoryB = b.Categories[categoryIndex];
                if (categoryA.AnalyticsName != categoryB.AnalyticsName ||
                    categoryA.LastSeedUsed != categoryB.LastSeedUsed ||
                    categoryA.NextOverrideIndex != categoryB.NextOverrideIndex ||
                    categoryA.Items.Length != categoryB.Items.Length)
                    return false;

                for (var itemIndex = 0; itemIndex < categoryA.Items.Length; itemIndex++)
                {
                    if (categoryA.Items[itemIndex].AssetGuid != categoryB.Items[itemIndex].AssetGuid ||
                        categoryA.Items[itemIndex].IsSoldOut != categoryB.Items[itemIndex].IsSoldOut)
                        return false;
                }
            }
        }

        for (var i = 0; i < left.PurchaseSets.Length; i++)
        {
            var a = left.PurchaseSets[i];
            var b = right.PurchaseSets[i];
            if (a.CategoryIdentifier != b.CategoryIdentifier || a.Seed != b.Seed ||
                a.Purchases.Length != b.Purchases.Length)
                return false;

            for (var j = 0; j < a.Purchases.Length; j++)
            {
                if (a.Purchases[j].ItemAssetGuid != b.Purchases[j].ItemAssetGuid ||
                    a.Purchases[j].UnlockType != b.Purchases[j].UnlockType ||
                    a.Purchases[j].Count != b.Purchases[j].Count)
                    return false;
            }
        }

        for (var i = 0; i < left.PendingSpawns.Length; i++)
        {
            var a = left.PendingSpawns[i];
            var b = right.PendingSpawns[i];
            if (a.SourceId != b.SourceId || !a.IdentifiableIds.SequenceEqual(b.IdentifiableIds))
                return false;
        }

        return true;
    }
}

internal sealed class ShopRuntimeState
{
    public string ShopAssetTag = string.Empty;
    public bool IsAvailable;
    public bool HasNewStock;
    public bool HasGameWorldTime;
    public double GameWorldTimeSeconds;
    public bool HasRealTime;
    public double RealTimeSeconds;
    public ShopCategoryState[] Categories = Array.Empty<ShopCategoryState>();

    internal void Serialise(PacketWriter writer)
    {
        writer.WriteString(ShopAssetTag);
        writer.WriteBool(IsAvailable);
        writer.WriteBool(HasNewStock);
        writer.WriteBool(HasGameWorldTime);
        writer.WriteDouble(GameWorldTimeSeconds);
        writer.WriteBool(HasRealTime);
        writer.WriteDouble(RealTimeSeconds);
        writer.WriteArray(Categories, static (packetWriter, category) => category.Serialise(packetWriter));
    }

    internal static ShopRuntimeState Deserialise(PacketReader reader) => new()
    {
        ShopAssetTag = reader.ReadPooledString() ?? string.Empty,
        IsAvailable = reader.ReadBool(),
        HasNewStock = reader.ReadBool(),
        HasGameWorldTime = reader.ReadBool(),
        GameWorldTimeSeconds = reader.ReadDouble(),
        HasRealTime = reader.ReadBool(),
        RealTimeSeconds = reader.ReadDouble(),
        Categories = reader.ReadArray(static packetReader => ShopCategoryState.Deserialise(packetReader))
                     ?? Array.Empty<ShopCategoryState>()
    };
}

internal sealed class ShopCategoryState
{
    public string AnalyticsName = string.Empty;
    public int LastSeedUsed;
    public int NextOverrideIndex;
    public ShopItemStockState[] Items = Array.Empty<ShopItemStockState>();

    internal void Serialise(PacketWriter writer)
    {
        writer.WriteString(AnalyticsName);
        writer.WritePackedInt(LastSeedUsed);
        writer.WritePackedInt(NextOverrideIndex);
        writer.WriteArray(Items, static (packetWriter, item) => item.Serialise(packetWriter));
    }

    internal static ShopCategoryState Deserialise(PacketReader reader) => new()
    {
        AnalyticsName = reader.ReadPooledString() ?? string.Empty,
        LastSeedUsed = reader.ReadPackedInt(),
        NextOverrideIndex = reader.ReadPackedInt(),
        Items = reader.ReadArray(static packetReader => ShopItemStockState.Deserialise(packetReader))
                ?? Array.Empty<ShopItemStockState>()
    };
}

internal sealed class ShopItemStockState
{
    public string AssetGuid = string.Empty;
    public bool IsSoldOut;

    internal void Serialise(PacketWriter writer)
    {
        writer.WriteString(AssetGuid);
        writer.WriteBool(IsSoldOut);
    }

    internal static ShopItemStockState Deserialise(PacketReader reader) => new()
    {
        AssetGuid = reader.ReadPooledString() ?? string.Empty,
        IsSoldOut = reader.ReadBool()
    };
}

internal sealed class ShopPurchaseSetState
{
    public string CategoryIdentifier = string.Empty;
    public int Seed;
    public ShopPurchaseEntryState[] Purchases = Array.Empty<ShopPurchaseEntryState>();

    internal void Serialise(PacketWriter writer)
    {
        writer.WriteString(CategoryIdentifier);
        writer.WritePackedInt(Seed);
        writer.WriteArray(Purchases, static (packetWriter, purchase) => purchase.Serialise(packetWriter));
    }

    internal static ShopPurchaseSetState Deserialise(PacketReader reader) => new()
    {
        CategoryIdentifier = reader.ReadPooledString() ?? string.Empty,
        Seed = reader.ReadPackedInt(),
        Purchases = reader.ReadArray(static packetReader => ShopPurchaseEntryState.Deserialise(packetReader))
                    ?? Array.Empty<ShopPurchaseEntryState>()
    };
}

internal sealed class ShopPurchaseEntryState
{
    public string ItemAssetGuid = string.Empty;
    public int UnlockType;
    public int Count;

    internal void Serialise(PacketWriter writer)
    {
        writer.WriteString(ItemAssetGuid);
        writer.WritePackedInt(UnlockType);
        writer.WritePackedInt(Count);
    }

    internal static ShopPurchaseEntryState Deserialise(PacketReader reader) => new()
    {
        ItemAssetGuid = reader.ReadPooledString() ?? string.Empty,
        UnlockType = reader.ReadPackedInt(),
        Count = reader.ReadPackedInt()
    };
}

internal sealed class ShopPendingSourceState
{
    public string SourceId = string.Empty;
    public int[] IdentifiableIds = Array.Empty<int>();

    internal void Serialise(PacketWriter writer)
    {
        writer.WriteString(SourceId);
        writer.WriteArray(IdentifiableIds, PacketWriterDels.PackedInt);
    }

    internal static ShopPendingSourceState Deserialise(PacketReader reader) => new()
    {
        SourceId = reader.ReadPooledString() ?? string.Empty,
        IdentifiableIds = reader.ReadArray(PacketReaderDels.PackedInt) ?? Array.Empty<int>()
    };
}
