using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Conduit.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

var configuration = new ConfigurationBuilder().Build();
foreach (var url in new[]
{
    "postgres://conduit@localhost/test?sslmode=verify-full",
    "postgres://conduit@localhost/test?sslrootcert=ca.pem",
    "postgres://conduit@localhost/test#sslmode=verify-full",
    "https://conduit@localhost/test"
})
{
    try
    {
        new ServiceCollection().AddConduitDb(url, configuration);
        throw new InvalidOperationException("Unsupported database URL settings were accepted.");
    }
    catch (ArgumentException error) when (error.ParamName == "databaseUrl")
    {
        Console.WriteLine("PASS: unsupported database URL rejected at registration.");
    }
}

if (args.Length is < 2 or > 3)
    throw new ArgumentException("Usage: RegressionChecks <HTTP base URL> <Npgsql connection string> [favorite|unfavorite|delete]. Use only a disposable trial database.");
var operations = args.Length == 3 ? new[] { args[2] } : ["favorite", "unfavorite", "delete"];
if (operations.Any(operation => operation is not ("favorite" or "unfavorite" or "delete")))
    throw new ArgumentException("Unknown regression operation.");

using var http = new HttpClient { BaseAddress = new Uri(args[0]), Timeout = TimeSpan.FromSeconds(30) };
await using var connection = new NpgsqlConnection(args[1]);
await connection.OpenAsync();
await using var observer = new NpgsqlConnection(args[1]);
await observer.OpenAsync();
var suffix = Guid.NewGuid().ToString("N");
var author = (await Send(HttpMethod.Post, "/api/users",
    new { user = new { username = $"race-author-{suffix}", email = $"race-author-{suffix}@example.test", password = "regression-password" } },
    status: HttpStatusCode.Created))!["user"]!;
var viewer = (await Send(HttpMethod.Post, "/api/users",
    new { user = new { username = $"race-viewer-{suffix}", email = $"race-viewer-{suffix}@example.test", password = "regression-password" } },
    status: HttpStatusCode.Created))!["user"]!;
var authorToken = author["token"]!.GetValue<string>();
var viewerToken = viewer["token"]!.GetValue<string>();
var viewerId = Convert.ToInt64(await Scalar(connection, "SELECT id FROM users WHERE username = $1", viewer["username"]!.GetValue<string>()));

