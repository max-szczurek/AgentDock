using AgentDock.Core;
using AgentDock.Core.Data;
using AgentDock.Core.Security;
using AgentDock.Web.Services;
using AgentDock.Web.Widget;
using Azure.Extensions.AspNetCore.DataProtection.Keys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var connectionString = config["AGENTDOCK_SQL_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("AGENTDOCK_SQL_CONNECTION_STRING is not configured.");
var csb = new SqlConnectionStringBuilder(connectionString);
if (csb.IntegratedSecurity && !OperatingSystem.IsWindows())
    throw new InvalidOperationException(
        "AGENTDOCK_SQL_CONNECTION_STRING enables Integrated Security (Trusted_Connection=True), which is not supported on this host. Set Trusted_Connection=False.");

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AllowAnonymousToPage("/Account/Login");
    o.Conventions.AllowAnonymousToPage("/Account/Denied");
    o.Conventions.AllowAnonymousToPage("/Error");
    o.Conventions.AllowAnonymousToPage("/Dev/Login");
});
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    o.AddPolicy(VisitorAuth.Policy, p => p.AddAuthenticationSchemes(VisitorAuth.Scheme).RequireAuthenticatedUser());
});

builder.Services.AddDbContext<AgentDockDbContext>(o => o.UseSqlServer(connectionString));
builder.Services.AddSingleton(sp => new SchemaMigrator(connectionString, sp.GetRequiredService<ILogger<SchemaMigrator>>()));

builder.Services.AddIdentity<IdentityUser, IdentityRole>(o => o.SignIn.RequireConfirmedAccount = false)
    .AddEntityFrameworkStores<AgentDockDbContext>()
    .AddDefaultTokenProviders();

var auth = builder.Services.AddAuthentication();
auth.AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, VisitorTokenHandler>(VisitorAuth.Scheme, null);
var googleId = config["AGENTDOCK_GOOGLE_CLIENT_ID"];
var googleSecret = config["AGENTDOCK_GOOGLE_CLIENT_SECRET"];
if (!string.IsNullOrWhiteSpace(googleId) && !string.IsNullOrWhiteSpace(googleSecret))
    auth.AddGoogle(o => { o.ClientId = googleId; o.ClientSecret = googleSecret; });

builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.AccessDeniedPath = "/Account/Denied";
    o.Cookie.Name = "AgentDock.Auth";
});

var dp = builder.Services.AddDataProtection().SetApplicationName("AgentDock").PersistKeysToDbContext<AgentDockDbContext>();
var dpKey = config["AGENTDOCK_DATAPROTECTION_KEY_ID"];
if (!string.IsNullOrWhiteSpace(dpKey))
    dp.ProtectKeysWithAzureKeyVault(new Uri(dpKey), CredentialFactory.Create(builder.Environment, config));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAgentDockCore();
builder.Services.AddSingleton<IKeyWrapper>(sp =>
{
    var kek = config["AGENTDOCK_KEYVAULT_KEY_ID"];
    if (string.IsNullOrWhiteSpace(kek))
        throw new InvalidOperationException("AGENTDOCK_KEYVAULT_KEY_ID is not configured; secrets cannot be protected.");
    return new KeyVaultKeyWrapper(new Uri(kek), CredentialFactory.Create(builder.Environment, config));
});
builder.Services.AddScoped<ICurrentAccountAccessor, CurrentAccountAccessor>();
builder.Services.AddSingleton<AttemptLimiter>();
builder.Services.AddScoped<AccessGate>();
builder.Services.AddScoped<AccountProvisioner>();
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<SchemaMigrator>().MigrateAsync();
    var gate = scope.ServiceProvider.GetRequiredService<AccessGate>();
    if (gate.DevLoginEnabled)
        app.Logger.LogWarning("DEV LOGIN IS ENABLED (/dev/login). Remove AGENTDOCK_DEV_LOGIN_SECRET to disable it.");
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c =>
    {
        if (c.Context.Request.Path.StartsWithSegments("/widget"))
            c.Context.Response.Headers.CacheControl = "public, max-age=300";
    },
});
app.UseRouting();
app.UseAuthentication();
app.UseMiddleware<AllowlistMiddleware>();
app.UseAuthorization();

app.MapRazorPages();
app.MapWidget();
app.MapGet("/health", async (AgentDockDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503))
    .AllowAnonymous();

app.Run();

public partial class Program;
