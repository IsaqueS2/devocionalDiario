using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrWhiteSpace(builder.Configuration["DATABASE_URL"]))
{
    var localDatabaseUrl = ReadLocalDatabaseUrl(builder.Environment.ContentRootPath);
    if (!string.IsNullOrWhiteSpace(localDatabaseUrl))
    {
        builder.Configuration["DATABASE_URL"] = localDatabaseUrl;
    }
}

var databaseUrl = builder.Configuration["DATABASE_URL"];
var connectionString = !string.IsNullOrWhiteSpace(databaseUrl)
    ? ToNpgsqlConnectionString(databaseUrl)
    : builder.Configuration["NEON_CONNECTION_STRING"]
        ?? builder.Configuration.GetConnectionString("Default");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Configure DATABASE_URL, NEON_CONNECTION_STRING ou ConnectionStrings:Default com uma conexão PostgreSQL.");
}

await using var dataSource = NpgsqlDataSource.Create(connectionString);
await using (var connection = await dataSource.OpenConnectionAsync())
{
    await using var command = connection.CreateCommand();
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS registros (
            data DATE NOT NULL,
            usuario TEXT NOT NULL,
            usuario_normalizado TEXT NOT NULL,
            tema TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (data, usuario_normalizado)
        );
        """;
    await command.ExecuteNonQueryAsync();
    command.CommandText = "ALTER TABLE registros ADD COLUMN IF NOT EXISTS tema TEXT NOT NULL DEFAULT '';";
    await command.ExecuteNonQueryAsync();
}

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/devocional/{ano:int}/{mes:int}", (int ano, int mes) =>
    GetMonthAsync(ano, mes, dataSource));

app.MapPost("/api/devocional/alternar", (RegistroDTO? dto) =>
    ToggleRecordAsync(dto, dataSource));

app.MapPut("/api/devocional", (RegistroDTO? dto) =>
    SaveRecordAsync(dto, dataSource));

app.MapDelete("/api/devocional", ([FromBody] RegistroDTO? dto) =>
    DeleteRecordAsync(dto, dataSource));

await app.RunAsync();

static async Task<IResult> GetMonthAsync(int ano, int mes, NpgsqlDataSource dataSource)
{
    if (ano is < 1 or > 9999 || mes is < 1 or > 12)
    {
        return Results.BadRequest(new { erro = "Ano ou mês inválido." });
    }

    var primeiroDia = new DateOnly(ano, mes, 1);
    var ultimoDia = new DateOnly(ano, mes, DateTime.DaysInMonth(ano, mes));
    var resultado = new SortedDictionary<string, List<RegistroResumo>>();

    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT data, usuario, tema
        FROM registros
        WHERE data BETWEEN @primeiroDia AND @ultimoDia
        ORDER BY data, usuario_normalizado;
        """;
    command.Parameters.AddWithValue("primeiroDia", primeiroDia);
    command.Parameters.AddWithValue("ultimoDia", ultimoDia);

    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var data = reader.GetFieldValue<DateOnly>(0)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!resultado.TryGetValue(data, out var pessoas))
        {
            pessoas = [];
            resultado.Add(data, pessoas);
        }

        pessoas.Add(new RegistroResumo(reader.GetString(1), reader.GetString(2)));
    }

    return Results.Ok(new { registros = resultado });
}

static async Task<IResult> ToggleRecordAsync(RegistroDTO? dto, NpgsqlDataSource dataSource)
{
    if (!TryParseRecord(dto, out var data, out var usuario, out var usuarioNormalizado, out var tema, out var erro))
    {
        return Results.BadRequest(new { erro });
    }

    await using var connection = await dataSource.OpenConnectionAsync();
    await using (var transaction = await connection.BeginTransactionAsync())
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@chave, 0));";
        command.Parameters.AddWithValue("chave", $"{data:yyyy-MM-dd}:{usuarioNormalizado}");
        await command.ExecuteNonQueryAsync();
        command.Parameters.Clear();

        command.CommandText = """
            SELECT 1
            FROM registros
            WHERE data = @data AND usuario_normalizado = @usuarioNormalizado;
            """;
        command.Parameters.AddWithValue("data", data);
        command.Parameters.AddWithValue("usuarioNormalizado", usuarioNormalizado);
        var jaRegistrado = await command.ExecuteScalarAsync() is not null;
        command.Parameters.Clear();

        command.CommandText = jaRegistrado
            ? "DELETE FROM registros WHERE data = @data AND usuario_normalizado = @usuarioNormalizado;"
            : """
              INSERT INTO registros (data, usuario, usuario_normalizado, tema)
              VALUES (@data, @usuario, @usuarioNormalizado, @tema);
              """;
        command.Parameters.AddWithValue("data", data);
        command.Parameters.AddWithValue("usuarioNormalizado", usuarioNormalizado);
        if (!jaRegistrado)
        {
            command.Parameters.AddWithValue("usuario", usuario);
            command.Parameters.AddWithValue("tema", tema);
        }

        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    var pessoas = new List<string>();
    await using (var command = connection.CreateCommand())
    {
        command.CommandText = """
            SELECT usuario
            FROM registros
            WHERE data = @data
            ORDER BY usuario_normalizado;
            """;
        command.Parameters.AddWithValue("data", data);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            pessoas.Add(reader.GetString(0));
        }
    }

    return Results.Ok(new { Data = data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Pessoas = pessoas });
}

