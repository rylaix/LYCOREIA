using SR2MP.Components.Drone;
using SR2MP.Packets.Ammo;
using SR2MP.Packets.Utils;

namespace SR2MP.Packets.Drone;

internal sealed class DroneUpdatePacket : IPacket
{
    public long StationId;
    public Vector3 Position;
    public Quaternion Rotation;
    public bool AtStation;
    public int AnimatorState;
    public List<NetworkDrone.AnimatorParameter> AnimatorParams = new();
    public NetworkAmmo? Ammo;

    public PacketType Type => PacketType.DroneUpdate;
    public PacketReliability Reliability => PacketReliability.Ordered;
    public NetworkChannel Channel => NetworkChannel.ActorUpdate;

    public void Serialise(PacketWriter writer)
    {
        writer.WritePackedLong(StationId);
        writer.WriteVector3(Position);
        writer.WriteQuaternion(Rotation);
        writer.WriteBool(AtStation);
        writer.WriteInt(AnimatorState);

        writer.WriteByte((byte)AnimatorParams.Count);
        foreach (var param in AnimatorParams)
        {
            writer.WriteInt(param.Hash);
            writer.WriteByte(param.Type);
            writer.WriteFloat(param.Value);
        }

        writer.WriteBool(Ammo != null);
        if (Ammo != null)
            writer.WriteNetObject(Ammo);
    }

    public void Deserialise(PacketReader reader)
    {
        StationId = reader.ReadPackedLong();
        Position = reader.ReadVector3();
        Rotation = reader.ReadQuaternion();
        AtStation = reader.ReadBool();
        AnimatorState = reader.ReadInt();

        var paramCount = reader.ReadByte();
        AnimatorParams = new List<NetworkDrone.AnimatorParameter>(paramCount);
        for (var i = 0; i < paramCount; i++)
        {
            AnimatorParams.Add(new NetworkDrone.AnimatorParameter
            {
                Hash = reader.ReadInt(),
                Type = reader.ReadByte(),
                Value = reader.ReadFloat()
            });
        }

        if (reader.ReadBool())
            Ammo = reader.ReadNetObject<NetworkAmmo>();
    }

#if SR2MP_TEST
    internal static bool VerifySerializationRoundTrip()
    {
        var expected = new DroneUpdatePacket
        {
            StationId = 0x123456,
            Position = new Vector3(1f, 2f, 3f),
            Rotation = new Quaternion(0f, 0.5f, 0f, 0.5f),
            AtStation = true,
            AnimatorState = 17,
            AnimatorParams = new List<NetworkDrone.AnimatorParameter>
            {
                new() { Hash = 123, Type = 2, Value = 0.75f }
            }
        };

        using var writer = PacketWriter.Borrow();
        expected.Serialise(writer);
        using var reader = PacketReader.Borrow(writer.ToSpan().ToArray());
        var actual = new DroneUpdatePacket();
        actual.Deserialise(reader);

        var hasParameter = actual.AnimatorParams.Count == expected.AnimatorParams.Count;
        var parameter = hasParameter ? actual.AnimatorParams[0] : default;
        var passed = reader.BytesRemaining == 0 && actual.StationId == expected.StationId &&
                     actual.Position.x == expected.Position.x &&
                     actual.Position.y == expected.Position.y &&
                     actual.Position.z == expected.Position.z &&
                     actual.Rotation.x == expected.Rotation.x &&
                     actual.Rotation.y == expected.Rotation.y &&
                     actual.Rotation.z == expected.Rotation.z &&
                     actual.Rotation.w == expected.Rotation.w &&
                     actual.AtStation == expected.AtStation && actual.AnimatorState == expected.AnimatorState &&
                     actual.Ammo == null && hasParameter &&
                     parameter.Hash == expected.AnimatorParams[0].Hash &&
                     parameter.Type == expected.AnimatorParams[0].Type &&
                     parameter.Value == expected.AnimatorParams[0].Value;

        if (!passed)
            SrLogger.LogWarning(
                $"[SR2MP_TEST] drone-update-codec-details bytesRemaining={reader.BytesRemaining} " +
                $"station={actual.StationId}/{expected.StationId} position={actual.Position}/{expected.Position} " +
                $"rotation={actual.Rotation}/{expected.Rotation} atStation={actual.AtStation}/{expected.AtStation} " +
                $"animatorState={actual.AnimatorState}/{expected.AnimatorState} ammoNull={actual.Ammo == null} " +
                $"parameterCount={actual.AnimatorParams.Count}/{expected.AnimatorParams.Count} " +
                $"parameter={parameter.Hash}:{parameter.Type}:{parameter.Value}/" +
                $"{expected.AnimatorParams[0].Hash}:{expected.AnimatorParams[0].Type}:{expected.AnimatorParams[0].Value}");

        return passed;
    }
#endif
}
