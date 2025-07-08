using Docker.DotNet;
using Ductus.FluentDocker.Builders;
using Ductus.FluentDocker.Services;
using LibGit2Sharp;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ModelingEvolution.AutoUpdater
{
    public record DockerComposeConfiguration : IDisposable
    {
        public string RepositoryLocation { get; init; } = string.Empty;
        public string RepositoryUrl { get; init; } = string.Empty;
        public string DockerComposeDirectory { get; init; } = "./";
        public string? DockerAuth { get; init; }
        public string? DockerRegistryUrl { get; init; }

        public DockerComposeConfiguration(string repositoryLocation, string repositoryUrl,
            string dockerComposeDirectory = "./", string? dockerAuth = null, string? dockerRegistryUrl = null)
        {
            this.RepositoryLocation = repositoryLocation;
            this.RepositoryUrl = repositoryUrl;
            this.DockerComposeDirectory = dockerComposeDirectory;
            this.DockerAuth = dockerAuth;
            this.DockerRegistryUrl = dockerRegistryUrl;

            // Add to DockerAuths if provided
            if (!string.IsNullOrEmpty(dockerAuth))
            {
                var registry = dockerRegistryUrl ?? "https://index.docker.io/v1/";
                DockerAuths.Add(new DockerRegistryPat(registry, dockerAuth));
            }
        }

        public DockerComposeConfiguration()
        {
            // Initialize DockerAuths from properties if set
            if (!string.IsNullOrEmpty(DockerAuth))
            {
                var registry = DockerRegistryUrl ?? "https://index.docker.io/v1/";
                DockerAuths.Add(new DockerRegistryPat(registry, DockerAuth));
            }
        }

        public string ComposeFolderPath => Path.Combine(RepositoryLocation, DockerComposeDirectory);
        public string MergerName { get; init; } = "pi-admin";
        public string MergerEmail { get; init; } = "admin@eventpi.com";
        public string FriendlyName => Path.GetFileName(RepositoryLocation);
        public IList<DockerRegistryPat> DockerAuths { get; } = new List<DockerRegistryPat>();

        public string? CurrentVersion
        {
            get
            {
                string stateFile = Path.Combine(ComposeFolderPath, "deployment.state.json");
                if (File.Exists(stateFile))
                    return JsonSerializer.Deserialize<DeploymentState>(File.ReadAllText(stateFile))?.Version;
                return null;
            }
        }

        public bool IsGitVersioned => Directory.Exists(RepositoryLocation) &&
                                      Directory.Exists(Path.Combine(this.RepositoryLocation, ".git"));

        public bool CloneRepository(ILogger logger)
        {
            if (!IsGitVersioned)
            {
                if (!Directory.Exists(RepositoryLocation))
                    Directory.CreateDirectory(RepositoryLocation);
                try
                {
                    var cloneOptions = new CloneOptions();
                    cloneOptions.FetchOptions.TagFetchMode = TagFetchMode.All;
                    Repository.Clone(RepositoryUrl, RepositoryLocation);
                    return true;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Cloning repository {RepositoryUrl} at {RepositoryLocation} failed",
                        RepositoryUrl, RepositoryLocation);
                    return false;
                }


            }

            return false;
        }

        private ICompositeService? _svc;

        internal ICompositeService? Service
        {
            get
            {
                if (_svc != null) return _svc;
                string? composeDir = ComposeFolderPath;
                if (Directory.Exists(composeDir))
                {
                    var file = Directory.GetFiles(composeDir, "*.yml");

                    _svc ??= new Builder()
                        .UseContainer()
                        .UseCompose()
                        .FromFile(file)
                        .RemoveOrphans()
                        .Build();
                }

                return _svc;
            }
        }

        private ObservableCollection<IContainerInfo>? _containers;

        //public async IEnumerable<IContainerInfo> Containers(UpdateHost host)
        //{
        //    _containers ??= new();
        //    Task.Run(async () =>
        //    {
        //        using var config = new DockerClientConfiguration();
        //        using var client = config.CreateClient();
        //        var container = await client.Containers.ListContainersAsync(new Docker.DotNet.Models.ContainersListParameters() { All = true });
        //        string? dockerComposeFolder = GetHostDockerComposeFolder(ComposeFolderPath, host.Volumes);
        //        foreach (var i in container)
        //        {
        //            var dir = i.Labels["com.docker.compose.project.working_dir"];
        //            if (dir != null && dir == dockerComposeFolder)
        //            {
        //                yield return new ContainerInfo2(this, i.Names.First(), i.ID);
        //            }
        //        }
        //    });
        //    return _containers;
        //}
        //public IEnumerable<IContainerInfo> Containers()
        //{
        //    if (Service != null)
        //    {
        //       foreach(var i in _svc.Containers)
        //       {
        //            yield return new ContainerInfo(this, i.Name, i);
        //       }
        //    }
        //}
        public bool IsUpgradeAvailable(ILogger logger)
        {
            return AvailableUpgrade(logger) != null;
        }

        public GitTagVersion? AvailableUpgrade(ILogger logger)
        {
            var nx = AvailableVersions(logger).OrderByDescending(i => i.Version).FirstOrDefault();
            return nx;
        }

        public IEnumerable<GitTagVersion> AvailableVersions(ILogger logger) =>
            GitTagVersion.TryParse(this.CurrentVersion, out var c)
                ? Versions(logger).Where(x => x.Version > c.Version)
                : Versions(logger);

        private readonly ObservableCollection<GitTagVersion> _versions = new();
        private DateTime _versionChecked = DateTime.MinValue;

        public IEnumerable<GitTagVersion> Versions(ILogger logger)
        {
            if (!IsGitVersioned)
                CloneRepository(logger);

            if (DateTime.Now.Subtract(_versionChecked).TotalSeconds < 10)
                return _versions;

            _versionChecked = DateTime.Now;

            using var repo = new Repository(RepositoryLocation);
            var origin = repo.Network.Remotes["origin"];
            if (origin != null)
            {
                var refSpecs = origin.FetchRefSpecs.Select(spec => spec.Specification);

                // Set up the fetch options
                var fetchOptions = new FetchOptions
                {
                    TagFetchMode = TagFetchMode.All
                };

                Commands.Fetch(repo, "origin", refSpecs, fetchOptions, null);
            }

            _versions.Clear();

            foreach (var i in repo.Tags)
                if (GitTagVersion.TryParse(i.FriendlyName, out var v))
                    _versions.Add(v);

            return _versions;
        }

        public bool Pull(ILogger logger)
        {
            if (!IsGitVersioned)
            {
                this.CloneRepository(logger);
                return true;
            }

            using var repo = new Repository(RepositoryLocation);
            var signature = new Signature(MergerName, MergerEmail, DateTimeOffset.Now);

            var pullOptions = new PullOptions()
            {
                FetchOptions = new FetchOptions() { },
                MergeOptions = new MergeOptions() { }
            };
            var result = Commands.Pull(repo, signature, pullOptions);
            return result.Status != MergeStatus.UpToDate;
        }

        public void Checkout(GitTagVersion version, ILogger logger)
        {
            if (!IsGitVersioned)
            {
                this.CloneRepository(logger);

            }

            using var repo = new Repository(RepositoryLocation);
            Tag tag = repo.Tags[version];

            if (tag == null)
                throw new Exception($"Tag {version} was not found.");


            // Checkout the tag
            CheckoutOptions options = new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.None,
                CheckoutNotifyFlags = CheckoutNotifyFlags.None,
            };

            Commands.Checkout(repo, tag.Target.Sha, options);
        }

        private static bool IsWindowsDrivePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length < 2)
                return false;

            // Check for drive letter pattern: [A-Z]:\
            return char.IsLetter(path[0]) &&
                   path[1] == ':' &&
                   (path.Length == 2 || path[2] == '\\' || path[2] == '/');
        }

        private string? GetHostDockerComposeFolder(string pathInContainer, IDictionary<string, string>? volumeMapping)
        {
            if (volumeMapping == null)
                return pathInContainer;
            foreach (var v in volumeMapping)
            {
                if (pathInContainer.StartsWith(v.Value))
                {
                    var result = pathInContainer.Replace(v.Value, v.Key);
                    if (!IsWindowsDrivePath(result)) return result;

                    // it's windows, most likely for debugging, we need to fix the path.
                    var driveLetter = result[0].ToString().ToLowerInvariant();
                    var windowsPath = $"/mnt/{driveLetter}/{result.Substring(3).Replace('\\', '/')}";
                    return windowsPath;

                }
            }

            return null;
        }

        public async Task Update(UpdateHost host)
        {
            var latest = this.Versions(host.Log).OrderByDescending(x => x.Version).FirstOrDefault();
            if ((CurrentVersion != null && CurrentVersion == latest) || latest.Version == null)
                return;

            Checkout(latest, host.Log);

            // we need to find update container in docker and examine volume mappings.

            string repName = Path.GetFileName(this.RepositoryLocation);
            DateTime n = DateTime.Now;
            string logFile =
                $"docker_compose_up_d_{n.Year}{n.Month}{n.Day}_{n.Hour}{n.Minute}{n.Second}.{n.Millisecond}.log";

            string[] dockerComposeFiles = Directory.GetFiles(ComposeFolderPath, "docker-compose*.yml")
                .OrderBy(x => x.Length).ToArray();
            string arg = string.Join(' ', dockerComposeFiles.Select(x => $"-f {Path.GetFileName(x)}"));

            string cmd = $"docker compose {arg} up -d > /tmp/{logFile} 2>&1";
            //if(DockerAuths.Count > 0)
            //{
            //    var sb = new StringBuilder();
            //    sb.Append("{");
            //    sb.Append("\"auths\": {");
            //    foreach(var i in DockerAuths)
            //    {
            //        sb.Append($"\"{i.Registry}\": {{");
            //        sb.Append($"\"auth\": \"{i.Base64}\"");
            //        sb.Append("},");
            //    }
            //    sb.Remove(sb.Length - 1,1);
            //    sb.Append("}}");
            //    cmd = $"export DOCKER_AUTH_CONFIG='{sb}'; {cmd}";
            //}
            var dockerComposeFolder = GetHostDockerComposeFolder(ComposeFolderPath, host.Volumes);
            await host.InvokeSsh(cmd, dockerComposeFolder, async (x) =>
            {
                if (x.IsSuccess)
                {
                    var logContent = await host.ReadHostFileContent($"/tmp/{logFile}");
                    if (!logContent.Contains("Error response from daemon"))
                    {
                        DeploymentState st = new DeploymentState(latest, n);
                        string stateFile = Path.Combine(ComposeFolderPath, "deployment.state.json");
                        await File.WriteAllTextAsync(stateFile, JsonSerializer.Serialize(st));
                    }
                    else throw new UpdateFailedException(logContent);
                }
                else throw new UpdateFailedException("Update failed: " + x.Error);

            });
        }

        /// <summary>
        /// Performs update using direct to docker communication.
        /// </summary>
        /// <returns></returns>
        public async Task<bool> InlineUpdate()
        {
            string? composeDir = ComposeFolderPath;
            if (Directory.Exists(composeDir))
            {
                var file = Directory.GetFiles(composeDir, "*.yml");

                using var svc = new Builder()
                    .UseContainer()
                    .UseCompose()
                    .FromFile(file)
                    .RemoveOrphans()
                    .Build();

                svc.Start();
                return true;
            }
            else
            {
                return false;
            }



        }

        public void Dispose()
        {

        }
    }

    public class UpdateFailedException : Exception
    {
        public UpdateFailedException(string message) : base(message)
        {
        }

        public UpdateFailedException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
