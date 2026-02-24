using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Extensions.Configuration;
using MustMail;
using Serilog;
using Serilog.Events;
using SmtpServer;
using System.Reflection;
using System.Text.Json;
using ServiceProvider = SmtpServer.ComponentModel.ServiceProvider;

// --------------------------
// Banner
// --------------------------
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("Must Mail");
Console.WriteLine(Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3));
Console.ForegroundColor = ConsoleColor.White;

// --------------------------
// Load configuration
// --------------------------
IConfigurationBuilder builder = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables();

IConfiguration configuration = builder.Build();
Configuration? config = configuration.Get<Configuration>();

if (config == null || config.Graph == null || config.Smtp == null || config.SendFrom == null)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Could not load configuration! Check your .env or appsettings.json.");
    Environment.Exit(1);
}

// --------------------------
// Logger
// --------------------------
string logLevelString = configuration["LogLevel"] ?? "Information";
bool parsed = Enum.TryParse<LogEventLevel>(logLevelString, ignoreCase: true, out var logLevel);
if (!parsed) logLevel = LogEventLevel.Information;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(logLevel)
    .WriteTo.Console(
        theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Literate,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

Log.Information("Configuration: \n {Serialize}", JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

// --------------------------
// SMTP Server Options
// --------------------------
ISmtpServerOptions options = new SmtpServerOptionsBuilder()
    .ServerName(config.Smtp.Host)
    .Port(config.Smtp.Port, false)
    .Build();

// --------------------------
// Azure Graph Setup
// --------------------------
bool useGovCloud = configuration["AzureCloud"]?.Equals("Government", StringComparison.OrdinalIgnoreCase) ?? true;

string authorityHost = useGovCloud
    ? AzureAuthorityHosts.AzureGovernment
    : AzureAuthorityHosts.AzurePublicCloud;

string[] graphScopes = useGovCloud
    ? new[] { "https://graph.microsoft.us/.default" }
    : new[] { "https://graph.microsoft.com/.default" };

var clientSecretCredential = new ClientSecretCredential(
    config.Graph.TenantId,
    config.Graph.ClientId,
    config.Graph.ClientSecret,
    new ClientSecretCredentialOptions
    {
        AuthorityHost = authorityHost
    }
);

// Graph client options
var graphClientOptions = new GraphServiceClientOptions
{
    BaseUrl = useGovCloud
        ? "https://graph.microsoft.us/v1.0"
        : "https://graph.microsoft.com/v1.0"
};

// Graph client (modern SDK)
var graphClient = new GraphServiceClient(clientSecretCredential, graphScopes, graphClientOptions);

// --------------------------
// Validate SendFrom User
// --------------------------
try
{
    var user = await graphClient.Users[config.SendFrom].GetAsync(req =>
        req.QueryParameters.Select = new[] { "displayName", "mail", "mailboxSettings" });

    if (user == null || (user.Mail == null && user.UserPrincipalName == null))
    {
        Log.Error("SendFrom '{From}' does not exist or has no email.", config.SendFrom);
        Environment.Exit(1);
    }

    if (user.MailboxSettings == null)
        Log.Warning("Mailbox settings for '{From}' not found. Sending may fail.", config.SendFrom);

    Log.Information("SendFrom '{From}' is valid. DisplayName: '{DisplayName}'", config.SendFrom, user.DisplayName);
}
catch (Microsoft.Graph.Models.ODataErrors.ODataError error)
{
    Log.Error("SendFrom '{From}' not found. Graph error: {Error}", config.SendFrom, error.Message);
    Environment.Exit(1);
}

// --------------------------
// SMTP Email Service
// --------------------------
ServiceProvider emailServiceProvider = new();
emailServiceProvider.Add(new MessageHandler(graphClient, Log.Logger.ForContext<MessageHandler>(), config.SendFrom));

// Create and start SMTP server
SmtpServer.SmtpServer smtpServer = new(options, emailServiceProvider);

Log.Information("SMTP server started on {Host}:{Port}", config.Smtp.Host, config.Smtp.Port);

await smtpServer.StartAsync(CancellationToken.None);
