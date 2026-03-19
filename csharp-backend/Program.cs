using PacienteRcv.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(12);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.Name = "PACIENTE_RCV_SESSION";
});

builder.Services.AddControllers();

var appRoot = builder.Environment.ContentRootPath;

builder.Services.AddSingleton(new AppState(appRoot));
builder.Services.AddSingleton(new TemplateService(Path.Combine(appRoot, "templates")));
builder.Services.AddSingleton(new ExtractionService());
builder.Services.AddSingleton(new ExcelService(Path.Combine(appRoot, "output")));
builder.Services.AddSingleton(new OcrService(appRoot));

var app = builder.Build();

app.UseSession();
app.MapControllers();

_ = Task.Run(() =>
{
    using var scope = app.Services.CreateScope();
    var ocr = scope.ServiceProvider.GetRequiredService<OcrService>();
    ocr.TryEnsureInitialized(out _);
});

app.Run("http://0.0.0.0:5000");
