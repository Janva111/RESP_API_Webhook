var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Poslední známá statistika pro zobrazení na obrazovce terminálu
int aktualniPatro = 1;
int posledniPocetHlasu = 0;

// 1. Endpoint pro příjem webhooků od centrály
app.MapPost("/webhook/notifikace", (StatistikaDto data) => {
    if (data.Patro == aktualniPatro)
    {
        posledniPocetHlasu = data.CelkemHlasu;
        Console.WriteLine($"[WEBHOOK] Přijata aktualizace pro patro {data.Patro}: celkem hlasů = {data.CelkemHlasu}");
    }
    return Results.Ok();
});

// 2. Jednoduchý pohled na statistiku patra v prohlížeči
app.MapGet("/", () => $"Terminál pro patro {aktualniPatro}. Aktuální počet hlasů: {posledniPocetHlasu}");

// Spuštění registrace u centrály na pozadí (předpokládáme, že centrála běží např. na portu 5001)
_ = Task.Run(async () => {
    await Task.Delay(2000); // Počkáme, než centrála nabehne
    using var client = new HttpClient();
    var registrace = new { Patro = aktualniPatro, Url = "http://localhost:5002/webhook/notifikace" }; // Uprav porty podle sebe

    try
    {
        var response = await client.PostAsJsonAsync("http://localhost:5001/api/webhooky", registrace);
        Console.WriteLine(response.IsSuccessStatusCode ? "Terminál úspěšně zaregistrován u centrály!" : "Registrace selhala.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Chyba při komunikaci s centrálou: {ex.Message}");
    }
});

app.Run();

record StatistikaDto(int Patro, int CelkemHlasu);