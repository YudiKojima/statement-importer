// Gera um CSV de extrato para demonstrações e provas.
// Uso: dotnet run tools/generate-csv.cs -- --lines 1000000 --error-rate 0.01 --seed 42 --out data/1m.csv
using System.Globalization;
using System.Text;

var options = ParseArgs(args);
var lines = int.Parse(options.GetValueOrDefault("lines", "1000"), CultureInfo.InvariantCulture);
var errorRate = double.Parse(options.GetValueOrDefault("error-rate", "0.01"), CultureInfo.InvariantCulture);
var seed = int.Parse(options.GetValueOrDefault("seed", "42"), CultureInfo.InvariantCulture);
var output = options.GetValueOrDefault("out", $"data/{lines}.csv");
var prefix = options.GetValueOrDefault("prefix", $"TX{seed}");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
var random = new Random(seed);
string[] descriptions = ["PIX RECEBIDO", "PIX ENVIADO", "COMPRA CARTAO", "BOLETO PAGO", "TARIFA BANCARIA", "TED RECEBIDA", "SAQUE"];
var start = new DateOnly(2026, 1, 1);
var errors = 0;

using (var writer = new StreamWriter(output, append: false, new UTF8Encoding(false)) { NewLine = "\n" })
{
    writer.WriteLine("transaction_id,date,description,amount");
    for (var i = 1; i <= lines; i++)
    {
        var id = $"{prefix}-{i:D8}";
        var date = start.AddDays(random.Next(0, 365)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var description = $"{descriptions[random.Next(descriptions.Length)]} {random.Next(1000, 9999)}";
        var amount = (random.Next(-500_000, 500_000) / 100m).ToString("0.00", CultureInfo.InvariantCulture);

        if (random.NextDouble() < errorRate)
        {
            errors++;
            (id, date, description, amount) = (errors % 6) switch
            {
                0 => (id, date, description, amount + ",extra"),
                1 => ("", date, description, amount),
                2 => (id, "2026-02-30", description, amount),
                3 => (id, date, "", amount),
                4 => (id, date, description, amount.Replace('.', ',')),
                _ => ($"{prefix}-{Math.Max(1, i - 1):D8}", date, description, amount),
            };
        }

        writer.WriteLine($"{id},{date},{description},{amount}");
    }
}

Console.WriteLine($"{output}: {lines} linhas, ~{errors} com erro proposital (seed {seed}).");

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>();
    for (var i = 0; i + 1 < args.Length; i += 2)
        result[args[i].TrimStart('-')] = args[i + 1];
    return result;
}
