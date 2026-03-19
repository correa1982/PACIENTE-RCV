using Microsoft.AspNetCore.Mvc;
using PacienteRcv.Infrastructure;
using PacienteRcv.Services;
using static PacienteRcv.Infrastructure.RecordUtils;

namespace PacienteRcv.Controllers;

[Route("")]
public sealed class RecordsController : ControllerBase
{
    private readonly AppState _state;
    private readonly ExcelService _excel;

    public RecordsController(AppState state, ExcelService excel)
    {
        _state = state;
        _excel = excel;
    }

    [HttpGet("api/resultados")]
    public IActionResult ApiResultados()
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Redirect("/login");
        }

        return Ok(_state.GetMigratedSerializable());
    }

    [HttpGet("descargar/{idx:int}")]
    public IActionResult Descargar(int idx)
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Redirect("/login");
        }

        var original = _state.GetAt(idx);
        if (original is null)
        {
            return StatusCode(404, new { error = "Índice no válido" });
        }

        var datos = AppState.MigrateRecord(original);
        var columnasOrden = new[]
        {
            "Tipo de Documento", "Número Documento", "Nombre", "ID Atención",
            "Fecha Nacimiento", "Sexo Biológico", "Edad", "Diagnóstico",
            "Especialidad", "Aseguradora", "Procedimiento", "Cama", "timestamp"
        };

        var ts = GetOrEmpty(datos, "timestamp");
        var nombreExcel = $"paciente_{SanitizeTimestamp(ts, "sin-fecha")}.xlsx";
        var rutaExcel = _excel.ExportSingle(datos, nombreExcel, columnasOrden);

        return PhysicalFile(
            rutaExcel,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Path.GetFileName(rutaExcel));
    }

    [HttpGet("descargar-todos")]
    public IActionResult DescargarTodos()
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Redirect("/login");
        }

        var registros = _state.GetAll();
        if (registros.Count == 0)
        {
            return StatusCode(404, new { error = "No hay datos para exportar" });
        }

        var datosMigrados = registros.Select(AppState.MigrateRecord).ToList();
        var columnasOrden = new[]
        {
            "Tipo de Documento", "Número Documento", "Nombre", "ID Atención",
            "Fecha Nacimiento", "Sexo Biológico", "Edad", "Diagnóstico",
            "Especialidad", "Aseguradora", "Procedimiento", "Cama", "timestamp"
        };

        var nombreExcel = $"resultados_completos_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        var rutaExcel = _excel.ExportMany(datosMigrados, nombreExcel, columnasOrden);

        return PhysicalFile(
            rutaExcel,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Path.GetFileName(rutaExcel));
    }

    [HttpDelete("eliminar/{idx:int}")]
    public IActionResult Eliminar(int idx)
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Redirect("/login");
        }

        if (_state.DeleteAt(idx))
        {
            return Ok(new { ok = true });
        }

        return StatusCode(404, new { error = "Índice no válido" });
    }

    [HttpPut("actualizar/{idx:int}")]
    public async Task<IActionResult> Actualizar(int idx)
    {
        if (!HttpContext.IsAuthenticated())
        {
            return Redirect("/login");
        }

        var cambios = await RequestJsonReader.ReadDictionaryAsync(Request);
        if (cambios.Count == 0)
        {
            return StatusCode(400, new { error = "Datos vacíos" });
        }

        if (_state.UpdateAt(idx, cambios))
        {
            return Ok(new { ok = true });
        }

        return StatusCode(404, new { error = "Índice no válido" });
    }
}
