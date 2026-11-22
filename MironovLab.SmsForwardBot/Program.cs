using System.Globalization;
using Microsoft.Extensions.Hosting.WindowsServices;
using MironovLab.SmsForwardBot.Bot;
using MironovLab.SmsForwardBot.Controllers;

CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = DiscordWebhookController.MaxRequestBodySize);

builder.Services.AddSystemd();
builder.Services.AddWindowsService();
builder.Services.AddSmsForwardBot(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options => options.SuppressMapClientErrors = true);
builder.Services.AddOpenApi();

var app = builder.Build();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapControllers();
app.Run();
