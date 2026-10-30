using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using SR2MP.Packets.Utils;
using SR2MP.Shared.Utils;

namespace SR2MP.Shared.Managers;

internal sealed class ReliabilityManager
{
    private sealed class PendingPacket : IRecyclable
    {
        public SplitResult SplitData;
        public IPEndPoint Destination = null!;
        public ushort PacketId;
        public byte PacketType;
        public DateTime FirstSendTime;
        public DateTime LastSendTime;
        public int SendCount;

        public bool IsRecycled { get; set; }

        private void Initialize(SplitResult splitData, IPEndPoint destination, ushort packetId,
            byte packetType)
        {
            SplitData = splitData;
            Destination = destination;
            PacketId = packetId;
            PacketType = packetType;

            FirstSendTime = DateTime.UtcNow;
            LastSendTime = DateTime.UtcNow;
            SendCount = 1;
        }

        public void Recycle()
        {
            SplitData.Dispose();
            Destination = null!;
        }

        public void Dispose() => Return(this);

        public static PendingPacket Borrow(SplitResult splitData, IPEndPoint destination,
            ushort packetId, byte packetType)
        {
            var packet = RecyclePool<PendingPacket>.Borrow();
            packet.Initialize(splitData, destination, packetId, packetType);
            return packet;
        }

        public static void Return(PendingPacket packet) => RecyclePool<PendingPacket>.Return(packet);
    }

    private readonly ConcurrentDictionary<PacketKey, PendingPacket> pendingPackets = new();
    private readonly ConcurrentDictionary<ChannelKey, ushort> lastProcessedSequence = new();
    private readonly ConcurrentDictionary<ChannelKey, int> sequenceNumbersByChannel = new();
    private readonly ConcurrentDictionary<ChannelKey, SortedDictionary<ushort, BufferedPacket>> reorderBuffers = new();

    private readonly record struct BufferedPacket(Action Action, DateTime ReceivedAt);
    private readonly ConcurrentDictionary<ChannelKey, object> reorderLocks = new();
    private readonly ConcurrentQueue<PendingPacket> pendingDisposal = new();

    private readonly Action<ArraySegment<byte>, IPEndPoint> sendRawCallback;

    private Thread? resendThread;
    private volatile bool isRunning;

    private static readonly TimeSpan ResendInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRetryTime = TimeSpan.FromSeconds(10);
    // Large split packets (join snapshots: 250-400 chunks) get as long as the receiver keeps partial
    // chunks (PacketChunkManager.PacketTimeout), and their resends are paced and rotated: blasting all
    // chunks in order made the tail of every burst overflow the path/receiver, so the same last chunks
    // were lost on every attempt and the guest never got its InitialActors snapshot.
    private static readonly TimeSpan MaxSplitRetryTime = TimeSpan.FromSeconds(30);
    private const int LargeSplitChunkThreshold = 32;
    private const int PacedChunksPerBurst = 24;
    private const int PacedBurstGapMilliseconds = 5;
    private const int MaxResendAttempts = 64;
    // Every packet that reaches the reorder logic has already been ACKed, so it must never be dropped: a dropped
    // ACKed packet is lost for good (its resend is discarded as a duplicate). Past this size packets are delivered
    // immediately and a placeholder keeps the sequence moving.
    private const int MaxReorderBufferSize = 4096;
    // The sender gives up after MaxRetryTime; a gap older than that will never fill, so skip it.
    private static readonly TimeSpan ReorderGapTimeout = TimeSpan.FromSeconds(12);
    private static readonly Action DeliveredPlaceholder = static () => { };

    public ReliabilityManager(Action<ArraySegment<byte>, IPEndPoint> sendRawCallback)
    {
        this.sendRawCallback = sendRawCallback;
    }

    public void Start()
    {
        if (isRunning)
            return;

        isRunning = true;
        resendThread = new Thread(new Action(ResendLoop))
        {
            IsBackground = true,
            Name = "ReliabilityResendThread"
        };
        resendThread.Start();

        SrLogger.LogMessage("ReliabilityManager started");
    }

    public void Stop()
    {
        if (!isRunning)
            return;

        isRunning = false;

        foreach (var packet in pendingPackets.Values)
            PendingPacket.Return(packet);

        pendingPackets.Clear();
        
        while (pendingDisposal.TryDequeue(out var leftover))
            PendingPacket.Return(leftover);

        lastProcessedSequence.Clear();
        sequenceNumbersByChannel.Clear();
        reorderBuffers.Clear();
        reorderLocks.Clear();

        SrLogger.LogMessage("ReliabilityManager stopped");
    }

    public void TrackPacket(SplitResult splitData, IPEndPoint destination, ushort packetId,
        byte packetType, PacketReliability reliability)
    {
        if (!reliability.HasFlag(PacketReliability.Reliable))
            return;

        var key = new PacketKey(packetType, packetId, destination);
        pendingPackets[key] = PendingPacket.Borrow(splitData, destination, packetId, packetType);
    }

