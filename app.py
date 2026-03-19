from flask import Flask, render_template, request, send_file, jsonify, session, redirect, url_for
from functools import wraps
import cv2
import easyocr
import pandas as pd
import re
import os
import numpy as np
from datetime import datetime
import json
import threading
import hashlib

app = Flask(__name__)
app.secret_key = os.environ.get("SECRET_KEY", "rcv-secret-2026-cambia-esto")

ADMIN_USER = os.environ.get("ADMIN_USER", "admin")
ADMIN_PASSWORD_HASH = hashlib.sha256(
    os.environ.get("ADMIN_PASSWORD", "admin123").encode()
).hexdigest()

def _hash_password(password):
    return hashlib.sha256(str(password).encode()).hexdigest()

def login_requerido(f):
    @wraps(f)
    def decorado(*args, **kwargs):
        if not session.get("autenticado"):
            return redirect(url_for("login"))
        return f(*args, **kwargs)
    return decorado

UPLOAD_FOLDER = "uploads"
OUTPUT_FOLDER = "output"
RESULTADOS_FILE = "resultados.json"
os.makedirs(UPLOAD_FOLDER, exist_ok=True)
os.makedirs(OUTPUT_FOLDER, exist_ok=True)

# Inicializar OCR (lazy loading - solo cuando sea necesario)
reader = None

def inicializar_ocr():
    global reader
    if reader is None:
        print("Inicializando EasyOCR (primera vez, puede tardar)...")
        reader = easyocr.Reader(['es'], gpu=False)
        print("✓ EasyOCR listo")
    return reader

# Almacenar resultados en memoria y en JSON
resultados_list = []

def _migrar_registro(registro):
    if not isinstance(registro, dict):
        return {}

    item = dict(registro)
    rc_valor = str(item.get("RC", "")).strip()
    if rc_valor and not str(item.get("Número Documento", "")).strip():
        item["Número Documento"] = rc_valor
    if rc_valor and not str(item.get("Tipo de Documento", "")).strip():
        item["Tipo de Documento"] = "RC"
    item.setdefault("Tipo de Documento", "")
    item.setdefault("Número Documento", "")
    item.pop("RC", None)
    return item

def cargar_resultados():
    global resultados_list
    if os.path.exists(RESULTADOS_FILE):
        try:
            with open(RESULTADOS_FILE, 'r', encoding='utf-8') as f:
                data = json.load(f)
                resultados_list = [_migrar_registro(item) for item in data]
        except:
            resultados_list = []
    
def guardar_resultados():
    with open(RESULTADOS_FILE, 'w', encoding='utf-8') as f:
        json.dump(resultados_list, f, ensure_ascii=False, indent=2)

def _normalizar_texto(valor):
    return re.sub(r"\s+", " ", str(valor or "").strip()).lower()

def _firma_datos(datos):
    """Firma estable para comparar registros sin usar timestamp."""
    campos = [
        "Nombre", "RC", "Edad", "Fecha Nacimiento", "ID Atención",
        "Especialidad", "Sexo Biológico", "Diagnóstico",
        "Aseguradora", "Procedimiento", "Cama"
    ]
    return tuple(_normalizar_texto(datos.get(campo)) for campo in campos)

def _es_duplicado_reciente(datos, segundos=120):
    """Evita duplicados idénticos consecutivos en una ventana corta de tiempo."""
    if not resultados_list:
        return False

    ultimo = resultados_list[0]
    if _firma_datos(ultimo) != _firma_datos(datos):
        return False

    ts = ultimo.get("timestamp", "")
    try:
        momento = datetime.strptime(ts, "%Y-%m-%d %H:%M:%S")
        return (datetime.now() - momento).total_seconds() <= segundos
    except Exception:
        # Si no hay timestamp válido, tratarlo como potencial duplicado inmediato
        return True

def _buscar_idx_por_timestamp(timestamp):
    if not timestamp:
        return -1
    for idx, item in enumerate(resultados_list):
        if item.get("timestamp") == timestamp:
            return idx
    return -1

