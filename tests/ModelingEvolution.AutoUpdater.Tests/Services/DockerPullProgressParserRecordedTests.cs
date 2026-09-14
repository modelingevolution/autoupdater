using FluentAssertions;
using ModelingEvolution.AutoUpdater.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static ModelingEvolution.AutoUpdater.Tests.Services.PullFixtures;

namespace ModelingEvolution.AutoUpdater.Tests.Services
{
    /// <summary>
    /// Parser over recorded <c>docker compose --progress json pull</c> transcripts, with the size table the resolver builds
    /// from the matching recorded manifests (Fixtures/PullProgress).
    /// </summary>
    public class DockerPullProgressParserRecordedTests
    {
        [Theory]
        [InlineData("overlay2-compose2.40/pull-cold.jsonl")]
        [InlineData("overlay2-compose5.5/pull-cold.jsonl")]
        public async Task Feed_ColdPullWithTable_TotalIsTheResolvedSumFromBeforeTheFirstLineToTheEnd(string transcript)
        {
            var table = await ColdTableAsync();
            var parser = new DockerPullProgressParser(table);

            parser.Current.BytesTotal.Should().Be(ColdTotal, "the first snapshot already carries the true total");
            var snapshots = FeedAll(parser, Lines(transcript));

            snapshots.Should().NotBeEmpty();
            snapshots.Select(s => s.BytesTotal).Should().AllSatisfy(t => t.Should().Be(ColdTotal));
            snapshots.Select(s => (s.LayersKnown, s.LayersTotal)).Should().AllSatisfy(l => l.Should().Be((ColdLayers, ColdLayers)));
        }

        [Theory]
        [MemberData(nameof(ColdTranscripts), MemberType = typeof(PullFixtures))]
        public async Task Feed_ColdPullWithTable_DownloadedClimbsMonotonicallyToTheTotal(string transcript)
        {
            var parser = new DockerPullProgressParser(await ColdTableAsync());

            var snapshots = FeedAll(parser, Lines(transcript));

            snapshots.Select(s => s.BytesDownloaded).Should().BeInAscendingOrder();
            snapshots.Should().AllSatisfy(s => s.BytesDownloaded.Should().BeLessThanOrEqualTo(s.BytesTotal));
            parser.Current.BytesDownloaded.Should().Be(parser.Current.BytesTotal);
            parser.Current.BytesTotal.Should().BeGreaterThanOrEqualTo(ColdTotal);
            parser.Current.BytesPercent.Should().Be(100f);
            parser.Current.IsComplete.Should().BeTrue();
        }

        [Theory]
        [MemberData(nameof(ColdTranscripts), MemberType = typeof(PullFixtures))]
        public async Task Feed_ColdPullWithTable_TotalRightAfterTheFirstPulledImageIsUnchanged(string transcript)
        {
            // The v1.0.80 defect: a Pulled image's layers left the sum, so the total dropped and the bar restarted.
            var lines = Lines(transcript);
            var parser = new DockerPullProgressParser(await ColdTableAsync());
            var firstPulled = lines.ToList().FindIndex(l => l.Contains("\"text\":\"Pulled\""));
            firstPulled.Should().BeGreaterThan(0);

            FeedAll(parser, lines.Take(firstPulled));
            var before = parser.Current;
            parser.Feed(lines[firstPulled]);

            parser.Current.ImagesPulled.Should().Be(before.ImagesPulled + 1);
            parser.Current.BytesTotal.Should().Be(ColdTotal);
            parser.Current.BytesDownloaded.Should().BeGreaterThanOrEqualTo(before.BytesDownloaded);
        }

        [Theory]
        [MemberData(nameof(ColdTranscripts), MemberType = typeof(PullFixtures))]
        public async Task Feed_ColdPullWithTable_NeverReadsCompleteBeforeTheLastImageIsDone(string transcript)
        {
            // Seen end to end on saturn: "built" is Skipped first, so without the table's expected images the first snapshot
            // was 1/1 images, 100 %, "All images pulled", before a single byte had moved.
            var parser = new DockerPullProgressParser(await ColdTableAsync());
            parser.Current.ImagesTotal.Should().Be(3);
            parser.Current.IsComplete.Should().BeFalse();

            var snapshots = FeedAll(parser, Lines(transcript));

            snapshots.SkipLast(1).Should().AllSatisfy(s => s.IsComplete.Should().BeFalse());
            snapshots.Last().IsComplete.Should().BeTrue();
        }

