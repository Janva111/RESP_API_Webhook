using Microsoft.AspNetCore.Mvc;
using QRCoder;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Fair Central API", Version = "v1" });
});

var app = builder.Build();

app.UseSwagger(c =>
{
    c.RouteTemplate = "openapi/{documentName}/openapi.json";
});
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/openapi/v1/openapi.json", "Fair Central API v1");
    c.RoutePrefix = "docs";
});

var booths = new List<Booth>
{
    new("S101", "Smart Greenhouse with ESP32", ["Jan Novak"], "C4b", 1, "101"),
    new("S102", "Autonomous Rover ROS2", ["Petr Svoboda", "Jiri Kral"], "C4b", 1, "102"),
    new("S103", "Tesla Coil and Wireless Power Transfer", ["Martin Dvorak"], "C4c", 1, "103"),
    new("S201", "School Classroom Reservation System", ["Marek Horak"], "C4a", 2, "201"),
    new("S202", "Smart Mirror with Raspberry Pi", ["Lucie Cerna", "Eva Vesela"], "C4a", 2, "202"),
    new("S203", "IoT Air Quality Monitoring Station", ["Tomas Prochazka"], "C4c", 2, "203"),
    new("S301", "2D Game Engine in C++", ["David Kucera"], "C4b", 3, "301"),
    new("S302", "Neural Network OCR Reader", ["Filip Vesely", "Ondrej Marek"], "C4a", 3, "302")
};

var configuredPublicUrl = Environment.GetEnvironmentVariable("PUBLIC_URL") ?? "http://localhost:5133";
if (!Uri.TryCreate(configuredPublicUrl, UriKind.Absolute, out var publicUri) ||
    publicUri.Scheme is not ("http" or "https"))
{
    throw new InvalidOperationException("PUBLIC_URL musí být platná HTTP(S) adresa.");
}

var publicUrl = configuredPublicUrl.TrimEnd('/');

var webhooks = new ConcurrentDictionary<int, string>();
var subscriptions = new ConcurrentDictionary<Guid, Subscription>();
var votes = new ConcurrentDictionary<string, ConcurrentDictionary<string, Vote>>(
    StringComparer.OrdinalIgnoreCase);

var usernamePattern = new Regex(
    @"^[\p{L}\p{N}][\p{L}\p{N}._-]{1,31}$",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);

StandStats CreateStandStats(Booth booth)
{
    var boothVotes = votes.TryGetValue(booth.Id, out var storedVotes)
        ? storedVotes.Values.ToArray()
        : [];
    var voteCount = boothVotes.Length;
    var ratingSum = boothVotes.Sum(vote => vote.Rating);

    return new StandStats(
        booth.Id,
        booth.Title,
        booth.Floor,
        voteCount,
        ratingSum,
        voteCount == 0 ? 0.0m : Math.Round((decimal)ratingSum / voteCount, 2),
        boothVotes.Count(vote => vote.Favorite));
}

async Task NotifySubscribersAsync(VoteNotification notification)
{
    var recipients = subscriptions.Values
        .Where(subscription => subscription.Floor is null || subscription.Floor == notification.Floor)
        .ToArray();
    var payloadBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(notification);

    using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    await Task.WhenAll(recipients.Select(async subscription =>
    {
        try
        {
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(subscription.Secret);
            using var hmac = new HMACSHA256(keyBytes);
            var hashBytes = hmac.ComputeHash(payloadBytes);
            var signature = $"sha256={Convert.ToHexString(hashBytes).ToLowerInvariant()}";

            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Endpoint)
            {
                Content = new ByteArrayContent(payloadBytes)
            };
            request.Headers.Add("X-Signature", signature);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Doručení události odběru {subscription.Id} selhalo ({(int)response.StatusCode}).");
            }
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Doručení události odběru {subscription.Id} selhalo: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine($"Doručení události odběru {subscription.Id} vypršelo.");
        }
    }));
}

app.MapPost("/api/subscriptions", (SubscriptionRequest request) =>
{
    if (request.Floor is < 1 || string.IsNullOrWhiteSpace(request.Endpoint) ||
        !Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpointUri) ||
        endpointUri.Scheme is not ("http" or "https"))
    {
        return Results.BadRequest("Floor musí být kladné číslo a endpoint musí být platná HTTP(S) adresa.");
    }

    var subscription = new Subscription(
        Guid.NewGuid(),
        endpointUri.ToString(),
        request.Floor,
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    subscriptions[subscription.Id] = subscription;

    return Results.Created($"/api/subscriptions/{subscription.Id}", new SubscriptionCreatedResponse(
        subscription.Id,
        subscription.Secret));
})
.WithName("CreateSubscription")
.WithOpenApi(operation =>
{
    operation.Summary = "Vytvoří odběr změn hlasování";
    operation.Description = "Po uložení hlasu centrála odešle na endpoint POST s událostí vote_created. Odběr s floor null dostává události ze všech pater.";
    operation.RequestBody.Content["application/json"].Example = new JsonObject
    {
        ["endpoint"] = "http://localhost:5207/webhooks/votes",
        ["floor"] = 2
    };
    return operation;
});

