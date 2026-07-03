namespace DevAgent.Hub.Api.Admin;

using System.Security.Claims;
using DevAgent.Store;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Local admin authentication for the admin console.
///
/// SECURITY:
///   * Cookie auth (HttpOnly, SameSite=Strict), 8h sliding expiry.
///   * One local admin user; only a PBKDF2-SHA512 hash is stored.
///   * Bootstrap password comes from DEVAGENT_ADMIN_PASSWORD (or
///     Admin:Password); if absent on first run, a random password is
///     generated and printed ONCE to the console.
///   * Mutating admin endpoints additionally require the custom
///     X-DevAgent-Admin header — a lightweight CSRF defence that works
///     because cross-origin requests cannot attach custom headers without a
///     CORS preflight (and no CORS is enabled).
///   * The scheme is standard AddAuthentication().AddCookie(), so an OIDC
///     provider (Entra ID, Keycloak, …) can be added later without touching
///     the UI or the endpoints.
/// </summary>
public static class AdminAuth
{
    public const string HeaderName = "X-DevAgent-Admin";

    public static void AddAdminAuth(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "devagent.admin";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;

                // The admin console is an API-driven SPA: return status codes,
                // never HTML redirects.
                options.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorization();
    }

    /// <summary>Creates the admin user on first run (hash only, never the password).</summary>
    public static async Task EnsureAdminUserAsync(DevAgentDbContext db, IConfiguration configuration)
    {
        if (await db.AdminUsers.AnyAsync())
        {
            return;
        }

        var password = Environment.GetEnvironmentVariable("DEVAGENT_ADMIN_PASSWORD")
                       ?? configuration["Admin:Password"];

        if (string.IsNullOrWhiteSpace(password))
        {
            password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));
            Console.WriteLine("==============================================================");
            Console.WriteLine($"  DevAgent admin console — generated admin password: {password}");
            Console.WriteLine("  (Set DEVAGENT_ADMIN_PASSWORD to choose your own. Shown once.)");
            Console.WriteLine("==============================================================");
        }

        var (hash, salt, iterations) = PasswordHasher.Hash(password!);
        db.AdminUsers.Add(new AdminUserEntity
        {
            Username = configuration["Admin:Username"] ?? "admin",
            PasswordHashBase64 = hash,
            SaltBase64 = salt,
            Iterations = iterations,
        });
        await db.SaveChangesAsync();
    }

    public sealed record LoginBody(string Username, string Password);

    public static void MapAdminAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/admin/api/login", async (
            LoginBody body,
            HttpContext http,
            IDbContextFactory<DevAgentDbContext> dbFactory,
            CancellationToken ct) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var user = await db.AdminUsers.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Username == body.Username, ct);

            // Verify against a dummy hash when the user is unknown so response
            // timing does not reveal valid usernames.
            var ok = user is not null
                ? PasswordHasher.Verify(body.Password, user.PasswordHashBase64, user.SaltBase64, user.Iterations)
                : PasswordHasher.Verify(body.Password, DummyHash.Hash, DummyHash.Salt, PasswordHasher.DefaultIterations) && false;

            if (!ok)
            {
                return Results.Unauthorized();
            }

            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, user!.Username), new Claim(ClaimTypes.Role, "admin") },
                CookieAuthenticationDefaults.AuthenticationScheme);

            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.Ok(new { username = user.Username });
        });

        app.MapPost("/admin/api/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        }).RequireAuthorization();

        app.MapGet("/admin/api/session", (HttpContext http) =>
            http.User.Identity?.IsAuthenticated == true
                ? Results.Ok(new { authenticated = true, username = http.User.Identity!.Name })
                : Results.Ok(new { authenticated = false, username = (string?)null }));
    }

    private static class DummyHash
    {
        public static readonly string Hash;
        public static readonly string Salt;

        static DummyHash() => (Hash, Salt, _) = PasswordHasher.Hash(Guid.NewGuid().ToString("N"));
    }
}
