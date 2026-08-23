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

### Uso de Supabase (PostgreSQL) - ¡Recomendado para la Capa Gratuita!
La aplicación está configurada con soporte híbrido:
1. **SQLite (Local / Por defecto)**: Ideal para pruebas rápidas. Si no configuras la variable `SUPABASE_CONNECTION_STRING` en Render, se usará SQLite en local.
   * *Nota*: Para producción con SQLite, necesitarás mantener el plan **Starter** y el volumen **disk** en `render.yaml`, de lo contrario tus datos se borrarán con cada reinicio.
2. **Supabase (PostgreSQL / Nube)**: Si proporcionas la variable `SUPABASE_CONNECTION_STRING`, la aplicación guardará todos los datos en tu base de datos de Supabase en la nube.
   * **¡Capa Gratuita Soportada!**: Como la base de datos está externa (en Supabase), puedes cambiar con total seguridad el plan de Render a la **capa gratuita (Free)** sin riesgo de perder datos.

#### Cómo configurar Supabase en Render:
1. Crea un proyecto en [Supabase](https://supabase.com/).
2. Ve a las configuraciones de tu proyecto de Supabase -> **Database** -> **Connection Strings** -> selecciona la pestaña **URI** (o copia los datos de Host, User, Password y Database para armar una cadena de conexión estándar).
3. El formato de la cadena debe ser similar a:
   `Host=db.xxxx.supabase.co;Database=postgres;Username=postgres;Password=tu-contraseña;Port=5432;`
4. Al desplegar el Blueprint en Render, se te pedirá el valor para `SUPABASE_CONNECTION_STRING`. Pega allí tu cadena de conexión.
5. Si deseas usar el plan gratuito de Render con Supabase, abre `render.yaml`, cambia `plan: starter` a `plan: free` y elimina o comenta la sección `disk:`.