app.MapGet("/api/subscriptions", () =>
    Results.Ok(subscriptions.Values
        .Select(subscription => new SubscriptionSummary(
            subscription.Id,
            subscription.Endpoint,
            subscription.Floor))
        .OrderBy(subscription => subscription.Id)
        .ToList()))
.WithName("GetSubscriptions")
.WithOpenApi();

app.MapDelete("/api/subscriptions/{id:guid}", (Guid id) =>
    subscriptions.TryRemove(id, out _)
        ? Results.NoContent()
        : Results.NotFound());

app.MapPost("/api/webhooky", (WebhookRegistration registration) =>
{
    if (registration.Patro < 1 || string.IsNullOrWhiteSpace(registration.Url) ||
        !Uri.TryCreate(registration.Url, UriKind.Absolute, out var callbackUrl) ||
        callbackUrl.Scheme is not ("http" or "https"))
    {
        return Results.BadRequest("Patro musí být kladné číslo a Url musí být platná HTTP(S) adresa.");
    }

    webhooks[registration.Patro] = callbackUrl.ToString();
    return Results.Ok(new { message = "Terminál byl zaregistrován." });
})
.WithName("RegisterWebhook")
.WithOpenApi();

app.MapPost("/api/statistika", async (Statistika statistika, CancellationToken cancellationToken) =>
{
    if (statistika.Patro < 1 || statistika.CelkemHlasu < 0)
    {
        return Results.BadRequest("Patro musí být kladné číslo a počet hlasů nesmí být záporný.");
    }

    if (webhooks.TryGetValue(statistika.Patro, out var webhookUrl))
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        try
        {
            await client.PostAsJsonAsync(webhookUrl, statistika, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return Results.Problem("Aktualizaci se nepodařilo doručit terminálu.", statusCode: 502);
        }
    }

    return Results.Ok();
})
.WithName("PublishStatistics")
.WithOpenApi();

