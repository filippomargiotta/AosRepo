using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Microsoft.Extensions.Options;

namespace Aos.WebApi.Services;

public sealed class FileManifestWriter : IManifestWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly EventLogOptions _options;
    private readonly string _rootPath;
    private readonly ILogger<FileManifestWriter> _logger;

    public FileManifestWriter(
        IOptions<EventLogOptions> options,
        IHostEnvironment hostEnvironment,
        ILogger<FileManifestWriter> logger)
    {
        _options = options.Value;
        _rootPath = hostEnvironment.ContentRootPath;
        _logger = logger;
    }

    public async Task WriteAsync(ManifestRecord record, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_rootPath, _options.Directory, record.Manifest.RunId);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, _options.ManifestFileName);
        _logger.LogInformation(
            "Writing manifest for run {RunId} to {Path}",
            record.Manifest.RunId,
            path);

        var json = JsonSerializer.Serialize(record, JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);

        _logger.LogInformation("Wrote manifest for run {RunId}", record.Manifest.RunId);
    }
}
