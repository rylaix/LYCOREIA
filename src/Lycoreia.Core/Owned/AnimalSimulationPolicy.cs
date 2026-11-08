// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Actors
{
    public static class AnimalSimulationPolicy
    {
        public static bool MayReproduce(bool networked, bool authorized, bool hibernating, bool loading)
            => !networked || authorized && !hibernating && !loading;
        // Native RegistryUpdate increments from an old deadline. A missed interval must
        // not keep creating/destroying capped offspring on every following frame.
        public static double NextDeadline(double previous, double now, double period)
            => double.IsFinite(previous) && previous > now ? previous : now + System.Math.Max(1, period);
        public static int ReproductionCap(bool onRanch) => onRanch ? ChickenCap.RanchWithin30m : ChickenCap.Within30m;
        public static bool MayBreedAtPopulation(int population, bool onRanch) => population < ReproductionCap(onRanch);
    }

    public static class LoadingHazardPolicy
    {
        public const double PreLoadWindowSeconds = 2.0;
        // The native loader requests its pause before it publishes its loading flag; a player crossing the
        // ocean volume in that gap must not die before the destination geometry exists.
        public static bool DeferPlayerKill(bool isLocalPlayer, bool nativeLoading, double now, double lastLoadPauseRequest)
            => isLocalPlayer && (nativeLoading || (now >= lastLoadPauseRequest && now - lastLoadPauseRequest <= PreLoadWindowSeconds));
    }
}