app.MapPost("/api/booths/{standId}/votes", (HttpResponse response, string standId, VoteRequest? request) =>
{
    var booth = booths.FirstOrDefault(b => string.Equals(b.Id, standId, StringComparison.OrdinalIgnoreCase));
    if (booth is null)
    {
        return Results.NotFound();
    }

    var errors = new Dictionary<string, string[]>();
    var username = request?.Username?.Trim();

    if (string.IsNullOrWhiteSpace(username) || !usernamePattern.IsMatch(username))
    {
        errors["username"] = ["Username musí mít 2 až 32 znaků a smí obsahovat pouze písmena, číslice, tečku, podtržítko nebo pomlčku."];
    }

    if (request?.Rating is null || request.Rating < 1 || request.Rating > 5)
    {
        errors["rating"] = ["Rating musí být celé číslo od 1 do 5."];
    }

    if (request?.Favorite is null)
    {
        errors["favorite"] = ["Favorite je povinné a musí být true nebo false."];
    }

    if (errors.Count > 0)
    {
        return Results.Json(new { errors }, statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    var boothVotes = votes.GetOrAdd(booth.Id, _ => new ConcurrentDictionary<string, Vote>(StringComparer.OrdinalIgnoreCase));
    var normalizedUsername = username!.ToUpperInvariant();
    var vote = new Vote(username, request!.Rating!.Value, request.Favorite!.Value);

    if (!boothVotes.TryAdd(normalizedUsername, vote))
    {
        return Results.Conflict(new { message = "Tento žák už pro tento stánek hlasoval." });
    }

    var notification = new VoteNotification(
        "vote_created",
        booth.Floor,
        booth.Id,
        booth.Title,
        vote.Rating,
        vote.Favorite);
    response.OnCompleted(() => NotifySubscribersAsync(notification));

    return Results.Created($"/api/booths/{booth.Id}/votes", new
    {
        message = "Hlas byl uložen.",
        booth = booth.Id,
        username = vote.Username,
        rating = vote.Rating,
        favorite = vote.Favorite
    });
})
.WithName("CreateBoothVote")
.Accepts<VoteRequest>("application/json")
.Produces(StatusCodes.Status201Created)
.Produces(StatusCodes.Status404NotFound)
.Produces(StatusCodes.Status409Conflict)
.Produces(StatusCodes.Status422UnprocessableEntity)
.WithOpenApi(operation =>
{
    operation.Summary = "Uloží jeden hlas pro stánek";
    operation.Description = "Jeden username může pro konkrétní stánek hlasovat pouze jednou. Username se porovnává bez ohledu na velikost písmen.";
    operation.RequestBody.Content["application/json"].Example = new JsonObject
    {
        ["username"] = "novak.jan",
        ["rating"] = 5,
        ["favorite"] = true
    };
    return operation;
});

app.MapGet("/api/stats", ([FromQuery] int? floor) =>
{
    var selectedBooths = floor.HasValue
        ? booths.Where(booth => booth.Floor == floor.Value)
        : booths.AsEnumerable();
    var stands = selectedBooths.Select(CreateStandStats).ToList();

    return Results.Ok(new StatsResponse(
        DateTime.UtcNow,
        stands.Sum(stand => stand.Votes),
        stands));
})
.WithName("GetStats")
.WithOpenApi();

app.MapGet("/api/stats/{standId}", (string standId) =>
{
    var booth = booths.FirstOrDefault(b => string.Equals(b.Id, standId, StringComparison.OrdinalIgnoreCase));
    return booth is null
        ? Results.NotFound()
        : Results.Ok(CreateStandStats(booth));
})
.WithName("GetStandStats")
.WithOpenApi();

app.MapGet("/api/booths/{standId}", (HttpContext httpContext, string standId) =>
{
    var booth = booths.FirstOrDefault(b => string.Equals(b.Id, standId, StringComparison.OrdinalIgnoreCase));
    return booth is null
        ? Results.NotFound()
        : Results.Content(CreateBoothPage(booth, httpContext.Request.PathBase), "text/html; charset=utf-8");
})
.WithName("GetBoothPage")
.WithOpenApi();

app.MapGet("/qr/{standId}.svg", (string standId) =>
{
    var booth = booths.FirstOrDefault(b => string.Equals(b.Id, standId, StringComparison.OrdinalIgnoreCase));
    if (booth is null)
    {
        return Results.NotFound();
    }

    var targetUrl = $"{publicUrl}/api/booths/{Uri.EscapeDataString(booth.Id)}";
    using var generator = new QRCodeGenerator();
    using var data = generator.CreateQrCode(targetUrl, QRCodeGenerator.ECCLevel.Q);
    var svg = new SvgQRCode(data).GetGraphic(6);

    return Results.Content(svg, "image/svg+xml; charset=utf-8");
})
.WithName("GetBoothQrCode")
.WithOpenApi();

app.MapGet("/qr", () =>
{
    var boothCards = booths.Select(booth =>
    {
        var title = WebUtility.HtmlEncode(booth.Title);
        var className = WebUtility.HtmlEncode(booth.ClassName);
        var room = WebUtility.HtmlEncode(booth.Room);
        var id = WebUtility.HtmlEncode(booth.Id);

        return $"""
            <article class="booth">
                <img src="/qr/{Uri.EscapeDataString(booth.Id)}.svg" alt="QR kód stánku {id}">
                <strong>{id}</strong>
                <span>Učebna: {room} ({className})</span>
                <span>{title}</span>
            </article>
            """;
    });

    var html = $$"""
        <!doctype html>
        <html lang="cs">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>QR kódy stánků</title>
            <style>
                body { font-family: sans-serif; margin: 2rem; }
                .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 2rem; }
                .booth { break-inside: avoid; display: flex; flex-direction: column; gap: .35rem; text-align: center; }
                .booth img { width: 180px; height: 180px; margin: auto; }
                @media print { body { margin: 1rem; } }
            </style>
        </head>
        <body>
            <h1>QR kódy stánků</h1>
            <section class="grid">{{string.Join(Environment.NewLine, boothCards)}}</section>
        </body>
        </html>
        """;

    return Results.Content(html, "text/html; charset=utf-8");
})
.WithName("GetAllBoothQrCodes")
.WithOpenApi();

app.MapGet("/api/booths", ([FromQuery] int? floor) =>
{
    var result = floor.HasValue
        ? booths.Where(b => b.Floor == floor.Value).ToList()
        : booths;

    return Results.Ok(result);
})
.WithName("GetBooths")
.WithOpenApi(operation => {
    operation.Summary = "Gets a list of all booths or booths on a specific floor";
    return operation;
});

string CreateBoothPage(Booth booth, PathString pathBase)
{
    var id = WebUtility.HtmlEncode(booth.Id);
    var title = WebUtility.HtmlEncode(booth.Title);
    var authors = WebUtility.HtmlEncode(string.Join(", ", booth.Authors));
    var className = WebUtility.HtmlEncode(booth.ClassName);
    var room = WebUtility.HtmlEncode(booth.Room);
    var voteUrl = $"{pathBase}/api/booths/{Uri.EscapeDataString(booth.Id)}/votes";

    return $$"""
        <!doctype html>
        <html lang="cs">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Hlasování – {{title}}</title>
            <style>
                :root { color-scheme: light; font-family: system-ui, sans-serif; }
                body { margin: 0; background: #f3f4f6; color: #172033; }
                main { max-width: 32rem; margin: auto; padding: 1rem; }
                .card { background: white; border-radius: 1rem; padding: 1.25rem; box-shadow: 0 2px 12px #0001; }
                label { display: block; margin-top: 1rem; font-weight: 600; }
                input[type=text], input[type=number] { box-sizing: border-box; width: 100%; padding: .8rem; margin-top: .35rem; border: 1px solid #c7ccd5; border-radius: .5rem; font-size: 1rem; }
                .favorite { display: flex; gap: .5rem; align-items: center; }
                button { width: 100%; margin-top: 1.25rem; padding: .85rem; border: 0; border-radius: .5rem; background: #1769e0; color: white; font-size: 1rem; font-weight: 700; }
                #result { margin-top: 1rem; padding: .75rem; border-radius: .5rem; }
                .success { background: #dcfce7; color: #166534; }
                .error { background: #fee2e2; color: #991b1b; }
                small { color: #586174; }
            </style>
        </head>
        <body>
            <main>
                <section class="card">
                    <h1>{{title}}</h1>
                    <p><strong>Autoři:</strong> {{authors}}</p>
                    <p><strong>Třída:</strong> {{className}}<br><strong>Učebna:</strong> {{room}}</p>
                    <hr>
                    <form id="vote-form">
                        <label for="username">Jméno žáka</label>
                        <input id="username" name="username" type="text" autocomplete="username" placeholder="novak.jan" required pattern="[A-Za-zÀ-ž0-9][A-Za-zÀ-ž0-9._-]{1,31}">
                        <small>Např. novak.jan</small>

                        <label for="rating">Hodnocení (1–5)</label>
                        <input id="rating" name="rating" type="number" min="1" max="5" step="1" required>

                        <label class="favorite"><input id="favorite" name="favorite" type="checkbox"> Označit jako favorita</label>
                        <button type="submit">Odeslat hlas</button>
                    </form>
                    <div id="result" hidden></div>
                </section>
            </main>
            <script>
                const form = document.getElementById('vote-form');
                const result = document.getElementById('result');
                form.addEventListener('submit', async (event) => {
                    event.preventDefault();
                    result.hidden = true;
                    const body = {
                        username: document.getElementById('username').value,
                        rating: Number(document.getElementById('rating').value),
                        favorite: document.getElementById('favorite').checked
                    };
                    try {
                        const response = await fetch('{{voteUrl}}', {
                            method: 'POST',
                            headers: { 'Content-Type': 'application/json' },
                            body: JSON.stringify(body)
                        });
                        const data = await response.json();
                        result.className = response.ok ? 'success' : 'error';
                        result.textContent = response.status === 409
                            ? 'Tento žák už pro tento stánek hlasoval.'
                            : response.ok ? data.message : Object.values(data.errors ?? {}).flat().join(' ');
                    } catch {
                        result.className = 'error';
                        result.textContent = 'Hlas se nepodařilo odeslat. Zkontrolujte připojení.';
                    }
                    result.hidden = false;
                });
            </script>
        </body>
        </html>
        """;
}

app.Run();

public record Booth(
    string Id,
    string Title,
    List<string> Authors,
    [property: System.Text.Json.Serialization.JsonPropertyName("class_name")] string ClassName,
    int Floor,
    string Room
);

public record WebhookRegistration(int Patro, string Url);

public record Statistika(int Patro, int CelkemHlasu);

public record SubscriptionRequest(string Endpoint, int? Floor);

public record SubscriptionCreatedResponse(Guid Id, string Secret);

public record SubscriptionSummary(Guid Id, string Endpoint, int? Floor);

public record Subscription(Guid Id, string Endpoint, int? Floor, string Secret);

public record VoteNotification(
    [property: System.Text.Json.Serialization.JsonPropertyName("event")] string Event,
    int Floor,
    [property: System.Text.Json.Serialization.JsonPropertyName("stand_id")] string StandId,
    string Title,
    int Rating,
    bool Favorite);

public record VoteRequest(string? Username, int? Rating, bool? Favorite);

public record Vote(string Username, int Rating, bool Favorite);

public record StatsResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("generated_at")] DateTime GeneratedAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("total_votes")] int TotalVotes,
    List<StandStats> Stands
);

public record StandStats(
    [property: System.Text.Json.Serialization.JsonPropertyName("stand_id")] string StandId,
    string Title,
    int Floor,
    int Votes,
    [property: System.Text.Json.Serialization.JsonPropertyName("rating_sum")] int RatingSum,
    [property: System.Text.Json.Serialization.JsonPropertyName("avg_rating")] decimal AvgRating,
    int Favorites
);