using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using System.Text;

namespace ModelingEvolution.AutoUpdater.IntegrationTests.Infrastructure;

/// <summary>
/// Builds Docker images for testing purposes using Docker.DotNet
/// </summary>
public class DockerImageBuilder : IDisposable
{
    private readonly ILogger<DockerImageBuilder> _logger;
    private readonly DockerClient _dockerClient;
    private readonly List<string> _builtImages = new();

    public DockerImageBuilder(ILogger<DockerImageBuilder> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Create Docker client - try different connection methods
        _dockerClient = CreateDockerClient();
    }

    private static DockerClient CreateDockerClient()
    {
        // Try different Docker connection methods
        var possibleUris = new[]
        {
            "unix:///var/run/docker.sock",  // Linux/macOS
            "npipe://./pipe/docker_engine", // Windows
            "tcp://localhost:2375",         // TCP (if enabled)
        };

        foreach (var uri in possibleUris)
        {
            try
            {
                var config = new DockerClientConfiguration(new Uri(uri));
                var client = config.CreateClient();
                
                // Test the connection
                var versionTask = client.System.GetVersionAsync();
                versionTask.Wait(TimeSpan.FromSeconds(5));
                
                return client;
            }
            catch
            {
                // Try next connection method
                continue;
            }
        }

        throw new InvalidOperationException("Cannot connect to Docker daemon. Ensure Docker is running and accessible.");
    }

    /// <summary>
    /// Builds a Docker image from a build context
    /// </summary>
    public async Task<string> BuildImageAsync(
        string contextPath,
        string dockerfilePath,
        string imageName,
        string tag,
        Dictionary<string, string>? buildArgs = null,
        CancellationToken cancellationToken = default)
    {
        var fullImageName = $"{imageName}:{tag}";
        
        _logger.LogInformation("Building Docker image {ImageName} from {ContextPath}", fullImageName, contextPath);

        if (!Directory.Exists(contextPath))
            throw new DirectoryNotFoundException($"Build context directory not found: {contextPath}");

        var dockerfileRelativePath = Path.GetRelativePath(contextPath, dockerfilePath);
        if (!File.Exists(dockerfilePath))
            throw new FileNotFoundException($"Dockerfile not found: {dockerfilePath}");

        // Create tar archive of build context
        var buildContext = await CreateBuildContextTarAsync(contextPath, cancellationToken);

        var buildParameters = new ImageBuildParameters
        {
            Dockerfile = dockerfileRelativePath,
            Tags = new[] { fullImageName },
            BuildArgs = buildArgs ?? new Dictionary<string, string>(),
            Pull = "false",
            NoCache = false
        };

        try
        {
            var buildOutput = new List<string>();
            
            await _dockerClient.Images.BuildImageFromDockerfileAsync(
                buildParameters,
                buildContext,
                null, // auth headers
                new Dictionary<string, string>(), // headers
                new Progress<JSONMessage>(message =>
                {
                    if (!string.IsNullOrEmpty(message.Stream))
                    {
                        buildOutput.Add(message.Stream.Trim());
                        _logger.LogDebug("Docker build: {Message}", message.Stream.Trim());
                    }
                    if (!string.IsNullOrEmpty(message.ErrorMessage))
                    {
                        _logger.LogError("Docker build error: {Error}", message.ErrorMessage);
                    }
                }),
                cancellationToken);

            _builtImages.Add(fullImageName);
            _logger.LogInformation("Successfully built Docker image: {ImageName}", fullImageName);
            
            return fullImageName;
        }
        finally
        {
            buildContext.Dispose();
        }
    }

    /// <summary>
    /// Builds the VersionApp image with specified version
    /// </summary>
    public async Task<string> BuildVersionAppAsync(
        string sourceDirectory,
        string version,
        CancellationToken cancellationToken = default)
    {
        var dockerfile = Path.Combine(sourceDirectory, "Dockerfile");
        var imageName = "versionapp";
        var buildArgs = new Dictionary<string, string> { ["VERSION"] = version };

        return await BuildImageAsync(sourceDirectory, dockerfile, imageName, version, buildArgs, cancellationToken);
    }

    /// <summary>
    /// Checks if an image exists locally
    /// </summary>
    public async Task<bool> ImageExistsAsync(string imageName, CancellationToken cancellationToken = default)
    {
        try
        {
            var images = await _dockerClient.Images.ListImagesAsync(new ImagesListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["reference"] = new Dictionary<string, bool> { [imageName] = true }
                }
            }, cancellationToken);

