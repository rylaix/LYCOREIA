// Copyright (c) 2026 Rylaix and Kum1te. All rights reserved.
// Lycoreia Source-Available License 1.0 - see LICENSE. Copying, redistribution and derivative works are prohibited.
namespace Lycoreia.Tests
{
    static class Suites
    {
        public static void Register()
        {
            WireTests.Register(); LinkTests.Register(); SessionTests.Register(); MessageTests.Register(); SnapshotTests.Register(); AppTests.Register(); ActorTests.Register(); WorldTests.Register(); PlayerTests.Register(); StageATests.Register(); StageBTests.Register(); StageCTests.Register(); StageB2Tests.Register(); DesyncTests.Register(); SyncFixTests.Register(); RegressionKTests.Register(); PauseTests.Register(); DiscordPresenceTests.Register(); PlayerReportTests.Register(); UpdateCheckTests.Register(); Release21Tests.Register(); LogSinkAndGateTests.Register(); GardenPendingTests.Register(); CoopWheelPolishTests.Register(); CoopWheelJobsTests.Register(); CoopWorldTrackerTests.Register(); FxPolishTests.Register(); CoopTextTests.Register(); MenuPolishTests.Register(); LayoutTextTests.Register(); WorldLabelTests.Register(); MapLabelTests.Register(); BoostFxAndCueTests.Register(); ConnectivityTests.Register();
        }
    }
}
