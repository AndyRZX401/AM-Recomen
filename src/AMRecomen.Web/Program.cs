using Microsoft.EntityFrameworkCore;
using AMRecomen.Domain.Interfaces;
using AMRecomen.Domain.Entities;
using AMRecomen.Infrastructure.Data;
using AMRecomen.Infrastructure.Repositories;
using AMRecomen.Infrastructure.Services;
using AMRecomen.Infrastructure.BackgroundJobs;
using AMRecomen.Application.Interfaces;
using AMRecomen.Application.Services;
using Npgsql;
using Microsoft.AspNetCore.Authentication.Cookies;

// Habilitar comportamiento heredado de marcas de tiempo para admitir DateTimes de tipo Local/Unspecified en PostgreSQL
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddMemoryCache();

// 1. Configurar base de datos con resiliencia de arranque (PostgreSQL principal, SQLite de respaldo)
var rawConnectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
                          ?? builder.Configuration["DATABASE_URL"]
                          ?? builder.Configuration.GetConnectionString("DefaultConnection");

var connectionString = rawConnectionString?.Trim().Trim('"', '\'');

// Convertir automáticamente formato de URI de Render (postgres:// o postgresql://) a formato estándar de ADO.NET compatible con Npgsql
if (!string.IsNullOrEmpty(connectionString) && (connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)))
{
    try
    {
        var parts = connectionString.Split(';', 2);
        var uriPart = parts[0].Trim();
        var extraParams = parts.Length > 1 ? parts[1].Trim() : "";
        
        var uriString = System.Text.RegularExpressions.Regex.Replace(uriPart, "^postgresql://", "postgres://", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var uri = new Uri(uriString);
        var userInfo = uri.UserInfo.Split(':', 2);
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 5432;
        var database = uri.AbsolutePath.TrimStart('/');
        
        connectionString = $"Host={host};Port={port};Database={database};Username={username};Password={password}";
        
        if (!string.IsNullOrEmpty(extraParams))
        {
            connectionString += $";{extraParams}";
        }
        else
        {
            connectionString += ";SSL Mode=Prefer;Trust Server Certificate=true";
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($">> [BD] Error al parsear URI de PostgreSQL: {ex.Message}. Se evaluará respaldo SQLite.");
        connectionString = null;
    }
}

bool usePostgres = false;
if (!string.IsNullOrEmpty(connectionString) &&
    !connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
    !connectionString.Contains("127.0.0.1"))
{
    try
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = 5,
            CommandTimeout = 10
        };

        if (!string.IsNullOrWhiteSpace(csb.Host))
        {
            // Verificar primero resolución DNS para evitar SocketException (Name or service not known) en tiempo de ejecución
            System.Net.Dns.GetHostAddresses(csb.Host);

            // Verificar que el servidor PostgreSQL responda realmente antes de fijarlo como proveedor
            using var testConn = new NpgsqlConnection(csb.ConnectionString);
            testConn.Open();
            usePostgres = true;
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($">> [BD] No se pudo conectar al servidor PostgreSQL ({ex.GetType().Name}: {ex.Message}). Activando respaldo automático SQLite...");
        Console.ResetColor();
        usePostgres = false;
    }
}

if (usePostgres)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine(">> [BD] Conectado a PostgreSQL (Gama Alta) de forma determinista...");
    Console.ResetColor();
    
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(connectionString, npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
        }));
}
else
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine(">> [BD] Servidor PostgreSQL local u offline. Iniciando base de datos SQLite...");
    Console.ResetColor();
    
    // Detectar volumen persistente de Render (/data) y verificar permisos reales de escritura para evitar caídas
    string dbFolder = "/data";
    bool canWrite = false;
    try
    {
        if (Directory.Exists(dbFolder))
        {
            string testFile = Path.Combine(dbFolder, ".write_test");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            canWrite = true;
        }
    }
    catch
    {
        canWrite = false;
    }

    if (!canWrite)
    {
        dbFolder = AppDomain.CurrentDomain.BaseDirectory;
    }
    string dbPath = Path.Combine(dbFolder, "am_recomen.db");
    
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite($"Data Source={dbPath}"));
}

// Registrar Servicio de Traducción Resiliente
builder.Services.AddHttpClient<TranslationService>();

// 2. Registrar Repositorios
builder.Services.AddScoped<IMediaItemRepository, MediaItemRepository>();
builder.Services.AddScoped<ITagRepository, TagRepository>();
builder.Services.AddScoped<ITrendMetricRepository, TrendMetricRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ILibraryRepository, LibraryRepository>();

// Configurar Cookie Authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth/Login";
        options.LogoutPath = "/Auth/Logout";
        options.AccessDeniedPath = "/Auth/AccessDenied";
    });

// 3. Registrar Clientes HTTP para APIs externas
builder.Services.AddHttpClient<IAnimeApiService, JikanAnimeApiService>();
builder.Services.AddHttpClient<IMovieApiService, TmdbMovieApiService>();
builder.Services.AddHttpClient<IComicApiService, MangaDexComicApiService>();

// 4. Registrar Servicio de Negocio
builder.Services.AddScoped<SearchAndIngestionService>();

// 5. Registrar Servicio de Fondo para Sincronizar Tendencias (Hosted Service)
builder.Services.AddHostedService<TrendSyncBackgroundService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();

// Inicializar la base de datos automáticamente (crear tablas según el proveedor activo)
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<AppDbContext>();
        // EnsureCreatedAsync creará la base de datos y tablas de forma dinámica sin importar si es SQLite o PostgreSQL
        await context.Database.EnsureCreatedAsync();

        // Sembrar cuenta de prueba "andy" con contraseña "1234" si no existe
        var userExists = await context.Users.AnyAsync(u => u.Username == "andy");
        if (!userExists)
        {
            var testUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "andy",
                Email = "andy@prueba.com",
                CreatedAt = DateTime.UtcNow
            };
            var passwordHasher = new Microsoft.AspNetCore.Identity.PasswordHasher<User>();
            testUser.PasswordHash = passwordHasher.HashPassword(testUser, "1234");

            await context.Users.AddAsync(testUser);
            await context.SaveChangesAsync();
        }
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Ocurrió un error al inicializar las tablas de la base de datos.");
    }
}

app.Run();
