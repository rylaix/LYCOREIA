// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using Lycoreia.Domain;

namespace Lycoreia.Net.Messages
{
    /// <summary>
    /// Cosmetic only: a player boost-jumped off another player. A guest sends it to the host, which stamps the jumper from the
    /// connection, plays it itself and relays it; the host's own boosts are broadcast. Unreliable (a lost one is just a missed puff).
    /// </summary>
    [Msg(414, Direction.Both, Delivery.Unreliable, Lane.Fx)]
    public sealed class BoostFx : Msg, IPlayerScoped
    {
        public string JumperId, StoodOnId; public Vec3 Position; public float Strength;
        public string ScopedPlayerId => JumperId;
        public override void Describe(Codec c) { c.Str(ref JumperId, 64); c.Str(ref StoodOnId, 64); c.V3(ref Position); c.F32(ref Strength); }
    }
}