    public void HandleAck(IPEndPoint sender, ushort packetId, byte packetType)
    {
        var key = new PacketKey(packetType, packetId, sender);

        if (!pendingPackets.TryRemove(key, out var packet))
            return;

        SrLogger.LogPacketSize(
            $"ACK received for packet {packetId} (type={packetType}) " +
            $"after {packet.SendCount} sends, " +
            $"latency={(DateTime.UtcNow - packet.FirstSendTime).TotalMilliseconds:F1}ms");
        
        pendingDisposal.Enqueue(packet);
    }

    /// <summary>
    /// Returns the next sequence number for a given packet type / channel / destination.
    /// Sequence numbers wrap from <see cref="ushort.MaxValue"/> back to 1.
    /// </summary>
    public ushort GetNextSequenceNumber(NetworkChannel channel, byte packetType, IPEndPoint destination)
    {
        var key = new ChannelKey(destination, channel, packetType);
        var seq = sequenceNumbersByChannel.AddOrUpdate(
            key,
            1,
            (_, current) => current >= ushort.MaxValue ? 1 : current + 1);

        return (ushort)seq;
    }

    public bool ShouldProcessOrderedPacket(IPEndPoint sender, ushort sequenceNumber, byte packetType,
        NetworkChannel channel, PacketReliability reliability, Action? processAction = null)
    {
        var key     = new ChannelKey(sender, channel, packetType);
        var lockObj = reorderLocks.GetOrAdd(key, _ => new object());

        lock (lockObj)
        {
            // Senders number every stream from 1. Adopting the first *arrival* as the baseline silently dropped
            // every earlier packet that arrived late (already ACKed, so never resent). Peer state is reset on
            // (re)connect, so a missing entry always means "nothing processed yet".
            if (!lastProcessedSequence.TryGetValue(key, out var lastSequence))
                lastSequence = 0;

            SkipTimedOutGap(key, lockObj);
            lastSequence = lastProcessedSequence.TryGetValue(key, out var afterSkip) ? afterSkip : lastSequence;

            if (IsSequenceNewer(sequenceNumber, lastSequence) || sequenceNumber == NextSequence(lastSequence))
            {
                if (reliability == PacketReliability.Ordered)
                {
                    lastProcessedSequence[key] = sequenceNumber;
                    return true;
                }

                var expectedSequence = NextSequence(lastSequence);

                if (sequenceNumber == expectedSequence)
                {
                    lastProcessedSequence[key] = sequenceNumber;
                    DrainReorderBuffer(key, lockObj);
                    return true;
                }

                if (processAction != null)
                {
                    var buffer = reorderBuffers.GetOrAdd(key, _ => new SortedDictionary<ushort, BufferedPacket>());

                    if (buffer.ContainsKey(sequenceNumber))
                        return false;

                    if (buffer.Count < MaxReorderBufferSize)
                    {
                        buffer[sequenceNumber] = new BufferedPacket(processAction, DateTime.UtcNow);
                        SrLogger.LogPacketSize(
                            $"Buffered out-of-order packet: " +
                            $"expected seq={expectedSequence}, got seq={sequenceNumber}, " +
                            $"type={packetType}, channel={channel}, buffer size={buffer.Count}");
                    }
                    else
                    {
                        buffer[sequenceNumber] = new BufferedPacket(DeliveredPlaceholder, DateTime.UtcNow);
                        SrLogger.LogWarning(
                            $"Reorder buffer full; delivering out of order: " +
                            $"seq={sequenceNumber}, type={packetType}, channel={channel}");
                        return true;
                    }
                }
                else
                {
                    SrLogger.LogPacketAcknowledge(
                        $"Out-of-order packet dropped: " +
                        $"expected seq={expectedSequence}, got seq={sequenceNumber}, " +
                        $"type={packetType}, channel={channel}");
                }
            }

            return false;
        }
    }

    // todo: review
    private void DrainReorderBuffer(ChannelKey key, object lockObj)
    {
        if (!reorderBuffers.TryGetValue(key, out var buffer) || buffer.Count == 0)
            return;

        while (buffer.Count > 0)
        {
            var next = NextSequence(lastProcessedSequence.TryGetValue(key, out var last) ? last : (ushort)0);
            if (!buffer.TryGetValue(next, out var buffered))
                break;

            buffer.Remove(next);
            lastProcessedSequence[key] = next;

            Monitor.Exit(lockObj);
            try
            {
                buffered.Action();
            }
            finally
            {
                Monitor.Enter(lockObj);
            }
        }
    }

    /// <summary>
    /// Resends a large split packet starting at a different chunk each attempt (so chunks lost at the
    /// end of one burst lead the next), with short gaps between bursts. Runs on the resend thread.
    /// </summary>
    private void ResendPaced(PendingPacket packet)
    {
        var count = packet.SplitData.Count;
        var start = (int)((packet.SendCount * 7919L) % count);
        for (var sent = 0; sent < count; sent++)
        {
            sendRawCallback(packet.SplitData.Chunks[(start + sent) % count], packet.Destination);
            if ((sent + 1) % PacedChunksPerBurst == 0 && sent + 1 < count)
                Thread.Sleep(PacedBurstGapMilliseconds);
        }
    }

