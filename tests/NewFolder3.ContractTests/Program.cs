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
        passed += Run("potd-speed-floor-gate", TestPotdSpeedFloorGate);
        passed += Run("potd-machinist-action-block", TestPotdMachinistActionBlock);
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
        }
    }

    private static void TestPotdSpeedFloorGate()
    {
        for (int floor = 1; floor <= PotdMovementSpeedPolicy.MaximumFloor; floor++)
        {
            bool expected = floor % 10 != 0;
            Assert(PotdMovementSpeedPolicy.IsEligibleFloor(
                    PotdMovementSpeedPolicy.PalaceOfTheDeadDungeonId, floor) == expected,
                $"Unexpected PotD speed eligibility for floor {floor}.");
        }

        Assert(!PotdMovementSpeedPolicy.IsEligibleFloor(0, 1),
            "Unknown dungeons must not enable PotD speed adjustment.");
        Assert(!PotdMovementSpeedPolicy.IsEligibleFloor(2, 11),
            "Other Deep Dungeons must not enable PotD speed adjustment.");
        Assert(!PotdMovementSpeedPolicy.IsEligibleFloor(1, 0) &&
               !PotdMovementSpeedPolicy.IsEligibleFloor(1, 201),
            "Floors outside the PotD 1-200 range must not enable speed adjustment.");

        Assert(PotdMovementSpeedPolicy.ApplySpeedAddition(1, 11, 6f, 0.5f, true) == 6.5f,
            "Navigation speed must include the active PotD speed addition.");
        Assert(PotdMovementSpeedPolicy.ApplySpeedAddition(1, 10, 6f, 0.5f, true) == 6f &&
               PotdMovementSpeedPolicy.ApplySpeedAddition(2, 11, 6f, 0.5f, true) == 6f &&
               PotdMovementSpeedPolicy.ApplySpeedAddition(1, 11, 6f, 0.5f, false) == 6f,
            "Navigation speed must remain unchanged outside an active PotD mob floor.");
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
