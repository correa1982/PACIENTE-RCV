using Microsoft.AspNetCore.Mvc;
using PacienteRcv.Infrastructure;
using PacienteRcv.Services;
using static PacienteRcv.Infrastructure.RecordUtils;

namespace PacienteRcv.Controllers;

[Route("")]
public sealed class ProcessingController : ControllerBase
{
    private readonly AppState _state;
    private readonly OcrService _ocr;
    private readonly ExtractionService _extraction;
    private readonly ExcelService _excel;
    private readonly ILogger<ProcessingController> _logger;

    public ProcessingController(
        AppState state,
        OcrService ocr,
        ExtractionService extraction,
        ExcelService excel,
        ILogger<ProcessingController> logger)
    {
        _state = state;
        _ocr = ocr;
        _extraction = extraction;
        _excel = excel;
        _logger = logger;
    }

    [HttpPost("guardar-datos")]
    public async Task<IActionResult> GuardarDatos()
    {
        try
        {
            var datosEntrada = await RequestJsonReader.ReadDictionaryAsync(Request);
            if (datosEntrada.Count == 0)
            {
                return StatusCode(400, new { error = "Nombre o RC es obligatorio" });
            }

            var datos = AppState.NormalizeRecordForStorage(datosEntrada);
            if (IsNullOrWhite(datos, "Nombre") && IsNullOrWhite(datos, "RC"))
            {
                return StatusCode(400, new { error = "Nombre o RC es obligatorio" });
            }

            var timestampObjetivo = GetOrEmpty(datos, "timestamp").Trim();
            var idx = _state.FindByTimestamp(timestampObjetivo);

            if (idx >= 0)
            {
                var registroActual = _state.GetAt(idx)!;
                foreach (var kv in datos)
                {
                    if (!string.Equals(kv.Key, "timestamp", StringComparison.Ordinal))
                    {
                        registroActual[kv.Key] = kv.Value;
                    }
                }

                registroActual["timestamp"] = timestampObjetivo;
                _state.SetAt(idx, registroActual);
                var registroGuardado = _state.GetAt(idx) ?? registroActual;

                _logger.LogInformation("Datos actualizados: {Nombre}", GetOrEmpty(registroGuardado, "Nombre"));

                try
                {
                    _excel.ExportSingle(registroGuardado);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error guardando Excel individual");
                }

                var payload = ToObjectDictionary(registroGuardado);
                payload["updated"] = true;
                return Ok(payload);
            }

            if (_state.IsRecentDuplicate(datos))
            {
                var existente = _state.GetAt(0) ?? new Dictionary<string, string>(StringComparer.Ordinal);
                var payload = ToObjectDictionary(existente);
                payload["duplicado"] = true;
                _logger.LogInformation("Registro duplicado detectado en guardado manual");
                return Ok(payload);
            }

            datos["timestamp"] = NowTimestamp();
            _state.InsertFirst(datos);
            var registroGuardadoNuevo = _state.GetAt(0) ?? datos;

            _logger.LogInformation("Datos guardados manualmente: {Nombre}", GetOrEmpty(registroGuardadoNuevo, "Nombre"));

            try
            {
                _excel.ExportSingle(registroGuardadoNuevo);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error guardando Excel individual");
            }

            return StatusCode(201, registroGuardadoNuevo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en /guardar-datos");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("procesar")]
    public async Task<IActionResult> Procesar()
    {
        try
        {
            if (!Request.HasFormContentType)
            {
                return StatusCode(400, new { error = "No se encontró imagen" });
            }

            var form = await Request.ReadFormAsync();
            var archivo = form.Files.GetFile("imagen");
            if (archivo is null || archivo.Length == 0)
            {
                return StatusCode(400, new { error = "No se encontró imagen" });
            }

            await using var ms = new MemoryStream();
            await archivo.CopyToAsync(ms);
            var imageBytes = ms.ToArray();

            if (!_ocr.TryEnsureInitialized(out var ocrError))
            {
                return StatusCode(500, new { error = $"OCR no disponible: {ocrError}" });
            }

            Models.OcrCombinedResult ocrResult;
            try
            {
                ocrResult = _ocr.ExtractCombinedLines(imageBytes, _extraction.IsUiLine);
            }
            catch (ArgumentException ex)
            {
                return StatusCode(400, new { error = ex.Message });
            }

            var texto = string.Join("\n", ocrResult.Lines);
            var datosExtraidos = _extraction.ExtractData(texto);
            var datos = AppState.NormalizeRecordForStorage(datosExtraidos);

            if (_state.IsRecentDuplicate(datos))
            {
                _logger.LogInformation("OCR duplicado reciente: se evita inserción duplicada");
                var existente = _state.GetAt(0) ?? new Dictionary<string, string>(StringComparer.Ordinal);
                var payload = ToObjectDictionary(existente);
                payload["duplicado"] = true;
                return Ok(payload);
            }

            datos["timestamp"] = NowTimestamp();
            _state.InsertFirst(datos);
            var registroGuardado = _state.GetAt(0) ?? datos;

            try
            {
                _excel.ExportSingle(registroGuardado);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error guardando Excel individual");
            }

            return StatusCode(201, registroGuardado);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en /procesar");
            return StatusCode(500, new { error = $"Error al procesar: {ex.Message}" });
        }
    }

    [HttpPost("debug/text")]
    public async Task<IActionResult> DebugText()
    {
        if (!Request.HasFormContentType)
        {
            return StatusCode(400, new { error = "No se encontró imagen" });
        }

        var form = await Request.ReadFormAsync();
        var archivo = form.Files.GetFile("imagen");
        if (archivo is null || archivo.Length == 0)
        {
            return StatusCode(400, new { error = "No se encontró imagen" });
        }

        await using var ms = new MemoryStream();
        await archivo.CopyToAsync(ms);
        var imageBytes = ms.ToArray();

        if (!_ocr.TryEnsureInitialized(out var ocrError))
        {
            return StatusCode(500, new { error = $"OCR no disponible: {ocrError}" });
        }

        Models.OcrCombinedResult ocrResult;
        try
        {
            ocrResult = _ocr.ExtractCombinedLines(imageBytes, _ => false);
        }
        catch (ArgumentException ex)
        {
            return StatusCode(400, new { error = ex.Message });
        }

        var texto = string.Join("\n", ocrResult.Lines);
        return Ok(new
        {
            imagen_shape = ocrResult.ImageShape,
            texto_extraido = texto,
            texto_length = texto.Length,
            lineas = ocrResult.Lines
        });
    }
}
