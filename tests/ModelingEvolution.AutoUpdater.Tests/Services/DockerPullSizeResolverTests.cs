using FluentAssertions;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static ModelingEvolution.AutoUpdater.Tests.Services.PullFixtures;

namespace ModelingEvolution.AutoUpdater.Tests.Services
{
    /// <summary>
    /// Resolver over recorded docker output (Fixtures/PullProgress).
    /// </summary>
    public class DockerPullSizeResolverTests
    {
        [Fact]
        public async Task ResolveAsync_NothingLocal_SumsDevicePlatformLayersCountingSharedBaseOnce()
        {
            var table = await ColdTableAsync();

            table.BytesTotal.Should().Be(ColdTotal);
            table.LayersToFetch.Should().HaveCount(ColdLayers);
            table.LayersToFetch[AlpineBaseLayer].Should().Be(AlpineBaseSize);
            table.LayersToFetch[BusyboxLayer].Should().Be(BusyboxSize);
            table.LayersPresent.Should().BeEmpty();
            table.Images.Should().OnlyContain(i => i.IsSized);
            table.Images.Select(i => i.Reference).Should().BeEquivalentTo(new[] { Alpine, Busybox, Nginx });
        }

        [Fact]
        public async Task ResolveAsync_SameImageForTwoServices_IsOneImageNamingBothServices()
        {
            var table = await ColdTableAsync();

            table.Images.Single(i => i.Reference == Alpine).Services.Should().BeEquivalentTo(new[] { "a", "a2" });
            table.FindImage("a2")!.Reference.Should().Be(Alpine);
        }

        [Fact]
        public async Task ResolveAsync_BuildOnlyService_IsNotAnImage()
        {
            var table = await ColdTableAsync();

            table.FindImage("built").Should().BeNull();
            table.Images.Should().HaveCount(3);
        }

        [Fact]
        public async Task ResolveAsync_Compose5ImageId_FindsImageByReference()
        {
            var table = await ColdTableAsync();

            table.FindImage("Image " + Nginx)!.LayersToFetch.Should().HaveCount(1 + NginxOwnLayers);
        }

        [Fact]
        public async Task ResolveAsync_BaseLayerLocalUnderAnotherRepository_IsSubtracted()
        {
            // alpine and busybox local, nginx not: nginx's bottom layer is the local alpine layer.
            var table = await PartialTableAsync();

            table.BytesTotal.Should().Be(NginxOwnSize);
            table.LayersToFetch.Should().HaveCount(NginxOwnLayers);
            table.LayersToFetch.Should().NotContainKey(AlpineBaseLayer);
            table.LayersPresent.Should().Contain(new[] { AlpineBaseLayer, BusyboxLayer });
            table.FindImage("n")!.LayersToFetch.Should().HaveCount(NginxOwnLayers);
            table.FindImage("a")!.LayersToFetch.Should().BeEmpty();
        }

        [Fact]
        public async Task ResolveAsync_EverythingLocal_NothingToFetchButAllSized()
        {
            var table = await WarmTableAsync();

            table.BytesTotal.Should().Be(0);
            table.LayersToFetch.Should().BeEmpty();
            table.Images.Should().OnlyContain(i => i.IsSized);
        }

        [Fact]
        public async Task ResolveAsync_LocalImageManifestUnreadable_CountsItsLayersAsToFetch()
        {
            // The honest direction: what cannot be proven local is counted, so the total can only be too large.
            var device = new Device().LocalImages(Text("overlay2-compose2.40/image-ls-partial.jsonl"));
            device.Fail(DockerPullSizeResolver.ManifestCommand(AlpineLocal), Text("manifests/manifest-error-notfound.txt"));

            var table = await device.ResolveAsync();

            table.LayersToFetch.Should().ContainKey(AlpineBaseLayer);
            table.BytesTotal.Should().Be(AlpineBaseSize + NginxOwnSize);
        }

        [Fact]
        public async Task ResolveAsync_OneManifestUnreadable_DegradesOnlyThatImage()
        {
            var device = new Device();
            device.Fail(DockerPullSizeResolver.ManifestCommand(Nginx), Text("manifests/manifest-error-notfound.txt"));

            var table = await device.ResolveAsync();

            table.FindImage("n")!.IsSized.Should().BeFalse();
            table.FindImage("a")!.IsSized.Should().BeTrue();
            table.FindImage("b")!.IsSized.Should().BeTrue();
            table.UnsizedImages.Should().Be(1);
            table.BytesTotal.Should().Be(AlpineBaseSize + BusyboxSize);
        }

