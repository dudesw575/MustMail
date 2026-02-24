using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using Microsoft.Graph.Auth;
using MustMail;
using Serilog;
using Serilog.Events;
using SmtpServer;
using System.Reflection;
using System.Text.Json;
using ServiceProvider = SmtpServer.ComponentModel.ServiceProvider;

// Version and copyright message
Console.ForegroundColor = ConsoleColor.Cyan; 
Console.WriteLine("Must Mail");
Console.WriteLine(Assembly.GetEntryAssembly()!.GetName().Version?.ToString(3));
Console.ForegroundColor = ConsoleColor.White;

// Configuration
IConfigurationBuilder builder = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables();

IConfiguration configuration = builder.Build();

// Get log level string
string logLevelString = configuration["LogLevel"] ?? "Information";

// Parse to Serilog log level enum
bool parsed = Enum.TryParse<LogEventLevel>(logLevelString, ignoreCase: true, out var logLevel);

if (!parsed)
{
    logLevel = LogEventLevel.Information; 
}

// Creating logger
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(logLevel)
     .WriteTo.Console(
        theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Literate,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// Parse config
Configuration? config  = configuration.Get<Configuration>();

if (config == null || config.Graph == null || config.Smtp == null || config.SendFrom == null)
{
    Log.Error("Could not load the configuration! Please see the README for how to set the configuration!");
    Environment.Exit(1);
}

// Log configuration
Log.Information("Configuration: \n {Serialize}", JsonSerializer.Serialize(config, new JsonSerializerOptions{WriteIndented = true}));

// Create SMTP Server options
ISmtpServerOptions? options = new SmtpServerOptionsBuilder()
    .ServerName(config.Smtp.Host)
    .Port(config.Smtp.Port, false)
    .Build();

// --------------------------
// Azure Government Fix Start
// --------------------------

// Determine cloud type via environment variable (optional)
bool useGovCloud = configuration["AzureCloud"]?.Equals("Government", StringComparison.OrdinalIgnoreCase) ?? true;

// Select AuthorityHost and Graph endpoints
string authorityHost = useGovCloud
    ? AzureAuthorityHosts.AzureGovernment
    : AzureAuthorityHosts.AzurePublicCloud;

string[] graphScopes = useGovCloud
    ? new[] { "https://graph.microsoft.us/.default" }
    : new[] { "https://graph.microsoft.com/.default" };

string graphBaseUrl = useGovCloud
    ? "https://graph.microsoft.us/v1.0"
    : "https://graph.microsoft.com/v1.0";

// Create client secret credential
ClientSecretCredential clientSecretCredential = new(
    config.Graph.TenantId,
    config.Graph.ClientId,
    config.Graph.ClientSecret,
    new ClientSecretCredentialOptions
    {
        AuthorityHost = authorityHost
    }
);

// Create Graph client
GraphServiceClient graphClient = new GraphServiceClient(
    graphBaseUrl,
    new TokenCredentialAuthProvider(graphScopes, clientSecretCredential)
);

// ------------------------
// Azure Government Fix End
// ------------------------

// SendFrom checks
try
{
    User? user = await graphClient.Users[config.SendFrom].GetAsync(rc => rc.QueryParameters.Select = new[] { "displayName", "mail", "mailboxSettings" });

    if (user == null)
    {
        Log.Error("The specified SendFrom address: '{From}' does not exist in the tenant!", config.SendFrom);
        Environment.Exit(1);
    }

    if (user.Mail == null && user.UserPrincipalName == null)
    {
        Log.Error("The user '{From}' has no email address configured and cannot send mail.", config.SendFrom);
        Environment.Exit(1);
    }

    if (user.MailboxSettings == null)
    {
        Log.Warning("Mailbox settings for user '{From}' not found. Sending mail might not be available.", config.SendFrom);
    }

    Log.Information("The user '{From}' has an email address configured and can send mail, the display name for the SendFrom address is: '{DisplayName}'", config.SendFrom, user.DisplayName);
}
catch (Microsoft.Graph.Models.ODataErrors.ODataError error)
{
    Log.Error("The specified SendFrom address: '{From}' does not exist in the tenant! The Microsoft Graph error message is: '{Error}'", config.SendFrom, error.Message);
    Environment.Exit(1);
}

// Create email service provider
ServiceProvider emailServiceProvider = new();
emailServiceProvider.Add(new MessageHandler(graphClient, Log.Logger.ForContext<MessageHandler>(), config.SendFrom));

// Create the SMTP server
SmtpServer.SmtpServer smtpServer = new(options, emailServiceProvider);

// Log server start
Log.Information("Smtp server started on {SmtpHost}:{SmtpPort}", config.Smtp.Host, config.Smtp.Port);

// Start the server
await smtpServer.StartAsync(CancellationToken.None);
