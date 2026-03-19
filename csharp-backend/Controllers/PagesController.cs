using Microsoft.AspNetCore.Mvc;
using PacienteRcv.Infrastructure;
using PacienteRcv.Services;

namespace PacienteRcv.Controllers;

[Route("")]
public sealed class PagesController : Controller
{
    private readonly TemplateService _templates;
    private readonly AppState _state;
    private readonly OcrService _ocr;

    public PagesController(TemplateService templates, AppState state, OcrService ocr)
    {
        _templates = templates;
        _state = state;
        _ocr = ocr;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        return Content(_templates.GetTemplate("upload.html"), "text/html; charset=utf-8");
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        var ready = _ocr.TryEnsureInitialized(out var error);
        return Json(new
        {
            status = "ok",
            ocr_ready = ready,
            ocr_error = error ?? string.Empty
        });
    }

    [HttpGet("login")]
    public IActionResult Login()
    {
        if (HttpContext.IsAuthenticated())
        {
            return Redirect("/resultados");
        }

        var error = Request.Query["error"].ToString();
        return Content(_templates.RenderLogin(error), "text/html; charset=utf-8");
    }

    [HttpPost("login")]
    public async Task<IActionResult> LoginPost()
    {
        if (HttpContext.IsAuthenticated())
        {
            return Redirect("/resultados");
        }

        var form = await Request.ReadFormAsync();
        var usuario = form["usuario"].ToString().Trim();
        var contrasena = form["contrasena"].ToString();

        if (_state.ValidateLogin(usuario, contrasena))
        {
            HttpContext.Session.SetString("autenticado", "true");
            return Redirect("/resultados");
        }

        var error = Uri.EscapeDataString("Usuario o contraseña incorrectos");
        return Redirect($"/login?error={error}");
    }

    [HttpGet("logout")]
    public IActionResult Logout()
    {
        HttpContext.Session.Clear();
        return Redirect("/login");
    }

    [HttpGet("resultados")]
    public IActionResult Resultados()
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Redirect("/login");
        }

        return Content(_templates.GetTemplate("resultados.html"), "text/html; charset=utf-8");
    }

    [HttpGet("debug")]
    public IActionResult DebugPage()
    {
        return Content(_templates.GetTemplate("debug.html"), "text/html; charset=utf-8");
    }
}
