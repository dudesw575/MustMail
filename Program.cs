using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using MustMail;
using Serilog;
using Serilog.Events;
using SmtpServer;
using System.Reflection;
using System.Text.Json;
using ServiceProvider = SmtpServer.ComponentModel.ServiceProvider;
using System.Net.Http;
using Azure.Core;
using System.Threading.Tasks;

 

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

// Prase config
Configuration? config  = configuration.Get<Configuration>();

// If configuration can not be parsed to config - exit
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

// Azure Government configuration
var authorityHost = AzureAuthorityHosts.AzureGovernment;
var graphScopes = new[] { "https://graph.microsoft.us/.default" };

// Create client secrete credential with explicit Government Cloud authority
ClientSecretCredential clientSecretCredential = new(
    config.Graph.TenantId, 
    config.Graph.ClientId, 
    config.Graph.ClientSecret,
    new ClientSecretCredentialOptions
    {
        AuthorityHost = authorityHost
    }
);

// Create HttpClient with BaseAddress pointing at the Government Graph endpoint
var handler = new TokenCredentialHttpHandler(clientSecretCredential, graphScopes)
{
    InnerHandler = new HttpClientHandler()
};

var httpClient = new HttpClient(handler)
{
    BaseAddress = new Uri("https://graph.microsoft.us/v1.0")
};

GraphServiceClient graphClient = new(httpClient);

// DEBUG: Log token and cloud configuration
try
{
    var token = await clientSecretCredential.GetTokenAsync(
        new Azure.Core.TokenRequestContext(graphScopes));
    
    // Decode JWT to see claims (basic decoding - not validation)
    var parts = token.Token.Split('.');
    if (parts.Length == 3)
    {
        var payload = parts[1];
        // Add padding if needed
        payload += new string('=', (4 - (payload.Length % 4)) % 4);
        var jsonBytes = Convert.FromBase64String(payload);
        var json = System.Text.Encoding.UTF8.GetString(jsonBytes);
        
        Log.Information("DEBUG - Token Claims: {TokenClaims}", json);
        
        // Parse and check issuer (accept both v1 "sts.windows.net" and v2 login endpoints for Gov)
        var jsonDoc = JsonDocument.Parse(json);
        var root = jsonDoc.RootElement;

        if (root.TryGetProperty("iss", out var issuerElement))
        {
            var issuer = issuerElement.GetString() ?? "";

            // Additional helpful claims
            var tenantRegion = root.TryGetProperty("tenant_region_scope", out var tr) ? tr.GetString() : null;
            var aud = root.TryGetProperty("aud", out var audEl) ? audEl.GetString() : null;
            var tid = root.TryGetProperty("tid", out var tidEl) ? tidEl.GetString() : null;

            Log.Information("DEBUG - Token Issuer: {Issuer}, Audience: {Aud}, TenantRegion: {TenantRegion}, Tid: {Tid}", issuer, aud, tenantRegion, tid);

            // Determine if token appears to be from Azure Government Cloud.
            // v2 tokens often have an issuer containing 'microsoftonline.us', but v1-style tokens use 'https://sts.windows.net/{tid}/'.
            var isGov = (tenantRegion != null && tenantRegion.Equals("USGov", StringComparison.OrdinalIgnoreCase))
                        || (aud != null && aud.Contains("graph.microsoft.us", StringComparison.OrdinalIgnoreCase))
                        || issuer.Contains("microsoftonline.us", StringComparison.OrdinalIgnoreCase);

            if (!isGov)
            {
                Log.Error("ERROR: Token issuer does not look like Azure Government Cloud!");
                Log.Error("Issuer: {Issuer}", issuer);
                Log.Error("TenantRegion: {TenantRegion}", tenantRegion);
                Log.Error("If you created the app registration in the Azure Government portal (portal.azure.us), ensure the TenantId/ClientId/Secret configured are from that registration.");
                Environment.Exit(1);
            }
        }
    }
    
    Log.Information("DEBUG - Azure Cloud Configuration: Authority={Authority}, Scopes={Scopes}", 
        authorityHost.ToString(), string.Join(", ", graphScopes));
}
catch (Exception ex)
{
    Log.Warning("DEBUG - Could not log token details: {Error}", ex.Message);
}

// SendFrom checks
try
{

    User? user = await graphClient.Users[config.SendFrom].GetAsync(rc => rc.QueryParameters.Select = new[] { "displayName", "mail", "mailboxSettings" });

    if (user == null)
    {
        Log.Error("The specifed SendFrom address: '{From}' does not exist in the tenant!", config.SendFrom);
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
    Log.Error("The specifed SendFrom address: '{From}' does not exist in the tenant! The Micrsoft Graph error message is: '{Error}'", config.SendFrom, error.Message);
    Environment.Exit(1);
}

// Create email service provider
ServiceProvider emailServiceProvider = new();

// Add the message handler to the service provider
emailServiceProvider.Add(new MessageHandler(graphClient, Log.Logger.ForContext<MessageHandler>(), config.SendFrom));

// Create the server
SmtpServer.SmtpServer smtpServer = new(options, emailServiceProvider);

// Log server start
Log.Information("Smtp server started on {SmtpHost}:{SmtpPort}", config.Smtp.Host, config.Smtp.Port);

// Start the server
await smtpServer.StartAsync(CancellationToken.None);

