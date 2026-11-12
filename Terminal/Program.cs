using System.Net.Http.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Fair Terminal API", Version = "v1" });
});

var centralUrl = builder.Configuration["CENTRAL_URL"] ?? "http://localhost:5133";
if (!Uri.TryCreate(centralUrl, UriKind.Absolute, out var centralUri) ||
    centralUri.Scheme is not ("http" or "https"))
{
    throw new InvalidOperationException("CENTRAL_URL musí být platná HTTP(S) adresa.");
}

if (!int.TryParse(builder.Configuration["FLOOR"], out var floor) || floor < 1)
{
    throw new InvalidOperationException("FLOOR musí být kladné celé číslo.");
}

var myUrl = builder.Configuration["MY_URL"] ?? "http://localhost:5207";
if (!Uri.TryCreate(myUrl, UriKind.Absolute, out var myUri) ||
    myUri.Scheme is not ("http" or "https"))
{
    throw new InvalidOperationException("MY_URL musí být platná HTTP(S) adresa.");
}

var app = builder.Build();

app.UseSwagger(c =>
{
    c.RouteTemplate = "openapi/{documentName}/openapi.json";
});
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/openapi/v1/openapi.json", "Fair Terminal API v1");
    c.RoutePrefix = "docs";
});

var latestStats = new StatsResponse(floor, 0, []);
var subscriptionsUri = new Uri(
    new Uri(centralUri.ToString().TrimEnd('/') + "/"),
    "api/subscriptions");
var callbackUri = new Uri(
    new Uri(myUri.ToString().TrimEnd('/') + "/"),
    "webhooks/votes");
Guid? subscriptionId = null;
string? subscriptionSecret = null;
var statsLock = new object();
var standTotals = new Dictionary<string, LocalStandTotals>(StringComparer.OrdinalIgnoreCase);
var totalVotes = 0;

app.Lifetime.ApplicationStarted.Register(() =>
{
    _ = RegisterSubscriptionAsync();
});
app.Lifetime.ApplicationStopping.Register(() =>
{
    _ = UnregisterSubscriptionAsync();
});

async Task RegisterSubscriptionAsync()
{
    using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    var registration = new { endpoint = callbackUri.ToString(), floor };

    for (var attempt = 1; attempt <= 15; attempt++)
    {
        try
        {
            using var response = await client.PostAsJsonAsync(subscriptionsUri, registration);
            if (response.IsSuccessStatusCode)
            {
                var created = await response.Content.ReadFromJsonAsync<SubscriptionCreatedResponse>();
                if (created is not null)
                {
                    subscriptionId = created.Id;
                    subscriptionSecret = created.Secret;
                    Console.WriteLine($"Terminál zaregistrován: odběr {created.Id} pro patro {floor}.");
                    return;
                }
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Registrace odběru selhala ({(int)response.StatusCode}): {error}");
            }
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Centrála zatím není dostupná, registrace ({attempt}/15): {ex.Message}");
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    Console.WriteLine("Terminál se nepodařilo zaregistrovat po 15 pokusech.");
}

async Task UnregisterSubscriptionAsync()
{
    if (subscriptionId is not Guid id)
    {
        return;
    }

    using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    try
    {
        var subscriptionDeleteUri = new Uri(
            $"{subscriptionsUri.AbsoluteUri.TrimEnd('/')}/{id}");
        using var response = await client.DeleteAsync(subscriptionDeleteUri, CancellationToken.None);
        Console.WriteLine(response.IsSuccessStatusCode
            ? $"Terminál odregistrován: odběr {id}."
            : $"Odregistrace odběru selhala ({(int)response.StatusCode}).");
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"Odregistrace odběru selhala: {ex.Message}");
    }
}

app.MapPost("/webhooks/votes", async (HttpContext context) =>
{
    context.Request.EnableBuffering();
    using var memoryStream = new MemoryStream();
    await context.Request.Body.CopyToAsync(memoryStream);
    var rawBody = memoryStream.ToArray();
    context.Request.Body.Position = 0;

    var signatureHeader = context.Request.Headers["X-Signature"].FirstOrDefault();
    if (string.IsNullOrEmpty(signatureHeader) ||
        !signatureHeader.StartsWith("sha256=", StringComparison.Ordinal) ||
        string.IsNullOrEmpty(subscriptionSecret))
    {
        return Results.Unauthorized();
    }

    var secretBytes = System.Text.Encoding.UTF8.GetBytes(subscriptionSecret);
    using var hmac = new System.Security.Cryptography.HMACSHA256(secretBytes);
    var hashBytes = hmac.ComputeHash(rawBody);
    var expectedSignature = $"sha256={Convert.ToHexString(hashBytes).ToLowerInvariant()}";
    var expectedSignatureBytes = System.Text.Encoding.UTF8.GetBytes(expectedSignature);
    var receivedSignatureBytes = System.Text.Encoding.UTF8.GetBytes(signatureHeader);

    if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        expectedSignatureBytes,
        receivedSignatureBytes))
    {
        return Results.Unauthorized();
    }

    var notification = System.Text.Json.JsonSerializer.Deserialize<VoteNotification>(
        rawBody,
        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (notification is null)
    {
        return Results.BadRequest();
    }

    if (notification.Floor != floor || notification.Event != "vote_created")
    {
        return Results.NoContent();
    }

    lock (statsLock)
    {
        if (standTotals.TryGetValue(notification.StandId, out var current))
        {
            current = current with
            {
                Votes = current.Votes + 1,
                RatingSum = current.RatingSum + notification.Rating,
                Favorites = current.Favorites + (notification.Favorite ? 1 : 0)
            };
        }
        else
        {
            current = new LocalStandTotals(
                notification.StandId,
                notification.Title,
                1,
                notification.Rating,
                notification.Favorite ? 1 : 0);
        }

        standTotals[notification.StandId] = current;
        totalVotes++;
        latestStats = new StatsResponse(
            floor,
            totalVotes,
            standTotals.Values
                .Select(stand => new StandStats(
                    stand.StandId,
                    stand.Title,
                    stand.Votes,
                    Math.Round((decimal)stand.RatingSum / stand.Votes, 2),
                    stand.Favorites))
                .OrderByDescending(stand => stand.AvgRating)
                .ThenBy(stand => stand.StandId)
                .ToList());
    }

    return Results.Accepted();
});