    private void ResendLoop()
    {
        while (isRunning)
        {
            try
            {
                var count = pendingPackets.Count;
                if (count == 0)
                {
                    DrainDisposalQueue();
                    Thread.Sleep(10);
                    continue;
                }

                var now           = DateTime.UtcNow;
                var keysToRemove  = ArrayPool<PacketKey>.Shared.Rent(count);
                var removeCount   = 0;

                foreach (var (key, packet) in pendingPackets)
                {
                    if (packet.IsRecycled)
                        continue;
                    
                    var largeSplit = packet.SplitData.Count > LargeSplitChunkThreshold;
                    if (now - packet.FirstSendTime > (largeSplit ? MaxSplitRetryTime : MaxRetryTime) ||
                        packet.SendCount >= MaxResendAttempts)
                    {
                        SrLogger.LogPacketAcknowledge(
                            $"Packet {packet.PacketId} (type={packet.PacketType}) " +
                            $"failed after {packet.SendCount} attempts");
                        keysToRemove[removeCount++] = key;
                        continue;
                    }

                    // Checks if packet should be resent
                    if (now - packet.LastSendTime > ResendInterval)
                    {
                        if (largeSplit)
                            ResendPaced(packet);
                        else
                            for (var i = 0; i < packet.SplitData.Count; i++)
                                sendRawCallback(packet.SplitData.Chunks[i], packet.Destination);

                        packet.LastSendTime = now;
                        packet.SendCount++;

                        if (packet.SendCount % 10 == 0)
                        {
                            SrLogger.LogPacketAcknowledge(
                                $"Resending packet {packet.PacketId} " +
                                $"(type={packet.PacketType}) attempt #{packet.SendCount}");
                        }
                    }
                }

                // Remove timed out packets
                for (var i = 0; i < removeCount; i++)
                {
                    if (pendingPackets.TryRemove(keysToRemove[i], out var timedOutPacket))
                        PendingPacket.Return(timedOutPacket);
                }

                ArrayPool<PacketKey>.Shared.Return(keysToRemove);

                DrainDisposalQueue();

                Thread.Sleep(10);
            }
            catch (Exception ex)
            {
                SrLogger.LogError($"ResendLoop error: {ex}");
            }
        }
    }
    
    private void DrainDisposalQueue()
    {
        while (pendingDisposal.TryDequeue(out var packet))
            PendingPacket.Return(packet);
    }

    // Matches GetNextSequenceNumber: sequences wrap from ushort.MaxValue back to 1 (0 is never sent).
    private static ushort NextSequence(ushort sequence) => sequence >= ushort.MaxValue ? (ushort)1 : (ushort)(sequence + 1);

    private static ushort PreviousSequence(ushort sequence) => sequence <= 1 ? ushort.MaxValue : (ushort)(sequence - 1);

    private void SkipTimedOutGap(ChannelKey key, object lockObj)
    {
        if (!reorderBuffers.TryGetValue(key, out var buffer) || buffer.Count == 0)
            return;

        var oldestSequence = (ushort)0;
        var oldestAt = DateTime.MaxValue;
        foreach (var (sequence, buffered) in buffer)
        {
            if (buffered.ReceivedAt >= oldestAt)
                continue;
            oldestAt = buffered.ReceivedAt;
            oldestSequence = sequence;
        }

        if (DateTime.UtcNow - oldestAt < ReorderGapTimeout)
            return;

        SrLogger.LogWarning($"Ordered stream gap never filled; skipping to seq={oldestSequence}");
        lastProcessedSequence[key] = PreviousSequence(oldestSequence);
        DrainReorderBuffer(key, lockObj);
    }

    /// <summary>
    /// Forgets all reliability state for one peer, so a reconnect from the same address starts fresh streams.
    /// </summary>
    public void ResetPeer(IPEndPoint peer)
    {
        foreach (var key in lastProcessedSequence.Keys)
            if (key.Peer.Equals(peer))
                lastProcessedSequence.TryRemove(key, out _);
        foreach (var key in sequenceNumbersByChannel.Keys)
            if (key.Peer.Equals(peer))
                sequenceNumbersByChannel.TryRemove(key, out _);
        foreach (var key in reorderBuffers.Keys)
            if (key.Peer.Equals(peer))
                reorderBuffers.TryRemove(key, out _);
        foreach (var (key, packet) in pendingPackets)
            if (key.EndPoint.Equals(peer) && pendingPackets.TryRemove(key, out _))
                pendingDisposal.Enqueue(packet);
    }

    private static bool IsSequenceNewer(ushort s1, ushort s2)
    {
        return ((s1 > s2) && (s1 - s2 <= 32768)) ||
               ((s1 < s2) && (s2 - s1 > 32768));
    }

    public int GetPendingPacketCount() => pendingPackets.Count;
}