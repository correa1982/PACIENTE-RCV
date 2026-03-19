using System.Globalization;
using System.Text.RegularExpressions;

namespace PacienteRcv.Services;

public sealed class ExtractionService
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex AgeLabelRegex = new(@"\b(?:edad|edac|edao|edqd|edod)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AgeNumberRegex = new(@"\b(\d{1,3})\b", RegexOptions.Compiled);
    private static readonly Regex AgeYearsRegex = new(@"\b(\d{1,3})\s*(?:a(?:ñ|n|fi|f|h)?os?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NameTagRegex = new(@"\b(?:nombre|paciente)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DocumentTagRegex = new(@"^(?:cc|rc|c\.c\.?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LettersRegex = new(@"[A-Za-zÁÉÍÓÚÜÑáéíóúüñ]", RegexOptions.Compiled);
    private static readonly Regex WordBlocksRegex = new(@"[A-Za-zÁÉÍÓÚÜÑáéíóúüñ]{2,}", RegexOptions.Compiled);

    private static readonly string[] UiKeywords =
    {
        "sube la imagen", "cargar imagen", "haz clic", "arrastra",
        "procesar", "limpiar", "datos extraidos", "datos extraídos",
        "ctrl+v", "se mostrarán", "se mostraran", "descargar", "ver todos",
        "administrador", "ocr de pacientes", "extrayendo datos", "no hay imagen",
        "previsualización", "previsualizacion", "o pega", "upload", "subir imagen"
    };

    public bool IsUiLine(string line)
    {
        var value = NormalizeLine(line).ToLowerInvariant();
        if (value.Length < 2)
        {
            return true;
        }

        return UiKeywords.Any(keyword => value.Contains(keyword, StringComparison.Ordinal));
    }

    public Dictionary<string, string> ExtractData(string text)
    {
        var datos = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Nombre"] = string.Empty,
            ["RC"] = string.Empty,
            ["Edad"] = string.Empty,
            ["Fecha Nacimiento"] = string.Empty,
            ["ID Atención"] = string.Empty,
            ["Especialidad"] = string.Empty,
            ["Sexo Biológico"] = string.Empty,
            ["Diagnóstico"] = string.Empty,
            ["Aseguradora"] = string.Empty,
            ["Procedimiento"] = string.Empty,
            ["Cama"] = string.Empty,
        };

        var edadDetectada = ExtractAgeFromLines(text);
        if (!string.IsNullOrWhiteSpace(edadDetectada))
        {
            datos["Edad"] = edadDetectada;
        }

        var nombreDetectado = ExtractNameFromLines(text);
        if (string.IsNullOrWhiteSpace(nombreDetectado))
        {
            nombreDetectado = ExtractNameByDocumentContext(text);
        }
        if (!string.IsNullOrWhiteSpace(nombreDetectado))
        {
            datos["Nombre"] = nombreDetectado;
        }

        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var patrones = new Dictionary<string, Regex>(StringComparer.Ordinal)
        {
            ["Nombre"] = new Regex(@"(?:PRUEBAS\s+SISTEMAS|Paciente|Nombre)\s*[,:'""*\s]+(.+?)(?=\n|CC|RC|C\.C|$)", options | RegexOptions.Singleline),
            ["RC"] = new Regex(@"(?:CC|RC|C\.C\.?)\s*[:\s]*(\d[\d\s\.\-]{4,20})", options),
            ["Edad"] = new Regex(@"(?:Edad|Edac|Edao)\s*[:\-]?\s*(\d{1,3}\s*(?:a(?:ñ|n|fi|f|h)?os?)?)", options),
            ["Fecha Nacimiento"] = new Regex(@"Fecha\s*(?:de\s*)?[Nn]ac(?:imiento)?\s*[:\s]*([\d]{1,2}[/\-\.][\d]{1,2}[/\-\.][\d]{2,4})", options),
            ["ID Atención"] = new Regex(@"[Ii]d\s*[:\s]*[Aa]tenci[oó]n\s*[:\s]*(\d+)", options),
            ["Especialidad"] = new Regex(@"[Ee]specialidad\s*[:\s]+(.+?)(?=\n|[Ss]exo|$)", options | RegexOptions.Singleline),
            ["Sexo Biológico"] = new Regex(@"[Ss]exo\s*[Bb]iol[oó]gico\s*[:\s]*(\w+)", options),
            ["Diagnóstico"] = new Regex(@"[Dd]iagn[oó]s?tico\s*[:\s]*(.+?)(?=\n|[Aa]seguradora|$)", options | RegexOptions.Singleline),
            ["Aseguradora"] = new Regex(@"[Aa]seguradora\s*[:\s]*(.+?)(?=\n|[Pp]rocedimiento|$)", options | RegexOptions.Singleline),
            ["Procedimiento"] = new Regex(@"[Pp]rocedimiento\s*[:\s]*(.+?)(?=\n|[Cc]ama|$)", options | RegexOptions.Singleline),
            ["Cama"] = new Regex(@"[Cc]ama\s*[:\s]*([\w\d]{2,12})", options),
        };

        foreach (var (campo, regex) in patrones)
        {
            if (campo == "Nombre" && !string.IsNullOrWhiteSpace(datos["Nombre"]))
            {
                continue;
            }

            if (campo == "Edad" && !string.IsNullOrWhiteSpace(datos["Edad"]))
            {
                continue;
            }

            var match = regex.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var valor = NormalizeLine(match.Groups[1].Value);
            datos[campo] = valor;
        }

        return datos;
    }

    private static string ExtractAgeFromLines(string text)
    {
        var lines = SplitLines(text);
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        for (var idx = 0; idx < lines.Count; idx++)
        {
            var candidates = new List<string> { lines[idx] };
            if (idx + 1 < lines.Count)
            {
                candidates.Add(lines[idx + 1]);
            }

            if (AgeLabelRegex.IsMatch(lines[idx]))
            {
                foreach (var candidate in candidates)
                {
                    var yearsMatch = AgeYearsRegex.Match(candidate);
                    if (yearsMatch.Success)
                    {
                        return FormatAge(yearsMatch.Groups[1].Value);
                    }

                    var numberMatch = AgeNumberRegex.Match(candidate);
                    if (numberMatch.Success)
                    {
                        return FormatAge(numberMatch.Groups[1].Value);
                    }
                }
            }
        }

        foreach (var line in lines)
        {
            var yearsMatch = AgeYearsRegex.Match(line);
            if (yearsMatch.Success)
            {
                return FormatAge(yearsMatch.Groups[1].Value);
            }
        }

        return string.Empty;
    }

    private static string ExtractNameFromLines(string text)
    {
        var lines = SplitLines(text);
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        for (var idx = 0; idx < lines.Count; idx++)
        {
            var line = lines[idx];
            if (!NameTagRegex.IsMatch(line))
            {
                continue;
            }

            var sameLine = Regex.Match(line, @"(?:nombre|paciente)\s*[:\-]?\s*(.+)$", RegexOptions.IgnoreCase);
            if (sameLine.Success)
            {
                var nombre = CleanName(sameLine.Groups[1].Value);
                if (IsValidName(nombre))
                {
                    return nombre;
                }
            }

            if (idx + 1 < lines.Count)
            {
                var nextLine = CleanName(lines[idx + 1]);
                if (IsValidName(nextLine))
                {
                    return nextLine;
                }
            }
        }

        var indexDoc = lines.FindIndex(line => DocumentTagRegex.IsMatch(line));
        var candidates = indexDoc >= 0
            ? lines.Take(indexDoc).ToList()
            : lines.Where(line => !IsFieldLine(line)).ToList();

        foreach (var candidate in candidates)
        {
            var nombre = CleanName(candidate);
            if (IsValidName(nombre))
            {
                return nombre;
            }
        }

        return string.Empty;
    }

    private static string ExtractNameByDocumentContext(string text)
    {
        var lines = SplitLines(text);
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var documentRegex = new Regex(@"\b(?:cc|rc|c\.c\.?)\b", RegexOptions.IgnoreCase);
        var cutRegex = new Regex(
            @"\b(?:cc|rc|c\.c\.?|edad|edac|fecha|id|especialidad|sexo|diagn[oó]stico|aseguradora|procedimiento|cama)\b",
            RegexOptions.IgnoreCase);

        var idxDoc = lines.FindIndex(line => documentRegex.IsMatch(line));
        if (idxDoc < 0)
        {
            return string.Empty;
        }

        var start = Math.Max(0, idxDoc - 4);
        for (var idx = idxDoc - 1; idx >= start; idx--)
        {
            var candidate = CleanName(lines[idx]);
            var split = cutRegex.Split(candidate, 2);
            candidate = split.FirstOrDefault()?.Trim(" -,:;|\"'".ToCharArray()) ?? string.Empty;

            if (IsValidName(candidate, allowSingleBlock: true))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static bool IsValidName(string value, bool allowSingleBlock = false)
    {
        var nombre = CleanName(value);
        if (string.IsNullOrWhiteSpace(nombre))
        {
            return false;
        }

        if (nombre.Length is < 5 or > 120)
        {
            return false;
        }

        if (IsFieldLine(nombre))
        {
            return false;
        }

        if (!LettersRegex.IsMatch(nombre))
        {
            return false;
        }

        if (nombre.Any(char.IsDigit))
        {
            return false;
        }

        var blocks = WordBlocksRegex.Matches(nombre).Select(m => m.Value).ToList();
        if (blocks.Count < 2)
        {
            if (!(allowSingleBlock && string.Concat(blocks).Length >= 10))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFieldLine(string line)
    {
        var value = NormalizeLine(line).ToLowerInvariant();
        var prefixes = new[]
        {
            "cc", "rc", "c.c", "edad", "fecha", "id", "especialidad",
            "sexo", "diagnostico", "diagnóstico", "aseguradora",
            "procedimiento", "cama", "nombre", "paciente",
            "tipo de documento", "documento", "atencion", "atención"
        };

        return prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string FormatAge(string value)
    {
        var match = Regex.Match(value ?? string.Empty, @"\d{1,3}");
        if (!match.Success)
        {
            return string.Empty;
        }

        return $"{int.Parse(match.Value, CultureInfo.InvariantCulture)} años";
    }

    private static List<string> SplitLines(string text)
    {
        return text
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeLine)
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static string CleanName(string value)
    {
        var normalized = NormalizeLine(value);
        normalized = normalized.Trim(" -,:;|\"'".ToCharArray());
        return WhitespaceRegex.Replace(normalized, " ").Trim();
    }

    private static string NormalizeLine(string value)
    {
        return WhitespaceRegex.Replace(value ?? string.Empty, " ").Trim();
    }
}
