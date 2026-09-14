using Microsoft.Extensions.Logging.Abstractions;
using ModelingEvolution.AutoUpdater.Services;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Tests.Services
{
    /// <summary>
    /// Recorded docker output under Fixtures/PullProgress (see its README.md) and an <see cref="ISshService"/> that replays it.
    /// </summary>
    internal static class PullFixtures
    {
        public const string Registry = "public.ecr.aws/docker/library";
        public const string Alpine = Registry + "/alpine:3.21.3";
        public const string Busybox = Registry + "/busybox:1.36";
        public const string Nginx = Registry + "/nginx:1.27-alpine";
        public const string AlpineLocal = Registry + "/alpine@sha256:a8560b36e8b8210634f77d9f7f9efd7ffa463e380b75e2e74aff4511df3ef88c";
        public const string BusyboxLocal = Registry + "/busybox@sha256:73aaf090f3d85aa34ee199857f03fa3a95c8ede2ffd4cc2cdb5b94e566b11662";
        public const string NginxLocal = Registry + "/nginx@sha256:65645c7bb6a0661892a8b03b89d0743208a18dd2f3f17a54ef4b76fb8e2f2a10";

        public static readonly string[] ComposeFiles = { "compose.yml" };
        public const string WorkingDirectory = "/fx";

        // linux/amd64 layer sizes, read off the recorded manifests.
        public const string AlpineBaseLayer = "f18232174bc9";
        public const long AlpineBaseSize = 3642247;
        public const string BusyboxLayer = "034d6572bf28";
        public const long BusyboxSize = 2206402;
        /// <summary>nginx:1.27-alpine layers above the shared alpine base.</summary>
        public const long NginxOwnSize = 17318384;
        public const int NginxOwnLayers = 7;
        public const long ColdTotal = AlpineBaseSize + BusyboxSize + NginxOwnSize;
        public const int ColdLayers = 1 + 1 + NginxOwnLayers;

        public static string Path(string relative) =>
            System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "PullProgress", relative);

        public static string Text(string relative) => File.ReadAllText(Path(relative));

        public static string[] Lines(string relative) =>
            File.ReadAllLines(Path(relative)).Where(l => l.Length > 0).ToArray();

        public static IEnumerable<object[]> ColdTranscripts() => new[]
        {
            new object[] { "containerd-compose2.40/pull-cold.jsonl" },
            new object[] { "overlay2-compose2.40/pull-cold.jsonl" },
            new object[] { "overlay2-compose5.5/pull-cold.jsonl" },
        };

        public static IEnumerable<object[]> AllSuccessfulTranscripts() => ColdTranscripts().Concat(new[]
        {
            new object[] { "containerd-compose2.40/pull-warm.jsonl" },
            new object[] { "containerd-compose2.40/pull-partial.jsonl" },
            new object[] { "overlay2-compose2.40/pull-partial.jsonl" },
            new object[] { "overlay2-compose5.5/pull-warm.jsonl" },
        });

        /// <summary>
        /// An SSH service answering the resolver's commands with recorded output.
        /// </summary>
        public sealed class Device
        {
            private readonly Dictionary<string, SshCommandResult> _answers = new(StringComparer.Ordinal);

            public ISshService Ssh { get; } = Substitute.For<ISshService>();

            public Device()
            {
                Ssh.ExecuteCommandAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<string?>())
                    .Returns(call =>
                    {
                        var command = call.ArgAt<string>(0);
                        return Task.FromResult(_answers.TryGetValue(command, out var result)
                            ? result
                            : SshCommandResult.Failed(command, 127, string.Empty, $"unexpected command: {command}"));
                    });

                Answer(DockerPullSizeResolver.ConfigCommand(ComposeFiles), Text("compose-config.json"));
                Answer(DockerPullSizeResolver.VersionCommand, Text("docker-version.txt"));
                Answer(DockerPullSizeResolver.ManifestCommand(Alpine), Text("manifests/manifest-alpine_3.21.3.json"));
                Answer(DockerPullSizeResolver.ManifestCommand(Busybox), Text("manifests/manifest-busybox_1.36.json"));
                Answer(DockerPullSizeResolver.ManifestCommand(Nginx), Text("manifests/manifest-nginx_1.27-alpine.json"));
                Answer(DockerPullSizeResolver.ManifestCommand(AlpineLocal), Text("manifests/manifest-alpine-by-local-digest.json"));
                Answer(DockerPullSizeResolver.ManifestCommand(BusyboxLocal), Text("manifests/manifest-busybox-by-local-digest.json"));
                Answer(DockerPullSizeResolver.ManifestCommand(NginxLocal), Text("manifests/manifest-nginx-by-local-digest.json"));
                LocalImages(string.Empty);
            }

            public Device Answer(string command, string output)
            {
                _answers[command] = new SshCommandResult(command, output);
                return this;
            }

            public Device Fail(string command, string error, int exitCode = 1)
            {
                _answers[command] = SshCommandResult.Failed(command, exitCode, string.Empty, error);
                return this;
            }

            public Device LocalImages(string imageLsOutput) => Answer(DockerPullSizeResolver.ImageListCommand, imageLsOutput);

            public Task<PullSizeTable> ResolveAsync() =>
                new DockerPullSizeResolver(Ssh, NullLogger<DockerPullSizeResolver>.Instance).ResolveAsync(ComposeFiles, WorkingDirectory);
        }

        public static Task<PullSizeTable> ColdTableAsync() => new Device().ResolveAsync();

        public static Task<PullSizeTable> PartialTableAsync() =>
            new Device().LocalImages(Text("overlay2-compose2.40/image-ls-partial.jsonl")).ResolveAsync();

        public static Task<PullSizeTable> WarmTableAsync() =>
            new Device().LocalImages(Text("containerd-compose2.40/image-ls-warm.jsonl")).ResolveAsync();

        public static List<PullProgress> FeedAll(DockerPullProgressParser parser, IEnumerable<string> lines, List<int>? lineIndexes = null)
        {
            var snapshots = new List<PullProgress>();
            var index = 0;
            foreach (var line in lines)
            {
                if (parser.Feed(line))
                {
                    snapshots.Add(parser.Current);
                    lineIndexes?.Add(index);
                }
                index++;
            }
            return snapshots;
        }
    }
}
