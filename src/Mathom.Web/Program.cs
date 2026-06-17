using Mathom.Web.Data;
using Mathom.Web.Processing;
using Mathom.Web.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<MathomDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Mathom")));

builder.Services.AddRazorPages();
builder.Services.AddScoped<SearchService>();
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

// Apply EF Core migrations on startup so a fresh container/DB is ready to use.
// Skipped under the Testing environment, where integration tests manage the schema themselves.
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<MathomDbContext>().Database.Migrate();
}

app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapControllers();
app.MapRazorPages();

app.Run();

// Exposed so WebApplicationFactory<Program> can boot the app in tests.
public partial class Program { }
