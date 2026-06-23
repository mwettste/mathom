using Mathom.Web.Data;
using Mathom.Web.Domain;
using Mathom.Web.Media;
using Mathom.Web.Processing;
using Mathom.Web.Search;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<MathomDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Mathom")));

// Persist Data Protection keys to a configured directory (a mounted volume in
// Docker) so auth cookies and anti-forgery tokens survive container recreation.
// When unset (local `dotnet run`, tests) the framework default is used.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("Mathom");
}

builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireDigit = false;
    })
    .AddEntityFrameworkStores<MathomDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Login";
    o.LogoutPath = "/Logout";
    o.AccessDeniedPath = "/Login";
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;

    o.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/capture") && HttpMethods.IsPost(ctx.Request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
});

builder.Services.AddRazorPages();
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<Mathom.Web.Notes.NoteService>();
builder.Services.AddScoped<Mathom.Web.Glossary.GlossaryService>();
builder.Services.AddScoped<Mathom.Web.Admin.UserAdminService>();
builder.Services.AddControllers();

builder.Services.AddScoped<ItemProcessor>();
builder.Services.AddSingleton<IMediaStore, LocalDiskMediaStore>();
builder.Services.AddHttpClient<InfomaniakLlmClient>();
builder.Services.AddHttpClient<OpenRouterLlmClient>();
builder.Services.AddHttpClient<InfomaniakTranscriber>();
builder.Services.AddScoped<ITranscriber>(sp => sp.GetRequiredService<InfomaniakTranscriber>());
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

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

app.MapGet("/healthz", () => Results.Ok("ok"));

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();

app.Run();

// Exposed so WebApplicationFactory<Program> can boot the app in tests.
public partial class Program { }
