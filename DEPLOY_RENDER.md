# Guía de Despliegue en Render - Facturix Web

Esta guía te indica los pasos necesarios para subir el código a GitHub y desplegar la aplicación en Render de forma fácil y segura.

---

## Paso 1: Subir el código a GitHub

Ya hemos inicializado Git localmente y configurado tu repositorio remoto `https://github.com/Artronic1/Facturix_Web.git`.

Para subir los cambios por primera vez, abre tu terminal en la carpeta del proyecto y ejecuta:

```bash
# 1. Añadir todos los archivos locales al control de versión
git add .

# 2. Crear el primer commit
git commit -m "Configure Docker and Render deployment"

# 3. Renombrar la rama principal a 'main'
git branch -M main

# 4. Subir el código a GitHub (esto te pedirá autenticarte si no lo has hecho)
git push -u origin main
```

---

## Paso 2: Crear una cuenta en Render

1. Entra a [Render](https://render.com/) y regístrate (puedes iniciar sesión directamente con tu cuenta de GitHub).

---

## Paso 3: Desplegar usando el Blueprint (`render.yaml`)

El archivo `render.yaml` que hemos configurado le dice a Render exactamente cómo aprovisionar la aplicación y su almacenamiento persistente en un solo paso.

1. En el panel de control de Render (Dashboard), haz clic en el botón **New** (Nuevo) arriba a la derecha.
2. Selecciona **Blueprint**.
3. Render te mostrará una lista de tus repositorios de GitHub. Selecciona el repositorio `Facturix_Web`.
4. Configura los siguientes parámetros:
   - **Blueprint Name**: `facturix-blueprint` (o el nombre que prefieras).
   - **Branch**: `main`.
5. Haz clic en **Approve** (Aprobar).

Render comenzará automáticamente a leer el `Dockerfile`, compilar la imagen de .NET 8, crear el volumen de disco persistente para la base de datos y levantar la aplicación.

---

## Paso 4: Probar y Acceder a la Aplicación

Una vez que el despliegue finalice y el estado cambie a **Live**:

1. En la parte superior de la página del servicio en Render, verás un enlace similar a:
   `https://facturix-web-xxxx.onrender.com`
2. **IMPORTANTE**: La aplicación tiene configurada una ruta base (`/facturix`). Para acceder, debes añadir `/facturix` al final del enlace.
   - **Enlace de acceso**: `https://facturix-web-xxxx.onrender.com/facturix`

---

## Consideraciones Adicionales

### ¿Plan de Pago (Starter) o Capa Gratuita (Free)?
Por defecto, `render.yaml` está configurado para usar el plan **Starter** porque permite conectar un **disco persistente**. SQLite guarda toda la información en archivos locales, y sin disco persistente, los datos se perderían cada vez que Render reinicie el contenedor (aproximadamente una vez al día).

Si prefieres usar la **capa gratuita** de Render (solo para pruebas breves):
1. Abre `render.yaml` en tu editor de código.
2. Modifica la línea `plan: starter` por `plan: free`.
3. Elimina o comenta las líneas correspondientes a la directiva `disk:`:
   ```yaml
   # Comentar o eliminar esto para la capa gratuita:
   # disk:
   #   name: facturix-data
   #   mountPath: /data
   #   sizeGB: 1
   ```
4. Guarda el archivo, haz un nuevo commit y haz `git push origin main`.
