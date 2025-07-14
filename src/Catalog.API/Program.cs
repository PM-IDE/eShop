using Asp.Versioning.Builder;
using System.Reflection;
using ProcfilerLoggerProvider;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddApplicationServices();
builder.Services.AddProblemDetails();
builder.Logging.AddProcfilerLogger(s =>
{
    s.LogLevel = LogLevel.Debug;
    s.MessageLogKind = MessageLogKind.OriginalFormat;
});

var withApiVersioning = builder.Services.AddApiVersioning();

builder.AddDefaultOpenApi(withApiVersioning);

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseStatusCodePages();

app.MapCatalogApi();

app.UseDefaultOpenApi();
app.Run();
