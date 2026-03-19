# PACIENTE-RCV en C# (ASP.NET Core)

Migracion del backend Flask a ASP.NET Core Minimal API, conservando las mismas rutas y pantallas HTML.

## 1. Requisitos

- .NET SDK 8.0 o superior
- Windows (recomendado para esta configuracion)
- Archivos de idioma de Tesseract

## 2. OCR (Tesseract)

Esta version C# usa Tesseract en lugar de EasyOCR.

1. Copia `spa.traineddata` en `csharp-backend/tessdata`.
2. Opcional: agrega `eng.traineddata` como respaldo.
3. Opcional: define `TESSDATA_PATH` si no usaras esa carpeta.

## 3. Variables de entorno

Opcionales:

- `ADMIN_USER` (default: `admin`)
- `ADMIN_PASSWORD` (default: `admin123`)
- `OCR_LANG` (default: `spa`)
- `TESSDATA_PATH` (ruta de tessdata)

## 4. Ejecutar

Desde `csharp-backend`:

```powershell
dotnet restore
dotnet run
```

Servidor en:

- http://localhost:5000

## 5. Rutas migradas

- GET `/`
- GET `/health`
- GET/POST `/login`
- GET `/logout`
- GET `/resultados`
- GET `/debug`
- POST `/procesar`
- POST `/guardar-datos`
- GET `/api/resultados`
- GET `/descargar/{idx}`
- GET `/descargar-todos`
- DELETE `/eliminar/{idx}`
- PUT `/actualizar/{idx}`
- POST `/debug/text`

## 6. Datos y archivos

Ahora el backend C# es autosuficiente y usa sus propios artefactos dentro de `csharp-backend`:

- `resultados.json`
- carpeta `output/`
- carpeta `uploads/`
- plantillas de `templates/`

## 7. Notas

- Esta migracion mantiene la logica de deduplicacion, login por sesion, exportacion Excel y edicion de registros.
- El front-end (HTML/JS) permanece igual para evitar romper el flujo de usuarios.
