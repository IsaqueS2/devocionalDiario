using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using WebPush;

if (args is ["--generate-reminder-secrets"])
{
    var keys = VapidHelper.GenerateVapidKeys();
    var cronToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
    Console.WriteLine($"VAPID_PUBLIC_KEY={keys.PublicKey}");
    Console.WriteLine($"VAPID_PRIVATE_KEY={keys.PrivateKey}");
    Console.WriteLine($"REMINDER_CRON_TOKEN={cronToken}");
    return;
}

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
    command.CommandText = """
        CREATE TABLE IF NOT EXISTS inscricoes_notificacao (
            endpoint TEXT PRIMARY KEY,
            usuario TEXT NOT NULL,
            usuario_normalizado TEXT NOT NULL,
            timezone TEXT NOT NULL,
            p256dh TEXT NOT NULL,
            auth TEXT NOT NULL,
            ultima_verificacao DATE,
            criada_em TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );
        """;
    await command.ExecuteNonQueryAsync();
}

var vapidPublicKey = builder.Configuration["VAPID_PUBLIC_KEY"];
var vapidPrivateKey = builder.Configuration["VAPID_PRIVATE_KEY"];
var vapidSubject = builder.Configuration["VAPID_SUBJECT"]
    ?? "https://devocionaldiario.onrender.com/";
var pushIsConfigured = !string.IsNullOrWhiteSpace(vapidPublicKey) &&
    !string.IsNullOrWhiteSpace(vapidPrivateKey);
if ((!string.IsNullOrWhiteSpace(vapidPublicKey) ||
     !string.IsNullOrWhiteSpace(vapidPrivateKey)) && !pushIsConfigured)
{
    throw new InvalidOperationException("Configure VAPID_PUBLIC_KEY e VAPID_PRIVATE_KEY em conjunto.");
}

var vapidDetails = pushIsConfigured
    ? new VapidDetails(vapidSubject, vapidPublicKey!, vapidPrivateKey!)
    : null;
using var pushHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
using var webPushClient = new WebPushClient(pushHttpClient);
var reminderCronToken = builder.Configuration["REMINDER_CRON_TOKEN"];

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

app.MapGet("/api/notificacoes/chave-publica", () =>
    pushIsConfigured
        ? Results.Ok(new { chavePublica = vapidPublicKey })
        : Results.Problem("As notificações ainda não foram configuradas no servidor.", statusCode: 503));

app.MapPut("/api/notificacoes/inscricao", (InscricaoNotificacaoDTO? dto) =>
    SaveNotificationSubscriptionAsync(dto, dataSource, pushIsConfigured));

app.MapDelete("/api/notificacoes/inscricao", ([FromBody] CancelarInscricaoDTO? dto) =>
    DeleteNotificationSubscriptionAsync(dto, dataSource));

app.MapPost("/api/notificacoes/processar", (HttpRequest request) =>
    ProcessDueRemindersAsync(request, dataSource, webPushClient, vapidDetails, reminderCronToken, app.Logger));

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

