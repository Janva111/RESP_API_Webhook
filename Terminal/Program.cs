var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

int aktualniPatro = 1;
int posledniPocetHlasu = 0;

app.MapPost("/webhook/notifikace", (StatistikaDto data) => {
    if (data.Patro == aktualniPatro)
    {
        posledniPocetHlasu = data.CelkemHlasu;
        Console.WriteLine($"[WEBHOOK] Přijata aktualizace pro patro {data.Patro}: celkem hlasů = {data.CelkemHlasu}");
    }
    return Results.Ok();
});

app.MapGet("/", () => $"Terminál pro patro {aktualniPatro}. Aktuální počet hlasů: {posledniPocetHlasu}");

_ = Task.Run(async () =>
{
    using var client = new HttpClient(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    var registrace = new { Patro = aktualniPatro, Url = "http://localhost:5207/webhook/notifikace" };

    for (var pokus = 1; pokus <= 15; pokus++)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("http://localhost:5133/api/webhooky", registrace);

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("Terminál úspěšně zaregistrován u centrály!");
                return;
            }

            var chyba = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Registrace selhala ({(int)response.StatusCode}): {chyba}");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Centrála zatím není dostupná (pokus {pokus}/15): {ex.Message}");
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    Console.WriteLine("Terminál se nepodařilo zaregistrovat po 15 pokusech.");
});

app.Run();

record StatistikaDto(int Patro, int CelkemHlasu);