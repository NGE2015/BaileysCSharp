using System;
using System.IO;
using WhatsAppApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Load configuration
var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
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
});

builder.Services.AddSingleton<IWhatsAppServiceV2, WhatsAppServiceV2>();
builder.Services.AddHostedService<WhatsAppHostedServiceV2>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowBlazorClient");
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Conditional Kestrel configuration based on the platform
if (OperatingSystem.IsLinux())
{
    // Delete the Unix socket file if it exists
    if (File.Exists(unixSocketPath))
    {
        File.Delete(unixSocketPath);
    }

    // Configure Kestrel to use Unix socket
    app.Urls.Clear();
    app.Urls.Add($"unix://{unixSocketPath}");
}
else
{
    // Use standard HTTP URL for development on Windows
    app.Urls.Add("https://localhost:5001");
    app.Urls.Add("http://localhost:5000");
}

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
