// Standard ASP.NET configuration sources load the environment file after the base file.
var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<SandboxPoolOptions>(builder.Configuration.GetSection("SandboxPool"));
var app = builder.Build();
app.Run();