# Cargar resultados al iniciar
cargar_resultados()

# Pre-cargar OCR en background para que el health check funcione
def _precargar_ocr():
    try:
        inicializar_ocr()
    except Exception as e:
        print(f"Error precargando OCR: {e}")

threading.Thread(target=_precargar_ocr, daemon=True).start()

# Solo Windows
# pytesseract.pytesseract.tesseract_cmd = r"C:\Program Files\Tesseract-OCR\tesseract.exe"

# Palabras clave de la interfaz web que el OCR puede leer por error
_UI_KEYWORDS = [
    "sube la imagen", "cargar imagen", "haz clic", "arrastra",
    "procesar", "limpiar", "datos extraidos", "datos extraídos",
    "ctrl+v", "se mostrarán", "se mostraran", "descargar", "ver todos",
    "administrador", "ocr de pacientes", "extrayendo datos", "no hay imagen",
    "previsualización", "previsualizacion", "o pega", "upload", "subir imagen",
]

def _filtrar_ui(lineas):
    """Elimina líneas que pertenecen a la interfaz web (no al documento)."""
    resultado = []
    for linea in lineas:
        linea_s = linea.lower().strip()
        if len(linea_s) < 2:
            continue
        if any(kw in linea_s for kw in _UI_KEYWORDS):
            continue
        resultado.append(linea)
    return resultado

def _preprocesar(imagen):
    """Escala y mejora el contraste de la imagen para optimizar el OCR."""
    h, w = imagen.shape[:2]
    # Escalar para que el lado mayor tenga al menos 1800 px
    lado_mayor = max(h, w)
    if lado_mayor < 1800:
        factor = 1800 / lado_mayor
        imagen = cv2.resize(
            imagen,
            (int(w * factor), int(h * factor)),
            interpolation=cv2.INTER_CUBIC
        )
    gris = cv2.cvtColor(imagen, cv2.COLOR_BGR2GRAY)
    # Threshold adaptativo: mejor para iluminación irregular y capturas de pantalla
    procesada = cv2.adaptiveThreshold(
        gris, 255,
        cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
        cv2.THRESH_BINARY,
        15, 8
    )
    return procesada

