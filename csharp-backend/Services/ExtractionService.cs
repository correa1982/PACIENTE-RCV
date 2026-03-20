using System.Globalization;
using System.Text;
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
    private static readonly Regex DiagnosticoTagRegex = new(@"\b(?:diagn[o0]s?t(?:i|1|l)?co|diag|dx)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DiagnosticoInlineRegex = new(@"(?ix)(?:diagn[o0]s?t(?:i|1|l)?co|diag|dx)\s*[:\-]?\s*(.+)$", RegexOptions.Compiled);
    private static readonly Regex LettersRegex = new(@"[A-Za-zÁÉÍÓÚÜÑáéíóúüñ]", RegexOptions.Compiled);
    private static readonly Regex WordBlocksRegex = new(@"[A-Za-zÁÉÍÓÚÜÑáéíóúüñ]{2,}", RegexOptions.Compiled);

    // FIX Bug 4: regexes de patrones y contexto de documento como campos estáticos,
    // para no compilarlos en cada llamada a ExtractData / ExtractNameByDocumentContext.
    private const RegexOptions DefaultOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Dictionary<string, Regex> Patrones = new(StringComparer.Ordinal)
    {
        ["Nombre"] = new Regex(@"(?:PRUEBAS\s+SISTEMAS|Paciente|Nombre)\s*[,:'""*\s]+(.+?)(?=\n|CC|RC|C\.C|$)", DefaultOptions | RegexOptions.Singleline),
        ["RC"] = new Regex(@"(?:CC|RC|C\.C\.?)\s*[:\s]*(\d[\d\s\.\-]{4,20})", DefaultOptions),
        ["Edad"] = new Regex(@"(?:Edad|Edac|Edao)\s*[:\-]?\s*(\d{1,3}\s*(?:a(?:ñ|n|fi|f|h)?os?)?)", DefaultOptions),
        ["Fecha Nacimiento"] = new Regex(@"Fecha\s*(?:de\s*)?[Nn]ac(?:imiento)?\s*[:\s]*([\d]{1,2}[/\-\.][\d]{1,2}[/\-\.][\d]{2,4})", DefaultOptions),
        ["ID Atención"] = new Regex(@"[Ii]d\s*[:\s]*[Aa]tenci[oó]n\s*[:\s]*(\d+)", DefaultOptions),
        ["Especialidad"] = new Regex(@"[Ee]specialidad\s*[:\s]+(.+?)(?=\n|[Ss]exo|$)", DefaultOptions | RegexOptions.Singleline),
        ["Sexo Biológico"] = new Regex(@"[Ss]exo\s*[Bb]iol[oó]gico\s*[:\s]*(\w+)", DefaultOptions),
        ["Diagnóstico"] = new Regex(@"[Dd]iagn[oó]s?tico\s*[:\s]*(.+?)(?=\n|[Aa]seguradora|[Pp]rocedimiento|[Cc]ama|$)", DefaultOptions | RegexOptions.Singleline),
        ["Aseguradora"] = new Regex(@"[Aa]seguradora\s*[:\s]*(.+?)(?=\n|[Pp]rocedimiento|$)", DefaultOptions | RegexOptions.Singleline),
        ["Procedimiento"] = new Regex(@"[Pp]rocedimiento\s*[:\s]*(.+?)(?=\n|[Cc]ama|$)", DefaultOptions | RegexOptions.Singleline),
    };

    // FIX Bug 4: también estáticos para ExtractNameByDocumentContext y CleanDiagnosticoValue.
    private static readonly Regex DiagnosticoStopWordsRegex = new(
        @"\b(?:aseguradora|procedimiento|cama|especialidad|sexo(?:\s+biologico)?|id\s*atenci[oó]n|fecha\s*(?:de\s*)?nac(?:imiento)?|tipo\s+de\s+documento|documento)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DocumentContextRegex = new(@"\b(?:cc|rc|c\.c\.?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NameCutRegex = new(
        @"\b(?:cc|rc|c\.c\.?|edad|edac|fecha|id|especialidad|sexo|diagn[oó]stico|aseguradora|procedimiento|cama)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            ["Número Documento"] = string.Empty,
            ["Tipo de Documento"] = string.Empty,
            ["Edad"] = string.Empty,
            ["Fecha Nacimiento"] = string.Empty,
            ["ID Atención"] = string.Empty,
            ["Especialidad"] = string.Empty,
            ["Sexo Biológico"] = string.Empty,
            ["Diagnóstico"] = string.Empty,
            ["Aseguradora"] = string.Empty,
            ["Procedimiento"] = string.Empty,
        };

        var edadDetectada = ExtractAgeFromLines(text);
        if (!string.IsNullOrWhiteSpace(edadDetectada))
        {
            datos["Edad"] = edadDetectada;
        }

        var (tipoDocumento, numeroDocumento) = ExtractDocumentFromLines(text);
        if (!string.IsNullOrWhiteSpace(numeroDocumento))
        {
            // FIX Bug 6: eliminado datos["RC"] = numeroDocumento — el número se guarda
            // bajo la clave semánticamente correcta "Número Documento".
            datos["Número Documento"] = numeroDocumento;
        }
        if (!string.IsNullOrWhiteSpace(tipoDocumento))
        {
            datos["Tipo de Documento"] = tipoDocumento;
        }
        else if (!string.IsNullOrWhiteSpace(numeroDocumento))
        {
            datos["Tipo de Documento"] = "CC";
        }

        var atencionDetectada = ExtractAtencionFromLines(text);
        if (!string.IsNullOrWhiteSpace(atencionDetectada))
        {
            datos["ID Atención"] = atencionDetectada;
        }

        var diagnosticoDetectado = ExtractDiagnosticoFromLines(text);
        if (!string.IsNullOrWhiteSpace(diagnosticoDetectado))
        {
            datos["Diagnóstico"] = diagnosticoDetectado;
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

        // FIX Bug 4: usar el diccionario estático Patrones en lugar de recrearlo aquí.
        foreach (var (campo, regex) in Patrones)
        {
            if (campo == "Nombre" && !string.IsNullOrWhiteSpace(datos["Nombre"]))
                continue;

            if (campo == "Edad" && !string.IsNullOrWhiteSpace(datos["Edad"]))
                continue;

            // FIX Bug 6: clave de guarda es "Número Documento", no "RC".
            if (campo == "RC" && !string.IsNullOrWhiteSpace(datos["Número Documento"]))
                continue;

            if (campo == "ID Atención" && !string.IsNullOrWhiteSpace(datos["ID Atención"]))
                continue;

            if (campo == "Diagnóstico" && !string.IsNullOrWhiteSpace(datos["Diagnóstico"]))
                continue;

            var match = regex.Match(text);
            if (!match.Success)
                continue;

            var valor = NormalizeLine(match.Groups[1].Value);

            // FIX Bug 6: cuando el patrón "RC" coincide, guardar en "Número Documento".
            var claveDestino = campo == "RC" ? "Número Documento" : campo;
            datos[claveDestino] = valor;
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

    private static string ExtractAtencionFromLines(string text)
    {
        var lines = SplitLines(text);
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        for (var idx = 0; idx < lines.Count; idx++)
        {
            var normalized = NormalizeForOcrComparison(lines[idx]);
            var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);

            var hasAtencion = compact.Contains("atencion", StringComparison.Ordinal);
            var hasId = compact.Contains("id", StringComparison.Ordinal) ||
                        compact.Contains("ld", StringComparison.Ordinal) ||
                        compact.Contains("1d", StringComparison.Ordinal);

            if (hasAtencion || (hasId && compact.Contains("atenc", StringComparison.Ordinal)))
            {
                var candidates = new List<string> { lines[idx] };
                if (idx + 1 < lines.Count)
                {
                    candidates.Add(lines[idx + 1]);
                }

                foreach (var candidate in candidates)
                {
                    var numero = ExtractNumericCandidate(candidate);
                    if (!string.IsNullOrWhiteSpace(numero))
                    {
                        return numero;
                    }
                }
            }
        }

        var globalMatch = Regex.Match(
            text ?? string.Empty,
            @"(?:\b(?:id|ld|1d)\b[\s:;,\-]{0,6}(?:aten\w+)?[\s:;,\-]{0,6})(\d[\d\s\-]{3,20}\d)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (globalMatch.Success)
        {
            var numero = ExtractNumericCandidate(globalMatch.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(numero))
            {
                return numero;
            }
        }

        foreach (var line in lines)
        {
            var normalized = NormalizeForOcrComparison(line).Replace(" ", string.Empty, StringComparison.Ordinal);
            if (!normalized.Contains("atencion", StringComparison.Ordinal))
            {
                continue;
            }

            var numero = ExtractNumericCandidate(line);
            if (!string.IsNullOrWhiteSpace(numero))
            {
                return numero;
            }
        }

        return string.Empty;
    }

    private static (string Tipo, string Numero) ExtractDocumentFromLines(string text)
    {
        var lines = SplitLines(text);
        if (lines.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        for (var idx = 0; idx < lines.Count; idx++)
        {
            var line = lines[idx];
            var normalized = NormalizeForOcrComparison(line);
            var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);

            // FIX Bug 5: eliminado Contains("doc") — demasiado genérico.
            var hasDocLabel = compact.Contains("documento", StringComparison.Ordinal) ||
                              compact.Contains("identificacion", StringComparison.Ordinal) ||
                              compact.Contains("identidad", StringComparison.Ordinal);

            var tipoLinea = NormalizeDocumentType(line);
            if (string.IsNullOrWhiteSpace(tipoLinea))
            {
                tipoLinea = NormalizeDocumentType(normalized);
            }

            if (!hasDocLabel && string.IsNullOrWhiteSpace(tipoLinea))
            {
                continue;
            }

            var candidates = new List<string> { line };
            if (idx + 1 < lines.Count)
            {
                candidates.Add(lines[idx + 1]);
            }
            if (idx + 2 < lines.Count)
            {
                candidates.Add(lines[idx + 2]);
            }

            foreach (var candidate in candidates)
            {
                var numero = ExtractNumericCandidate(candidate);
                if (string.IsNullOrWhiteSpace(numero))
                {
                    continue;
                }

                var tipo = tipoLinea;
                if (string.IsNullOrWhiteSpace(tipo))
                {
                    tipo = NormalizeDocumentType(candidate);
                }

                return (tipo, numero);
            }
        }

        var tipoNumeroGlobal = Regex.Match(
            text ?? string.Empty,
            @"(?ix)
            (?:
                (c\.?\s*c\.?|cc|c0|co|r\.?\s*c\.?|rc|t\.?\s*i\.?|ti|t1|c\.?\s*e\.?|ce|n\.?\s*i\.?\s*t\.?|nit|pasaporte|pa)
            )
            [\s:;,#\-]{0,8}
            (\d[\d\s\-\.]{3,22}\d)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (tipoNumeroGlobal.Success)
        {
            var tipo = NormalizeDocumentType(tipoNumeroGlobal.Groups[1].Value);
            var numero = ExtractNumericCandidate(tipoNumeroGlobal.Groups[2].Value);
            if (!string.IsNullOrWhiteSpace(numero))
            {
                return (tipo, numero);
            }
        }

        var docNumeroGlobal = Regex.Match(
            text ?? string.Empty,
            @"(?ix)
            (?:documento|identificacion|identidad)
            [\s:;,#\-]{0,10}
            (\d[\d\s\-\.]{3,22}\d)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (docNumeroGlobal.Success)
        {
            var numero = ExtractNumericCandidate(docNumeroGlobal.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(numero))
            {
                return ("CC", numero);
            }
        }

        return (string.Empty, string.Empty);
    }

    private static string ExtractDiagnosticoFromLines(string text)
    {
        var lines = SplitLines(text);
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        for (var idx = 0; idx < lines.Count; idx++)
        {
            var line = lines[idx];
            var normalized = NormalizeForOcrComparison(line);
            if (!DiagnosticoTagRegex.IsMatch(normalized))
            {
                continue;
            }

            var parts = new List<string>();

            var inlineMatch = DiagnosticoInlineRegex.Match(line);
            if (inlineMatch.Success)
            {
                var inline = CleanDiagnosticoValue(inlineMatch.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(inline))
                {
                    parts.Add(inline);
                }
            }
            else
            {
                var afterTag = Regex.Replace(
                    line,
                    @"(?ix)^.*?\b(?:diagn[o0]s?t(?:i|1|l)?co|diag|dx)\b\s*[:\-]?\s*",
                    string.Empty,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();

                var cleanedAfterTag = CleanDiagnosticoValue(afterTag);
                if (!string.IsNullOrWhiteSpace(cleanedAfterTag))
                {
                    parts.Add(cleanedAfterTag);
                }
            }

            for (var j = idx + 1; j < lines.Count && j <= idx + 2; j++)
            {
                var next = lines[j];
                if (IsDiagnosticStopLine(next) || IsFieldLine(next))
                {
                    break;
                }

                parts.Add(next);
            }

            var joined = CleanDiagnosticoValue(string.Join(" ", parts));
            if (!string.IsNullOrWhiteSpace(joined))
            {
                return joined;
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

        // FIX Bug 4: usar instancias estáticas DocumentContextRegex y NameCutRegex.
        var idxDoc = lines.FindIndex(line => DocumentContextRegex.IsMatch(line));
        if (idxDoc < 0)
        {
            return string.Empty;
        }

        var start = Math.Max(0, idxDoc - 4);
        for (var idx = idxDoc - 1; idx >= start; idx--)
        {
            var candidate = CleanName(lines[idx]);
            var split = NameCutRegex.Split(candidate, 2);
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
            "cc", "rc", "c.c", "edad", "fecha",
            // FIX Bug 2: "id" reemplazado por prefijos más específicos para evitar
            // descartar palabras como "idóneo" o "identificación del médico".
            "id atención", "id atencion",
            "especialidad", "sexo", "diagnostico", "diagnóstico",
            "aseguradora", "procedimiento", "cama", "nombre", "paciente",
            "tipo de documento", "documento", "atencion", "atención"
        };

        return prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool IsDiagnosticStopLine(string value)
    {
        var compact = NormalizeForOcrComparison(value).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(compact))
        {
            return true;
        }

        return compact.Contains("aseguradora", StringComparison.Ordinal) ||
               compact.Contains("procedimiento", StringComparison.Ordinal) ||
               compact.Contains("cama", StringComparison.Ordinal) ||
               compact.Contains("especialidad", StringComparison.Ordinal) ||
               compact.Contains("sexobiologico", StringComparison.Ordinal) ||
               compact.StartsWith("sexo", StringComparison.Ordinal) ||
               compact.StartsWith("idatencion", StringComparison.Ordinal) ||
               compact.StartsWith("atencion", StringComparison.Ordinal) ||
               compact.StartsWith("tipodedocumento", StringComparison.Ordinal) ||
               compact.StartsWith("documento", StringComparison.Ordinal) ||
               compact.StartsWith("edad", StringComparison.Ordinal) ||
               compact.StartsWith("fecha", StringComparison.Ordinal) ||
               compact.StartsWith("cc", StringComparison.Ordinal) ||
               compact.StartsWith("rc", StringComparison.Ordinal);
    }

    private static string CleanDiagnosticoValue(string value)
    {
        var normalized = NormalizeLine(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        // FIX: Regex.Split estático no tiene sobrecarga (string, string, int).
        // Se usa la instancia estática con el método de instancia Split(string, int).
        var cut = DiagnosticoStopWordsRegex.Split(normalized, 2);

        normalized = (cut.FirstOrDefault() ?? string.Empty).Trim(" -,:;|\"'".ToCharArray());
        normalized = NormalizeLine(normalized);

        if (normalized.Length < 3)
        {
            return string.Empty;
        }

        if (normalized.All(ch => !char.IsLetter(ch)))
        {
            return string.Empty;
        }

        return normalized;
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
        // FIX Bug 3: manejar text null con el operador ?? para evitar NullReferenceException.
        return (text ?? string.Empty)
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

    private static string NormalizeForOcrComparison(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }

        var normalized = builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        normalized = normalized
            .Replace('0', 'o')
            .Replace('1', 'i')
            .Replace('5', 's')
            .Replace('|', 'i');

        return WhitespaceRegex.Replace(normalized, " ").Trim();
    }

    private static string NormalizeDocumentType(string value)
    {
        var normalized = NormalizeForOcrComparison(value).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (normalized.Contains("pasaporte", StringComparison.Ordinal) || normalized == "pa")
        {
            return "PASAPORTE";
        }

        if (normalized.Contains("nit", StringComparison.Ordinal))
        {
            return "NIT";
        }

        // FIX Bug 1: usar comparación exacta en lugar de Contains para evitar
        // falsos positivos con palabras que contengan "rc", "cc", "ti", "ce" internamente.
        if (normalized is "rc" or "r.c" or "r.c.")
        {
            return "RC";
        }

        if (normalized is "ti" or "t.i" or "t.i." or "t1" or "t.1")
        {
            return "TI";
        }

        if (normalized is "ce" or "c.e" or "c.e.")
        {
            return "CE";
        }

        if (normalized is "cc" or "c.c" or "c.c." or "co" or "c0")
        {
            return "CC";
        }

        return string.Empty;
    }

    private static string ExtractNumericCandidate(string value)
    {
        var match = Regex.Match(value ?? string.Empty, @"\d[\d\s\-]{3,20}\d");
        if (!match.Success)
        {
            return string.Empty;
        }

        var digits = new string(match.Value.Where(char.IsDigit).ToArray());
        if (digits.Length is < 4 or > 14)
        {
            return string.Empty;
        }

        return digits;
    }

    private static string NormalizeLine(string value)
    {
        return WhitespaceRegex.Replace(value ?? string.Empty, " ").Trim();
    }
}
