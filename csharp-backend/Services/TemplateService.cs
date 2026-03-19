using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PacienteRcv.Services;

public sealed class TemplateService
{
    private static readonly Regex LoginErrorBlockRegex = new(
        @"\{\%\s*if error\s*\%\}[\s\S]*?\{\%\s*endif\s*\%\}",
        RegexOptions.Compiled);

    private readonly string _templatesFolder;

    public TemplateService(string templatesFolder)
    {
        _templatesFolder = templatesFolder;
    }

    public string GetTemplate(string fileName)
    {
        var fullPath = Path.Combine(_templatesFolder, fileName);
        if (!File.Exists(fullPath))
        {
            return $"<h1>Plantilla no encontrada: {WebUtility.HtmlEncode(fileName)}</h1>";
        }

        return File.ReadAllText(fullPath, Encoding.UTF8);
    }

    public string RenderLogin(string? error)
    {
        var html = GetTemplate("login.html");
        var bloqueError = string.IsNullOrWhiteSpace(error)
            ? string.Empty
            : $"<div class=\"error\">{WebUtility.HtmlEncode(error)}</div>";

        return LoginErrorBlockRegex.Replace(html, bloqueError);
    }
}
