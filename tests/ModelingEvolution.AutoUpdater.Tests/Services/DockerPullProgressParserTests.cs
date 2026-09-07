using FluentAssertions;
using ModelingEvolution.AutoUpdater.Services;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace ModelingEvolution.AutoUpdater.Tests.Services
{
    /// <summary>
    /// Lines below were captured verbatim from <c>docker compose --progress json pull</c> (compose 2.40.3)
    /// for two services "a" (alpine) and "b" (busybox).
    /// </summary>
    public class DockerPullProgressParserTests
    {
        public static readonly string[] CapturedPull =
        {
            """{"id":"b","text":"Pulling"}""",
            """{"id":"a","text":"Pulling"}""",
            """{"id":"17a39c0ba978","parent_id":"a","text":"Pulling fs layer"}""",
            """{"id":"034d6572bf28","parent_id":"b","text":"Pulling fs layer"}""",
            """{"id":"17a39c0ba978","parent_id":"a","text":"Downloading","status":"[===============>                                   ]  1.049MB/3.42MB","current":1048576,"total":3419815,"percent":30}""",
            """{"id":"034d6572bf28","parent_id":"b","text":"Downloading","status":"[=======================>                           ]  1.049MB/2.206MB","current":1048576,"total":2206402,"percent":47}""",
            """{"id":"034d6572bf28","parent_id":"b","text":"Download complete","percent":100}""",
            """{"id":"17a39c0ba978","parent_id":"a","text":"Download complete","percent":100}""",
            """{"id":"034d6572bf28","parent_id":"b","text":"Extracting","status":"1 s","current":1}""",
            """{"id":"17a39c0ba978","parent_id":"a","text":"Extracting","status":"1 s","current":1}""",
            """{"id":"034d6572bf28","parent_id":"b","text":"Pull complete"}""",
            """{"id":"17a39c0ba978","parent_id":"a","text":"Pull complete"}""",
            """{"id":"5108bc06b83a","parent_id":"b","text":"Download complete","percent":100}""",
            """{"id":"b","text":"Pulled"}""",
            """{"id":"fd18d7b2aa35","parent_id":"a","text":"Download complete","percent":100}""",
            """{"id":"ef1614f30685","parent_id":"a","text":"Download complete","percent":100}""",
            """{"id":"a","text":"Pulled"}""",
        };

        private static List<PullProgress> FeedAll(DockerPullProgressParser parser, IEnumerable<string> lines)
        {
            var snapshots = new List<PullProgress>();
            foreach (var line in lines)
            {
                if (parser.Feed(line))
                {
                    snapshots.Add(parser.Current);
                }
            }
            return snapshots;
        }

        [Fact]
        public void Feed_CapturedPull_CountsImagesAndBytes()
        {
            var parser = new DockerPullProgressParser();

            FeedAll(parser, CapturedPull);

            parser.Current.ImagesTotal.Should().Be(2);
            parser.Current.ImagesPulled.Should().Be(2);
            parser.Current.IsComplete.Should().BeTrue();
            parser.Current.BytesTotal.Should().Be(0, "no image is in flight any more");
            parser.Current.BytesPercent.Should().Be(100f);
            parser.ErrorMessage.Should().BeNull();
        }

        [Fact]
        public void Feed_CapturedPull_ImagesPulledStepsThroughEachImage()
        {
            var parser = new DockerPullProgressParser();

            var snapshots = FeedAll(parser, CapturedPull);

            snapshots.Select(s => s.ImagesPulled).Distinct().Should().Equal(0, 1, 2);
            snapshots.Select(s => s.ImagesPulled).Should().BeInAscendingOrder();
        }

        [Fact]
        public void Feed_WhileDownloading_ReportsAggregatedBytes()
        {
            var parser = new DockerPullProgressParser();

            FeedAll(parser, CapturedPull.Take(6));

            parser.Current.ImagesTotal.Should().Be(2);
            parser.Current.ImagesPulled.Should().Be(0);
            parser.Current.BytesDownloaded.Should().Be(2 * 1048576);
            parser.Current.BytesTotal.Should().Be(3419815 + 2206402);
            parser.Current.BytesPercent.Should().BeApproximately(100f * 2 * 1048576 / (3419815 + 2206402), 0.01f);
        }

        [Fact]
        public void Feed_BeforeAnyLayerSizeIsKnown_HasNoBytesPercent()
        {
            var parser = new DockerPullProgressParser();

            FeedAll(parser, CapturedPull.Take(4));

            parser.Current.ImagesTotal.Should().Be(2);
            parser.Current.BytesTotal.Should().Be(0);
            parser.Current.BytesPercent.Should().BeNull();
        }

        [Fact]
        public void Feed_WhenPulledImageLeavesFlight_BytesCoverOnlyRemainingImages()
        {
            var parser = new DockerPullProgressParser();

            // "b" finishes first; its layer must drop out so the byte bar restarts for "a"
            FeedAll(parser, CapturedPull.Take(14));

            parser.Current.ImagesPulled.Should().Be(1);
            parser.Current.BytesTotal.Should().Be(3419815);
            parser.Current.BytesDownloaded.Should().Be(3419815);
            parser.Current.BytesPercent.Should().Be(100f);
        }

        [Fact]
        public void Feed_WhenNewLayerAppearsLate_PercentReflectsGrownTotal()
        {
            var parser = new DockerPullProgressParser();
            var lines = new[]
            {
                """{"id":"a","text":"Pulling"}""",
                """{"id":"l1","parent_id":"a","text":"Downloading","current":900,"total":1000}""",
                """{"id":"l2","parent_id":"a","text":"Downloading","current":0,"total":10000}""",
                """{"id":"l2","parent_id":"a","text":"Downloading","current":5000,"total":10000}""",
            };

            var snapshots = FeedAll(parser, lines);

            var percents = snapshots.Where(s => s.BytesPercent.HasValue).Select(s => s.BytesPercent!.Value).ToList();
            percents.Should().HaveCount(3);
            percents[0].Should().Be(90f);
            percents[1].Should().BeApproximately(100f * 900 / 11000, 0.01f);
            percents[2].Should().BeApproximately(100f * 5900 / 11000, 0.01f);
        }

        [Fact]
        public void Feed_SmallImagesPulledBeforeLargeOneStartsDownloading_DoesNotStickAtHundredPercent()
        {
            // Shape observed live on saturn: two cached images report Pulled, then the third starts downloading.
            var parser = new DockerPullProgressParser();
            var lines = new[]
            {
                """{"id":"a","text":"Pulling"}""",
                """{"id":"b","text":"Pulling"}""",
                """{"id":"c","text":"Pulling"}""",
                """{"id":"la","parent_id":"a","text":"Downloading","current":3500,"total":3500}""",
                """{"id":"a","text":"Pulled"}""",
                """{"id":"b","text":"Pulled"}""",
                """{"id":"lc","parent_id":"c","text":"Downloading","current":5500,"total":18300}""",
            };

            FeedAll(parser, lines);

            parser.Current.ImagesPulled.Should().Be(2);
            parser.Current.BytesTotal.Should().Be(18300);
            parser.Current.BytesPercent.Should().BeApproximately(100f * 5500 / 18300, 0.01f);
        }

        [Fact]
        public void Feed_DownloadCompleteForKnownLayer_MarksLayerFullyDownloaded()
        {
            var parser = new DockerPullProgressParser();

            parser.Feed("""{"id":"l1","parent_id":"a","text":"Downloading","current":10,"total":1000}""");
            parser.Feed("""{"id":"l1","parent_id":"a","text":"Download complete"}""");

            parser.Current.BytesDownloaded.Should().Be(1000);
            parser.Current.BytesTotal.Should().Be(1000);
        }

        [Fact]
        public void Feed_ImageKnownOnlyThroughLayers_IsStillCounted()
        {
            var parser = new DockerPullProgressParser();

            parser.Feed("""{"id":"l1","parent_id":"svc","text":"Pulling fs layer"}""");

            parser.Current.ImagesTotal.Should().Be(1);
        }

        /// <summary>
        /// Captured verbatim on saturn (compose 2.40.3) for a service "x" whose image does not exist on the registry.
        /// </summary>
        public static readonly string[] CapturedFailedPull =
        {
            """{"id":"x","text":"Pulling"}""",
            """{"id":"x","text":"Error","status":"pull access denied for modelingevolution/does-not-exist-zz, repository does not exist or may require 'docker login'"}""",
            """{"error":true,"message":"Error response from daemon: pull access denied for modelingevolution/does-not-exist-zz, repository does not exist or may require 'docker login'"}""",
        };

        [Fact]
        public void Feed_CapturedFailedPull_ExposesDaemonMessageAndKeepsImageCount()
        {
            var parser = new DockerPullProgressParser();

            var snapshots = FeedAll(parser, CapturedFailedPull);

            parser.ErrorMessage.Should().Be("Error response from daemon: pull access denied for modelingevolution/does-not-exist-zz, repository does not exist or may require 'docker login'");
            parser.Current.ImagesTotal.Should().Be(1);
            parser.Current.ImagesPulled.Should().Be(1, "a failed image is finished as far as the bar is concerned");
            snapshots.Should().HaveCount(2, "the 'Pulling' and 'Error' lines change the snapshot; the final error line does not");
        }

        [Fact]
        public void Feed_ImageLevelErrorWithoutFinalErrorLine_StillExposesReason()
        {
            var parser = new DockerPullProgressParser();

            parser.Feed(CapturedFailedPull[0]);
            parser.Feed(CapturedFailedPull[1]);

            parser.ErrorMessage.Should().StartWith("pull access denied for modelingevolution/does-not-exist-zz");
        }

        /// <summary>
        /// Captured verbatim on saturn (compose 2.40.3): a build-only service "built" next to an image service "a".
        /// </summary>
        public static readonly string[] CapturedPullWithBuildOnlyService =
        {
            """{"id":"built","text":"Skipped - No image to be pulled"}""",
            """{"id":"a","text":"Pulling"}""",
            """{"id":"a","text":"Pulled"}""",
        };

        [Fact]
        public void Feed_BuildOnlyServiceIsSkipped_CountsAsFinishedSoTheBarCompletes()
        {
            var parser = new DockerPullProgressParser();

            FeedAll(parser, CapturedPullWithBuildOnlyService);

            parser.Current.ImagesTotal.Should().Be(2);
            parser.Current.ImagesPulled.Should().Be(2);
            parser.Current.IsComplete.Should().BeTrue();
            parser.Current.BytesPercent.Should().Be(100f);
        }

        [Fact]
        public void Feed_FailedImage_CountsAsFinishedSoTheBarDoesNotStall()
        {
            var parser = new DockerPullProgressParser();

            FeedAll(parser, CapturedFailedPull);

            parser.Current.ImagesTotal.Should().Be(1);
            parser.Current.ImagesPulled.Should().Be(1);
            parser.ErrorMessage.Should().NotBeNull();
        }

        [Fact]
        public void Feed_SharedBaseLayerUnderTwoImages_IsCountedPerImage()
        {
            var parser = new DockerPullProgressParser();
            var lines = new[]
            {
                """{"id":"a","text":"Pulling"}""",
                """{"id":"b","text":"Pulling"}""",
                """{"id":"base","parent_id":"a","text":"Downloading","current":100,"total":1000}""",
                """{"id":"base","parent_id":"b","text":"Downloading","current":100,"total":1000}""",
                """{"id":"base","parent_id":"a","text":"Download complete"}""",
            };

            FeedAll(parser, lines);

            parser.Current.BytesTotal.Should().Be(2000);
            parser.Current.BytesDownloaded.Should().Be(1100);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(" a Pulling ")]
        [InlineData(" 034d6572bf28 Download complete ")]
        [InlineData("{not json")]
        [InlineData("[1,2,3]")]
        [InlineData("""{"text":"Pulling"}""")]
        public void Feed_NonProgressLine_IsIgnored(string? line)
        {
            var parser = new DockerPullProgressParser();

            parser.Feed(line).Should().BeFalse();

            parser.Current.Should().Be(PullProgress.Empty);
        }

        [Fact]
        public void Feed_RepeatedIdenticalLine_ReportsNoChange()
        {
            var parser = new DockerPullProgressParser();
            const string line = """{"id":"a","text":"Pulling"}""";

            parser.Feed(line).Should().BeTrue();
            parser.Feed(line).Should().BeFalse();
        }

        [Theory]
        [InlineData(0, "0 B")]
        [InlineData(1023, "1023 B")]
        [InlineData(1048576, "1.0 MB")]
        [InlineData(432013312, "412.0 MB")]
        [InlineData(1288490188, "1.2 GB")]
        public void FormatBytes_ProducesHumanReadableSizes(long bytes, string expected)
        {
            PullProgress.FormatBytes(bytes).Should().Be(expected);
        }
    }
}
