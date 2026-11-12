using Microsoft.AspNetCore.Mvc;
using QRCoder;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net;

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

app.MapGet("/api/booths/{standId}", (string standId) =>
{
    var booth = booths.FirstOrDefault(b => string.Equals(b.Id, standId, StringComparison.OrdinalIgnoreCase));
    return booth is null ? Results.NotFound() : Results.Ok(booth);
})
.WithName("GetBooth")
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