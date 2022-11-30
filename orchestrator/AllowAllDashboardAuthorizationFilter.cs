using Hangfire.Annotations;
using Hangfire.Dashboard;
namespace ModelScanner;

internal class AllowAllDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize([NotNull] DashboardContext context)
    {
        // Authorization happens earlier in the middleware pipeline
        return true;
    }
}
