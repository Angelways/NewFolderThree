using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Runtime;

namespace NewFolderThree;

internal static class Program
{
    public static int Main()
    {
        int passed = 0;
        passed += Run("y-axis-movement-packets", YAxisPacketTests.Run);
        passed += Run("detailed-map-host-options-no-service", TestDetailedMapHostOptionsNoService);
        passed += Run("catalog-manager-no-http-when-unavailable", TestCatalogManagerNoHttpWhenUnavailable);
        passed += Run("local-manifest-identity", TestLocalManifestIdentity);
        passed += Run("full-save-replacement", TestFullSaveReplacement);
        passed += Run("normal-summit-progression", TestNormalSummitProgression);
        passed += Run("current-duty-continuation-floor-routing", TestCurrentDutyContinuationFloorRouting);
        passed += Run("potd-speed-floor-gate", TestPotdSpeedFloorGate);
        passed += Run("potd-machinist-action-block", TestPotdMachinistActionBlock);
        passed += Run("boss-pull-on-idle-tenth-floor", TestBossPullTimer);
        Console.WriteLine($"NewFolderThree contract tests: {passed} passed");
        return 0;
    }

    private static int Run(string name, Action test)
    {
        test();
        Console.WriteLine($"  ok  {name}");
        return 1;
    }

