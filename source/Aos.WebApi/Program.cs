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
builder.Services.Configure<PlannerWorkflowOptions>(
    builder.Configuration.GetSection(PlannerWorkflowOptions.SectionName));
builder.Services.Configure<RouterOptions>(
    builder.Configuration.GetSection(RouterOptions.SectionName));
builder.Services.Configure<RouterMetricsOptions>(
    builder.Configuration.GetSection(RouterMetricsOptions.SectionName));
builder.Services.Configure<CapabilityTokenOptions>(
    builder.Configuration.GetSection(CapabilityTokenOptions.SectionName));
builder.Services.Configure<SandboxPoolOptions>(
    builder.Configuration.GetSection(SandboxPoolOptions.SectionName));
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
builder.Services.AddSingleton<HmacJwtCapabilityTokenService>();
builder.Services.AddSingleton<ICapabilityTokenIssuer>(serviceProvider =>
    serviceProvider.GetRequiredService<HmacJwtCapabilityTokenService>());
builder.Services.AddSingleton<ICapabilityTokenValidator>(serviceProvider =>
    serviceProvider.GetRequiredService<HmacJwtCapabilityTokenService>());
builder.Services.AddSingleton<PreWarmedSandboxPool>(serviceProvider =>
{
    var opts = serviceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<SandboxPoolOptions>>()
        .Value;
    return new PreWarmedSandboxPool(opts.PoolSize, SandboxSlotFactory.Create(opts));
});
builder.Services.AddSingleton<PooledSandboxToolExecutor>();
builder.Services.AddSingleton<IToolExecutor>(serviceProvider =>
    new CapabilityEnforcingToolExecutor(
        serviceProvider.GetRequiredService<ICapabilityTokenValidator>(),
        serviceProvider.GetRequiredService<PooledSandboxToolExecutor>()));
builder.Services.AddSingleton<IHelloWorkflowService, HelloWorkflowService>();
builder.Services.AddSingleton<AllowedActionCatalog>(serviceProvider =>
{
    var options = serviceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<PlannerWorkflowOptions>>()
        .Value;
    return new AllowedActionCatalog(PlannerConfiguration.CreateAllowedActions(options));
});
builder.Services.AddSingleton<IPlaybookStore>(serviceProvider =>
{
    var options = serviceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<PlannerWorkflowOptions>>()
        .Value;
    return new InMemoryPlaybookStore(PlannerConfiguration.CreatePlaybooks(options));
});
builder.Services.AddSingleton<DeterministicPlaybookRetriever>();
builder.Services.AddSingleton<IPlannerCandidateProvider, DeterministicPlaybookCandidateProvider>();
builder.Services.AddSingleton<PlannerPlanValidator>(serviceProvider =>
    new PlannerPlanValidator(serviceProvider.GetRequiredService<AllowedActionCatalog>()));
builder.Services.AddSingleton<IPlannerService, DeterministicPlannerService>();
builder.Services.AddSingleton<PlannerStepExecutor>();
builder.Services.AddSingleton<IPlannerWorkflowService, PlannerWorkflowService>();
builder.Services.AddSingleton<IRouterMetricsStore>(serviceProvider =>
    new InMemoryRouterMetricsStore(
        serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RouterMetricsOptions>>()));
builder.Services.AddSingleton<IRouterService>(serviceProvider =>
    new DeterministicRouterService(
        serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RouterOptions>>(),
        serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RouterMetricsOptions>>(),
        serviceProvider.GetRequiredService<IRouterMetricsStore>()));

builder.Services.AddOpenTelemetry()
    .WithTracing(tracerProviderBuilder =>
    {
        tracerProviderBuilder
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter();
    });

var app = builder.Build();

// Materialize the pool before accepting traffic so the first workflow request uses warm slots.
_ = app.Services.GetRequiredService<PreWarmedSandboxPool>();

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