static async Task<IResult> SaveNotificationSubscriptionAsync(
    InscricaoNotificacaoDTO? dto,
    NpgsqlDataSource dataSource,
    bool pushIsConfigured)
{
    if (!pushIsConfigured)
    {
        return Results.Problem("As notificações ainda não foram configuradas no servidor.", statusCode: 503);
    }

    var usuario = dto?.Usuario?.Trim().Normalize(NormalizationForm.FormC) ?? string.Empty;
    if (string.IsNullOrWhiteSpace(usuario) || usuario.Length > 80 || usuario.Any(char.IsControl))
    {
        return Results.BadRequest(new { erro = "Informe um nome com até 80 caracteres." });
    }

    if (dto?.Subscription is not { } subscription)
    {
        return Results.BadRequest(new { erro = "A inscrição de notificações é inválida." });
    }

    var endpoint = subscription.Endpoint;
    var keys = subscription.Keys;
    var p256dh = keys?.P256dh;
    var auth = keys?.Auth;
    if (!IsAllowedPushEndpoint(endpoint) ||
        string.IsNullOrWhiteSpace(p256dh) ||
        p256dh.Length > 256 ||
        string.IsNullOrWhiteSpace(auth) ||
        auth.Length > 256)
    {
        return Results.BadRequest(new { erro = "A inscrição de notificações é inválida." });
    }

    TimeZoneInfo timezone;
    try
    {
        timezone = TimeZoneInfo.FindSystemTimeZoneById(dto.Timezone ?? string.Empty);
    }
    catch (TimeZoneNotFoundException)
    {
        return Results.BadRequest(new { erro = "Fuso horário não reconhecido." });
    }
    catch (InvalidTimeZoneException)
    {
        return Results.BadRequest(new { erro = "Fuso horário inválido." });
    }

    var usuarioNormalizado = usuario.ToUpperInvariant();
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO inscricoes_notificacao (
            endpoint, usuario, usuario_normalizado, timezone, p256dh, auth, ultima_verificacao
        )
        VALUES (@endpoint, @usuario, @usuarioNormalizado, @timezone, @p256dh, @auth, NULL)
        ON CONFLICT (endpoint) DO UPDATE SET
            usuario = EXCLUDED.usuario,
            usuario_normalizado = EXCLUDED.usuario_normalizado,
            timezone = EXCLUDED.timezone,
            p256dh = EXCLUDED.p256dh,
            auth = EXCLUDED.auth,
            ultima_verificacao = NULL;
        """;
    command.Parameters.AddWithValue("endpoint", endpoint!);
    command.Parameters.AddWithValue("usuario", usuario);
    command.Parameters.AddWithValue("usuarioNormalizado", usuarioNormalizado);
    command.Parameters.AddWithValue("timezone", timezone.Id);
    command.Parameters.AddWithValue("p256dh", p256dh);
    command.Parameters.AddWithValue("auth", auth);
    await command.ExecuteNonQueryAsync();

    return Results.Ok(new { inscrito = true });
}

static async Task<IResult> DeleteNotificationSubscriptionAsync(
    CancelarInscricaoDTO? dto,
    NpgsqlDataSource dataSource)
{
    if (!IsAllowedPushEndpoint(dto?.Endpoint))
    {
        return Results.BadRequest(new { erro = "A inscrição de notificações é inválida." });
    }

    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "DELETE FROM inscricoes_notificacao WHERE endpoint = @endpoint;";
    command.Parameters.AddWithValue("endpoint", dto!.Endpoint!);
    await command.ExecuteNonQueryAsync();

    return Results.Ok(new { removido = true });
}

static async Task<IResult> ProcessDueRemindersAsync(
    HttpRequest request,
    NpgsqlDataSource dataSource,
    WebPushClient webPushClient,
    VapidDetails? vapidDetails,
    string? reminderCronToken,
    ILogger logger)
{
    if (string.IsNullOrWhiteSpace(reminderCronToken))
    {
        return Results.Problem("O agendador de notificações não está configurado.", statusCode: 503);
    }

    var authorization = request.Headers.Authorization.ToString();
    var suppliedToken = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? authorization["Bearer ".Length..]
        : string.Empty;
    var expectedBytes = Encoding.UTF8.GetBytes(reminderCronToken);
    var suppliedBytes = Encoding.UTF8.GetBytes(suppliedToken);
    if (expectedBytes.Length != suppliedBytes.Length ||
        !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
    {
        return Results.Unauthorized();
    }

    if (vapidDetails is null)
    {
        return Results.Problem("O envio de notificações não está configurado.", statusCode: 503);
    }

    var subscriptions = new List<InscricaoSalva>();
    await using (var connection = await dataSource.OpenConnectionAsync())
    await using (var command = connection.CreateCommand())
    {
        command.CommandText = """
            SELECT endpoint, usuario, usuario_normalizado, timezone, p256dh, auth
            FROM inscricoes_notificacao
            ORDER BY criada_em;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            subscriptions.Add(new InscricaoSalva(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }
    }

    var agora = DateTimeOffset.UtcNow;
    var enviados = 0;
    var jaRealizados = 0;
    var expirados = 0;
    var falhas = 0;

    foreach (var inscricao in subscriptions)
    {
        TimeZoneInfo timezone;
        try
        {
            timezone = TimeZoneInfo.FindSystemTimeZoneById(inscricao.Timezone);
        }
        catch (TimeZoneNotFoundException)
        {
            logger.LogWarning("Removing reminder subscription with unknown timezone {Timezone}", inscricao.Timezone);
            await RemoveNotificationSubscriptionAsync(inscricao.Endpoint, dataSource);
            expirados++;
            continue;
        }
        catch (InvalidTimeZoneException)
        {
            logger.LogWarning("Removing reminder subscription with invalid timezone {Timezone}", inscricao.Timezone);
            await RemoveNotificationSubscriptionAsync(inscricao.Endpoint, dataSource);
            expirados++;
            continue;
        }

        var localNow = TimeZoneInfo.ConvertTime(agora, timezone);
        if (localNow.TimeOfDay < new TimeSpan(22, 30, 0))
        {
            continue;
        }

        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ultima_verificacao
            FROM inscricoes_notificacao
            WHERE endpoint = @endpoint
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("endpoint", inscricao.Endpoint);
        var lastChecked = await command.ExecuteScalarAsync();
        if (lastChecked is null)
        {
            await transaction.CommitAsync();
            continue;
        }

        if (lastChecked is DateOnly checkedDate && checkedDate >= localDate)
        {
            await transaction.CommitAsync();
            continue;
        }

        command.Parameters.Clear();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM registros
                WHERE data = @data AND usuario_normalizado = @usuarioNormalizado
            );
            """;
        command.Parameters.AddWithValue("data", localDate);
        command.Parameters.AddWithValue("usuarioNormalizado", inscricao.UsuarioNormalizado);
        var devotionalWasRecorded = (bool)(await command.ExecuteScalarAsync() ?? false);
        if (devotionalWasRecorded)
        {
            command.Parameters.Clear();
            command.CommandText = """
                UPDATE inscricoes_notificacao
                SET ultima_verificacao = @data
                WHERE endpoint = @endpoint;
                """;
            command.Parameters.AddWithValue("data", localDate);
            command.Parameters.AddWithValue("endpoint", inscricao.Endpoint);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            jaRealizados++;
            continue;
        }

        var payload = JsonSerializer.Serialize(new
        {
            title = "Hora do devocional",
            body = "Seu momento de hoje ainda não foi registrado. Separe alguns minutos para a Palavra.",
            url = "/"
        });
        var subscription = new PushSubscription(inscricao.Endpoint, inscricao.P256dh, inscricao.Auth);
        var options = new Dictionary<string, object>
        {
            ["vapidDetails"] = vapidDetails,
            ["TTL"] = 3600
        };

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await webPushClient.SendNotificationAsync(subscription, payload, options, timeout.Token);
        }
        catch (WebPushException exception) when (
            exception.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            command.Parameters.Clear();
            command.CommandText = "DELETE FROM inscricoes_notificacao WHERE endpoint = @endpoint;";
            command.Parameters.AddWithValue("endpoint", inscricao.Endpoint);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            expirados++;
            continue;
        }
        catch (WebPushException exception)
        {
            logger.LogError(
                exception,
                "Push provider rejected a reminder for endpoint {Endpoint} with status {StatusCode}",
                new Uri(inscricao.Endpoint).Host,
                exception.StatusCode);
            await transaction.RollbackAsync();
            falhas++;
            continue;
        }
        catch (HttpRequestException exception)
        {
            logger.LogError(
                exception,
                "Push provider request failed for endpoint {Endpoint}",
                new Uri(inscricao.Endpoint).Host);
            await transaction.RollbackAsync();
            falhas++;
            continue;
        }
        catch (OperationCanceledException exception)
        {
            logger.LogError(
                exception,
                "Push provider request timed out for endpoint {Endpoint}",
                new Uri(inscricao.Endpoint).Host);
            await transaction.RollbackAsync();
            falhas++;
            continue;
        }

        command.Parameters.Clear();
        command.CommandText = """
            UPDATE inscricoes_notificacao
            SET ultima_verificacao = @data
            WHERE endpoint = @endpoint;
            """;
        command.Parameters.AddWithValue("data", localDate);
        command.Parameters.AddWithValue("endpoint", inscricao.Endpoint);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        enviados++;
    }

    var result = new
    {
        inscricoes = subscriptions.Count,
        enviados,
        jaRealizados,
        expirados,
        falhas
    };
    return falhas > 0
        ? Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(result);
}

static async Task RemoveNotificationSubscriptionAsync(string endpoint, NpgsqlDataSource dataSource)
{
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "DELETE FROM inscricoes_notificacao WHERE endpoint = @endpoint;";
    command.Parameters.AddWithValue("endpoint", endpoint);
    await command.ExecuteNonQueryAsync();
}

static bool IsAllowedPushEndpoint(string? endpoint)
{
    if (string.IsNullOrWhiteSpace(endpoint) ||
        endpoint.Length > 2048 ||
        !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
        uri.Scheme != Uri.UriSchemeHttps ||
        !string.IsNullOrEmpty(uri.UserInfo) ||
        uri.Port != 443 ||
        System.Net.IPAddress.TryParse(uri.Host, out _))
    {
        return false;
    }

    var host = uri.Host.TrimEnd('.');
    return host.Equals("fcm.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("fcmregistrations.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("updates.push.services.mozilla.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("web.push.apple.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".notify.windows.com", StringComparison.OrdinalIgnoreCase);
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
record InscricaoNotificacaoDTO(string? Usuario, string? Timezone, InscricaoBrowserDTO? Subscription);
record InscricaoBrowserDTO(string? Endpoint, InscricaoChavesDTO? Keys);
record InscricaoChavesDTO(string? P256dh, string? Auth);
record CancelarInscricaoDTO(string? Endpoint);
record InscricaoSalva(
    string Endpoint,
    string Usuario,
    string UsuarioNormalizado,
    string Timezone,
    string P256dh,
    string Auth);
