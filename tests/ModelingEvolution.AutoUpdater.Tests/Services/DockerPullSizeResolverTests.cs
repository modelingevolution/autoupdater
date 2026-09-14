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
            await device.Ssh.Received(1).ExecuteCommandAsync($"sudo docker manifest inspect --verbose {Nginx}", Arg.Any<TimeSpan>(), WorkingDirectory);
        }

        [Theory]
        [InlineData("linux/amd64", "sha256:f18232174bc91741fdf3da96d85011092101a032a93a388b79e99e69c2d5c870", 3642247)]
        [InlineData("linux/arm64", "sha256:6e771e15690e2fabf2332d3a3b744495411d6e0b00b2aea64419b58b0066cf81", 3993029)]
        [InlineData("linux/arm64/v8", "sha256:6e771e15690e2fabf2332d3a3b744495411d6e0b00b2aea64419b58b0066cf81", 3993029)]
        [InlineData("linux/arm/v7", "sha256:85f3b18f9f5a8655db86c6dfb02bb01011ffef63d10a173843c5c65c3e9137b7", 3098123)]
        public void SelectPlatformLayers_MultiPlatformIndex_PicksTheDevicePlatformManifest(string platform, string digest, long size)
        {
            var layers = DockerPullSizeResolver.SelectPlatformLayers(
                Text("manifests/manifest-alpine_3.21.3.json"), DockerPullSizeResolver.Platform.Parse(platform)!);

            layers.Should().ContainSingle().Which.Should().Be(new DockerPullSizeResolver.ManifestLayer(digest, size));
        }

        [Fact]
        public void SelectPlatformLayers_ArmWithoutVariant_IsAmbiguousSoNotSized()
        {
            // The index has linux/arm v6 and v7; guessing could size the wrong one.
            var layers = DockerPullSizeResolver.SelectPlatformLayers(
                Text("manifests/manifest-alpine_3.21.3.json"), DockerPullSizeResolver.Platform.Parse("linux/arm")!);

            layers.Should().BeNull();
        }

        [Fact]
        public void SelectPlatformLayers_SingleManifestObject_ReturnsItsLayers()
        {
            var layers = DockerPullSizeResolver.SelectPlatformLayers(
                Text("manifests/manifest-alpine-amd64-single.json"), DockerPullSizeResolver.Platform.Parse("linux/amd64")!);

            layers.Should().ContainSingle().Which.Size.Should().Be(AlpineBaseSize);
        }

        [Fact]
        public void SelectPlatformLayers_IndexLayers_AreInManifestOrderBottomFirst()
        {
            var layers = DockerPullSizeResolver.SelectPlatformLayers(
                Text("manifests/manifest-nginx_1.27-alpine.json"), DockerPullSizeResolver.Platform.Parse("linux/amd64")!)!;

            layers.Should().HaveCount(1 + NginxOwnLayers);
            PullSizeTable.ShortId(layers[0].Digest).Should().Be(AlpineBaseLayer);
            layers.Sum(l => l.Size).Should().Be(AlpineBaseSize + NginxOwnSize);
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
