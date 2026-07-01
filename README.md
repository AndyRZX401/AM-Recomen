# 🌌 AM Recomen - Plataforma Inteligente de Recomendaciones

AM Recomen es una aplicación web premium de recomendación automática de contenido multimedia que abarca **Anime, Películas, Series de TV, Manhwas y Manhuas**. El sistema recopila tendencias en tiempo real desde APIs globales, traduce descripciones al español de forma automática y permite a los usuarios gestionar su biblioteca personal de seguimiento y valoraciones con un rendimiento optimizado para más de 100,000 usuarios concurrentes.

---

## ✨ Características Principales

*   **Sincronización en Segundo Plano:** Obtiene diariamente las tendencias globales desde APIs como *TMDb (Películas y Series)*, *Jikan (Anime)* y *MangaDex (Cómics)*.
*   **Traducción Automática al Español:** Traduce descripciones y sinopsis en inglés al español en caliente con un sistema de reintentos resiliente.
*   **Cuentas de Usuario y Autenticación:** Registro e inicio de sesión seguros mediante encriptación de contraseñas (`PasswordHasher`) y autenticación basada en cookies.
*   **Biblioteca Personal / Watchlist:** Los usuarios pueden organizar sus obras marcándolas como *"Por ver"* o *"Vistos/Completados"*.
*   **Biblioteca de Gustos (Valoración 1-10):** Permite calificar las obras y visualizarlas en una sección exclusiva ordenada de mayor a menor puntuación.
*   **Optimización de Alta Concurrencia:** 
    *   **Caché en Memoria RAM (`IMemoryCache`)** en la página de inicio y el autocompletado para mitigar consultas redundantes.
    *   Consultas optimizadas sin rastreo (`AsNoTracking`) en repositorios para reducir el consumo de CPU y memoria.
    *   Índices compuestos sobre usuarios y estados de biblioteca.
*   **Resiliencia de Carga de Portadas:** Captura de errores a nivel de cliente para sustituir de manera automática portadas caídas o rotas por una portada premium alternativa.
*   **Base de Datos Dual:** Configurado para PostgreSQL en producción y SQLite local de respaldo automático para desarrollo rápido.

---

## 🛠️ Tecnologías Utilizadas

*   **Backend:** .NET 10.0, ASP.NET Core Razor Pages, Web APIs.
*   **Acceso a Datos:** Entity Framework Core (ORM), PostgreSQL, SQLite.
*   **Frontend:** Vanilla CSS (Glassmorphism & Neon Glows), JavaScript (Vanilla), Tailwind CSS, FontAwesome.
*   **Caché:** Microsoft.Extensions.Caching.Memory.

---

## 🚀 Instalación y Configuración Local

### Prerrequisitos
*   [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) instalado.

### Pasos de Ejecución
1.  Clona el repositorio o descarga la carpeta del proyecto.
2.  Abre la terminal en la raíz del proyecto y compila la solución:
    ```bash
    dotnet build
    ```
3.  Ejecuta el proyecto web:
    ```bash
    dotnet run --project src/AMRecomen.Web/AMRecomen.Web.csproj
    ```
4.  Abre el navegador e ingresa a: **`http://localhost:5047`**

*Nota: Durante el primer arranque, la aplicación creará la base de datos de manera automática, sembrará una cuenta de prueba e iniciará la ingesta de tendencias.*

---

## 👥 Cuenta de Prueba Incluida (Seed)
Para probar las funciones de biblioteca y valoraciones al instante sin registrarte:
*   **Usuario:** `andy`
*   **Contraseña:** `1234`
