// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Net;
using Lycoreia.Net.Messages;

namespace Lycoreia.Owned.Shared.Managers;

internal static class ShopCodec
{
    internal static void Arr<T>(Codec c, ref T[] a, ItemFn<T> fn)
    {
        var l = a == null ? new List<T>() : new List<T>(a); c.List(ref l, fn); a = l.ToArray();
    }
    internal static ItemFn<T> Of<T>() where T : class, IWire, new() => Items.OfClass<T>();
}

internal sealed class ShopStateSnapshotPacket : IWire
{
    public Guid SessionId;
    public ulong Revision;
    public ShopRuntimeState[] Runtimes = Array.Empty<ShopRuntimeState>();
    public ShopPurchaseSetState[] PurchaseSets = Array.Empty<ShopPurchaseSetState>();
    public ShopPendingSourceState[] PendingSpawns = Array.Empty<ShopPendingSourceState>();

    /// <summary>The state arrays travel as the message blob; session and revision are message fields.</summary>
    public void Describe(Codec c)
    {
        ShopCodec.Arr(c, ref Runtimes, ShopCodec.Of<ShopRuntimeState>());
        ShopCodec.Arr(c, ref PurchaseSets, ShopCodec.Of<ShopPurchaseSetState>());
        ShopCodec.Arr(c, ref PendingSpawns, ShopCodec.Of<ShopPendingSourceState>());
    }

    public static implicit operator ShopStateSnapshot(ShopStateSnapshotPacket p)
    {
        var w = new NetWriter(); p.Describe(new Codec(w));
        return new ShopStateSnapshot { SessionId = p.SessionId, Revision = p.Revision, State = w.Span.ToArray() };
    }
    public static ShopStateSnapshotPacket From(ShopStateSnapshot m)
    {
        var p = new ShopStateSnapshotPacket { SessionId = m.SessionId, Revision = m.Revision };
        var r = NetReader.Rent(m.State ?? Array.Empty<byte>(), 0, (m.State ?? Array.Empty<byte>()).Length);
        try { p.Describe(new Codec(r)); } finally { r.Return(); }
        return p;
    }

    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new ShopStateSnapshotPacket
        {
            SessionId = Guid.Parse("f622f592-48bd-4520-a0ad-75f4d1905b3f"),
            Revision = 0x123456789ABCDEF0UL,
            Runtimes = new[] { new ShopRuntimeState { ShopAssetTag = "shop-a", IsAvailable = true, HasGameWorldTime = true, GameWorldTimeSeconds = 12345.5,
                Categories = new[] { new ShopCategoryState { AnalyticsName = "daily", LastSeedUsed = 41, NextOverrideIndex = 3,
                    Items = new[] { new ShopItemStockState { AssetGuid = "item-a", IsSoldOut = true }, new ShopItemStockState { AssetGuid = "item-b" } } } } } },
            PurchaseSets = new[] { new ShopPurchaseSetState { CategoryIdentifier = "category-a", Seed = 9, Purchases = new[] { new ShopPurchaseEntryState { ItemAssetGuid = "item-a", UnlockType = 2, Count = 1 } } } },
            PendingSpawns = new[] { new ShopPendingSourceState { SourceId = "source-a", IdentifiableIds = new[] { 11, 23 } } }
        };
        var actual = From(expected);
        return actual.SessionId == expected.SessionId && actual.Revision == expected.Revision &&
               actual.Runtimes.Length == 1 && actual.Runtimes[0].Categories[0].Items[0].IsSoldOut && actual.Runtimes[0].Categories[0].Items[1].AssetGuid == "item-b" &&
               actual.Runtimes[0].GameWorldTimeSeconds == 12345.5 && actual.PurchaseSets[0].Purchases[0].UnlockType == 2 &&
               actual.PendingSpawns[0].IdentifiableIds.SequenceEqual(new[] { 11, 23 });
    }
}

internal sealed class ShopRuntimeState : IWire
{
    public string ShopAssetTag = string.Empty;
    public bool IsAvailable, HasNewStock, HasGameWorldTime, HasRealTime;
    public double GameWorldTimeSeconds, RealTimeSeconds;
    public ShopCategoryState[] Categories = Array.Empty<ShopCategoryState>();
    public void Describe(Codec c)
    {
        c.Str(ref ShopAssetTag, 256); c.Flag(ref IsAvailable); c.Flag(ref HasNewStock); c.Flag(ref HasGameWorldTime); c.F64(ref GameWorldTimeSeconds);
        c.Flag(ref HasRealTime); c.F64(ref RealTimeSeconds); ShopCodec.Arr(c, ref Categories, ShopCodec.Of<ShopCategoryState>()); ShopAssetTag ??= string.Empty;
    }
}

internal sealed class ShopCategoryState : IWire
{
    public string AnalyticsName = string.Empty; public int LastSeedUsed, NextOverrideIndex;
    public ShopItemStockState[] Items = Array.Empty<ShopItemStockState>();
    public void Describe(Codec c) { c.Str(ref AnalyticsName, 256); c.Var(ref LastSeedUsed); c.Var(ref NextOverrideIndex); ShopCodec.Arr(c, ref Items, ShopCodec.Of<ShopItemStockState>()); AnalyticsName ??= string.Empty; }
}

internal sealed class ShopItemStockState : IWire
{
    public string AssetGuid = string.Empty; public bool IsSoldOut;
    public void Describe(Codec c) { c.Str(ref AssetGuid, 128); c.Flag(ref IsSoldOut); AssetGuid ??= string.Empty; }
}

internal sealed class ShopPurchaseSetState : IWire
{
    public string CategoryIdentifier = string.Empty; public int Seed;
    public ShopPurchaseEntryState[] Purchases = Array.Empty<ShopPurchaseEntryState>();
    public void Describe(Codec c) { c.Str(ref CategoryIdentifier, 256); c.Var(ref Seed); ShopCodec.Arr(c, ref Purchases, ShopCodec.Of<ShopPurchaseEntryState>()); CategoryIdentifier ??= string.Empty; }
}

internal sealed class ShopPurchaseEntryState : IWire
{
    public string ItemAssetGuid = string.Empty; public int UnlockType, Count;
    public void Describe(Codec c) { c.Str(ref ItemAssetGuid, 128); c.Var(ref UnlockType); c.Var(ref Count); ItemAssetGuid ??= string.Empty; }
}

internal sealed class ShopPendingSourceState : IWire
{
    public string SourceId = string.Empty; public int[] IdentifiableIds = Array.Empty<int>();
    public void Describe(Codec c) { c.Str(ref SourceId, 256); ShopCodec.Arr(c, ref IdentifiableIds, Items.Var32); SourceId ??= string.Empty; }
}

internal static partial class NetworkShopManager
{
    /// <summary>Glue: handlers, the join snapshot step and the host retry pump.</summary>
    internal static void Register()
    {
        Mod.Node.On<ShopPurchaseRequest>(Side.Host, (c, m) =>
        {
            // host-run native transaction: currency and shop hooks must see the commit (lift the applying flag)
            using (Applying.Suspend()) HandlePurchaseRequest(m, c.Sender);
            return Verdict.Reject;
        });
        Mod.Node.On<ShopPurchaseResult>(Side.Guest, (c, m) => HandlePurchaseResult(m));
        Mod.Node.On<ShopStateSnapshot>(Side.Guest, (c, m) => ApplySnapshot(ShopStateSnapshotPacket.From(m)));
        Mod.Snapshots.Register(Lycoreia.Snapshot.SnapStep.Shop, ctx => SendSnapshot(ctx.Peer));
    }
}
