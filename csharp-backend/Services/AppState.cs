using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using static PacienteRcv.Infrastructure.RecordUtils;

namespace PacienteRcv.Services;

public sealed class AppState
{
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly string[] SignatureFields =
    {
        "Nombre", "RC", "Edad", "Fecha Nacimiento", "ID Atención",
        "Especialidad", "Sexo Biológico", "Diagnóstico",
        "Aseguradora", "Procedimiento", "Cama"
    };

    private static readonly string[] StorageFields =
    {
        "Nombre", "RC", "Edad", "Fecha Nacimiento", "ID Atención",
        "Especialidad", "Sexo Biológico", "Diagnóstico",
        "Aseguradora", "Procedimiento", "Cama",
        "Tipo de Documento", "Número Documento", "timestamp"
    };

    private readonly object _sync = new();
    private readonly string _resultadosFile;
    private List<Dictionary<string, string>> _resultados = new();

    public string AdminUser { get; }
    public string AdminPasswordHash { get; }

    public AppState(string repoRoot)
    {
        _resultadosFile = Path.Combine(repoRoot, "resultados.json");

        var uploads = Path.Combine(repoRoot, "uploads");
        var output = Path.Combine(repoRoot, "output");
        Directory.CreateDirectory(uploads);
        Directory.CreateDirectory(output);

        AdminUser = Environment.GetEnvironmentVariable("ADMIN_USER") ?? "admin";
        var adminPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD") ?? "admin123";
        AdminPasswordHash = HashPassword(adminPassword);

        Load();
    }

    public bool ValidateLogin(string user, string password)
    {
        return string.Equals(user, AdminUser, StringComparison.Ordinal) &&
               string.Equals(HashPassword(password), AdminPasswordHash, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Dictionary<string, string>> GetAll()
    {
        lock (_sync)
        {
            return _resultados.Select(Clone).ToList();
        }
    }

    public Dictionary<string, string>? GetAt(int index)
    {
        lock (_sync)
        {
            if (index < 0 || index >= _resultados.Count)
            {
                return null;
            }

            return Clone(_resultados[index]);
        }
    }

    public void SetAt(int index, Dictionary<string, string> data)
    {
        lock (_sync)
        {
            if (index < 0 || index >= _resultados.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            _resultados[index] = NormalizeRecordForStorage(data);
            SaveLocked();
        }
    }

    public void InsertFirst(Dictionary<string, string> data)
    {
        lock (_sync)
        {
            _resultados.Insert(0, NormalizeRecordForStorage(data));
            SaveLocked();
        }
    }

    public bool DeleteAt(int index)
    {
        lock (_sync)
        {
            if (index < 0 || index >= _resultados.Count)
            {
                return false;
            }

            _resultados.RemoveAt(index);
            SaveLocked();
            return true;
        }
    }

    public bool UpdateAt(int index, Dictionary<string, string> changes)
    {
        lock (_sync)
        {
            if (index < 0 || index >= _resultados.Count)
            {
                return false;
            }

            var item = Clone(_resultados[index]);
            foreach (var kv in changes)
            {
                item[kv.Key] = kv.Value ?? string.Empty;
            }

            _resultados[index] = NormalizeRecordForStorage(item);

            SaveLocked();
            return true;
        }
    }

    public int FindByTimestamp(string timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
        {
            return -1;
        }

        lock (_sync)
        {
            for (var idx = 0; idx < _resultados.Count; idx++)
            {
                if (string.Equals(GetOrEmpty(_resultados[idx], "timestamp"), timestamp, StringComparison.Ordinal))
                {
                    return idx;
                }
            }
        }

        return -1;
    }

    public bool IsRecentDuplicate(Dictionary<string, string> data, int seconds = 120)
    {
        lock (_sync)
        {
            if (_resultados.Count == 0)
            {
                return false;
            }

            var ultimo = _resultados[0];
            if (!string.Equals(Signature(ultimo), Signature(data), StringComparison.Ordinal))
            {
                return false;
            }

            var ts = GetOrEmpty(ultimo, "timestamp");
            if (DateTime.TryParseExact(ts, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var momento))
            {
                return (DateTime.Now - momento).TotalSeconds <= seconds;
            }

            return true;
        }
    }

    public List<Dictionary<string, string>> GetMigratedSerializable()
    {
        lock (_sync)
        {
            var datos = new List<Dictionary<string, string>>(_resultados.Count);
            foreach (var item in _resultados)
            {
                var migrado = MigrateRecord(item);
                var limpio = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in migrado)
                {
                    limpio[kv.Key] = kv.Value ?? string.Empty;
                }
                datos.Add(limpio);
            }
            return datos;
        }
    }

    public static Dictionary<string, string> NormalizeRecordForStorage(Dictionary<string, string> registro)
    {
        var item = Clone(registro);

        var rcValor = GetOrEmpty(item, "RC").Trim();
        var numeroDocumento = GetOrEmpty(item, "Número Documento").Trim();
        var tipoDocumento = GetOrEmpty(item, "Tipo de Documento").Trim();

        if (string.IsNullOrWhiteSpace(rcValor) && !string.IsNullOrWhiteSpace(numeroDocumento))
        {
            rcValor = numeroDocumento;
        }

        if (string.IsNullOrWhiteSpace(numeroDocumento) && !string.IsNullOrWhiteSpace(rcValor))
        {
            numeroDocumento = rcValor;
        }

        if (string.IsNullOrWhiteSpace(tipoDocumento) && !string.IsNullOrWhiteSpace(numeroDocumento))
        {
            tipoDocumento = "RC";
        }

        item["RC"] = rcValor;
        item["Número Documento"] = numeroDocumento;
        item["Tipo de Documento"] = tipoDocumento;

        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var campo in StorageFields)
        {
            normalized[campo] = GetOrEmpty(item, campo);
        }

        return normalized;
    }

    public static Dictionary<string, string> MigrateRecord(Dictionary<string, string> registro)
    {
        var item = NormalizeRecordForStorage(registro);
        var rcValor = GetOrEmpty(item, "RC").Trim();

        if (!string.IsNullOrWhiteSpace(rcValor) && string.IsNullOrWhiteSpace(GetOrEmpty(item, "Número Documento")))
        {
            item["Número Documento"] = rcValor;
        }

        if (!string.IsNullOrWhiteSpace(rcValor) && string.IsNullOrWhiteSpace(GetOrEmpty(item, "Tipo de Documento")))
        {
            item["Tipo de Documento"] = "RC";
        }

        if (!item.ContainsKey("Tipo de Documento"))
        {
            item["Tipo de Documento"] = string.Empty;
        }

        if (!item.ContainsKey("Número Documento"))
        {
            item["Número Documento"] = string.Empty;
        }

        item.Remove("RC");
        return item;
    }

    private void Load()
    {
        if (!File.Exists(_resultadosFile))
        {
            _resultados = new List<Dictionary<string, string>>();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_resultadosFile, Encoding.UTF8));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                _resultados = new List<Dictionary<string, string>>();
                return;
            }

