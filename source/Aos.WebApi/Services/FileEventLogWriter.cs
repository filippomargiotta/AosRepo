using System.Text;
using System.Text.Json;
using Aos.WebApi.Models;
using Aos.WebApi.Options;
using Microsoft.Extensions.Options;

namespace Aos.WebApi.Services;

public sealed class FileEventLogWriter : IEventLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly EventLogOptions _options;
    private readonly string _rootPath;
    private readonly ILogger<FileEventLogWriter> _logger;

    public FileEventLogWriter(
        IOptions<EventLogOptions> options,
        IHostEnvironment hostEnvironment,
        ILogger<FileEventLogWriter> logger)
    {
        _options = options.Value;
        _rootPath = hostEnvironment.ContentRootPath;
        _logger = logger;
    }

    public async Task WriteAsync(EventLogRecord record, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_rootPath, _options.Directory, record.Entry.RunId);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, _options.FileName);
        _logger.LogInformation(
            "Writing event log entry {EventType} for run {RunId} to {Path}",
            record.Entry.EventType,
            record.Entry.RunId,
            path);
        var json = JsonSerializer.Serialize(record, JsonOptions);
        await File.AppendAllTextAsync(path, json + Environment.NewLine, Encoding.UTF8, cancellationToken);
        _logger.LogInformation(
            "Wrote event log entry {EventType} for run {RunId}",
            record.Entry.EventType,
            record.Entry.RunId);
    }
}
