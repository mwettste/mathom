using Mathom.Web.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<MathomDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Mathom")));

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok("ok"));

app.Run();

// Exposed so WebApplicationFactory<Program> can boot the app in tests.
public partial class Program { }
