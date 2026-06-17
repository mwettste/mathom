using Mathom.Web.Data;
using Mathom.Web.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<MathomDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Mathom")));

builder.Services.AddControllers();

builder.Services.AddScoped<ItemProcessor>();
builder.Services.AddHttpClient<InfomaniakLlmClient>();
builder.Services.AddHttpClient<OpenRouterLlmClient>();
builder.Services.AddScoped<ILlmClient>(sp => new FallbackLlmClient(
    new ILlmClient[]
    {
        sp.GetRequiredService<InfomaniakLlmClient>(),
        sp.GetRequiredService<OpenRouterLlmClient>(),
    },
    sp.GetRequiredService<ILogger<FallbackLlmClient>>()));

if (!builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<ProcessingWorker>();

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapControllers();

app.Run();

// Exposed so WebApplicationFactory<Program> can boot the app in tests.
public partial class Program { }