        [Theory]
        [InlineData("overlay2-compose2.40/pull-cold.jsonl")]
        [InlineData("containerd-compose2.40/pull-cold.jsonl")]
        public async Task Feed_SecondServiceSkippedAsAlreadyBeingPulled_DoesNotCreditTheImageYet(string transcript)
        {
            // compose 2.40: {"id":"a2","text":"Skipped - Image is already being pulled by a"} arrives before "a" has downloaded.
            var lines = Lines(transcript);
            var skipped = lines.ToList().FindIndex(l => l.Contains("\"id\":\"a2\"") && l.Contains("already being pulled"));
            skipped.Should().BeGreaterThanOrEqualTo(0);
            var parser = new DockerPullProgressParser(await ColdTableAsync());

            FeedAll(parser, lines.Take(skipped + 1));

            parser.Current.BytesDownloaded.Should().Be(0);
        }

        [Theory]
        [MemberData(nameof(AllSuccessfulTranscripts), MemberType = typeof(PullFixtures))]
        public void Feed_AnyTranscriptWithoutTable_TotalAndDownloadedNeverDecrease(string transcript)
        {
            var parser = new DockerPullProgressParser();

            var snapshots = FeedAll(parser, Lines(transcript));

            snapshots.Select(s => s.BytesTotal).Should().BeInAscendingOrder();
            snapshots.Select(s => s.BytesDownloaded).Should().BeInAscendingOrder();
            parser.Current.IsComplete.Should().BeTrue();
        }

