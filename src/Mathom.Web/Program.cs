using Mathom.Web.Data;
using Mathom.Web.Processing;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<MathomDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Mathom")));

builder.Services.AddControllers();

builder.Services.AddScoped<ItemProcessor>();
// TODO(Task 9): replace with real provider + fallback
builder.Services.AddScoped<ILlmClient, NotConfiguredLlmClient>();

if (!builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<ProcessingWorker>();

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapControllers();

app.Run();

// Exposed so WebApplicationFactory<Program> can boot the app in tests.
public partial class Program { }
