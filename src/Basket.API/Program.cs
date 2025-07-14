using ProcfilerLoggerProvider;

var builder = WebApplication.CreateBuilder(args);

builder.AddBasicServiceDefaults();
builder.AddApplicationServices();
builder.Logging.AddProcfilerLogger(s =>
{
    s.LogLevel = LogLevel.Debug;
    s.MessageLogKind = MessageLogKind.OriginalFormat;
});

builder.Services.AddGrpc();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGrpcService<BasketService>();

app.Run();