    private static void TestLocalManifestIdentity()
    {
        string manifestPath = Path.Combine(AppContext.BaseDirectory, ProductIdentity.InternalName + ".json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = manifest.RootElement;
        Assert(root.GetProperty("InternalName").GetString() == ProductIdentity.InternalName,
            "Dalamud must use the local plugin identity for the config file and directory.");
        Assert(root.GetProperty("Name").GetString() == ProductIdentity.DisplayName,
            "The manifest and the plugin must have the same display name.");
        Assert(root.GetProperty("Author").GetString() == "John Smith",
            "The local manifest must retain the upstream author.");
    }

    private static void TestFullSaveReplacement()
    {
        Assert(!new FarmingSettings().AutoDeleteSaveWhenFull,
            "Fresh and legacy settings must leave save replacement off.");
        var first = new SaveSlotSnapshot(0, false, 11, "11", "first") { RowFingerprint = "row-1" };
        var second = new SaveSlotSnapshot(1, false, 31, "31", "second") { RowFingerprint = "row-2" };
        Assert(SaveSlotReplacementPolicy.FindCandidate([first, second]) == second,
            "A full list must select only the second slot.");
        Assert(SaveSlotReplacementPolicy.IsStillEligible([first, second with { Enterable = false }], second),
            "The reset list may change enterability of the same save.");
        Assert(!SaveSlotReplacementPolicy.IsStillEligible([first with { Empty = true }, second], second),
            "An empty slot must prevent deletion.");
        Assert(!SaveSlotReplacementPolicy.IsStillEligible([first, second with { RowFingerprint = "new save" }], second),
            "A changed save must prevent deletion.");
        Assert(SaveSlotReplacementPolicy.FindCandidate([first, second with { RowFingerprint = null }]) == null,
            "Replacement must require a readable save fingerprint.");
    }

    private static void TestNormalSummitProgression()
    {
        foreach (var (dungeon, first, summit) in new (uint, int, int)[] { (1, 51, 200), (2, 21, 100), (4, 71, 100) })
        {
            Assert(FarmingPlan.SummitStartFloor(dungeon) == first, "Unexpected summit start floor.");
            Assert(FarmingPlan.TryCreate(FarmingMode.NormalSummit, SaveUse.Create, first,
                1, false, false, true, true, false, out var plan, out _, dungeon),
                "Normal summit must allow the dungeon's direct start.");
            Assert(!FarmingPlan.TryCreate(FarmingMode.NormalSummit, SaveUse.Create, 1,
                1, false, false, true, true, false, out _, out _, dungeon),
                "Normal summit must not start on floor one.");

            for (int floor = first; floor <= summit; floor += 10)
            {
                var result = new FarmingAttemptResult(floor, true, false, false, false);
                var next = FarmingSessionPolicy.Decide(plan!, result);
                bool terminal = floor + 9 == summit;
                Assert(next.Error == "" && next.CountCycle == terminal && next.DeleteOwnedSave == terminal &&
                    next.NextFloor == (terminal ? first : floor + 10), "Unexpected progression after a clear.");
                Assert(FarmingSessionPolicy.ReachedStopBoundary(plan! with { StopAfterFloor = first + 9 }, result),
                    "A ten-floor validation boundary must stop the run.");
            }

            var retry = FarmingSessionPolicy.Decide(plan!, new(first + 10, false, true, false, false));
            Assert(retry is { CountCycle: false, DeleteOwnedSave: true, RecoverFailure: true } && retry.NextFloor == first,
                "A retry must return to the configured summit start.");

            var attachedPlan = plan! with { ContinuesExistingProgress = true };
            var interrupted = FarmingSessionPolicy.Decide(attachedPlan,
                new FarmingAttemptResult(first, false, false, false, true));
            var failedAttached = FarmingSessionPolicy.Decide(attachedPlan,
                new FarmingAttemptResult(first, false, true, false, false));
            var completedAttached = FarmingSessionPolicy.Decide(attachedPlan,
                new FarmingAttemptResult(summit - 9, true, false, false, false));
            Assert(!interrupted.DeleteOwnedSave && !failedAttached.DeleteOwnedSave &&
                   !failedAttached.RecoverFailure && failedAttached.Error.Length > 0,
                "Interrupted current-duty continuation must preserve its source save.");
            Assert(completedAttached.CountCycle && !completedAttached.DeleteOwnedSave,
                "Completed current-duty continuation must preserve its source save.");
        }
    }

    private static void TestCurrentDutyContinuationFloorRouting()
    {
        foreach (var (dungeon, maxFloor) in new (uint, int)[] { (1, 200), (2, 100) })
        {
            for (int floor = 1; floor <= maxFloor; floor++)
            {
                Assert(CurrentDutyContinuationPolicy.TryCreate(dungeon, floor,
                        out var continuation, out var error) && error.Length == 0,
                    $"Floor {floor} must be recognized for dungeon {dungeon}.");
                int blockStart = floor % 10 == 0 ? floor - 9 : floor - ((floor - 1) % 10);
                Assert(continuation.BlockStartFloor == blockStart &&
                       continuation.IsTenFloorBoundary == (floor % 10 == 0 && floor != maxFloor) &&
                       continuation.IsResultFloor == (floor == maxFloor) &&
                       continuation.BlockEndFloor == Math.Min(blockStart + 9, maxFloor) &&
                       continuation.RequiresSaveSlot == (blockStart + 9 < maxFloor),
                    $"Floor {floor} must route to the correct ten-floor block.");
            }
        }

        Assert(!CurrentDutyContinuationPolicy.TryCreate(4, 1, out _, out _) &&
               !CurrentDutyContinuationPolicy.TryCreate(1, 0, out _, out _) &&
               !CurrentDutyContinuationPolicy.TryCreate(2, 101, out _, out _),
            "Only valid floors in PotD and Heaven-on-High can use current-duty continuation.");

        var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 41, 1, false, false, true, true, false)
        { DungeonId = 1, ContinuesExistingProgress = true };
        var session = new FarmingSession(plan, continuationSlot: 0);
        Assert(session.SourceSlot == 0 && session.OwnedSlot == -1,
            "A selected slot is available for first-block pause verification before it is bound by entry.");
        session.CompleteAttempt(new(false, 51, false, false, ""), 0);
        var source = session.PrepareSource();
        var original = new SaveSlotSnapshot(0, false, 41, "第 41 層", "original") { Enterable = false };
        var unrelated = new SaveSlotSnapshot(1, false, 51, "第 51 層", "unrelated");
        Assert(!source.Observe([original, unrelated], out _, out _),
            "Matching the next floor in the other slot must not take over an unrelated save.");
        var advanced = original with { StartFloor = 51, Enterable = true };
        Assert(source.Observe([advanced, unrelated], out var resolved, out _) && resolved == advanced &&
               session.BindContinuationSave() && session.OwnedSlot == 0,
            "The selected slot must bind after advancing to the expected floor, even when the other slot has the same floor.");
    }

