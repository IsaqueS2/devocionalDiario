using System.Globalization;
using System.Text;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration["NEON_CONNECTION_STRING"];
if (string.IsNullOrWhiteSpace(connectionString))
{
    connectionString = builder.Configuration.GetConnectionString("Default");
}

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Configure NEON_CONNECTION_STRING ou ConnectionStrings:Default com uma conexão PostgreSQL.");
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
            PRIMARY KEY (data, usuario_normalizado)
        );
        """;
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

await app.RunAsync();

static async Task<IResult> GetMonthAsync(int ano, int mes, NpgsqlDataSource dataSource)
{
    if (ano is < 1 or > 9999 || mes is < 1 or > 12)
    {
        return Results.BadRequest(new { erro = "Ano ou mês inválido." });
    }

    var primeiroDia = new DateOnly(ano, mes, 1);
    var ultimoDia = new DateOnly(ano, mes, DateTime.DaysInMonth(ano, mes));
    var resultado = new SortedDictionary<string, List<string>>();

    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT data, usuario
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

        pessoas.Add(reader.GetString(1));
    }

    return Results.Ok(resultado);
}

static async Task<IResult> ToggleRecordAsync(RegistroDTO? dto, NpgsqlDataSource dataSource)
{
    if (dto is null ||
        !DateOnly.TryParseExact(
            dto.Data,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var data))
    {
        return Results.BadRequest(new { erro = "Informe uma data válida no formato AAAA-MM-DD." });
    }

    var usuario = dto.Usuario?.Trim().Normalize(NormalizationForm.FormC);
    if (string.IsNullOrWhiteSpace(usuario) ||
        usuario.Length > 80 ||
        usuario.Any(char.IsControl))
    {
        return Results.BadRequest(new { erro = "Informe um nome com até 80 caracteres." });
    }

    var usuarioNormalizado = usuario.ToUpperInvariant();
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
              INSERT INTO registros (data, usuario, usuario_normalizado)
              VALUES (@data, @usuario, @usuarioNormalizado);
              """;
        command.Parameters.AddWithValue("data", data);
        command.Parameters.AddWithValue("usuarioNormalizado", usuarioNormalizado);
        if (!jaRegistrado)
        {
            command.Parameters.AddWithValue("usuario", usuario);
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

record RegistroDTO(string Data, string Usuario);
