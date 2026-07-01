using Mathom.Web.Data;
using Mathom.Web.Domain;
using Mathom.Web.Embeddings;
using Mathom.Web.Media;
using Pgvector.Npgsql;
using Mathom.Web.Processing;
using Mathom.Web.Search;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

var mathomConnectionString = builder.Configuration.GetConnectionString("Mathom")!;
var mathomDataSource = BuildMathomDataSource(mathomConnectionString);
builder.Services.AddDbContext<MathomDbContext>(o =>
    o.UseNpgsql(mathomDataSource, npgsql => npgsql.UseVector()));

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
        // Throttle password brute-force: lock an account for 15 min after 10 failed
        // attempts. Per-account (not IP-based), so it works regardless of the proxy.
        o.Lockout.MaxFailedAccessAttempts = 10;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
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

// Behind the platform's reverse proxy (Caddy terminates TLS), honor X-Forwarded-Proto
// so the app sees requests as HTTPS — needed so the auth + anti-forgery cookies are
// flagged Secure. Gated by config: the standalone compose is directly exposed and must
// NOT trust these headers (they'd be spoofable). The deploy compose sets it to true.
if (builder.Configuration.GetValue<bool>("ForwardedHeaders:Enabled"))
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        // The proxy is a separate container on an unpredictable IP, so the default
        // loopback-only trust won't match. Clearing both makes the middleware honor
        // the headers regardless of source — safe because nothing but the edge proxy
        // can reach this container (no published host port).
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

builder.Services.AddRazorPages();
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "RequestVerificationToken";
    // Flag the anti-forgery cookie Secure when the request is HTTPS (the default is None,
    // i.e. never Secure). Combined with forwarded-headers handling above, this makes it
    // Secure behind the proxy while staying usable over plain HTTP in local dev.
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<Mathom.Web.Notes.NoteService>();
builder.Services.AddScoped<Mathom.Web.Glossary.GlossaryService>();
builder.Services.AddScoped<Mathom.Web.Languages.UserLanguageService>();
builder.Services.AddScoped<Mathom.Web.Contexts.ContextService>();
builder.Services.AddScoped<Mathom.Web.Admin.UserAdminService>();
builder.Services.AddControllers();

builder.Services.AddScoped<ItemProcessor>();
builder.Services.AddSingleton<IMediaStore, LocalDiskMediaStore>();
builder.Services.AddSingleton<ImageVariantProcessor>();
builder.Services.AddScoped<PhotoVariantService>();
builder.Services.AddHttpClient<InfomaniakLlmClient>();
builder.Services.AddHttpClient<OpenRouterLlmClient>();
builder.Services.AddHttpClient<InfomaniakTranscriber>();
builder.Services.AddScoped<ITranscriber>(sp => sp.GetRequiredService<InfomaniakTranscriber>());
builder.Services.AddHttpClient<OpenRouterImageReader>();
builder.Services.AddScoped<IImageReader>(sp => sp.GetRequiredService<OpenRouterImageReader>());
builder.Services.AddScoped<ILlmClient>(sp => new FallbackLlmClient(
    new ILlmClient[]
    {
        sp.GetRequiredService<InfomaniakLlmClient>(),
        sp.GetRequiredService<OpenRouterLlmClient>(),
    },
    sp.GetRequiredService<ILogger<FallbackLlmClient>>()));

builder.Services.AddHttpClient<InfomaniakEmbeddingClient>();
builder.Services.AddHttpClient<OpenRouterEmbeddingClient>();
builder.Services.AddScoped<IEmbeddingClient>(sp => new FallbackEmbeddingClient(
    new IEmbeddingClient[]
    {
        sp.GetRequiredService<InfomaniakEmbeddingClient>(),
        sp.GetRequiredService<OpenRouterEmbeddingClient>(),
    },
    sp.GetRequiredService<ILogger<FallbackEmbeddingClient>>()));

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddHostedService<ProcessingWorker>();
    builder.Services.AddHostedService<EmbeddingBackfillWorker>();
}

var app = builder.Build();

// Apply EF Core migrations on startup so a fresh container/DB is ready to use.
// Skipped under the Testing environment, where integration tests manage the schema themselves.
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    var sp = scope.ServiceProvider;
    // pgvector's type mapping resolves the `vector` type when a connection first opens.
    // On a fresh DB the extension may not exist yet, so create it on a plain (unmapped)
    // connection first. Idempotent; the migration also declares it.
    using (var bootstrap = new Npgsql.NpgsqlConnection(mathomConnectionString))
    {
        bootstrap.Open();
        using var cmd = bootstrap.CreateCommand();
        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS vector";
        cmd.ExecuteNonQuery();
    }
    sp.GetRequiredService<MathomDbContext>().Database.Migrate();
    await Mathom.Web.Admin.AdminBootstrap.EnsureRoleAndPromoteAsync(
        sp.GetRequiredService<RoleManager<IdentityRole>>(),
        sp.GetRequiredService<UserManager<ApplicationUser>>(),
        Mathom.Web.Admin.AdminBootstrap.AdminEmailsFromConfig(app.Configuration));
}

// Must run before anything that reads the request scheme (cookie/anti-forgery setup).
if (app.Configuration.GetValue<bool>("ForwardedHeaders:Enabled"))
    app.UseForwardedHeaders();

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

app.MapGet("/healthz", () => Results.Ok("ok"));

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<Mathom.Web.Auth.ApprovalGateMiddleware>();

app.MapControllers();
app.MapRazorPages();

app.Run();

// Builds an Npgsql data source with the pgvector type mapping enabled. Building the
// data source does NOT open a connection, so this is safe at EF design time. The `vector`
// extension is created at startup (below) before the first real query runs.
// Note: Pgvector 0.3.x uses AddTypeInfoResolverFactory for Npgsql 10.x compatibility
// (UseVector(NpgsqlDataSourceBuilder) targets the older INpgsqlTypeMapper API).
static Npgsql.NpgsqlDataSource BuildMathomDataSource(string connectionString)
{
    var dsb = new Npgsql.NpgsqlDataSourceBuilder(connectionString);
#pragma warning disable NPG9001 // Internal Npgsql API; Pgvector uses this pattern with Npgsql 10.x
    dsb.AddTypeInfoResolverFactory(new Pgvector.Npgsql.VectorTypeInfoResolverFactory());
#pragma warning restore NPG9001
    return dsb.Build();
}

// Exposed so WebApplicationFactory<Program> can boot the app in tests.
public partial class Program { }
