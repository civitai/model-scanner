using Hangfire;
using Hangfire.Dashboard;
using Hangfire.Storage.SQLite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CloudStorageService>();
builder.Services.AddOptions<CloudStorageOptions>()
    .BindConfiguration(nameof(CloudStorageOptions))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<LocalStorageOptions>()
    .BindConfiguration(nameof(LocalStorageOptions))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHangfire(options =>
{
    options.UseSQLiteStorage(builder.Configuration.GetConnectionString("JobStorage"));
});
builder.Services.AddHangfireServer((_, options) =>
{
    options.WorkerCount = 1;
}, new SQLiteStorage(builder.Configuration.GetConnectionString("JobStorage")));

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.RequireAuthenticatedSignIn = false;
}).AddCookie();

var app = builder.Build();

app.UseAuthentication();

// Enable token authentication
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        if (!context.Request.Query.TryGetValue("token", out var token))
        {
            context.Response.StatusCode = 401; // Missing authentication token
            return;
        }

        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var validTokensConfiguration = configuration.GetRequiredSection("ValidTokens");

        var isValidToken = validTokensConfiguration
            .GetChildren()
            .Select(x => x.Get<string>())
            .Any(x => string.Equals(x, token, StringComparison.Ordinal));

        if (!isValidToken)
        {
            context.Response.StatusCode = 403; // Invalid token
            return;
        }

        // Set a cookie so that subsequent requests are authenticated
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity("Token")));
    }

    await next();
});

app.MapPost("/enqueue", (string fileUrl, string callbackUrl, IBackgroundJobClient backgroundJobClient) =>
{
    backgroundJobClient.Enqueue<FileProcessor>(x => x.ProcessFile(fileUrl, callbackUrl, CancellationToken.None));
});

#pragma warning disable ASP0014 // Hangfire dashboard is not compatible with top level routing
app.UseRouting();
app.UseEndpoints(routes =>
{
    routes.MapHangfireDashboard("");
});
#pragma warning restore ASP0014 // Suggest using top level route registrations


await app.RunAsync();
await app.WaitForShutdownAsync();
