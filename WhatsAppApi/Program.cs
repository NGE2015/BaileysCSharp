using System;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using WhatsAppApi.Services;
using WhatsAppApi.Middleware;
using WhatsAppApi.Configuration;
using Serilog;
using Serilog.Events;

// Configure Serilog early for startup logging
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .Enrich.WithEnvironmentName()
    .Enrich.WithProcessId()
    .Enrich.WithThreadId()
    .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/startup-.log", 
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting WhatsApp API application");

    var builder = WebApplication.CreateBuilder(args);

    // Replace default logging with Serilog
    builder.Host.UseSerilog((context, services, configuration) => 
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithEnvironmentName()
            .Enrich.WithProcessId()
            .Enrich.WithThreadId()
            .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: context.Configuration["Logging:File:Path"] ?? "logs/whatsapp-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: context.Configuration.GetValue<int>("Logging:File:RetainedFileCountLimit", 7),
                fileSizeLimitBytes: context.Configuration.GetValue<long>("Logging:File:FileSizeLimitBytes", 10485760),
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
            // SystemdJournal will be handled by systemd service configuration
    });
    // Load configuration from appsettings.json
    var configuration = builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true).Build();
    string unixSocketPath = configuration["Kestrel:UnixSocketPath"];

    // Configure detailed logging options
    builder.Services.Configure<DetailedLoggingOptions>(
        builder.Configuration.GetSection(DetailedLoggingOptions.SectionName));

    // Configure rate limiting options
    builder.Services.Configure<RateLimitingOptions>(
        builder.Configuration.GetSection(RateLimitingOptions.SectionName));

    // Add logging service
    builder.Services.AddSingleton<ILoggingService, LoggingService>();

    // Add services
    builder.Services.AddControllers();
    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowBlazorClient", policyBuilder =>
            policyBuilder.WithOrigins("https://localhost:7120")
                         .AllowAnyMethod()
                         .AllowAnyHeader()
                         .AllowCredentials());
        options.AddPolicy("AllowBlazorClientRubyManager", policyBuilder =>
        policyBuilder.WithOrigins(
            "https://school.rubymanager.app",
            "https://developmentschool.rubymanager.app",
            "https://demoschool.rubymanager.app"
        )
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials());

    });

    // Register WhatsAppServiceV2 as Singleton to maintain session state
    builder.Services.AddSingleton<IWhatsAppServiceV2, WhatsAppServiceV2>();

    // Configure HttpClient for CRM API calls separately
    builder.Services.AddHttpClient<WhatsAppServiceV2>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("User-Agent", "WhatsAppAPI/1.0");
    });

    builder.Services.AddHostedService<WhatsAppHostedServiceV2>();

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    builder.WebHost.ConfigureKestrel(options =>
    {
        // Unix socket only configuration - no HTTP/HTTPS ports
        if (string.IsNullOrEmpty(unixSocketPath))
        {
            throw new InvalidOperationException("Unix socket path is required. Configure Kestrel:UnixSocketPath in appsettings.json");
        }

        // Delete existing Unix socket file if it exists
        if (File.Exists(unixSocketPath))
        {
            File.Delete(unixSocketPath);
            Log.Information("Deleted existing Unix socket file: {UnixSocketPath}", unixSocketPath);
        }

        // Ensure directory exists
        var socketDir = Path.GetDirectoryName(unixSocketPath);
        if (!string.IsNullOrEmpty(socketDir) && !Directory.Exists(socketDir))
        {
            Directory.CreateDirectory(socketDir);
            Log.Information("Created Unix socket directory: {SocketDir}", socketDir);
        }

        // Configure Kestrel to listen ONLY on Unix socket
        options.ListenUnixSocket(unixSocketPath, listenOptions =>
        {
            // Configure Unix socket specific options if needed
            listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
        });
        
        Log.Information("Listening exclusively on Unix socket: {UnixSocketPath}", unixSocketPath);

        // Enable synchronous I/O for compatibility
        options.AllowSynchronousIO = true;
        
        // Unix socket only - no additional configuration needed
    });

    var app = builder.Build();

    // Configure the HTTP request pipeline
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseCors("AllowBlazorClient");
    app.UseCors("AllowBlazorClientRubyManager");

    // Enable static file serving for monitoring dashboard
    app.UseStaticFiles();

    // Add rate limiting middleware before authorization
    app.UseMiddleware<RateLimitingMiddleware>();

    // No HTTPS redirection needed for Unix socket
    // app.UseHttpsRedirection();
    
    app.UseAuthorization();
    app.MapControllers();

    Log.Information("WhatsApp API configured successfully. Starting application...");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.Information("WhatsApp API application shutting down");
    Log.CloseAndFlush();
}



//using WhatsAppApi.Services;

//var builder = WebApplication.CreateBuilder(args);

//// Add services to the container.

//builder.Services.AddControllers();
//// Configure CORS
//builder.Services.AddCors(options =>
//{
//    options.AddPolicy("AllowBlazorClient",
//        builder => builder.WithOrigins("https://localhost:7120") // Replace with your Blazor app's URL and port
//                          .AllowAnyMethod()
//                          .AllowAnyHeader()
//                          .AllowCredentials());
//});




//builder.Services.AddSingleton<IWhatsAppServiceV2, WhatsAppServiceV2>();

//builder.Services.AddHostedService<WhatsAppHostedServiceV2>();


//// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
//builder.Services.AddEndpointsApiExplorer();
//builder.Services.AddSwaggerGen();

//var app = builder.Build();

//// Configure the HTTP request pipeline.
//if (app.Environment.IsDevelopment())
//{
//    app.UseSwagger();
//    app.UseSwaggerUI();
//}
//app.UseCors("AllowBlazorClient");

//app.UseHttpsRedirection();

//app.UseAuthorization();

//app.MapControllers();

//app.Run();