app.MapGet("/stats", () => Results.Ok(latestStats));

app.MapGet("/", () => Results.Content("""
    <!doctype html>
    <html lang="cs">
    <head>
      <meta charset="utf-8">
      <meta name="viewport" content="width=device-width, initial-scale=1">
      <title>Terminál – patro __FLOOR__</title>
      <style>
        body { font-family: system-ui, sans-serif; margin: 2rem; background: #f5f7fa; color: #17202a; }
        table { border-collapse: collapse; width: 100%; background: white; }
        th, td { border: 1px solid #d5dbe3; padding: .7rem; text-align: left; }
        th { background: #e9eef5; }
        .summary { font-size: 1.2rem; margin-bottom: 1rem; }
      </style>
    </head>
    <body>
      <h1>Výsledky – patro __FLOOR__</h1>
      <div id="summary" class="summary">Načítám…</div>
      <table>
        <thead><tr><th>Stánek</th><th>Název</th><th>Hlasy</th><th>Průměr</th><th>Oblíbené</th></tr></thead>
        <tbody id="stands"></tbody>
      </table>
      <script>
        async function refresh() {
          const response = await fetch('/stats', { cache: 'no-store' });
          if (!response.ok) return;
          const data = await response.json();
          document.getElementById('summary').textContent = `Celkem hlasů: ${data.total_votes}`;
          document.getElementById('stands').innerHTML = data.stands.map(stand =>
            `<tr><td>${stand.stand_id}</td><td>${stand.title}</td><td>${stand.votes}</td><td>${stand.avg_rating}</td><td>${stand.favorites}</td></tr>`
          ).join('');
        }
        refresh();
        setInterval(refresh, 1000);
      </script>
    </body>
    </html>
    """.Replace("__FLOOR__", floor.ToString()), "text/html; charset=utf-8"));

app.Run();

record StatsResponse(
    [property: JsonPropertyName("floor")] int Floor,
    [property: JsonPropertyName("total_votes")] int TotalVotes,
    List<StandStats> Stands);

record SubscriptionCreatedResponse(Guid Id, string Secret);

record LocalStandTotals(
    string StandId,
    string Title,
    int Votes,
    int RatingSum,
    int Favorites);

record VoteNotification(
    [property: JsonPropertyName("event")] string Event,
    int Floor,
    [property: JsonPropertyName("stand_id")] string StandId,
    string Title,
    int Rating,
    bool Favorite);

record StandStats(
    [property: JsonPropertyName("stand_id")] string StandId,
    string Title,
    int Votes,
    [property: JsonPropertyName("avg_rating")] decimal AvgRating,
    int Favorites);
