using Docker.DotNet;
using Ductus.FluentDocker.Builders;
using Ductus.FluentDocker.Services;
using LibGit2Sharp;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace ModelingEvolution.AutoUpdater
{
    public record DockerComposeConfiguration : IDisposable
    {
        public string RepositoryLocation { get; init; } 
        public string RepositoryUrl { get; init; }
        public string DockerComposeDirectory { get; init; } = "./";
        private string? _dockerIoPat;
        public string? DockerIoAuth
        {
            get => DockerAuths.FirstOrDefault(x=>x.Registry == "https://index.docker.io/v1/")?.Base64; 
            init
            {
                if (value != null)
                {
                    DockerAuths.Add(new DockerRegistryPat("https://index.docker.io/v1/", value));
                }
            }
        }
        public DockerComposeConfiguration(string repositoryLocation, string repositoryUrl, string dockerComposeDirectory = "./", string? dockerIoAuth = null)
        {
            this.RepositoryLocation = repositoryLocation;
            this.RepositoryUrl = repositoryUrl;
            this.DockerComposeDirectory = dockerComposeDirectory;
            this.DockerIoAuth = dockerIoAuth;   
        }
        public DockerComposeConfiguration() {}
        
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
        public bool IsGitVersioned => Directory.Exists(RepositoryLocation) && Directory.Exists(Path.Combine(this.RepositoryLocation, ".git"));
        public bool CloneRepository()
        {
            if (!IsGitVersioned)
            {
                if (!Directory.Exists(RepositoryLocation))
                    Directory.CreateDirectory(RepositoryLocation);

                Repository.Clone(RepositoryUrl, RepositoryLocation);
                return true;
            }
            return false;
        }

        private ICompositeService _svc;
        internal ICompositeService Service
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
        private ObservableCollection<IContainerInfo> _containers;
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
        public bool IsUpgradeAvailable()
        {
            return AvailableUpgrade() != null;
        }
        public GitTagVersion? AvailableUpgrade()
        {
            var nx = AvailableVersions().OrderByDescending(i => i.Version).FirstOrDefault();
            return nx;
        }
        public IEnumerable<GitTagVersion> AvailableVersions()
        {
            if(GitTagVersion.TryParse(this.CurrentVersion, out var c))
            {
                return Versions().Where(x=>x.Version > c.Version);
            }
            return Versions();
        }
        private readonly ObservableCollection<GitTagVersion> _versions = new();
        private DateTime _versionChecked = DateTime.MinValue;
        public IEnumerable<GitTagVersion> Versions()
        {
            if (!IsGitVersioned)
                CloneRepository();

            if (DateTime.Now.Subtract(_versionChecked).TotalSeconds < 10)
                return _versions;

            _versionChecked = DateTime.Now;

            using var repo = new Repository(RepositoryLocation);
            var refSpecs = repo.Network.Remotes["origin"].FetchRefSpecs.Select(spec => spec.Specification);

            // Set up the fetch options
            var fetchOptions = new FetchOptions{
                TagFetchMode = TagFetchMode.All
            };

            Commands.Fetch(repo, "origin", refSpecs, fetchOptions,null);
            _versions.Clear();

            foreach (var i in repo.Tags)
                if (GitTagVersion.TryParse(i.FriendlyName, out var v))
                    _versions.Add(v);

            return _versions;
        }
        public bool Pull()
        {
            if (!IsGitVersioned)
            {
                this.CloneRepository();
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

        public void Checkout(GitTagVersion version)
        {
            if (!IsGitVersioned)
            {
                this.CloneRepository();
                
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


        private string? GetHostDockerComposeFolder(string pathInContainer, IDictionary<string, string>? volumeMapping)
        {
            if (volumeMapping == null)
                return pathInContainer;
            foreach(var v in volumeMapping)
            {
                if(pathInContainer.StartsWith(v.Value))
                    return pathInContainer.Replace(v.Value, v.Key);
            }
            return null;
        }
        
        public async Task Update(UpdateHost host)
        {
            var latest = this.Versions().OrderByDescending(x=>x.Version).FirstOrDefault();
            if ((CurrentVersion != null && CurrentVersion == latest) || latest.Version == null)
                return;

            Checkout(latest);
            
            // we need to find update container in docker and examine volume mappings.
            string dockerComposeFolder = GetHostDockerComposeFolder(ComposeFolderPath, host.Volumes) ?? throw new Exception("Cannot find path");

            string repName = Path.GetFileName(this.RepositoryLocation);
            DateTime n = DateTime.Now;
            string logFile = $"~/{repName}/docker_compose_up_d_{n.Year}{n.Month}{n.Day}_{n.Hour}{n.Minute}{n.Second}.{n.Millisecond}.log";
            
            string[] dockerComposeFiles = Directory.GetFiles(dockerComposeFolder, "docker-compose*.yml").OrderBy(x=>x.Length).ToArray();
            string arg = string.Join(' ', dockerComposeFiles.Select(x => $"-f {Path.GetFileName(x)}"));

            string cmd = $"nohup docker compose {arg} up -d > {logFile} 2>&1 &";
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

            await host.InvokeSsh(cmd, dockerComposeFolder, () =>
            {
                DeploymentState st = new DeploymentState(latest, n);
                string stateFile = Path.Combine(ComposeFolderPath, "deployment.state.json");
                File.WriteAllText(stateFile, JsonSerializer.Serialize(st));

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
}