            return images.Any(img => img.RepoTags?.Contains(imageName) == true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking if image exists: {ImageName}", imageName);
            return false;
        }
    }

    /// <summary>
    /// Gets image information
    /// </summary>
    public async Task<ImageInfo?> GetImageInfoAsync(string imageName, CancellationToken cancellationToken = default)
    {
        try
        {
            var image = await _dockerClient.Images.InspectImageAsync(imageName, cancellationToken);
            
            return new ImageInfo
            {
                Name = imageName,
                Id = image.ID,
                Created = image.Created,
                Size = image.Size
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting image info for {ImageName}", imageName);
            return null;
        }
    }

    /// <summary>
    /// Removes an image
    /// </summary>
    public async Task RemoveImageAsync(string imageName, bool force = false, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Removing Docker image: {ImageName}", imageName);

        try
        {
            await _dockerClient.Images.DeleteImageAsync(imageName, new ImageDeleteParameters
            {
                Force = force
            }, cancellationToken);

            _builtImages.Remove(imageName);
            _logger.LogInformation("Removed Docker image: {ImageName}", imageName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove Docker image {ImageName}", imageName);
        }
    }

    /// <summary>
    /// Cleans up all images built during tests
    /// </summary>
    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (_builtImages.Count == 0)
        {
            _logger.LogInformation("No Docker images to clean up");
            return;
        }

        _logger.LogInformation("Cleaning up {Count} Docker images: {Images}", 
            _builtImages.Count, string.Join(", ", _builtImages));

        var tasks = _builtImages.ToList().Select(image => RemoveImageAsync(image, true, cancellationToken));
        
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Docker image cleanup");
        }
    }

    /// <summary>
    /// Tags an existing image with a new tag
    /// </summary>
    public async Task TagImageAsync(string sourceImage, string targetImage, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Tagging Docker image {SourceImage} as {TargetImage}", sourceImage, targetImage);

        var parts = targetImage.Split(':');
        var repo = parts[0];
        var tag = parts.Length > 1 ? parts[1] : "latest";

        await _dockerClient.Images.TagImageAsync(sourceImage, new ImageTagParameters
        {
            RepositoryName = repo,
            Tag = tag
        }, cancellationToken);

        _builtImages.Add(targetImage);
        _logger.LogInformation("Successfully tagged Docker image: {TargetImage}", targetImage);
    }

    /// <summary>
    /// Runs a container from an image for testing
    /// </summary>
    public async Task<string> RunContainerAsync(
        string imageName,
        string? containerName = null,
        Dictionary<string, string>? portMappings = null,
        Dictionary<string, string>? environmentVariables = null,
        bool detached = true,
        CancellationToken cancellationToken = default)
    {
        containerName ??= $"test-{Guid.NewGuid():N}";
        
        _logger.LogInformation("Running Docker container {ContainerName} from image {ImageName}", containerName, imageName);

        var hostConfig = new HostConfig();
        
        // Add port mappings
        if (portMappings != null && portMappings.Count > 0)
        {
            hostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>();
            foreach (var (hostPort, containerPort) in portMappings)
            {
                hostConfig.PortBindings[$"{containerPort}/tcp"] = new List<PortBinding>
                {
                    new() { HostPort = hostPort }
                };
            }
        }

        // Add environment variables
        var env = new List<string>();
        if (environmentVariables != null)
        {
            env.AddRange(environmentVariables.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        }

        var createParams = new CreateContainerParameters
        {
            Image = imageName,
            Name = containerName,
            Env = env,
            HostConfig = hostConfig,
            AttachStdout = true,
            AttachStderr = true
        };

        var response = await _dockerClient.Containers.CreateContainerAsync(createParams, cancellationToken);
        
        if (!string.IsNullOrEmpty(response.ID))
        {
            await _dockerClient.Containers.StartContainerAsync(response.ID, new ContainerStartParameters(), cancellationToken);
            _logger.LogInformation("Started Docker container {ContainerName} with ID: {ContainerId}", containerName, response.ID);
            
            return response.ID;
        }

        throw new InvalidOperationException($"Failed to create container {containerName}");
    }

    /// <summary>
    /// Creates a tar archive of the build context
    /// </summary>
    private async Task<Stream> CreateBuildContextTarAsync(string contextPath, CancellationToken cancellationToken = default)
    {
        var memoryStream = new MemoryStream();
        
        await Task.Run(() =>
        {
            using var archive = new System.IO.Compression.ZipArchive(memoryStream, System.IO.Compression.ZipArchiveMode.Create, true);
            
            var files = Directory.GetFiles(contextPath, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                var relativePath = Path.GetRelativePath(contextPath, file);
                var entry = archive.CreateEntry(relativePath.Replace('\\', '/'));
                
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(file);
                fileStream.CopyTo(entryStream);
            }
        }, cancellationToken);
        
        memoryStream.Position = 0;
        return memoryStream;
    }

    public void Dispose()
    {
        _dockerClient?.Dispose();
    }
}

/// <summary>
/// Information about a Docker image
/// </summary>
public class ImageInfo
{
    public string Name { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public DateTime Created { get; set; }
    public long Size { get; set; }
}