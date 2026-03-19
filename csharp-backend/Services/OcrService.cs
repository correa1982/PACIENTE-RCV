using OpenCvSharp;
using PacienteRcv.Models;
using System.Text.RegularExpressions;
using Tesseract;

namespace PacienteRcv.Services;

public sealed class OcrService : IDisposable
{
    private readonly object _sync = new();
    private readonly string _repoRoot;
    private TesseractEngine? _engine;

    public OcrService(string repoRoot)
    {
        _repoRoot = repoRoot;
    }

    public bool IsReady => _engine is not null;

    public bool TryEnsureInitialized(out string? error)
    {
        try
        {
            EnsureInitialized();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void EnsureInitialized()
    {
        lock (_sync)
        {
            if (_engine is not null)
            {
                return;
            }

            var tessdataPath = Environment.GetEnvironmentVariable("TESSDATA_PATH");
            if (string.IsNullOrWhiteSpace(tessdataPath))
            {
                var candidates = new[]
                {
                    Path.Combine(_repoRoot, "tessdata"),
                    Path.Combine(_repoRoot, "csharp-backend", "tessdata")
                };

                tessdataPath = candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
            }

            if (!Directory.Exists(tessdataPath))
            {
                throw new InvalidOperationException(
                    $"No se encontró la carpeta tessdata en: {tessdataPath}. " +
                    "Configura TESSDATA_PATH o crea la carpeta con spa.traineddata.");
            }

            var preferredLanguage = Environment.GetEnvironmentVariable("OCR_LANG") ?? "spa";
            var languages = new List<string> { preferredLanguage };
            if (!languages.Contains("spa", StringComparer.OrdinalIgnoreCase))
            {
                languages.Add("spa");
            }
            if (!languages.Contains("eng", StringComparer.OrdinalIgnoreCase))
            {
                languages.Add("eng");
            }

            Exception? lastException = null;
            foreach (var language in languages)
            {
                try
                {
                    _engine = new TesseractEngine(tessdataPath, language, EngineMode.Default);
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            throw new InvalidOperationException(
                "No se pudo inicializar Tesseract con los idiomas configurados.",
                lastException);
        }
    }

    public OcrCombinedResult ExtractCombinedLines(byte[] imageBytes, Func<string, bool> isUiLine)
    {
        EnsureInitialized();

        using var image = Cv2.ImDecode(imageBytes, ImreadModes.Color);
        if (image.Empty())
        {
            throw new ArgumentException("Imagen inválida o formato no soportado");
        }

        var shape = $"({image.Rows}, {image.Cols}, {image.Channels()})";
        var intentos = new List<List<string>>();

        intentos.Add(ReadAttempt(image, isUiLine));

        if (image.Rows > 1)
        {
            var lowerHeight = image.Rows - (image.Rows / 2);
            using var mitadInferior = new Mat(image, new OpenCvSharp.Rect(0, image.Rows / 2, image.Cols, lowerHeight));
            intentos.Add(ReadAttempt(mitadInferior, isUiLine));

            using var mitadSuperior = new Mat(image, new OpenCvSharp.Rect(0, 0, image.Cols, image.Rows / 2));
            intentos.Add(ReadAttempt(mitadSuperior, isUiLine));
        }

        var combinadas = new List<string>();
        var vistas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var lineas in intentos)
        {
            foreach (var linea in lineas)
            {
                var normalizada = NormalizeLine(linea);
                if (normalizada.Length < 2)
                {
                    continue;
                }

                if (vistas.Add(normalizada))
                {
                    combinadas.Add(normalizada);
                }
            }
        }

        return new OcrCombinedResult(shape, combinadas);
    }

    private List<string> ReadAttempt(Mat image, Func<string, bool> isUiLine)
    {
        using var procesada = Preprocess(image);
        var texto = ReadText(procesada);

        var lineas = texto
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeLine)
            .Where(l => l.Length >= 2)
            .Where(l => !isUiLine(l))
            .ToList();

        return lineas;
    }

    private Mat Preprocess(Mat image)
    {
        var h = image.Rows;
        var w = image.Cols;
        var ladoMayor = Math.Max(h, w);

        using var escalada = new Mat();
        if (ladoMayor < 1800)
        {
            var factor = 1800.0 / ladoMayor;
            var newSize = new OpenCvSharp.Size((int)(w * factor), (int)(h * factor));
            Cv2.Resize(image, escalada, newSize, interpolation: InterpolationFlags.Cubic);
        }
        else
        {
            image.CopyTo(escalada);
        }

        using var gris = new Mat();
        Cv2.CvtColor(escalada, gris, ColorConversionCodes.BGR2GRAY);

        var procesada = new Mat();
        Cv2.AdaptiveThreshold(
            gris,
            procesada,
            255,
            AdaptiveThresholdTypes.GaussianC,
            ThresholdTypes.Binary,
            15,
            8);

        return procesada;
    }

    private string ReadText(Mat image)
    {
        lock (_sync)
        {
            if (_engine is null)
            {
                throw new InvalidOperationException("OCR no inicializado");
            }

            var bytes = image.ToBytes(".png");
            using var pix = Pix.LoadFromMemory(bytes);
            using var page = _engine.Process(pix);
            return page.GetText() ?? string.Empty;
        }
    }

    private static string NormalizeLine(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _engine?.Dispose();
            _engine = null;
        }
    }
}