static async Task<IResult> SaveRecordAsync(RegistroDTO? dto, NpgsqlDataSource dataSource)
{
    if (!TryParseRecord(dto, out var data, out var usuario, out var usuarioNormalizado, out var tema, out var erro))
    {
        return Results.BadRequest(new { erro });
    }

    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO registros (data, usuario, usuario_normalizado, tema)
        VALUES (@data, @usuario, @usuarioNormalizado, @tema)
        ON CONFLICT (data, usuario_normalizado)
        DO UPDATE SET usuario = EXCLUDED.usuario, tema = EXCLUDED.tema;
        """;
    command.Parameters.AddWithValue("data", data);
    command.Parameters.AddWithValue("usuario", usuario);
    command.Parameters.AddWithValue("usuarioNormalizado", usuarioNormalizado);
    command.Parameters.AddWithValue("tema", tema);
    await command.ExecuteNonQueryAsync();

    return Results.Ok(new
    {
        data = data.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        usuario,
        tema
    });
}

static async Task<IResult> DeleteRecordAsync(RegistroDTO? dto, NpgsqlDataSource dataSource)
{
    if (!TryParseRecord(dto, out var data, out _, out var usuarioNormalizado, out _, out var erro))
    {
        return Results.BadRequest(new { erro });
    }

    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        DELETE FROM registros
        WHERE data = @data AND usuario_normalizado = @usuarioNormalizado;
        """;
    command.Parameters.AddWithValue("data", data);
    command.Parameters.AddWithValue("usuarioNormalizado", usuarioNormalizado);
    var removidos = await command.ExecuteNonQueryAsync();

    return Results.Ok(new { removido = removidos > 0 });
}

static bool TryParseRecord(
    RegistroDTO? dto,
    out DateOnly data,
    out string usuario,
    out string usuarioNormalizado,
    out string tema,
    out string erro)
{
    data = default;
    usuario = string.Empty;
    usuarioNormalizado = string.Empty;
    tema = string.Empty;
    erro = string.Empty;

    if (dto is null ||
        !DateOnly.TryParseExact(
            dto.Data,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out data))
    {
        erro = "Informe uma data válida no formato AAAA-MM-DD.";
        return false;
    }

    usuario = dto.Usuario?.Trim().Normalize(NormalizationForm.FormC) ?? string.Empty;
    if (string.IsNullOrWhiteSpace(usuario) ||
        usuario.Length > 80 ||
        usuario.Any(char.IsControl))
    {
        erro = "Informe um nome com até 80 caracteres.";
        return false;
    }

    usuarioNormalizado = usuario.ToUpperInvariant();
    tema = dto.Tema?.Trim().Normalize(NormalizationForm.FormC) ?? string.Empty;
    if (tema.Length > 200 || tema.Any(char.IsControl))
    {
        erro = "Informe um tema com até 200 caracteres.";
        return false;
    }

    return true;
}

static string? ReadLocalDatabaseUrl(string contentRootPath)
{
    foreach (var fileName in new[] { ".env.local", ".env" })
    {
        var path = Path.Combine(contentRootPath, fileName);
        if (!File.Exists(path))
        {
            continue;
        }

        foreach (var line in File.ReadLines(path))
        {
            var trimmedLine = line.Trim();
            if (trimmedLine.StartsWith("export ", StringComparison.Ordinal))
            {
                trimmedLine = trimmedLine["export ".Length..].TrimStart();
            }

            if (trimmedLine.StartsWith('#') ||
                !trimmedLine.StartsWith("DATABASE_URL=", StringComparison.Ordinal))
            {
                continue;
            }

            var value = trimmedLine["DATABASE_URL=".Length..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            return value;
        }
    }

    return null;
}

static string ToNpgsqlConnectionString(string databaseUrl)
{
    if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out var uri) ||
        (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
    {
        return databaseUrl;
    }

    var userInfo = uri.GetComponents(UriComponents.UserInfo, UriFormat.Unescaped);
    var separator = userInfo.IndexOf(':');
    if (separator <= 0)
    {
        throw new InvalidOperationException("DATABASE_URL precisa incluir usuário e senha.");
    }

    var connectionString = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.IsDefaultPort ? 5432 : uri.Port,
        Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
        Username = userInfo[..separator],
        Password = userInfo[(separator + 1)..],
        SslMode = SslMode.Require,
        Pooling = true
    };

    foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = parameter.Split('=', 2);
        var name = Uri.UnescapeDataString(parts[0]).ToLowerInvariant();
        var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;

        switch (name)
        {
            case "sslmode":
                var sslMode = value.Replace("-", string.Empty, StringComparison.Ordinal);
                if (!Enum.TryParse<SslMode>(sslMode, ignoreCase: true, out var parsedSslMode))
                {
                    throw new InvalidOperationException($"DATABASE_URL contém SSL Mode inválido: {value}.");
                }

                connectionString.SslMode = parsedSslMode;
                break;
            case "channel_binding":
                if (!Enum.TryParse<ChannelBinding>(value, ignoreCase: true, out var channelBinding))
                {
                    throw new InvalidOperationException($"DATABASE_URL contém channel_binding inválido: {value}.");
                }

                connectionString.ChannelBinding = channelBinding;
                break;
            default:
                throw new InvalidOperationException(
                    $"DATABASE_URL contém um parâmetro não suportado pelo Npgsql: {name}.");
        }
    }

    return connectionString.ConnectionString;
}

record RegistroDTO(string Data, string Usuario, string? Tema = null);
record RegistroResumo(string Usuario, string Tema);
