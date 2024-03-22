using Hangfire;
using Hangfire.Dashboard;
using Hangfire.InMemory;
using Hangfire.Storage.SQLite;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ModelScanner;
using ModelScanner.Tasks;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CloudStorageService>();
builder.Services.AddSingleton<DockerService>();
builder.Services.AddSingleton<IJobTask, ImportTask>();
builder.Services.AddSingleton<IJobTask, ScanTask>();
builder.Services.AddSingleton<IJobTask, HashTask>();
builder.Services.AddSingleton<IJobTask, ConvertTask>();
builder.Services.AddSingleton<IJobTask, ParseMetadataTask>();

builder.Services.AddOptions<CloudStorageOptions>()
    .BindConfiguration(nameof(CloudStorageOptions))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<LocalStorageOptions>()
    .BindConfiguration(nameof(LocalStorageOptions))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("JobStorage");

builder.Services.AddHangfire(options =>
{
    if (connectionString is not null)
    {
        options.UseSQLiteStorage(builder.Configuration.GetConnectionString("JobStorage"));
    }
    else
    {
        options.UseInMemoryStorage();
    }
});
builder.Services.AddHangfireServer((_, options) =>
{
    options.WorkerCount = builder.Configuration.GetValue<int?>("Concurrency") ?? Environment.ProcessorCount;
    options.Queues = new[] { "default", "cleanup", "delete-objects", "low-prio" };
}, connectionString is not null ? new SQLiteStorage(builder.Configuration.GetConnectionString("JobStorage")) : new InMemoryStorage());

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
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TokenAuth");

        if (!context.Request.Query.TryGetValue("token", out var token))
        {
            logger.LogWarning("Expected a token query parameter to be present, none found");

            context.Response.StatusCode = 401; // Missing authentication token
            return;
        }

        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var validTokensConfiguration = configuration.GetRequiredSection("ValidTokens");
        var validTokens = validTokensConfiguration
            .GetChildren()
            .Select(x => x.Get<string>());

        var isValidToken = validTokens
            .Any(x => string.Equals(x, token, StringComparison.Ordinal));

        if (!isValidToken)
        {
            logger.LogWarning("The passed in token: {token} was not present in the list of valid tokens: {validTokens}", token, validTokens.ToArray());

            context.Response.StatusCode = 403; // Invalid token
            return;
        }

        // Set a cookie so that subsequent requests are authenticated
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity("Token")));
    }

    await next();
});

app.MapPost("/enqueue", (string fileUrl, string callbackUrl, JobTaskTypes? tasks, bool? lowPrio, IBackgroundJobClient backgroundJobClient) =>
{
    if (lowPrio == true)
    {
        backgroundJobClient.Enqueue<FileProcessor>(x => x.ProcessFileLowPrio(fileUrl, callbackUrl, tasks ?? JobTaskTypes.Default, CancellationToken.None));
    }
    else
    {
        backgroundJobClient.Enqueue<FileProcessor>(x => x.ProcessFile(fileUrl, callbackUrl, tasks ?? JobTaskTypes.Default, CancellationToken.None));
    }
});

app.MapPost("/cleanup", (IBackgroundJobClient backgroundJobClient) =>
{
    backgroundJobClient.Enqueue<CloudStorageService>(x => x.CleanupTempStorage(default));

    return Results.Accepted();
});

app.MapPost("/delete", (string key, IBackgroundJobClient backgroundJobClient) =>
{
    backgroundJobClient.Enqueue<CloudStorageService>(x => x.DeleteOject(key, default));

    return Results.Accepted();
});

#pragma warning disable ASP0014 // Hangfire dashboard is not compatible with top level routing
app.UseRouting();
app.UseEndpoints(routes =>
{
    routes.MapHangfireDashboard("", new DashboardOptions
    {
        Authorization = new IDashboardAuthorizationFilter[] { new AllowAllDashboardAuthorizationFilter() }
    });
});
#pragma warning restore ASP0014 // Suggest using top level route registrations

await app.RunAsync();
await app.WaitForShutdownAsync();
