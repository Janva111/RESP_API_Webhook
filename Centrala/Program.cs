var builder = WebApplication.CreateBuilder(args);

// Přidání Swaggeru pro /docs a /openapi.json
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Konfigurace Swagger UI na /docs (dle zadání)
if (app.Environment.IsDevelopment() || true) // nechej zapnuté i pro testování
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Centrala API V1");
        c.RoutePrefix = "docs"; // Zpřístupní dokumentaci na /docs
    });
}

// Paměťová úložiska pro ukázku
var stanky = new List<Stanek> {
    new Stanek(1, "Vývoj mobilních aplikací", 1),
    new Stanek(2, "3D Tisk a IoT", 2)
};
var hlasy = new List<Hlas>();
var webhooky = new List<WebhookRegistrace>();

// 1. Endpoint: Katalog stánků
app.MapGet("/api/stanky", () => stanky);

// 2. Endpoint: Příjem hlasu od žáka
app.MapPost("/api/hlasy", async (HlasDto dto, HttpContext context) => {
    var hlas = new Hlas(dto.UzivatelskeJmeno, dto.StanekId, DateTime.Now);
    hlasy.Add(hlas);

    // Zjištění patra stánku
    var stanek = stanky.FirstOrDefault(s => s.Id == dto.StanekId);
    if (stanek != null)
    {
        // Najdeme webhooky pro dané patro a pošleme jim notifikaci (Webhook)
        using var client = new HttpClient();
        foreach (var wh in webhooky.Where(w => w.Patro == stanek.Patro))
        {
            try
            {
                // Pošleme aktuální statistiku patra
                var statistika = new { Patro = stanek.Patro, CelkemHlasu = hlasy.Count(h => stanky.First(s => s.Id == h.StanekId).Patro == stanek.Patro) };
                await client.PostAsJsonAsync(wh.Url, statistika);
            }
            catch { /* Ošetření, kdyby terminál neodpovídal */ }
        }
    }

    return Results.Created($"/api/hlasy/{hlas.Id}", hlas);
});

// 3. Endpoint: Registrace webhooku od terminálu
app.MapPost("/api/webhooky", (WebhookRegistrace reg) => {
    webhooky.RemoveAll(w => w.Url == reg.Url); // Zamezení duplicit
    webhooky.Add(reg);
    return Results.Ok();
});

app.Run();

// Datové modely
record Stanek(int Id, string Nazev, int Patro);
record HlasDto(string UzivatelskeJmeno, int StanekId);
record Hlas(Guid Id, string UzivatelskeJmeno, int StanekId, DateTime Cas)
{
    public Hlas(string uzivatelskeJmeno, int stanekId, DateTime cas) : this(Guid.NewGuid(), uzivatelskeJmeno, stanekId, cas) { }
}
record WebhookRegistrace(int Patro, string Url);