    private static void TestBossPullTimer()
    {
        var timer = new BossPullTimer();
        foreach (var (dungeon, terminal) in new (uint, byte)[] { (1, 200), (2, 100), (4, 100) })
        {
            var floor = new DeepDungeonStateSnapshot(true, true, true, dungeon, 10, DeepDungeonFloorKind.Boss, false, 0);
            timer.Update(true, floor, false, 0);
            Assert(!timer.CanAttempt(29_999) && timer.CanAttempt(30_000), "Boss pull must wait 30 seconds.");
            timer.Attempted(30_000, dispatched: false);
            Assert(!timer.CanAttempt(30_999) && timer.CanAttempt(31_000), "An unavailable target may be retried.");
            timer.Attempted(31_000, dispatched: true);
            Assert(!timer.CanAttempt(60_000), "A dispatched boss pull must not be repeated on the same floor.");

            var next = floor with { Floor = 20 };
            timer.Update(true, next, false, 60_000);
            timer.Update(true, next, true, 70_000);
            timer.Update(true, next, false, 80_000);
            Assert(!timer.CanAttempt(90_000), "Natural combat must cancel the pull for that floor.");
            timer.Update(true, floor with { Floor = terminal }, false, 90_000);
            Assert(!timer.CanAttempt(120_000), "The final floor must never trigger the boss pull.");

            timer.Update(true, floor, false, 120_000);
            timer.Update(false, floor, false, 149_000);
            Assert(!timer.CanAttempt(150_000), "Stopping FSD must discard the pending pull.");
        }
    }

    private static void TestPotdSpeedFloorGate()
    {
        for (int floor = 1; floor <= PotdMovementSpeedPolicy.PalaceOfTheDeadMaximumFloor; floor++)
        {
            bool expected = floor % 10 != 0;
            Assert(PotdMovementSpeedPolicy.IsEligibleFloor(
                    PotdMovementSpeedPolicy.PalaceOfTheDeadDungeonId, floor) == expected,
                $"Unexpected PotD speed eligibility for floor {floor}.");
        }

        for (int floor = 1; floor <= PotdMovementSpeedPolicy.HeavenOnHighMaximumFloor; floor++)
        {
            bool expected = floor % 10 != 0;
            Assert(PotdMovementSpeedPolicy.IsEligibleFloor(
                    PotdMovementSpeedPolicy.HeavenOnHighDungeonId, floor) == expected,
                $"Unexpected Heaven-on-High speed eligibility for floor {floor}.");
        }

        Assert(!PotdMovementSpeedPolicy.IsEligibleFloor(0, 1),
            "Unknown dungeons must not enable PotD speed adjustment.");
        Assert(!PotdMovementSpeedPolicy.IsEligibleFloor(1, 0) &&
               !PotdMovementSpeedPolicy.IsEligibleFloor(1, 201),
            "Floors outside the PotD 1-200 range must not enable speed adjustment.");
        Assert(!PotdMovementSpeedPolicy.IsEligibleFloor(2, 0) &&
               !PotdMovementSpeedPolicy.IsEligibleFloor(2, 101),
            "Floors outside the Heaven-on-High 1-100 range must not enable speed adjustment.");

        Assert(PotdMovementSpeedPolicy.EstimateNavigationSpeed(1, 11, 9f, 6f, 0.5f, true) == 9f &&
               PotdMovementSpeedPolicy.EstimateNavigationSpeed(2, 91, 9f, 6f, 0.5f, true) == 9f,
            "An effective native target speed must reach navigation without applying the multiplier twice.");
        Assert(PotdMovementSpeedPolicy.EstimateNavigationSpeed(1, 11, 0f, 6f, 0.5f, true) == 9f &&
               PotdMovementSpeedPolicy.EstimateNavigationSpeed(2, 91, 0f, 0f, 0.5f, true) == 9f,
            "Without a native target speed, estimate from the base or default speed and the configured multiplier.");
        Assert(PotdMovementSpeedPolicy.EstimateNavigationSpeed(1, 10, 0f, 6f, 0.5f, true) == 6f &&
               PotdMovementSpeedPolicy.EstimateNavigationSpeed(2, 100, 0f, 6f, 0.5f, true) == 6f &&
               PotdMovementSpeedPolicy.EstimateNavigationSpeed(3, 11, 0f, 6f, 0.5f, true) == 6f &&
               PotdMovementSpeedPolicy.EstimateNavigationSpeed(1, 11, 0f, 6f, 0.5f, false) == 6f,
            "Navigation must use the unadjusted fallback speed outside active non-boss floors.");
    }

