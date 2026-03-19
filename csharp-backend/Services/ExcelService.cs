using ClosedXML.Excel;

namespace PacienteRcv.Services;

public sealed class ExcelService
{
    private readonly string _outputFolder;

    public ExcelService(string outputFolder)
    {
        _outputFolder = outputFolder;
        Directory.CreateDirectory(_outputFolder);
    }

    public string ExportSingle(
        Dictionary<string, string> data,
        string? fileName = null,
        IReadOnlyList<string>? orderedColumns = null)
    {
        fileName ??= $"paciente_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        return ExportMany(new List<Dictionary<string, string>> { data }, fileName, orderedColumns);
    }

    public string ExportMany(
        IReadOnlyList<Dictionary<string, string>> rows,
        string fileName,
        IReadOnlyList<string>? orderedColumns = null)
    {
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("No hay datos para exportar");
        }

        var columns = ResolveColumns(rows, orderedColumns);
        if (columns.Count == 0)
        {
            throw new InvalidOperationException("No hay columnas para exportar");
        }

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Datos");

        for (var c = 0; c < columns.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = columns[c];
            sheet.Cell(1, c + 1).Style.Font.Bold = true;
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (var c = 0; c < columns.Count; c++)
            {
                row.TryGetValue(columns[c], out var value);
                sheet.Cell(r + 2, c + 1).Value = value ?? string.Empty;
            }
        }

        sheet.Columns().AdjustToContents();

        var path = Path.Combine(_outputFolder, fileName);
        workbook.SaveAs(path);
        return path;
    }

    private static List<string> ResolveColumns(
        IReadOnlyList<Dictionary<string, string>> rows,
        IReadOnlyList<string>? orderedColumns)
    {
        if (orderedColumns is not null)
        {
            var ordered = new List<string>();
            foreach (var column in orderedColumns)
            {
                if (rows.Any(row => row.ContainsKey(column)))
                {
                    ordered.Add(column);
                }
            }
            return ordered;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var columns = new List<string>();
        foreach (var row in rows)
        {
            foreach (var key in row.Keys)
            {
                if (seen.Add(key))
                {
                    columns.Add(key);
                }
            }
        }

        return columns;
    }
}