        [Fact]
        public async Task ResolveAsync_DevicePlatformNotInIndex_LeavesImagesNotSized()
        {
            var device = new Device().Answer(DockerPullSizeResolver.VersionCommand, "linux/mips64le\n");

            var table = await device.ResolveAsync();

            table.Images.Should().HaveCount(3).And.OnlyContain(i => !i.IsSized);
            table.BytesTotal.Should().Be(0);
        }

        [Fact]
        public async Task ResolveAsync_DockerVersionFails_LeavesImagesNotSized()
        {
            var device = new Device().Fail(DockerPullSizeResolver.VersionCommand, "Cannot connect to the Docker daemon");

            var table = await device.ResolveAsync();

            table.Images.Should().HaveCount(3).And.OnlyContain(i => !i.IsSized);
        }

        [Fact]
        public async Task ResolveAsync_ComposeConfigFails_ReturnsEmptyTable()
        {
            var device = new Device().Fail(DockerPullSizeResolver.ConfigCommand(ComposeFiles), "unknown flag: --format");

            var table = await device.ResolveAsync();

            table.Images.Should().BeEmpty();
            table.BytesTotal.Should().Be(0);
        }

        [Fact]
        public async Task ResolveAsync_SshThrows_ReturnsWhatItKnowsWithoutThrowing()
        {
            var device = new Device();
            device.Ssh.ExecuteCommandAsync(DockerPullSizeResolver.ManifestCommand(Busybox), Arg.Any<TimeSpan>(), Arg.Any<string?>())
                .Throws(new TimeoutException("ssh command timed out"));

            var table = await device.ResolveAsync();

            table.FindImage("b")!.IsSized.Should().BeFalse();
            table.FindImage("n")!.IsSized.Should().BeTrue();
        }

        [Fact]
        public async Task ResolveAsync_RunsTheSudoDockerCommandsInTheComposeFolder()
        {
            var device = new Device();

            await device.ResolveAsync();

            await device.Ssh.Received(1).ExecuteCommandAsync("sudo docker compose -f \"compose.yml\" config --format json", Arg.Any<TimeSpan>(), WorkingDirectory);
            await device.Ssh.Received(1).ExecuteCommandAsync("sudo docker version --format '{{.Server.Os}}/{{.Server.Arch}}'", Arg.Any<TimeSpan>(), WorkingDirectory);
            await device.Ssh.Received(1).ExecuteCommandAsync($"sudo docker manifest inspect {Nginx}", Arg.Any<TimeSpan>(), WorkingDirectory);
        }

        [Theory]
        [InlineData("linux/amd64", "sha256:1c4eef651f65e2f7daee7ee785882ac164b02b78fb74503052a26dc061c90474")]
        [InlineData("linux/arm64", "sha256:757d680068d77be46fd1ea20fb21db16f150468c5e7079a08a2e4705aec096ac")]
        [InlineData("linux/arm64/v8", "sha256:757d680068d77be46fd1ea20fb21db16f150468c5e7079a08a2e4705aec096ac")]
        [InlineData("linux/arm/v7", "sha256:9c2d245b3c01c4d7da0d3319d278e7aa4dd899076721abd205b595b2d3b2383b")]
        public void SelectPlatformDigest_RecordedIndex_PicksTheDevicePlatformEntry(string platform, string digest)
        {
            var index = DockerPullSizeResolver.ParseManifest(Text("manifests/index-alpine_3.21.3.json"));

            index.Layers.Should().BeNull();
            DockerPullSizeResolver.SelectPlatformDigest(index.Platforms, DockerPullSizeResolver.Platform.Parse(platform)!).Should().Be(digest);
        }

        [Theory]
        [InlineData("linux/arm")]      // v6 and v7 both match: guessing could size the wrong one
        [InlineData("linux/mips64le")]
        public void SelectPlatformDigest_AmbiguousOrAbsentPlatform_IsNull(string platform)
        {
            var index = DockerPullSizeResolver.ParseManifest(Text("manifests/index-alpine_3.21.3.json"));

            DockerPullSizeResolver.SelectPlatformDigest(index.Platforms, DockerPullSizeResolver.Platform.Parse(platform)!).Should().BeNull();
        }