            var cargados = new List<Dictionary<string, string>>();
            var normalizedChanged = false;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in element.EnumerateObject())
                {
                    dict[prop.Name] = JsonElementToString(prop.Value);
                }

                var normalized = NormalizeRecordForStorage(dict);
                if (!DictionaryEquals(dict, normalized))
                {
                    normalizedChanged = true;
                }

                cargados.Add(normalized);
            }

            _resultados = cargados;
            if (normalizedChanged)
            {
                SaveLocked();
            }
        }
        catch
        {
            _resultados = new List<Dictionary<string, string>>();
        }
    }

    private void SaveLocked()
    {
        var json = JsonSerializer.Serialize(_resultados, JsonWriteOptions);
        File.WriteAllText(_resultadosFile, json, Encoding.UTF8);
    }

    private static string Signature(Dictionary<string, string> data)
    {
        var values = SignatureFields.Select(campo => NormalizeText(GetOrEmpty(data, campo)));
        return string.Join("|", values);
    }

    private static string NormalizeText(string value)
    {
        return Regex.Replace(value.Trim(), @"\s+", " ").ToLowerInvariant();
    }

    private static string HashPassword(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string JsonElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => element.ToString()
        };
    }

    private static Dictionary<string, string> Clone(Dictionary<string, string> data)
    {
        return new Dictionary<string, string>(data, StringComparer.Ordinal);
    }

    private static bool DictionaryEquals(Dictionary<string, string> left, Dictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var kv in left)
        {
            if (!right.TryGetValue(kv.Key, out var value))
            {
                return false;
            }

            if (!string.Equals(kv.Value ?? string.Empty, value ?? string.Empty, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
