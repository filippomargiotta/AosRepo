using Aos.WebApi.Options;
using Aos.WebApi.Services;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .Enrich.FromLogContext());

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<EventLogOptions>(
    builder.Configuration.GetSection(EventLogOptions.SectionName));
builder.Services.Configure<HelloWorkflowOptions>(
    builder.Configuration.GetSection(HelloWorkflowOptions.SectionName));
builder.Services.Configure<RouterOptions>(
    builder.Configuration.GetSection(RouterOptions.SectionName));
builder.Services.Configure<RouterMetricsOptions>(
    builder.Configuration.GetSection(RouterMetricsOptions.SectionName));
builder.Services.AddSingleton<IEventLogWriter, FileEventLogWriter>();
builder.Services.AddSingleton<IManifestWriter, FileManifestWriter>();
builder.Services.AddSingleton<IEventLogIntegrityChain>(serviceProvider =>
{
    var options = serviceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<EventLogOptions>>()
        .Value;
    return new HmacEventLogIntegrityChain(options.HmacKey, options.HmacKeyId);
});
builder.Services.AddSingleton<IManifestSigner>(serviceProvider =>
{
    var options = serviceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<EventLogOptions>>()
        .Value;
    return new HmacManifestSigner(options.HmacKey, options.HmacKeyId);
});
builder.Services.AddSingleton<ISeedGenerator, RandomSeedGenerator>();
builder.Services.AddSingleton<ISeedProvider, LockedSeedProvider>();
builder.Services.AddSingleton<ITimeSource, SystemTimeSource>();
builder.Services.AddSingleton<IHelloWorkflowService, HelloWorkflowService>();
builder.Services.AddSingleton<IRouterService, DeterministicRouterService>();
builder.Services.AddSingleton<IRouterMetricsStore, InMemoryRouterMetricsStore>();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracerProviderBuilder =>
    {
        tracerProviderBuilder
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter();
    });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSerilogRequestLogging();

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
