from flask import Flask, render_template, request, send_file, jsonify
import cv2
import easyocr
import pandas as pd
import re
import os
import numpy as np
from datetime import datetime
import json

app = Flask(__name__)

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

def cargar_resultados():
    global resultados_list
    if os.path.exists(RESULTADOS_FILE):
        try:
            with open(RESULTADOS_FILE, 'r', encoding='utf-8') as f:
                resultados_list = json.load(f)
        except:
            resultados_list = []
    
def guardar_resultados():
    with open(RESULTADOS_FILE, 'w', encoding='utf-8') as f:
        json.dump(resultados_list, f, ensure_ascii=False, indent=2)

# Cargar resultados al iniciar
cargar_resultados()

# Solo Windows
# pytesseract.pytesseract.tesseract_cmd = r"C:\Program Files\Tesseract-OCR\tesseract.exe"

def extraer_datos(imagen):
    try:
        # Obtener reader (inicializa si es necesario)
        ocr_reader = inicializar_ocr()
        
        # Convertir a escala de grises
        gris = cv2.cvtColor(imagen, cv2.COLOR_BGR2GRAY)
        gris = cv2.threshold(gris, 150, 255, cv2.THRESH_BINARY)[1]

        # Extraer texto con EasyOCR
        try:
            resultado = ocr_reader.readtext(gris, detail=0)
            texto = "\n".join(resultado)
            print(f"=== TEXTO EXTRAÍDO ===\n{texto}\n====================")
        except Exception as ocr_error:
            print(f"Error en OCR: {ocr_error}")
            texto = ""

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

        patrones = {
            "Nombre": r"(?:Nombre|PRUEBAS SISTEMAS)\s*[:\s]+(.+?)(?=\n|RC|$)",
            "RC": r"RC\s+(\d+)",
            "Edad": r"Edad\s+([\w\s]+?)(?=\n|Fecha|$)",
            "Fecha Nacimiento": r"Fecha\s+nacimiento\s+([\d/]+)",
            "ID Atención": r"Id\s+atención\s+(\d+)",
            "Especialidad": r"Especialidad\s+(.+?)(?=\n|Sexo|$)",
            "Sexo Biológico": r"Sexo\s+biológico\s+(\w+)",
            "Diagnóstico": r"Diagnóstico\s+(.+?)(?=\n|Aseguradora|$)",
            "Aseguradora": r"Aseguradora\s+(.+?)(?=\n|Procedimiento|$)",
            "Procedimiento": r"Procedimiento\s+(.+?)(?=\n|Cama|$)",
            "Cama": r"Cama\s+([\w\d]+)"
        }

        print("=== BUSCANDO PATRONES ===")
        for campo, patron in patrones.items():
            try:
                m = re.search(patron, texto, re.IGNORECASE | re.DOTALL)
                if m:
                    valor = m.group(1).strip()
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
        # Devolver datos vacíos en lugar de fallar
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

@app.route("/resultados", methods=["GET"])
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
        
        if not datos or not datos.get("Nombre") or not datos.get("RC"):
            return jsonify({"error": "Nombre y RC son obligatorios"}), 400

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
def api_resultados():
    try:
        # Asegurar que los datos son serializables
        datos_limpios = []
        for item in resultados_list:
            item_limpio = {}
            for key, value in item.items():
                item_limpio[key] = str(value) if value else ""
            datos_limpios.append(item_limpio)
        return jsonify(datos_limpios)
    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route("/descargar/<int:idx>", methods=["GET"])
def descargar(idx):
    if 0 <= idx < len(resultados_list):
        datos = resultados_list[idx]
        df = pd.DataFrame([datos])
        nombre_excel = f"paciente_{datos.get('timestamp', 'sin-fecha').replace(' ', '_').replace(':', '-')}.xlsx"
        ruta_excel = os.path.join(OUTPUT_FOLDER, nombre_excel)
        df.to_excel(ruta_excel, index=False)
        return send_file(ruta_excel, as_attachment=True, download_name=nombre_excel)
    return jsonify({"error": "Índice no válido"}), 404

@app.route("/eliminar/<int:idx>", methods=["DELETE"])
def eliminar(idx):
    global resultados_list
    if 0 <= idx < len(resultados_list):
        resultados_list.pop(idx)
        guardar_resultados()
        return jsonify({"ok": True})
    return jsonify({"error": "Índice no válido"}), 404

if __name__ == "__main__":
    app.run(debug=True, port=5000)
    
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