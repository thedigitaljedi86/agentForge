namespace DevAgent.Hub.Api.Admin;

using Hangfire.Dashboard;

/// <summary>
/// Gates the Hangfire dashboard behind the admin login outside Development.
/// (The default Hangfire filter only allows local requests; in a container,
/// everything looks remote, so we tie it to the same cookie as the console.)
/// </summary>
public sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    private readonly bool _isDevelopment;

    public HangfireDashboardAuthFilter(bool isDevelopment)
    {
        _isDevelopment = isDevelopment;
    }

    public bool Authorize(DashboardContext context) =>
        _isDevelopment || context.GetHttpContext().User.Identity?.IsAuthenticated == true;
}
