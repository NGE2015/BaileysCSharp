using System;
using System.IO;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using WhatsAppApi.Services;
using WhatsAppApi.Middleware;

var builder = WebApplication.CreateBuilder(args);
// Configure logging providers after the builder is created:
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Trace);
// Load configuration from appsettings.json
var configuration = builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true).Build();
string unixSocketPath = configuration["Kestrel:UnixSocketPath"];

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

// Configure HttpClient for CRM API calls
builder.Services.AddHttpClient<IWhatsAppServiceV2, WhatsAppServiceV2>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "WhatsAppAPI/1.0");
});

builder.Services.AddHostedService<WhatsAppHostedServiceV2>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.WebHost.ConfigureKestrel(options =>
{
    if (OperatingSystem.IsLinux())
    {
        // Delete existing Unix socket file if it exists
        if (File.Exists(unixSocketPath))
        {
            File.Delete(unixSocketPath);
            Console.WriteLine($"Deleted existing Unix socket file: {unixSocketPath}");
        }

        // Configure Kestrel to listen on the Unix socket
        options.ListenUnixSocket(unixSocketPath);
        Console.WriteLine($"Listening on Unix socket: {unixSocketPath}");
    }
    else
    {
        // Use standard HTTP URLs for non-Linux environments
        //options.ListenLocalhost(5000);
        //options.ListenLocalhost(5001, listenOptions => listenOptions.UseHttps());
        //Console.WriteLine("Listening on HTTP at http://localhost:5000 and HTTPS at https://localhost:5001");
    }

    // Enable synchronous I/O if needed
    options.AllowSynchronousIO = true;
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

// Add rate limiting middleware before authorization
app.UseMiddleware<RateLimitingMiddleware>();

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();



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
