using WhatsAppApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowBlazorClient",
        builder => builder.WithOrigins("https://localhost:7120") // Replace with your Blazor app's URL and port
                          .AllowAnyMethod()
                          .AllowAnyHeader()
                          .AllowCredentials());
});




//builder.Services.AddSingleton<IWhatsAppService, WhatsAppService>();
builder.Services.AddSingleton<IWhatsAppServiceV2, WhatsAppServiceV2>();

//builder.Services.AddHostedService<WhatsAppHostedService>();
builder.Services.AddHostedService<WhatsAppHostedServiceV2>();


// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseCors("AllowBlazorClient");

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