def _ocr_intentos(ocr_reader, imagen_orig):
    """Ejecuta OCR con varias estrategias y devuelve el mejor texto."""
    resultados_texto = []

    # Intento 1: imagen completa mejorada
    try:
        proc = _preprocesar(imagen_orig)
        lineas = ocr_reader.readtext(proc, detail=0, paragraph=False)
        lineas = _filtrar_ui(lineas)
        resultados_texto.append("\n".join(lineas))
        print(f"[OCR intento 1 - imagen completa]: {len(lineas)} líneas")
    except Exception as e:
        print(f"OCR intento 1 error: {e}")

    # Intento 2: mitad inferior de la imagen (donde suele estar la tarjeta del paciente)
    try:
        h = imagen_orig.shape[0]
        mitad_inf = imagen_orig[h // 2:, :]
        proc2 = _preprocesar(mitad_inf)
        lineas2 = ocr_reader.readtext(proc2, detail=0, paragraph=False)
        lineas2 = _filtrar_ui(lineas2)
        resultados_texto.append("\n".join(lineas2))
        print(f"[OCR intento 2 - mitad inferior]: {len(lineas2)} líneas")
    except Exception as e:
        print(f"OCR intento 2 error: {e}")

    # Devolver el texto más largo (más información extraída)
    if not resultados_texto:
        return ""
    mejor = max(resultados_texto, key=len)
    return mejor

def _normalizar_linea(linea):
    return re.sub(r"\s+", " ", str(linea or "")).strip()

def _formatear_edad(valor):
    numero = re.search(r"\d{1,3}", str(valor or ""))
    if not numero:
        return ""
    return f"{int(numero.group(0))} años"

def _extraer_edad_desde_lineas(texto):
    """Extrae edad tolerando variaciones comunes del OCR."""
    lineas = [_normalizar_linea(linea) for linea in texto.splitlines() if _normalizar_linea(linea)]
    if not lineas:
        return ""

    patron_etiqueta = re.compile(r"\b(?:edad|edac|edao|edqd|edod)\b", re.IGNORECASE)
    patron_numero = re.compile(r"\b(\d{1,3})\b")
    patron_anos = re.compile(r"\b(\d{1,3})\s*(?:a(?:ñ|n|fi|f|h)?os?)\b", re.IGNORECASE)

    for idx, linea in enumerate(lineas):
        candidatos = [linea]
        if idx + 1 < len(lineas):
            candidatos.append(lineas[idx + 1])

        if patron_etiqueta.search(linea):
            for candidato in candidatos:
                match_anos = patron_anos.search(candidato)
                if match_anos:
                    return _formatear_edad(match_anos.group(1))

                match_numero = patron_numero.search(candidato)
                if match_numero:
                    return _formatear_edad(match_numero.group(1))

    for linea in lineas:
        match_anos = patron_anos.search(linea)
        if match_anos:
            return _formatear_edad(match_anos.group(1))

    return ""

def extraer_datos(imagen):
    try:
        ocr_reader = inicializar_ocr()

        texto = _ocr_intentos(ocr_reader, imagen)
        print(f"=== TEXTO EXTRAÍDO ===\n{texto}\n====================")

        datos = {
            "Nombre": "",
            "RC": "",
            "Edad": "",
            "Fecha Nacimiento": "",
            "ID Atención": "",
            "Especialidad": "",
            "Sexo Biológico": "",
            "Diagnóstico": "",
            "Aseguradora": "",
            "Procedimiento": "",
            "Cama": ""
        }

        edad_detectada = _extraer_edad_desde_lineas(texto)
        if edad_detectada:
            datos["Edad"] = edad_detectada
            print(f"✓ Edad (líneas OCR): {edad_detectada}")
        else:
            print("✗ Edad (líneas OCR): no encontrada")

        # Patrones flexibles: aceptan variaciones de OCR y tildes opcionales
        patrones = {
            # Nombre: línea con "PRUEBAS SISTEMAS", "Paciente" o "Nombre" seguida del valor
            "Nombre": r"(?:PRUEBAS\s+SISTEMAS|Paciente|Nombre)\s*[,:'\"*\s]+(.+?)(?=\n|CC|RC|C\.C|$)",
            # CC (Cédula de Ciudadanía) o RC — el documento usa "CC"
            "RC":     r"(?:CC|RC|C\.C\.?)\s*[:\s]*(\d[\d\s\.\-]{4,20})",
            # Edad: flexible con variaciones OCR de etiqueta y formato
            "Edad":   r"(?:Edad|Edac|Edao)\s*[:\-]?\s*(\d{1,3}\s*(?:a(?:ñ|n|fi|f|h)?os?)?)",
            # Fecha de nacimiento: varios formatos dd/mm/yyyy, dd-mm-yyyy
            "Fecha Nacimiento": r"Fecha\s*(?:de\s*)?[Nn]ac(?:imiento)?\s*[:\s]*([\d]{1,2}[/\-\.][\d]{1,2}[/\-\.][\d]{2,4})",
            # ID de atención
            "ID Atención": r"[Ii]d\s*[:\s]*[Aa]tenci[oó]n\s*[:\s]*(\d+)",
            # Especialidad
            "Especialidad": r"[Ee]specialidad\s*[:\s]+(.+?)(?=\n|[Ss]exo|$)",
            # Sexo biológico
            "Sexo Biológico": r"[Ss]exo\s*[Bb]iol[oó]gico\s*[:\s]*(\w+)",
            # Diagnóstico: hasta nueva línea o "Aseguradora"
            "Diagnóstico":  r"[Dd]iagn[oó]s?tico\s*[:\s]*(.+?)(?=\n|[Aa]seguradora|$)",
            # Aseguradora
            "Aseguradora":  r"[Aa]seguradora\s*[:\s]*(.+?)(?=\n|[Pp]rocedimiento|$)",
            # Procedimiento
            "Procedimiento": r"[Pp]rocedimiento\s*[:\s]*(.+?)(?=\n|[Cc]ama|$)",
            # Cama: alfanumérico corto
            "Cama": r"[Cc]ama\s*[:\s]*([\w\d]{2,12})",
        }

        print("=== BUSCANDO PATRONES ===")
        for campo, patron in patrones.items():
            try:
                if campo == "Edad" and datos.get("Edad"):
                    print("• Edad: se conserva valor detectado por líneas")
                    continue

                m = re.search(patron, texto, re.IGNORECASE | re.DOTALL)
                if m:
                    valor = m.group(1).strip()
                    # Limpiar artefactos comunes del OCR
                    valor = re.sub(r"\s{2,}", " ", valor)
                    datos[campo] = valor
                    print(f"✓ {campo}: {valor}")
                else:
                    print(f"✗ {campo}: no encontrado")
            except Exception as e:
                print(f"Error extrayendo {campo}: {e}")

        print(f"=== DATOS FINALES ===\n{datos}\n====================")
        return datos

    except Exception as e:
        print(f"Error en extraer_datos: {e}")
        import traceback
        traceback.print_exc()
        return {
            "Nombre": "",
            "RC": "",
            "Edad": "",
            "Fecha Nacimiento": "",
            "ID Atención": "",
            "Especialidad": "",
            "Sexo Biológico": "",
            "Diagnóstico": "",
            "Aseguradora": "",
            "Procedimiento": "",
            "Cama": ""
        }

@app.route("/", methods=["GET"])
def index():
    return render_template("upload.html")

@app.route("/health", methods=["GET"])
def health():
    """Health check - responde rápido"""
    return jsonify({"status": "ok", "ocr_ready": reader is not None})

@app.route("/login", methods=["GET", "POST"])
def login():
    if session.get("autenticado"):
        return redirect(url_for("resultados_page"))

    error = ""
    if request.method == "POST":
        usuario = request.form.get("usuario", "").strip()
        contrasena = request.form.get("contrasena", "")

        if usuario == ADMIN_USER and _hash_password(contrasena) == ADMIN_PASSWORD_HASH:
            session["autenticado"] = True
            return redirect(url_for("resultados_page"))

        error = "Usuario o contraseña incorrectos"

    return render_template("login.html", error=error)

@app.route("/logout", methods=["GET"])
def logout():
    session.clear()
    return redirect(url_for("login"))

@app.route("/resultados", methods=["GET"])
@login_requerido
def resultados_page():
    return render_template("resultados.html")

@app.route("/debug", methods=["GET"])
def debug_page():
    return render_template("debug.html")

@app.route("/guardar-datos", methods=["POST"])
def guardar_datos():
    global resultados_list
    
    try:
        datos = request.get_json()
        
        if not datos or (not datos.get("Nombre") and not datos.get("RC")):
            return jsonify({"error": "Nombre o RC es obligatorio"}), 400

        timestamp_objetivo = str(datos.get("timestamp", "")).strip()

        # Si llega timestamp, actualizar ese registro en vez de insertar otro
        idx = _buscar_idx_por_timestamp(timestamp_objetivo)
        if idx >= 0:
            registro_actual = resultados_list[idx].copy()
            for key, value in datos.items():
                if key != "timestamp":
                    registro_actual[key] = value
            registro_actual["timestamp"] = timestamp_objetivo
            resultados_list[idx] = registro_actual
            guardar_resultados()

            print(f"♻️ Datos actualizados: {registro_actual.get('Nombre')}")

            try:
                df = pd.DataFrame([registro_actual])
                nombre_excel = f"paciente_{datetime.now().strftime('%Y%m%d_%H%M%S')}.xlsx"
                ruta_excel = os.path.join(OUTPUT_FOLDER, nombre_excel)
                df.to_excel(ruta_excel, index=False)
            except Exception as e:
                print(f"Error guardando Excel: {e}")

            return jsonify({**registro_actual, "updated": True}), 200

        if _es_duplicado_reciente(datos):
            print("⚠️ Registro duplicado detectado en guardado manual")
            return jsonify({**resultados_list[0], "duplicado": True}), 200

        # Agregar timestamp
        datos["timestamp"] = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        resultados_list.insert(0, datos)
        guardar_resultados()
        
        print(f"✅ Datos guardados manualmente: {datos.get('Nombre')}")
        
        # Guardar Excel
        try:
            df = pd.DataFrame([datos])
            nombre_excel = f"paciente_{datetime.now().strftime('%Y%m%d_%H%M%S')}.xlsx"
            ruta_excel = os.path.join(OUTPUT_FOLDER, nombre_excel)
            df.to_excel(ruta_excel, index=False)
        except Exception as e:
            print(f"Error guardando Excel: {e}")
        
        return jsonify(datos), 201
        
    except Exception as e:
        print(f"❌ Error en /guardar-datos: {e}")
        return jsonify({"error": str(e)}), 500
@app.route("/procesar", methods=["POST"])
def procesar():
    global resultados_list
    
    try:
        print("\n>>> NUEVA SOLICITUD DE PROCESAMIENTO <<<")
        archivo = request.files.get("imagen")
        if not archivo:
            return jsonify({"error": "No se encontró imagen"}), 400

        print(f"Archivo recibido: {archivo.filename}")
        
        # Leer imagen
        npimg = np.frombuffer(archivo.read(), np.uint8)
        imagen = cv2.imdecode(npimg, cv2.IMREAD_COLOR)
        
        if imagen is None:
            return jsonify({"error": "Imagen inválida o formato no soportado"}), 400

        print(f"Imagen decodificada: {imagen.shape}")

        # Extraer datos
        datos = extraer_datos(imagen)

        if _es_duplicado_reciente(datos):
            print("⚠️ OCR duplicado reciente: se evita inserción duplicada")
            return jsonify({**resultados_list[0], "duplicado": True}), 200
        
        # Agregar timestamp y guardar
        datos["timestamp"] = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        resultados_list.insert(0, datos)  # Insertar al inicio
        print(f"Total de registros: {len(resultados_list)}")
        
        guardar_resultados()
        print(f"Datos guardados en {RESULTADOS_FILE}")
        
        # Guardar Excel
        try:
            df = pd.DataFrame([datos])
            nombre_excel = f"paciente_{datetime.now().strftime('%Y%m%d_%H%M%S')}.xlsx"
            ruta_excel = os.path.join(OUTPUT_FOLDER, nombre_excel)
            df.to_excel(ruta_excel, index=False)
            print(f"Excel guardado en {ruta_excel}")
        except Exception as e:
            print(f"Error guardando Excel: {e}")
        
        print(">>> PROCESAMIENTO COMPLETADO <<<\n")
        return jsonify(datos), 201
        
    except Exception as e:
        print(f"❌ Error en /procesar: {e}")
        import traceback
        traceback.print_exc()
        return jsonify({"error": f"Error al procesar: {str(e)}"}), 500

@app.route("/api/resultados")
@login_requerido
def api_resultados():
    try:
        # Asegurar que los datos son serializables
        datos_limpios = []
        for item in resultados_list:
            item_migrado = _migrar_registro(item)
            item_limpio = {}
            for key, value in item_migrado.items():
                item_limpio[key] = str(value) if value else ""
            datos_limpios.append(item_limpio)
        return jsonify(datos_limpios)
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route("/descargar/<int:idx>", methods=["GET"])
@login_requerido
def descargar(idx):
    if 0 <= idx < len(resultados_list):
        datos = _migrar_registro(resultados_list[idx])
        df = pd.DataFrame([datos])
        
        # Reordenar columnas en el orden especificado
        columnas_orden = [
            "Tipo de Documento", "Número Documento", "Nombre", "ID Atención",
            "Fecha Nacimiento", "Sexo Biológico", "Edad", "Diagnóstico",
            "Especialidad", "Aseguradora", "Procedimiento", "Cama", "timestamp"
        ]
        columnas_disponibles = [col for col in columnas_orden if col in df.columns]
        df = df[columnas_disponibles]
        
        nombre_excel = f"paciente_{datos.get('timestamp', 'sin-fecha').replace(' ', '_').replace(':', '-')}.xlsx"
        ruta_excel = os.path.join(OUTPUT_FOLDER, nombre_excel)
        df.to_excel(ruta_excel, index=False)
        return send_file(ruta_excel, as_attachment=True, download_name=nombre_excel)
    return jsonify({"error": "Índice no válido"}), 404

@app.route("/descargar-todos", methods=["GET"])
@login_requerido
def descargar_todos():
    if not resultados_list:
        return jsonify({"error": "No hay datos para exportar"}), 404

    # Migrar todos los registros para normalizar campos
    datos_migrados = [_migrar_registro(item) for item in resultados_list]
    df = pd.DataFrame(datos_migrados)
    
    # Reordenar columnas en el orden especificado
    columnas_orden = [
        "Tipo de Documento", "Número Documento", "Nombre", "ID Atención",
        "Fecha Nacimiento", "Sexo Biológico", "Edad", "Diagnóstico",
        "Especialidad", "Aseguradora", "Procedimiento", "Cama", "timestamp"
    ]
    columnas_disponibles = [col for col in columnas_orden if col in df.columns]
    df = df[columnas_disponibles]
    
    nombre_excel = f"resultados_completos_{datetime.now().strftime('%Y%m%d_%H%M%S')}.xlsx"
    ruta_excel = os.path.join(OUTPUT_FOLDER, nombre_excel)
    df.to_excel(ruta_excel, index=False)
    return send_file(ruta_excel, as_attachment=True, download_name=nombre_excel)

@app.route("/eliminar/<int:idx>", methods=["DELETE"])
@login_requerido
def eliminar(idx):
    global resultados_list
    if 0 <= idx < len(resultados_list):
        resultados_list.pop(idx)
        guardar_resultados()
        return jsonify({"ok": True})
    return jsonify({"error": "Índice no válido"}), 404

@app.route("/actualizar/<int:idx>", methods=["PUT"])
@login_requerido
def actualizar(idx):
    global resultados_list
    if 0 <= idx < len(resultados_list):
        datos = request.get_json()
        if datos:
            # Actualizar los campos del registro
            resultados_list[idx].update(datos)
            guardar_resultados()
            return jsonify({"ok": True})
    return jsonify({"error": "Índice no válido"}), 404

# Endpoint de debug para inspeccionar OCR
@app.route("/debug/text", methods=["POST"])
def debug_text():
    """Endpoint para ver qué texto está extrayendo OCR"""
    archivo = request.files.get("imagen")
    if not archivo:
        return jsonify({"error": "No se encontró imagen"}), 400

    npimg = np.frombuffer(archivo.read(), np.uint8)
    imagen = cv2.imdecode(npimg, cv2.IMREAD_COLOR)
    
    if imagen is None:
        return jsonify({"error": "Imagen inválida"}), 400

    # Convertir a escala de grises
    gris = cv2.cvtColor(imagen, cv2.COLOR_BGR2GRAY)
    gris = cv2.threshold(gris, 150, 255, cv2.THRESH_BINARY)[1]

    ocr_reader = inicializar_ocr()
    try:
        resultado = ocr_reader.readtext(gris, detail=0)
        texto = "\n".join(resultado)
    except Exception as e:
        texto = f"Error en OCR: {str(e)}"

    return jsonify({
        "imagen_shape": str(imagen.shape),
        "texto_extraido": texto,
        "texto_length": len(texto),
        "lineas": texto.split("\n") if texto else []
    })

if __name__ == "__main__":
    app.run(debug=True, port=5000)