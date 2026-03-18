from flask import Flask, render_template, request, send_file
import cv2
import pytesseract
import pandas as pd
import re
import os
import numpy as np
from datetime import datetime

app = Flask(__name__)

UPLOAD_FOLDER = "uploads"
OUTPUT_FOLDER = "output"
os.makedirs(UPLOAD_FOLDER, exist_ok=True)
os.makedirs(OUTPUT_FOLDER, exist_ok=True)

# Solo Windows
# pytesseract.pytesseract.tesseract_cmd = r"C:\Program Files\Tesseract-OCR\tesseract.exe"

def extraer_datos(imagen):
    gris = cv2.cvtColor(imagen, cv2.COLOR_BGR2GRAY)
    gris = cv2.threshold(gris, 150, 255, cv2.THRESH_BINARY)[1]

    texto = pytesseract.image_to_string(gris, lang="spa")

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
        "Nombre": r"PRUEBAS SISTEMAS.*",
        "RC": r"RC\s(\d+)",
        "Edad": r"Edad\s([\w\s]+)",
        "Fecha Nacimiento": r"Fecha nacimiento\s([\d/]+)",
        "ID Atención": r"Id atención\s(\d+)",
        "Especialidad": r"Especialidad\s(.+)",
        "Sexo Biológico": r"Sexo biológico\s(\w+)",
        "Diagnóstico": r"Diagnóstico\s(.+)",
        "Aseguradora": r"Aseguradora\s(.+)",
        "Procedimiento": r"Procedimiento\s(.+)",
        "Cama": r"Cama\s([\w\d]+)"
    }

    for campo, patron in patrones.items():
        m = re.search(patron, texto)
        if m:
            datos[campo] = m.group(1).strip()

    return datos

@app.route("/", methods=["GET", "POST"])
def index():
    if request.method == "POST":
        archivo = request.files["imagen"]

        npimg = np.frombuffer(archivo.read(), np.uint8)
        imagen = cv2.imdecode(npimg, cv2.IMREAD_COLOR)

        datos = extraer_datos(imagen)

        df = pd.DataFrame([datos])
        nombre_excel = f"paciente_{datetime.now().strftime('%Y%m%d_%H%M%S')}.xlsx"
        ruta_excel = os.path.join(OUTPUT_FOLDER, nombre_excel)
        df.to_excel(ruta_excel, index=False)

        return send_file(ruta_excel, as_attachment=True)

    return render_template("index.html")

if __name__ == "__main__":
    app.run(debug=True)