    private static void TestPotdMachinistActionBlock()
    {
        bool policy(uint jobId, uint dungeonId, int floor, uint requestedId,
            uint adjustedId) =>
            PotdMachinistActionPolicy.ShouldBlock(
                jobId, dungeonId, floor, requestedId, adjustedId);
        Assert(policy(31, 1, 141, 2890, 2890) &&
               policy(31, 1, 199, 2890, 2890),
            "Machinist action 2890 must be blocked on eligible late PotD floors.");
        Assert(!policy(31, 1, 140, 2890, 2890) &&
               !policy(31, 1, 150, 2890, 2890) &&
               !policy(31, 1, 200, 2890, 2890) &&
               !policy(31, 1, 201, 2890, 2890),
            "The action block must exclude floor 140, every tenth floor, and floors above 200.");
        Assert(!policy(24, 1, 141, 2890, 2890) &&
               !policy(31, 2, 141, 2890, 2890) &&
               !policy(31, 1, 141, 2891, 2891) &&
               policy(31, 1, 141, 2866, 2890),
            "The block must match the machinist, PotD, and requested or adjusted action ID only.");
        Assert(policy(31, 2, 91, 2890, 2890) &&
               policy(31, 2, 99, 2890, 2890) &&
               policy(31, 2, 93, 2866, 2890),
            "Machinist action 2890 must also be blocked on Heaven-on-High floors 91-99.");
        Assert(!policy(31, 2, 90, 2890, 2890) &&
               !policy(31, 2, 100, 2890, 2890) &&
               !policy(24, 2, 91, 2890, 2890),
            "The Heaven-on-High block must exclude boundary floors and other jobs.");
    }

    private static void TestDetailedMapHostOptionsNoService()
    {
        var options = new DetailedMapHostOptions(
            catalogBaseUri: null,
            contributesAnonymousEvidence: false,
            deleteCatalogsWhenDisabled: false,
            supportsControlledPtSurvey: false);
        Assert(
            !options.HasOnlineCatalogService &&
            options.CatalogBaseUri == null,
            "Null catalog URI must mean no online catalog service.");

        bool rejected = false;
        try
        {
            _ = new DetailedMapHostOptions(
                catalogBaseUri: null,
                contributesAnonymousEvidence: true,
                deleteCatalogsWhenDisabled: false,
                supportsControlledPtSurvey: false);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Assert(
            rejected,
            "Anonymous evidence contribution must require an online catalog service.");
    }

    private static void TestCatalogManagerNoHttpWhenUnavailable()
    {
        string configRoot = Path.Combine(
            Path.GetTempPath(),
            $"newfolderthree-catalog-noservice-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(configRoot);
            var options = new DetailedMapHostOptions(
                catalogBaseUri: null,
                contributesAnonymousEvidence: false,
                deleteCatalogsWhenDisabled: false,
                supportsControlledPtSurvey: false);
            using var manager = new DetailedMapCatalogManager(configRoot, options);
            Assert(
                !manager.HasOnlineCatalogService,
                "Catalog manager must report no online catalog service.");

            string scenarioKey =
                DetailedMapEvidenceContract.PilgrimsTraverse21To30ScenarioKey;
            manager.Update(
                enabled: true,
                scenarioKey,
                runActive: false);
            DetailedMapCatalogStatusSnapshot status = manager.GetStatus(
                enabled: true,
                scenarioKey);
            Assert(
                status.Message == DetailedMapHostOptions.NoOnlineCatalogServiceMessage &&
                !status.Checking &&
                !status.HasValidCatalog,
                "Unavailable catalog service must surface the no-service status without checking.");

            Assert(
                !manager.TryAcquireRunSnapshot(
                    useDetailedMap: true,
                    scenarioKey,
                    out _,
                    out string detailedError) &&
                detailedError == DetailedMapHostOptions.NoOnlineCatalogServiceMessage,
                "Detailed-map acquisition must fail explicitly when no service is configured.");
            Assert(
                manager.TryAcquireRunSnapshot(
                    useDetailedMap: false,
                    scenarioKey,
                    out DetailedMapRunSnapshot palantir,
                    out string palantirError) &&
                palantir.Policy == DetailedMapRuntimePolicy.PalantirOnly &&
                palantirError.Length == 0,
                "Palantir-only acquisition must remain available without an online catalog service.");
            manager.ReleaseRunSnapshot();
        }
        finally
        {
            if (Directory.Exists(configRoot))
                Directory.Delete(configRoot, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