        [Fact]
        public void Feed_ColdPullWithoutTable_ReportsLayersNotSizedWhileTheyWait()
        {
            // compose 2.40 on the classic store: 6 nginx layers announce "Waiting" long before any size is known.
            var parser = new DockerPullProgressParser();
            var lines = Lines("overlay2-compose2.40/pull-cold.jsonl");
            var lastWaiting = lines.ToList().FindLastIndex(l => l.Contains("\"text\":\"Waiting\""));

            FeedAll(parser, lines.Take(lastWaiting + 1));

            parser.Current.LayersTotal.Should().Be(ColdLayers);
            parser.Current.LayersNotSized.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task Feed_SharedBaseLayerDownloadingUnderTwoImages_IsCreditedOnce()
        {
            var lines = Lines("overlay2-compose2.40/pull-cold.jsonl");
            lines.Where(l => l.Contains($"\"id\":\"{AlpineBaseLayer}\"") && l.Contains("\"text\":\"Downloading\""))
                .Select(l => l.Contains("\"parent_id\":\"a\"") ? "a" : l.Contains("\"parent_id\":\"n\"") ? "n" : "?")
                .Distinct().Should().BeEquivalentTo(new[] { "a", "n" }, "the recording downloads the base under both parents");
            var parser = new DockerPullProgressParser(await ColdTableAsync());

            var snapshots = FeedAll(parser, lines);

            snapshots.Should().AllSatisfy(s => s.BytesDownloaded.Should().BeLessThanOrEqualTo(ColdTotal));
            parser.Current.BytesDownloaded.Should().Be(ColdTotal);
        }

        [Theory]
        [InlineData("overlay2-compose2.40/pull-partial.jsonl")]
        [InlineData("containerd-compose2.40/pull-partial.jsonl")]
        public async Task Feed_PartialPullWithTable_LocalBaseLayerIsNeitherInTheTotalNorANotSizedLayer(string transcript)
        {
            // overlay2 announces the base "Already exists"; containerd does not mention it at all.
            var table = await PartialTableAsync();
            var parser = new DockerPullProgressParser(table);

            var snapshots = FeedAll(parser, Lines(transcript));

            snapshots.Should().AllSatisfy(s =>
            {
                s.BytesTotal.Should().Be(NginxOwnSize);
                s.LayersTotal.Should().Be(NginxOwnLayers);
                s.LayersKnown.Should().Be(NginxOwnLayers);
            });
            snapshots.Select(s => s.BytesDownloaded).Should().BeInAscendingOrder();
            parser.Current.BytesDownloaded.Should().Be(NginxOwnSize);
        }

        [Fact]
        public async Task Feed_LocalLayerAnnouncedBeforeAlreadyExists_NeverShowsAsNotSized()
        {
            // The classic daemon sometimes prints "Pulling fs layer" for a local layer before "Already exists" (seen in the plain
            // output of `docker pull docker:29-dind`: 4f4fb700ef54). The recorded partial transcript goes straight to
            // "Already exists", so the announcement is inserted in front of it, in compose 2.40's own line shape.
            var lines = Lines("overlay2-compose2.40/pull-partial.jsonl").ToList();
            var alreadyExists = lines.FindIndex(l => l.Contains(AlpineBaseLayer) && l.Contains("Already exists"));
            alreadyExists.Should().BeGreaterThan(0);
            lines.Insert(alreadyExists, $$"""{"id":"{{AlpineBaseLayer}}","parent_id":"n","text":"Pulling fs layer"}""");
            var parser = new DockerPullProgressParser(await PartialTableAsync());

            var snapshots = FeedAll(parser, lines);

            snapshots.Should().AllSatisfy(s => s.LayersNotSized.Should().Be(0));
        }

        [Fact]
        public async Task Feed_LayerTheResolverThoughtLocalIsDownloadedAnyway_TotalGrowsByItsSize()
        {
            // The resolver can be wrong in the direction it is allowed to be wrong in reverse: here it counted the alpine base as
            // present (partial table) but the daemon is cold (recorded cold transcript downloads it). The total must grow, not lie.
            var parser = new DockerPullProgressParser(await PartialTableAsync());

            var snapshots = FeedAll(parser, Lines("overlay2-compose2.40/pull-cold.jsonl"));

            snapshots.Select(s => s.BytesTotal).Should().BeInAscendingOrder();
            parser.Current.BytesTotal.Should().BeGreaterThanOrEqualTo(NginxOwnSize + AlpineBaseSize);
            parser.Current.BytesDownloaded.Should().BeGreaterThanOrEqualTo(NginxOwnSize + AlpineBaseSize);
        }

        [Theory]
        [InlineData("containerd-compose2.40/pull-cold.jsonl")]
        [InlineData("overlay2-compose2.40/pull-cold.jsonl")]
        public async Task Feed_ImageNotSized_TotalGrowsWhenItsLayerSizesArriveAndNeverShrinks(string transcript)
        {
            var device = new Device();
            device.Fail(DockerPullSizeResolver.ManifestCommand(Nginx), Text("manifests/manifest-error-notfound.txt"));
            var table = await device.ResolveAsync();
            var parser = new DockerPullProgressParser(table);

            parser.Current.BytesTotal.Should().Be(AlpineBaseSize + BusyboxSize);
            parser.Current.LayersKnown.Should().Be(2);
            parser.Current.LayersTotal.Should().Be(3, "the unsized image stands for at least one layer before compose lists them");

            var snapshots = FeedAll(parser, Lines(transcript));

            snapshots.Select(s => s.BytesTotal).Should().BeInAscendingOrder();
            snapshots.Should().Contain(s => s.LayersNotSized > 0);
            // Seen live on saturn: 14.8 MB of 14.8 MB known with 6 layers not sized read 100 % mid-pull.
            snapshots.Where(s => s.LayersNotSized > 0 && !s.IsComplete).Should().AllSatisfy(s => s.BytesPercent.Should().BeNull());
            snapshots.Should().AllSatisfy(s => s.BytesTotal.Should().BeGreaterThanOrEqualTo(AlpineBaseSize + BusyboxSize));
        }

        [Fact]
        public async Task Feed_ImageNotSized_DownloadingLineAddsTheLayerSizeAndMarksItKnown()
        {
            var device = new Device();
            device.Fail(DockerPullSizeResolver.ManifestCommand(Nginx), Text("manifests/manifest-error-notfound.txt"));
            var parser = new DockerPullProgressParser(await device.ResolveAsync());
            var lines = Lines("overlay2-compose2.40/pull-cold.jsonl");
            var firstNginxDownloading = lines.ToList().FindIndex(l =>
                l.Contains("\"parent_id\":\"n\"") && l.Contains("\"text\":\"Downloading\"") && !l.Contains(AlpineBaseLayer));
            firstNginxDownloading.Should().BeGreaterThan(0);

            FeedAll(parser, lines.Take(firstNginxDownloading));
            var before = parser.Current;
            parser.Feed(lines[firstNginxDownloading]);

            parser.Current.BytesTotal.Should().BeGreaterThan(before.BytesTotal);
            parser.Current.LayersKnown.Should().Be(before.LayersKnown + 1);
            parser.Current.LayersTotal.Should().Be(before.LayersTotal);
        }

        [Fact]
        public async Task Feed_ContainerdAttestationBlobs_OnlyTheOneWithBytesGrowsTheTotal()
        {
            // The containerd store also fetches the platform's attestation manifest layers (SBOM 9261b9aff737 = 872789 bytes,
            // provenance 2a84448aca9c; alpine's 09de0793c073/5d2b0d8b1d1e). The resolver does not size them (design.md):
            // those reported only as "Download complete" are ignored, the one with a Downloading line is real bytes and added.
            const string sbom = "9261b9aff737";
            const long sbomSize = 872789;
            var lines = Lines("containerd-compose2.40/pull-cold.jsonl");
            var sbomDownloading = lines.ToList().FindIndex(l => l.Contains($"\"id\":\"{sbom}\"") && l.Contains("Downloading"));
            sbomDownloading.Should().BeGreaterThan(0);
            var parser = new DockerPullProgressParser(await ColdTableAsync());

            var before = FeedAll(parser, lines.Take(sbomDownloading));
            var after = FeedAll(parser, lines.Skip(sbomDownloading));

            before.Should().AllSatisfy(s => (s.BytesTotal, s.LayersKnown, s.LayersTotal).Should().Be((ColdTotal, ColdLayers, ColdLayers)));
            after.Should().AllSatisfy(s => (s.BytesTotal, s.LayersKnown, s.LayersTotal).Should().Be((ColdTotal + sbomSize, ColdLayers + 1, ColdLayers + 1)));
            parser.Current.BytesDownloaded.Should().Be(ColdTotal + sbomSize);
        }

        [Theory]
        [InlineData("containerd-compose2.40/pull-warm.jsonl")]
        [InlineData("overlay2-compose5.5/pull-warm.jsonl")]
        public async Task Feed_TableExpectedDownloadsButEverythingWasLocal_PulledImagesCreditTheirLayers(string transcript)
        {
            // Resolver sized a cold daemon, the daemon had it all: compose reports only Pulling/Pulled, no layer lines.
            var parser = new DockerPullProgressParser(await ColdTableAsync());

            var snapshots = FeedAll(parser, Lines(transcript));

            snapshots.Select(s => s.BytesDownloaded).Should().BeInAscendingOrder();
            parser.Current.BytesTotal.Should().Be(ColdTotal);
            parser.Current.BytesDownloaded.Should().Be(ColdTotal);
        }

        [Fact]
        public async Task Feed_WarmPullWithTable_NothingToDownloadAndCompletes()
        {
            var parser = new DockerPullProgressParser(await WarmTableAsync());

            FeedAll(parser, Lines("containerd-compose2.40/pull-warm.jsonl"));

            parser.Current.BytesTotal.Should().Be(0);
            parser.Current.LayersTotal.Should().Be(0);
            parser.Current.IsComplete.Should().BeTrue();
            parser.Current.BytesPercent.Should().Be(100f);
        }

        [Theory]
        [InlineData("overlay2-compose2.40/pull-cold.jsonl", 5)]
        [InlineData("overlay2-compose5.5/pull-cold.jsonl", 4)]
        public void Feed_BuildOnlyAndDuplicateServices_AreTerminalImages(string transcript, int images)
        {
            // compose 2.40 lists "built" and "a2" as Skipped; compose 5.5 lists "built" and dedupes a/a2 into one image id.
            var parser = new DockerPullProgressParser();

            FeedAll(parser, Lines(transcript));

            parser.Current.ImagesTotal.Should().Be(images);
            parser.Current.ImagesPulled.Should().Be(images);
        }

        [Theory]
        [InlineData("overlay2-compose5.5/pull-ratelimited.jsonl", "toomanyrequests: Rate exceeded")]
        [InlineData("containerd-compose2.40/pull-ratelimited-dockerhub.jsonl", "429 Too Many Requests")]
        public void Feed_RecordedRegistryError_ExposesTheReasonAndDoesNotStall(string transcript, string reason)
        {
            var parser = new DockerPullProgressParser();
            var lines = Lines(transcript);
            var errorLine = lines.ToList().FindIndex(l => l.Contains("\"text\":\"Error\""));

            FeedAll(parser, lines.Take(errorLine + 1));

            parser.ErrorMessage.Should().Contain(reason);
        }
    }
}