foreach (var operation in operations)
{
    var article = (await Send(HttpMethod.Post, "/api/articles",
        new { article = new { title = $"Race {operation} {suffix}", description = "regression", body = "body" } },
        authorToken, HttpStatusCode.Created))!["article"]!;
    var slug = article["slug"]!.GetValue<string>();
    var id = Convert.ToInt64(await Scalar(connection, "SELECT id FROM articles WHERE slug = $1", slug));
    var renamedTitle = $"Renamed {operation} {suffix}";
    var renamedSlug = $"renamed-{operation}-{suffix}-{id}";
    Task<JsonNode?>? pending = null;
    try
    {
        if (operation != "delete")
            await Send(HttpMethod.Post, $"/api/articles/{slug}/favorite", token: viewerToken);

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            if (operation == "delete")
                await Execute(connection, "UPDATE articles SET title = $1, slug = $2 WHERE id = $3", renamedTitle, renamedSlug, id);
            else
                // An uncommitted update blocks both conflict checking and DELETE.
                await Execute(connection, "UPDATE favorites SET user_id = user_id WHERE user_id = $1 AND article_id = $2", viewerId, id);

            var method = operation == "favorite" ? HttpMethod.Post : HttpMethod.Delete;
            var path = $"/api/articles/{slug}" + (operation == "delete" ? "" : "/favorite");
            pending = Send(method, path, token: operation == "delete" ? authorToken : viewerToken,
                status: operation == "delete" ? HttpStatusCode.NoContent : HttpStatusCode.OK);
            await WaitForBlockedMutation(pending, operation, connection.ProcessID);

            if (operation != "delete")
            {
                var renamed = (await Send(HttpMethod.Put, $"/api/articles/{slug}",
                    new { article = new { title = renamedTitle } }, authorToken))!["article"]!;
                Assert(renamed["slug"]!.GetValue<string>() == renamedSlug, "Rename did not produce the expected slug.");
            }
            await transaction.CommitAsync();
        }

        var response = await pending;
        if (operation == "delete")
            Assert(Convert.ToInt64(await Scalar(connection, "SELECT count(*) FROM articles WHERE id = $1", id)) == 0,
                "Delete returned success but left the concurrently renamed article behind.");
        else
        {
            var returned = response!["article"]!;
            var expectedFavorite = operation == "favorite";
            Assert(returned["slug"]!.GetValue<string>() == renamedSlug, "Response did not follow the stable article ID.");
            Assert(returned["favorited"]!.GetValue<bool>() == expectedFavorite, "Response favorite state is incorrect.");
            var count = Convert.ToInt64(await Scalar(connection, "SELECT count(*) FROM favorites WHERE user_id = $1 AND article_id = $2", viewerId, id));
            Assert(count == (expectedFavorite ? 1 : 0), "Mutation did not affect the resolved article.");
        }
        Console.WriteLine($"PASS: deterministic {operation} concurrent-rename regression.");
    }
    finally
    {
        // Drain a request only after its blocking transaction has been disposed.
        if (pending is not null)
        {
            try { await pending; }
            catch (Exception error) { Console.Error.WriteLine($"Pending request failed during cleanup: {error.Message}"); }
        }
        await Execute(connection, "DELETE FROM articles WHERE id = $1", id);
    }
}
Console.WriteLine($"4 database URL checks and {operations.Length} deterministic rename regressions passed.");

async Task<JsonNode?> Send(HttpMethod method, string path, object? body = null, string? token = null, HttpStatusCode status = HttpStatusCode.OK)
{
    using var request = new HttpRequestMessage(method, path);
    if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
    if (body is not null) request.Content = JsonContent.Create(body);
    using var response = await http.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();
    Assert(response.StatusCode == status, $"{method} {path}: expected {(int)status}, got {(int)response.StatusCode}: {text}");
    return text.Length == 0 ? null : JsonNode.Parse(text);
}

async Task WaitForBlockedMutation(Task request, string operation, int blocker)
{
    var timer = Stopwatch.StartNew();
    var prefix = operation switch
    {
        "favorite" => "INSERT INTO favorites",
        "unfavorite" => "DELETE FROM favorites",
        _ => "DELETE FROM articles"
    };
    while (timer.Elapsed < TimeSpan.FromSeconds(10))
    {
        var blocked = Convert.ToBoolean(await Scalar(observer,
            "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND usename = current_user " +
            "AND wait_event_type = 'Lock' AND query LIKE $1 AND $2 = ANY(pg_blocking_pids(pid)))", prefix + "%", blocker));
        if (blocked) return;
        if (request.IsCompleted)
        {
            await request;
            throw new InvalidOperationException($"{operation} completed before the intended lock barrier.");
        }
        await Task.Delay(25);
    }
    throw new TimeoutException($"Did not observe {operation} blocked on the regression transaction.");
}

static NpgsqlCommand Command(NpgsqlConnection connection, string sql, object[] parameters)
{
    var command = new NpgsqlCommand(sql, connection);
    foreach (var parameter in parameters) command.Parameters.Add(new NpgsqlParameter { Value = parameter });
    return command;
}

static async Task<object?> Scalar(NpgsqlConnection connection, string sql, params object[] parameters)
{
    await using var command = Command(connection, sql, parameters);
    return await command.ExecuteScalarAsync();
}

static async Task Execute(NpgsqlConnection connection, string sql, params object[] parameters)
{
    await using var command = Command(connection, sql, parameters);
    await command.ExecuteNonQueryAsync();
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
