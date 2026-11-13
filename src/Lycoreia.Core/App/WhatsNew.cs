// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
using System.Collections.Generic;

namespace Lycoreia
{
    public static class WhatsNew
    {
        public sealed class Section { public string Title; public List<(string text, string tryIt)> Items = new List<(string, string)>(); }

        public static readonly List<Section> Sections = new List<Section>
        {
            new Section { Title = "New: co-op features", Items =
            {
                ("Boost jump: players are now solid. Stand on a friend and jump to reach much higher.", "Have a friend stand still, jump onto their head, then jump again."),
                ("Co-op wheel: hold the ping button (middle mouse) on a corral, garden, silo, Gordo or any spot to pin jobs for each other, claim them and tick them off. Jobs show on the compass and above the spot.", "Hold the middle mouse button while looking at a corral and pick \"Job: Feed this corral\"."),
                ("Expeditions: timed goals you play together (Plort Rush, Gordo Feast, Treasure Trail...). Finishing one pays the shared ranch.", "Pause > Multiplayer > Co-op board > Expeditions > Harvest Run, then vacuum up food together."),
                ("Slime notes: leave a note on a corral or a Gordo. Everyone sees it floating above it.", "Hold the middle mouse button on a Gordo and pick a note, or \"write your own...\"."),
                ("Session recap: what everyone did this session (plorts sold, slimes caught, money earned, jobs done).", "Pause > Multiplayer > Co-op board > Recap, or the main menu after you leave."),
                ("Update notice: the main menu tells you when GitHub or Nexus Mods has a newer version.", "Shown at the top of the main menu as soon as the game starts."),
            } },
            new Section { Title = "Fixed", Items =
            {
                ("Silos, plort collectors and auto-feeders now sync, and plorts are no longer doubled by the collectors.", "Collect plorts with a corral collector; both players see the same silo count."),
                ("Gates, ranch expansions, plort statue doors, puzzle slots and prisma barriers opened by one player open for everyone.", "Open a gate or buy an expansion; the other player sees it open."),
                ("Paddocks and plot upgrades from saves made before playing together now appear for the guest too.", "Join a save that already has corrals."),
                ("Ringtails no longer multiply at dawn and dusk.", "Watch Ringtails turn to stone and wake up with both players nearby."),
                ("Food eaten by a slime disappears for everyone (no more endless chickens).", "Shoot a chicken into the other player's corral."),
                ("Remote players' heads no longer spin; avatars no longer spin during short lag.", ""),
                ("The host returning to the main menu now ends the session cleanly.", ""),
                ("Fewer errors near unloaded doors; plorts can no longer show up slime-sized.", ""),
            } },
        };
    }
}
