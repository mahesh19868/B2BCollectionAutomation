using Collections.Web.Data;
using Collections.Web.Email;
using Collections.Web.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Collections.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real application against a throwaway SQL Server database (LocalDB by default;
/// override with the COLLECTIONS_TEST_SQL environment variable). Email is captured in memory and the clock is fixed.
/// </summary>
public sealed class AppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>15 Oct 2026 12:00 IST.</summary>
    public static readonly DateTimeOffset FixedNow = new(2026, 10, 15, 6, 30, 0, TimeSpan.Zero);

    public static readonly DateOnly Today = new(2026, 10, 15);

    private readonly string _connectionString;

    public AppFactory()
    {
        var databaseName = "CollectionsIT_" + Guid.NewGuid().ToString("N");
        var server = Environment.GetEnvironmentVariable("COLLECTIONS_TEST_SQL");
        _connectionString = string.IsNullOrWhiteSpace(server)
            ? $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Trusted_Connection=True;TrustServerCertificate=True"
            : $"{server.TrimEnd(';')};Database={databaseName}";
    }

    public TestEmailSender Email { get; } = new();
    public TestTimeProvider Time { get; } = new(FixedNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Email:Provider", "Capture");
        builder.UseSetting("Email:FromAddress", "reminders@test.invalid");
        builder.UseSetting("RateLimits:LoginPerMinute", "10000");
        builder.UseSetting("RateLimits:ExpensivePerMinute", "10000");
        builder.UseSetting("Import:MaxFileBytes", "65536");
        builder.UseSetting("Import:MaxRows", "200");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        });
    }

    public HttpClient CreateBrowser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>A context bound to one business, for arranging and asserting data directly.</summary>
    public AppDbContext CreateDbContext(int? businessId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(_connectionString).Options;
        return new AppDbContext(options, new FixedBusinessContext(businessId), Time);
    }

    public Task InitializeAsync()
    {
        // Starting the server applies migrations.
        _ = Server;
        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await using (var db = CreateDbContext(null))
        {
            await db.Database.EnsureDeletedAsync();
        }

        await base.DisposeAsync();
    }

    private sealed class FixedBusinessContext : ICurrentBusinessContext
    {
        public FixedBusinessContext(int? businessId) => BusinessId = businessId;
        public int? BusinessId { get; }
        public string? UserId => null;
    }
}

public sealed class TestTimeProvider : TimeProvider
{
    public TestTimeProvider(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

public sealed class TestEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = new();

    public bool Fail { get; set; }

    public IReadOnlyList<EmailMessage> Sent
    {
        get { lock (_sent) { return _sent.ToList(); } }
    }

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            return Task.FromResult(EmailSendResult.Failure("The mail server rejected the message (SMTP 550)."));
        }

        lock (_sent)
        {
            _sent.Add(message);
        }

        return Task.FromResult(EmailSendResult.Success("test-" + Guid.NewGuid().ToString("N")));
    }

    public int CountTo(string recipient) => Sent.Count(m => m.To == recipient);
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<AppFactory>
{
    public const string Name = "Integration";
}