        [Fact]
        public void ParseManifest_RecordedPlatformManifest_ReturnsLayersBottomFirst()
        {
            var manifest = DockerPullSizeResolver.ParseManifest(Text("manifests/manifest-nginx-amd64.json"));

            manifest.Layers.Should().HaveCount(1 + NginxOwnLayers);
            PullSizeTable.ShortId(manifest.Layers![0].Digest).Should().Be(AlpineBaseLayer);
            manifest.Layers.Sum(l => l.Size).Should().Be(AlpineBaseSize + NginxOwnSize);
        }

        [Fact]
        public async Task ResolveAsync_ImageWithoutIndex_UsesTheSingleManifestDirectly()
        {
            // A single-platform image: `docker manifest inspect <ref>` returns the image manifest itself.
            var device = new Device().Answer(DockerPullSizeResolver.ManifestCommand(Busybox), Text("manifests/manifest-busybox-amd64.json"));

            var table = await device.ResolveAsync();

            table.FindImage("b")!.LayersToFetch.Should().Equal(BusyboxLayer);
            await device.Ssh.DidNotReceive().ExecuteCommandAsync(DockerPullSizeResolver.ManifestCommand(BusyboxAmd64), Arg.Any<TimeSpan>(), Arg.Any<string?>());
        }

        [Fact]
        public async Task ResolveAsync_PlatformManifestUnreadable_DegradesThatImage()
        {
            var device = new Device().Fail(DockerPullSizeResolver.ManifestCommand(NginxAmd64), "toomanyrequests: Rate exceeded");

            var table = await device.ResolveAsync();

            table.FindImage("n")!.IsSized.Should().BeFalse();
            table.BytesTotal.Should().Be(AlpineBaseSize + BusyboxSize);
        }

        [Fact]
        public async Task ResolveAsync_LocalImagesSharingTheTargetPlatformManifest_ReadItOnce()
        {
            // Two registry requests per image, never --verbose (one request per platform in the index): rate limits bite.
            var device = new Device().LocalImages(Text("containerd-compose2.40/image-ls-warm.jsonl"));

            await device.ResolveAsync();

            await device.Ssh.Received(1).ExecuteCommandAsync(DockerPullSizeResolver.ManifestCommand(NginxAmd64), Arg.Any<TimeSpan>(), Arg.Any<string?>());
            await device.Ssh.DidNotReceive().ExecuteCommandAsync(Arg.Is<string>(c => c.Contains("--verbose")), Arg.Any<TimeSpan>(), Arg.Any<string?>());
            device.Ssh.ReceivedCalls().Count(c => c.GetArguments()[0] is string command && command.StartsWith("sudo docker manifest inspect"))
                .Should().Be(3 /* indexes by tag */ + 3 /* indexes by local digest */ + 3 /* platform manifests */);
        }

        [Fact]
        public void ParseComposeServices_RecordedConfig_SkipsBuildOnlyAndNormalisesReferences()
        {
            var services = DockerPullSizeResolver.ParseComposeServices(Text("compose-config.json"));

            services.Select(s => (s.Service, s.Reference)).Should().BeEquivalentTo(new[]
            {
                ("a", Alpine), ("a2", Alpine), ("b", Busybox), ("n", Nginx),
            });
        }

        [Fact]
        public void ParseLocalImages_RecordedListing_YieldsRepoDigestReferences()
        {
            var images = DockerPullSizeResolver.ParseLocalImages(Text("containerd-compose2.40/image-ls-warm.jsonl"));

            images.Select(i => i.RepoDigestReference).Should().BeEquivalentTo(new[] { AlpineLocal, BusyboxLocal, NginxLocal });
            images.Select(i => i.Repository).Should().Contain(Registry + "/nginx");
        }

        [Theory]
        [InlineData("alpine", "docker.io/library/alpine:latest")]
        [InlineData("alpine:3.20", "docker.io/library/alpine:3.20")]
        [InlineData("me/app:1", "docker.io/me/app:1")]
        [InlineData("docker.io/library/alpine:3.20", "docker.io/library/alpine:3.20")]
        [InlineData("index.docker.io/library/alpine:3.20", "docker.io/library/alpine:3.20")]
        [InlineData("localhost:5000/app", "localhost:5000/app:latest")]
        [InlineData("docker.modelingevolution.com/internal/rocketwelder:1.2.3", "docker.modelingevolution.com/internal/rocketwelder:1.2.3")]
        [InlineData("registry:5000/app@sha256:abc", "registry:5000/app@sha256:abc")]
        public void Normalize_Reference_MatchesComposeAndDockerSpelling(string reference, string expected)
        {
            ImageReference.Normalize(reference).Should().Be(expected);
        }
    }
}
