# Fase 01 — Núcleo do pipeline: plano de implementação

> **Para agentes:** SUB-SKILL OBRIGATÓRIA: usar `superpowers:subagent-driven-development` (recomendado) ou `superpowers:executing-plans` para executar tarefa por tarefa. Os passos usam checkbox (`- [ ]`).

**Objetivo:** API (Native AOT), worker (EF Core), migrator, Postgres e RabbitMQ rodando no compose, importando CSVs de 1M de linhas em blocos idempotentes, com testes unitários e de integração.

**Arquitetura:** a `api` grava o upload no volume `uploads`, registra a importação e publica `ImportRequested`. O `worker` divide o arquivo em blocos de 10.000 linhas e publica um `ProcessChunk` por bloco. Qualquer worker processa um bloco numa única transação e só dá ack depois do commit. O schema pertence ao EF Core e é aplicado pelo `migrator` (bundle one-shot). Desenho completo em [`../../architecture/system-design.md`](../../architecture/system-design.md); escopo da fase em [`design.md`](design.md).

**Stack:** .NET 10, ASP.NET Core Minimal API (Native AOT), Dapper.AOT, Npgsql 10, EF Core 10 + Npgsql provider + EFCore.NamingConventions, RabbitMQ.Client 7, xUnit v3 (Microsoft.Testing.Platform), Testcontainers 4, PostgreSQL 17, RabbitMQ 4, Docker Compose.

> **Validação prévia:** todo o código deste plano foi escrito e executado numa solution de rascunho em 2026-10-19/20. Resultados: build sem avisos, 39 testes unitários e 15 de integração verdes, `compose up --wait` saudável, 1M de linhas com contagem exata (inclusive com um worker morto no meio), recuperação com o RabbitMQ parado, reconciliador e hot reload. Se algo divergir do esperado, é sinal de ambiente diferente (versão do SDK/Docker), não de código não testado. Na execução da fase (2026-10-21), todo o plano rodou no repositório real, inclusive o que não tinha passado pelo rascunho. Ajustes feitos durante a execução: build antes do `dotnet ef migrations add` (Task 5) e um novo procedimento para o critério 8 (Task 13).

## Restrições globais

- Raiz do repositório: `C:\Program Files\source\statement-importer`. Comandos rodam a partir da raiz, no **Git Bash**.
- **Nunca commitar durante a fase** (regra do [`CLAUDE.md`](../../../CLAUDE.md)). Onde o skill pediria commit, há um **checkpoint**: rodar o build/teste indicado e seguir. O commit acontece só no `/finishing-a-development-branch`.
- SDK: `global.json` com `"version": "10.0.100"`, `"rollForward": "latestFeature"`, e `"test": { "runner": "Microsoft.Testing.Platform" }`. O .NET 10 SDK **não roda** xUnit v3 via VSTest; sem essa opção, `dotnet test` falha.
- Todos os projetos: `net10.0`, `Nullable` e `ImplicitUsings` habilitados, `InvariantGlobalization=true` (imagens chiseled não têm ICU).
- Versões centralizadas em `Directory.Packages.props` com `CentralPackageTransitivePinningEnabled=true` (resolve o conflito EF Relational 10.0.4 × 10.0.13).
- `StatementImporter.Api` e tudo que ele referencia: nenhum warning de trimming/AOT. A `api` usa `TreatWarningsAsErrors=true`.
- Imagens de terceiros com tag de versão: `postgres:17-alpine`, `rabbitmq:4-management-alpine`; .NET: `sdk:10.0`, `sdk:10.0-noble-aot`, `runtime:10.0-noble-chiseled`, `runtime-deps:10.0-noble-chiseled`.
- Bloco = **10.000** linhas não vazias; até **5** tentativas por reentrega; teto de **10.000** erros detalhados por importação; upload até **200 MB**.
- Testes de integração e tarefas de container precisam do **Docker Desktop rodando**. Se o Testcontainers acusar `Failed to connect to Docker endpoint`, confira primeiro `docker info`: nos testes do rascunho, a causa foi o Docker Desktop parado.
- Nada de segredo commitado: `secrets/db_password.txt` e `.env` ficam no `.gitignore`.

## Mapa de arquivos

```
global.json · Directory.Build.props · Directory.Packages.props · StatementImporter.slnx · dotnet-tools.json
.gitignore · .dockerignore · compose.yml · compose.dev.yml · secrets/db_password.txt.example
src/StatementImporter.Contracts/
  Csv/CsvFormat.cs · Csv/ErrorReasons.cs · Csv/LineValidator.cs · Csv/ChunkPlanner.cs · Csv/ChunkReader.cs
  Messaging/Messages.cs · Messaging/QueueTopology.cs · Messaging/MessagePublisher.cs
  Configuration/ServiceConfiguration.cs · Http/Responses.cs · Statuses.cs · ContractsJsonContext.cs
src/StatementImporter.Data/
  Entities/Import.cs · Entities/ImportChunk.cs · Entities/BankTransaction.cs · Entities/ImportError.cs
  StatementImporterDbContext.cs · DesignTimeDbContextFactory.cs · Migrations/ (gerado) · Dockerfile
src/StatementImporter.Worker/
  ImportOptions.cs · Imports/ImportRequestedHandler.cs · Imports/ProcessChunkHandler.cs · Imports/ChunkWriter.cs
  Hosting/Heartbeat.cs · Hosting/ConsumerHost.cs · Hosting/Reconciler.cs · Program.cs · appsettings.json · Dockerfile
src/StatementImporter.Api/
  HealthCheck.cs · Imports/UploadStorage.cs · Imports/Paging.cs · Imports/ImportRepository.cs · Imports/ImportEndpoints.cs
  Program.cs · Dockerfile
tests/StatementImporter.UnitTests/  Csv/*Tests.cs · Configuration/ServiceConfigurationTests.cs · Hosting/HeartbeatTests.cs
tests/StatementImporter.IntegrationTests/  InfrastructureFixture.cs · Worker/ImportProcessingTests.cs · Api/ImportEndpointsTests.cs
tools/generate-csv.cs
```

---

### Task 1: Base da solution

**Arquivos:**
- Criar: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `StatementImporter.slnx`, `dotnet-tools.json`, `secrets/db_password.txt.example`
- Conferir: `.gitignore` (já existe)

**Interfaces:**
- Produz: solution vazia `StatementImporter.slnx`; ferramenta local `dotnet ef` 10.0.13; versões de pacotes usadas por todas as tasks.

- [ ] **Passo 1: criar `global.json`**

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

- [ ] **Passo 2: criar `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

- [ ] **Passo 3: criar `Directory.Packages.props`**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Dapper" Version="2.1.89" />
    <PackageVersion Include="Dapper.AOT" Version="1.1.0" />
    <PackageVersion Include="EFCore.NamingConventions" Version="10.0.1" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.13" />
    <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.13" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.13" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.13" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.13" />
    <PackageVersion Include="Microsoft.Extensions.Configuration.Abstractions" Version="10.0.13" />
    <PackageVersion Include="Microsoft.Extensions.Configuration.EnvironmentVariables" Version="10.0.13" />
    <PackageVersion Include="Microsoft.Extensions.Configuration.KeyPerFile" Version="10.0.13" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.13" />
    <PackageVersion Include="Npgsql" Version="10.0.3" />
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
    <PackageVersion Include="RabbitMQ.Client" Version="7.2.2" />
    <PackageVersion Include="Scalar.AspNetCore" Version="2.18.0" />
    <PackageVersion Include="Testcontainers.PostgreSql" Version="4.16.0" />
    <PackageVersion Include="Testcontainers.RabbitMq" Version="4.16.0" />
    <PackageVersion Include="xunit.v3" Version="4.0.2" />
  </ItemGroup>
</Project>
```

- [ ] **Passo 4: criar solution e ferramenta local do EF**

```bash
dotnet new sln -n StatementImporter --format slnx
dotnet new tool-manifest
dotnet tool install dotnet-ef --version 10.0.13
```

No .NET 10, o manifest é criado como `dotnet-tools.json` **na raiz** (não em `.config/`). O Dockerfile do migrator (Task 10) depende desse caminho.

- [ ] **Passo 5: criar `secrets/db_password.txt.example`** com o conteúdo `change-me`, **sem quebra de linha no final**:

```bash
mkdir -p secrets && printf 'change-me' > secrets/db_password.txt.example
```

- [ ] **Passo 6: conferir `.gitignore`.** Ele já contém `bin/`, `obj/`, `.vs/`, `*.user`, `.env`, `secrets/*.txt`, `!secrets/*.example`, `data/` e `TestResults/`. Nada a mudar.

- [ ] **Passo 7: verificar**

Rodar: `dotnet --version && dotnet tool run dotnet-ef --version`
Esperado: versão `10.0.x` do SDK, e `10.0.13` do EF.

- [ ] **Checkpoint** (sem commit).

---

### Task 2: Formato do CSV e validação de linha

**Arquivos:**
- Criar: `src/StatementImporter.Contracts/StatementImporter.Contracts.csproj`, `Csv/CsvFormat.cs`, `Csv/ErrorReasons.cs`, `Csv/LineValidator.cs`
- Criar: `tests/StatementImporter.UnitTests/StatementImporter.UnitTests.csproj`, `Csv/CsvFormatTests.cs`, `Csv/LineValidatorTests.cs`

**Interfaces:**
- Produz:
  - `CsvFormat.Header` (`"transaction_id,date,description,amount"`), `CsvFormat.IsHeader(string) : bool`, `CsvFormat.IsBlank(ReadOnlySpan<char>) : bool`
  - `ErrorReasons.ColumnCount | InvalidTransactionId | InvalidDate | InvalidDescription | InvalidAmount | Duplicate` (strings)
  - `record ValidLine(string TransactionId, DateOnly Date, string Description, decimal Amount)`
  - `readonly record struct LineResult(ValidLine? Line, string? ErrorReason)` com `IsValid`
  - `LineValidator.Validate(string line) : LineResult`

- [ ] **Passo 1: criar os projetos**

`src/StatementImporter.Contracts/StatementImporter.Contracts.csproj` (os pacotes entram na Task 4):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>

</Project>
```

`tests/StatementImporter.UnitTests/StatementImporter.UnitTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\StatementImporter.Contracts\StatementImporter.Contracts.csproj" />
  </ItemGroup>

</Project>
```

```bash
dotnet sln StatementImporter.slnx add src/StatementImporter.Contracts/StatementImporter.Contracts.csproj tests/StatementImporter.UnitTests/StatementImporter.UnitTests.csproj
```

- [ ] **Passo 2: escrever os testes que falham**

`tests/StatementImporter.UnitTests/Csv/CsvFormatTests.cs`:

```csharp
using StatementImporter.Contracts.Csv;

namespace StatementImporter.UnitTests.Csv;

public class CsvFormatTests
{
    [Theory]
    [InlineData("transaction_id,date,description,amount")]
    [InlineData("transaction_id,date,description,amount\r")]
    [InlineData("\uFEFFtransaction_id,date,description,amount")]
    public void Header_is_recognized_with_bom_and_crlf(string line) => Assert.True(CsvFormat.IsHeader(line));

    [Theory]
    [InlineData("id,date,description,amount")]
    [InlineData("transaction_id,date,amount,description")]
    [InlineData("")]
    public void Other_headers_are_rejected(string line) => Assert.False(CsvFormat.IsHeader(line));

    [Theory]
    [InlineData("", true)]
    [InlineData("   \t\r", true)]
    [InlineData(" x ", false)]
    public void Blank_lines(string line, bool expected) => Assert.Equal(expected, CsvFormat.IsBlank(line));
}
```

`tests/StatementImporter.UnitTests/Csv/LineValidatorTests.cs`:

```csharp
using StatementImporter.Contracts.Csv;

namespace StatementImporter.UnitTests.Csv;

public class LineValidatorTests
{
    [Fact]
    public void Valid_line_is_parsed()
    {
        var result = LineValidator.Validate("TX-1,2026-03-15,PIX RECEBIDO JOAO,1500.00");

        Assert.True(result.IsValid);
        Assert.Equal(new ValidLine("TX-1", new DateOnly(2026, 3, 15), "PIX RECEBIDO JOAO", 1500.00m), result.Line);
    }

    [Theory]
    [InlineData("TX-1,2026-03-15,COMPRA,-234.9", -234.9)]
    [InlineData("TX-1,2026-03-15,COMPRA,10", 10)]
    [InlineData("TX-1,2026-03-15,COMPRA,0.01\r", 0.01)]
    public void Accepted_amount_formats(string line, double expected)
    {
        var result = LineValidator.Validate(line);

        Assert.True(result.IsValid);
        Assert.Equal((decimal)expected, result.Line!.Amount);
    }

    [Theory]
    [InlineData("TX-1,2026-03-15,COMPRA", ErrorReasons.ColumnCount)]
    [InlineData("TX-1,2026-03-15,COMPRA,10.00,extra", ErrorReasons.ColumnCount)]
    [InlineData(",2026-03-15,COMPRA,10.00", ErrorReasons.InvalidTransactionId)]
    [InlineData("   ,2026-03-15,COMPRA,10.00", ErrorReasons.InvalidTransactionId)]
    [InlineData("TX-1,2026-02-30,COMPRA,10.00", ErrorReasons.InvalidDate)]
    [InlineData("TX-1,15/03/2026,COMPRA,10.00", ErrorReasons.InvalidDate)]
    [InlineData("TX-1,2026-03-15,,10.00", ErrorReasons.InvalidDescription)]
    [InlineData("TX-1,2026-03-15,COMPRA,10,50", ErrorReasons.ColumnCount)]
    [InlineData("TX-1,2026-03-15,COMPRA,10.555", ErrorReasons.InvalidAmount)]
    [InlineData("TX-1,2026-03-15,COMPRA,abc", ErrorReasons.InvalidAmount)]
    [InlineData("TX-1,2026-03-15,COMPRA,+10.00", ErrorReasons.InvalidAmount)]
    [InlineData("TX-1,2026-03-15,COMPRA,12345678901234567.00", ErrorReasons.InvalidAmount)]
    public void Invalid_lines_report_the_first_failing_rule(string line, string expectedReason)
    {
        var result = LineValidator.Validate(line);

        Assert.False(result.IsValid);
        Assert.Equal(expectedReason, result.ErrorReason);
    }

    [Fact]
    public void Transaction_id_longer_than_64_characters_is_invalid()
    {
        var result = LineValidator.Validate($"{new string('X', 65)},2026-03-15,COMPRA,10.00");

        Assert.Equal(ErrorReasons.InvalidTransactionId, result.ErrorReason);
    }

    [Fact]
    public void Description_limit_is_200_characters()
    {
        Assert.True(LineValidator.Validate($"TX-1,2026-03-15,{new string('D', 200)},10.00").IsValid);
        Assert.Equal(ErrorReasons.InvalidDescription, LineValidator.Validate($"TX-1,2026-03-15,{new string('D', 201)},10.00").ErrorReason);
    }
}
```

- [ ] **Passo 3: rodar e ver falhar**

Rodar: `dotnet test --project tests/StatementImporter.UnitTests`
Esperado: erro de compilação `CS0103`/`CS0246` (`CsvFormat`, `LineValidator`, `ErrorReasons` não existem).

- [ ] **Passo 4: implementar**

`src/StatementImporter.Contracts/Csv/CsvFormat.cs`:

```csharp
namespace StatementImporter.Contracts.Csv;

public static class CsvFormat
{
    public const string Header = "transaction_id,date,description,amount";

    public static bool IsHeader(string line) => line.TrimStart('\uFEFF').TrimEnd('\r') == Header;

    public static bool IsBlank(ReadOnlySpan<char> line) => line.Trim(" \t\r").IsEmpty;
}
```

`src/StatementImporter.Contracts/Csv/ErrorReasons.cs`:

```csharp
namespace StatementImporter.Contracts.Csv;

public static class ErrorReasons
{
    public const string ColumnCount = "column_count";
    public const string InvalidTransactionId = "invalid_transaction_id";
    public const string InvalidDate = "invalid_date";
    public const string InvalidDescription = "invalid_description";
    public const string InvalidAmount = "invalid_amount";
    public const string Duplicate = "duplicate";
}
```

`src/StatementImporter.Contracts/Csv/LineValidator.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace StatementImporter.Contracts.Csv;

public sealed record ValidLine(string TransactionId, DateOnly Date, string Description, decimal Amount);

public readonly record struct LineResult(ValidLine? Line, string? ErrorReason)
{
    public bool IsValid => Line is not null;

    public static LineResult Valid(ValidLine line) => new(line, null);

    public static LineResult Invalid(string reason) => new(null, reason);
}

public static partial class LineValidator
{
    public const int MaxTransactionIdLength = 64;
    public const int MaxDescriptionLength = 200;

    public static LineResult Validate(string line)
    {
        var columns = line.TrimEnd('\r').Split(',');
        if (columns.Length != 4)
            return LineResult.Invalid(ErrorReasons.ColumnCount);

        var (transactionId, date, description, amount) = (columns[0], columns[1], columns[2], columns[3]);

        if (string.IsNullOrWhiteSpace(transactionId) || transactionId.Length > MaxTransactionIdLength)
            return LineResult.Invalid(ErrorReasons.InvalidTransactionId);

        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            return LineResult.Invalid(ErrorReasons.InvalidDate);

        if (string.IsNullOrWhiteSpace(description) || description.Length > MaxDescriptionLength)
            return LineResult.Invalid(ErrorReasons.InvalidDescription);

        if (!AmountPattern().IsMatch(amount)
            || !decimal.TryParse(amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsedAmount))
            return LineResult.Invalid(ErrorReasons.InvalidAmount);

        return LineResult.Valid(new ValidLine(transactionId, parsedDate, description, parsedAmount));
    }

    [GeneratedRegex(@"^-?[0-9]{1,16}(\.[0-9]{1,2})?$")]
    private static partial Regex AmountPattern();
}
```

`[0-9]` em vez de `\d` porque `\d` aceita dígitos Unicode de outros alfabetos.

- [ ] **Passo 5: rodar e ver passar**

Rodar: `dotnet test --project tests/StatementImporter.UnitTests`
Esperado: `falhou: 0`, todos os testes de `CsvFormatTests` e `LineValidatorTests` aprovados.

- [ ] **Checkpoint** (sem commit).

---

### Task 3: Divisão do arquivo em blocos

**Arquivos:**
- Criar: `src/StatementImporter.Contracts/Csv/ChunkPlanner.cs`, `Csv/ChunkReader.cs`
- Criar: `tests/StatementImporter.UnitTests/Csv/ChunkPlannerTests.cs`

**Interfaces:**
- Consome: `CsvFormat.IsBlank`, `CsvFormat.Header` (Task 2)
- Produz:
  - `record ChunkInfo(int Index, long StartOffset, int FirstLineNumber, int LineCount)`
  - `record ChunkPlan(int TotalLines, IReadOnlyList<ChunkInfo> Chunks)`
  - `ChunkPlanner.Plan(Stream file, int chunkSize) : ChunkPlan`: ignora o cabeçalho (linha física 1) e as linhas em branco; `FirstLineNumber` é o número da linha física
  - `record RawLine(int LineNumber, string Text)`
  - `ChunkReader.Read(Stream file, ChunkInfo chunk) : List<RawLine>`: busca o offset e lê `LineCount` linhas não vazias

Os dois leem **bytes** e quebram só em `\n` (removendo `\r` no final). Isso mantém os offsets e a contagem de linhas idênticos entre o divisor e o leitor. O `StreamReader.ReadLine` não serviria: ele também quebra em `\r` sozinho.

- [ ] **Passo 1: escrever o teste que falha**

`tests/StatementImporter.UnitTests/Csv/ChunkPlannerTests.cs`:

```csharp
using System.Text;
using StatementImporter.Contracts.Csv;

namespace StatementImporter.UnitTests.Csv;

public class ChunkPlannerTests
{
    private static MemoryStream Csv(string content, bool bom = false)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new MemoryStream(bom ? [.. Encoding.UTF8.GetPreamble(), .. bytes] : bytes);
    }

    private static string Lines(int count, string newline = "\n") =>
        string.Concat(Enumerable.Range(1, count).Select(i => $"TX-{i},2026-03-15,LINHA {i},1.00{newline}"));

    private static List<RawLine> ReadAll(Stream file, ChunkPlan plan) =>
        plan.Chunks.SelectMany(chunk => ChunkReader.Read(file, chunk)).ToList();

    [Fact]
    public void Header_only_file_has_no_lines_and_no_chunks()
    {
        var plan = ChunkPlanner.Plan(Csv(CsvFormat.Header + "\n"), chunkSize: 10);

        Assert.Equal(0, plan.TotalLines);
        Assert.Empty(plan.Chunks);
    }

    [Fact]
    public void Lines_are_split_into_chunks_of_the_given_size()
    {
        var plan = ChunkPlanner.Plan(Csv(CsvFormat.Header + "\n" + Lines(25)), chunkSize: 10);

        Assert.Equal(25, plan.TotalLines);
        Assert.Equal([10, 10, 5], plan.Chunks.Select(c => c.LineCount));
        Assert.Equal([0, 1, 2], plan.Chunks.Select(c => c.Index));
        Assert.Equal([2, 12, 22], plan.Chunks.Select(c => c.FirstLineNumber));
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    public void Reading_every_chunk_returns_every_line_once_in_order(string newline, bool bom)
    {
        var file = Csv(CsvFormat.Header + newline + Lines(23, newline), bom);
        var plan = ChunkPlanner.Plan(file, chunkSize: 5);

        var lines = ReadAll(file, plan);

        Assert.Equal(23, lines.Count);
        Assert.Equal(Enumerable.Range(1, 23).Select(i => $"TX-{i},2026-03-15,LINHA {i},1.00"), lines.Select(l => l.Text));
        Assert.Equal(Enumerable.Range(2, 23), lines.Select(l => l.LineNumber));
    }

    [Fact]
    public void Blank_lines_are_skipped_but_keep_physical_line_numbers()
    {
        var content = CsvFormat.Header + "\nTX-1,2026-03-15,A,1.00\n\n   \nTX-2,2026-03-15,B,1.00\n\nTX-3,2026-03-15,C,1.00\n";
        var file = Csv(content);
        var plan = ChunkPlanner.Plan(file, chunkSize: 2);

        var lines = ReadAll(file, plan);

        Assert.Equal(3, plan.TotalLines);
        Assert.Equal([2, 1], plan.Chunks.Select(c => c.LineCount));
        Assert.Equal([2, 5, 7], lines.Select(l => l.LineNumber));
    }

    [Fact]
    public void Last_line_without_newline_is_counted()
    {
        var file = Csv(CsvFormat.Header + "\nTX-1,2026-03-15,A,1.00\nTX-2,2026-03-15,B,1.00");
        var plan = ChunkPlanner.Plan(file, chunkSize: 10);

        var lines = ReadAll(file, plan);

        Assert.Equal(2, plan.TotalLines);
        Assert.Equal("TX-2,2026-03-15,B,1.00", lines[^1].Text);
    }

    [Fact]
    public void Multibyte_characters_do_not_break_offsets()
    {
        var file = Csv(CsvFormat.Header + "\nTX-1,2026-03-15,PADARIA SÃO JOÃO,1.00\nTX-2,2026-03-15,AÇAÍ,2.00\n");
        var plan = ChunkPlanner.Plan(file, chunkSize: 1);

        var lines = ReadAll(file, plan);

        Assert.Equal(["TX-1,2026-03-15,PADARIA SÃO JOÃO,1.00", "TX-2,2026-03-15,AÇAÍ,2.00"], lines.Select(l => l.Text));
    }
}
```

- [ ] **Passo 2: rodar e ver falhar**

Rodar: `dotnet test --project tests/StatementImporter.UnitTests`
Esperado: erro de compilação (`ChunkPlanner`, `ChunkReader`, `RawLine`, `ChunkPlan` não existem).

- [ ] **Passo 3: implementar**

`src/StatementImporter.Contracts/Csv/ChunkPlanner.cs`:

```csharp
namespace StatementImporter.Contracts.Csv;

public sealed record ChunkInfo(int Index, long StartOffset, int FirstLineNumber, int LineCount);

public sealed record ChunkPlan(int TotalLines, IReadOnlyList<ChunkInfo> Chunks);

public static class ChunkPlanner
{
    public static ChunkPlan Plan(Stream file, int chunkSize)
    {
        var chunks = new List<ChunkInfo>();
        var buffer = new byte[81920];
        long position = 0, lineStart = 0, chunkStart = 0;
        int lineNumber = 1, total = 0, chunkFirstLine = 0, chunkCount = 0;
        var lineHasContent = false;

        int read;
        while ((read = file.Read(buffer)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b == (byte)'\n')
                {
                    EndLine();
                    lineStart = position + i + 1;
                }
                else if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
                {
                    lineHasContent = true;
                }
            }
            position += read;
        }

        if (position > lineStart)
            EndLine();
        if (chunkCount > 0)
            chunks.Add(new ChunkInfo(chunks.Count, chunkStart, chunkFirstLine, chunkCount));

        return new ChunkPlan(total, chunks);

        void EndLine()
        {
            var isDataLine = lineNumber > 1 && lineHasContent;
            if (isDataLine)
            {
                if (chunkCount == 0)
                {
                    chunkStart = lineStart;
                    chunkFirstLine = lineNumber;
                }
                chunkCount++;
                total++;
                if (chunkCount == chunkSize)
                {
                    chunks.Add(new ChunkInfo(chunks.Count, chunkStart, chunkFirstLine, chunkCount));
                    chunkCount = 0;
                }
            }
            lineNumber++;
            lineHasContent = false;
        }
    }
}
```

`src/StatementImporter.Contracts/Csv/ChunkReader.cs`:

```csharp
using System.Buffers;
using System.Text;

namespace StatementImporter.Contracts.Csv;

public sealed record RawLine(int LineNumber, string Text);

public static class ChunkReader
{
    public static List<RawLine> Read(Stream file, ChunkInfo chunk)
    {
        file.Seek(chunk.StartOffset, SeekOrigin.Begin);
        var lines = new List<RawLine>(chunk.LineCount);
        var buffer = new byte[81920];
        var current = new ArrayBufferWriter<byte>(256);
        var lineNumber = chunk.FirstLineNumber;

        int read;
        while (lines.Count < chunk.LineCount && (read = file.Read(buffer)) > 0)
        {
            var span = buffer.AsSpan(0, read);
            while (lines.Count < chunk.LineCount)
            {
                var newline = span.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    current.Write(span);
                    break;
                }
                current.Write(span[..newline]);
                AddLine();
                span = span[(newline + 1)..];
            }
        }

        if (lines.Count < chunk.LineCount && current.WrittenCount > 0)
            AddLine();

        return lines;

        void AddLine()
        {
            var text = Encoding.UTF8.GetString(current.WrittenSpan).TrimEnd('\r');
            if (!CsvFormat.IsBlank(text))
                lines.Add(new RawLine(lineNumber, text));
            lineNumber++;
            current.ResetWrittenCount();
        }
    }
}
```

- [ ] **Passo 4: rodar e ver passar**

Rodar: `dotnet test --project tests/StatementImporter.UnitTests`
Esperado: `falhou: 0`.

- [ ] **Checkpoint** (sem commit).

---

### Task 4: Mensagens, status, respostas HTTP e configuração compartilhada

**Arquivos:**
- Modificar: `src/StatementImporter.Contracts/StatementImporter.Contracts.csproj`
- Criar: `src/StatementImporter.Contracts/Messaging/Messages.cs`, `Messaging/QueueTopology.cs`, `Messaging/MessagePublisher.cs`, `Configuration/ServiceConfiguration.cs`, `Statuses.cs`, `Http/Responses.cs`, `ContractsJsonContext.cs`
- Criar: `tests/StatementImporter.UnitTests/Configuration/ServiceConfigurationTests.cs`

**Interfaces:**
- Produz:
  - `record ImportRequested(Guid ImportId)`, `record ProcessChunk(Guid ImportId, int ChunkIndex)`
  - `QueueTopology.ImportRequested = "import-requested"`, `QueueTopology.ProcessChunk = "process-chunk"`, `QueueTopology.DeclareAsync(IChannel, CancellationToken)`: quorum queues duráveis
  - `MessagePublisher.CreateAsync(IConnection, CancellationToken) : Task<MessagePublisher>`, `PublishAsync<T>(string queue, T message, JsonTypeInfo<T> typeInfo, CancellationToken)`: com publisher confirms, mensagens persistentes, thread-safe
  - `ServiceConfiguration.AddDockerSecrets(this IConfigurationBuilder, string path = "/run/secrets")`, `BuildDbConnectionString(IConfiguration) : string` (chaves `Db:Host`, `Db:Port`, `Db:Database`, `Db:Username`, `Db:Password`), `CreateRabbitMqFactory(IConfiguration) : ConnectionFactory` (chaves `RabbitMq:Host|Port|Username|Password`, sem recuperação automática)
  - `ImportStatus.Queued|Processing|Completed|CompletedWithErrors|Failed`, `ChunkStatus.Pending|Done|Failed` (strings)
  - `ImportResponse`, `ImportErrorResponse`, `TransactionResponse` (`Date` como string `yyyy-MM-dd`), `PagedResponse<T>`, `ErrorResponse`
  - `ContractsJsonContext.Default` (JSON source-generated, camelCase), com as propriedades `ImportRequested`, `ProcessChunk`, `ImportResponse`, `PagedResponseImportErrorResponse`, `PagedResponseTransactionResponse`

- [ ] **Passo 1: escrever o teste que falha**

`tests/StatementImporter.UnitTests/Configuration/ServiceConfigurationTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Npgsql;
using StatementImporter.Contracts.Configuration;

namespace StatementImporter.UnitTests.Configuration;

public class ServiceConfigurationTests
{
    [Fact]
    public void Connection_string_comes_from_db_settings_and_trims_the_secret_newline()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Db:Host"] = "postgres",
            ["Db:Port"] = "5433",
            ["Db:Database"] = "statement_importer",
            ["Db:Username"] = "importer",
            ["Db:Password"] = "s3cret\n",
        }).Build();

        var builder = new NpgsqlConnectionStringBuilder(ServiceConfiguration.BuildDbConnectionString(config));

        Assert.Equal(("postgres", 5433, "statement_importer", "importer", "s3cret"),
            (builder.Host, builder.Port, builder.Database, builder.Username, builder.Password));
    }

    [Fact]
    public void Docker_secrets_are_read_as_configuration_keys()
    {
        var dir = Directory.CreateTempSubdirectory("secrets").FullName;
        File.WriteAllText(Path.Combine(dir, "Db__Password"), "from-file");

        var config = new ConfigurationBuilder().AddDockerSecrets(dir).Build();

        Assert.Equal("from-file", config["Db:Password"]);
    }

    [Fact]
    public void Missing_secrets_directory_is_ignored()
    {
        var config = new ConfigurationBuilder().AddDockerSecrets(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())).Build();

        Assert.Null(config["Db:Password"]);
    }
}
```

- [ ] **Passo 2: rodar e ver falhar**

Rodar: `dotnet test --project tests/StatementImporter.UnitTests`
Esperado: erro de compilação (`ServiceConfiguration` e `Npgsql` não existem).

- [ ] **Passo 3: adicionar os pacotes ao Contracts**

`src/StatementImporter.Contracts/StatementImporter.Contracts.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Configuration.KeyPerFile" />
    <PackageReference Include="Npgsql" />
    <PackageReference Include="RabbitMQ.Client" />
  </ItemGroup>

</Project>
```

- [ ] **Passo 4: implementar**

`src/StatementImporter.Contracts/Configuration/ServiceConfiguration.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Npgsql;
using RabbitMQ.Client;

namespace StatementImporter.Contracts.Configuration;

public static class ServiceConfiguration
{
    public const string DockerSecretsPath = "/run/secrets";

    public static IConfigurationBuilder AddDockerSecrets(this IConfigurationBuilder builder, string path = DockerSecretsPath) =>
        Directory.Exists(path) ? builder.AddKeyPerFile(path, optional: true) : builder;

    public static string BuildDbConnectionString(IConfiguration config) => new NpgsqlConnectionStringBuilder
    {
        Host = config["Db:Host"] ?? "localhost",
        Port = int.TryParse(config["Db:Port"], out var port) ? port : 5432,
        Database = config["Db:Database"] ?? "statement_importer",
        Username = config["Db:Username"] ?? "postgres",
        Password = config["Db:Password"]?.TrimEnd('\r', '\n'),
    }.ConnectionString;

    public static ConnectionFactory CreateRabbitMqFactory(IConfiguration config) => new()
    {
        HostName = config["RabbitMq:Host"] ?? "localhost",
        Port = int.TryParse(config["RabbitMq:Port"], out var port) ? port : AmqpTcpEndpoint.UseDefaultPort,
        UserName = config["RabbitMq:Username"] ?? "guest",
        Password = config["RabbitMq:Password"] ?? "guest",
        AutomaticRecoveryEnabled = false,
    };
}
```

`AutomaticRecoveryEnabled = false` é intencional: quando a conexão cai, `api` e `worker` encerram o processo e o restart policy do Docker os religa (Tasks 8 e 9).

`src/StatementImporter.Contracts/Messaging/Messages.cs`:

```csharp
namespace StatementImporter.Contracts.Messaging;

public sealed record ImportRequested(Guid ImportId);

public sealed record ProcessChunk(Guid ImportId, int ChunkIndex);
```

`src/StatementImporter.Contracts/Messaging/QueueTopology.cs`:

```csharp
using RabbitMQ.Client;

namespace StatementImporter.Contracts.Messaging;

public static class QueueTopology
{
    public const string ImportRequested = "import-requested";
    public const string ProcessChunk = "process-chunk";

    private static readonly Dictionary<string, object?> QuorumArguments = new() { ["x-queue-type"] = "quorum" };

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken = default)
    {
        foreach (var queue in new[] { ImportRequested, ProcessChunk })
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
                arguments: QuorumArguments, cancellationToken: cancellationToken);
        }
    }
}
```

`src/StatementImporter.Contracts/Messaging/MessagePublisher.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RabbitMQ.Client;

namespace StatementImporter.Contracts.Messaging;

public sealed class MessagePublisher : IAsyncDisposable
{
    private readonly IChannel _channel;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private MessagePublisher(IChannel channel) => _channel = channel;

    public static async Task<MessagePublisher> CreateAsync(IConnection connection, CancellationToken cancellationToken = default)
    {
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await QueueTopology.DeclareAsync(channel, cancellationToken);
        return new MessagePublisher(channel);
    }

    public async Task PublishAsync<T>(string queue, T message, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
        var properties = new BasicProperties { Persistent = true, ContentType = "application/json" };

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await _channel.BasicPublishAsync(exchange: "", routingKey: queue, mandatory: true,
                basicProperties: properties, body: body, cancellationToken: cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        _lock.Dispose();
    }
}
```

`src/StatementImporter.Contracts/Statuses.cs`:

```csharp
namespace StatementImporter.Contracts;

public static class ImportStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string CompletedWithErrors = "completed_with_errors";
    public const string Failed = "failed";
}

public static class ChunkStatus
{
    public const string Pending = "pending";
    public const string Done = "done";
    public const string Failed = "failed";
}
```

`src/StatementImporter.Contracts/Http/Responses.cs`:

```csharp
namespace StatementImporter.Contracts.Http;

public sealed record ImportResponse(
    Guid Id,
    string FileName,
    string Status,
    int TotalLines,
    int ProcessedLines,
    int ValidCount,
    int ErrorCount,
    string? FailureReason,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt);

public sealed record ImportErrorResponse(int LineNumber, string RawLine, string Reason);

public sealed record TransactionResponse(string TransactionId, string Date, string Description, decimal Amount, Guid ImportId, int LineNumber);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

public sealed record ErrorResponse(string Message);
```

`src/StatementImporter.Contracts/ContractsJsonContext.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using StatementImporter.Contracts.Http;
using StatementImporter.Contracts.Messaging;

namespace StatementImporter.Contracts;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ImportRequested))]
[JsonSerializable(typeof(ProcessChunk))]
[JsonSerializable(typeof(ImportResponse))]
[JsonSerializable(typeof(IReadOnlyList<ImportResponse>))]
[JsonSerializable(typeof(PagedResponse<ImportErrorResponse>))]
[JsonSerializable(typeof(PagedResponse<TransactionResponse>))]
[JsonSerializable(typeof(ErrorResponse))]
public partial class ContractsJsonContext : JsonSerializerContext;
```

- [ ] **Passo 5: rodar e ver passar**

Rodar: `dotnet build StatementImporter.slnx && dotnet test --project tests/StatementImporter.UnitTests`
Esperado: `0 Aviso(s)`, `0 Erro(s)`; testes com `falhou: 0`.

- [ ] **Checkpoint** (sem commit).

---

### Task 5: Modelo de dados e migration inicial

**Arquivos:**
- Criar: `src/StatementImporter.Data/StatementImporter.Data.csproj`, `Entities/Import.cs`, `Entities/ImportChunk.cs`, `Entities/BankTransaction.cs`, `Entities/ImportError.cs`, `StatementImporterDbContext.cs`, `DesignTimeDbContextFactory.cs`
- Gerar: `src/StatementImporter.Data/Migrations/*_InitialSchema.cs` (comando, não escrever à mão)

**Interfaces:**
- Consome: `ServiceConfiguration.AddDockerSecrets`, `BuildDbConnectionString` (Task 4)
- Produz:
  - Entidades `Import`, `ImportChunk` (propriedade `ChunkIndex` → coluna `chunk_index`), `BankTransaction` (tabela `transactions`), `ImportError`
  - `StatementImporterDbContext` com `Imports`, `ImportChunks`, `Transactions`, `ImportErrors`, a constante `MaxRawLineLength = 500` e o método `static Configure(DbContextOptionsBuilder, string connectionString)`, que liga Npgsql + snake_case
  - Tabelas `imports`, `import_chunks`, `transactions`, `import_errors`, todas em snake_case. A API (Dapper) depende desses nomes.

`chunk_index` e não `index`: `index` é palavra-chave do SQL, e o nome evita aspas no `FromSql`. A entidade chama `BankTransaction` para não confundir com transação de banco no código.

- [ ] **Passo 1: criar o projeto**

`src/StatementImporter.Data/StatementImporter.Data.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <GenerateRuntimeConfigurationFiles>true</GenerateRuntimeConfigurationFiles>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="EFCore.NamingConventions" />
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Extensions.Configuration.EnvironmentVariables" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\StatementImporter.Contracts\StatementImporter.Contracts.csproj" />
  </ItemGroup>

</Project>
```

`GenerateRuntimeConfigurationFiles` deixa a própria classlib servir de *startup project* para o `dotnet ef`. Assim o migration bundle não carrega o `Program` do worker, que tentaria conectar no RabbitMQ.

```bash
dotnet sln StatementImporter.slnx add src/StatementImporter.Data/StatementImporter.Data.csproj
```

- [ ] **Passo 2: criar as entidades**

`src/StatementImporter.Data/Entities/Import.cs`:

```csharp
namespace StatementImporter.Data.Entities;

public sealed class Import
{
    public Guid Id { get; set; }
    public required string FileName { get; set; }
    public required string FileHash { get; set; }
    public required string FilePath { get; set; }
    public required string Status { get; set; }
    public int TotalLines { get; set; }
    public int TotalChunks { get; set; }
    public int ProcessedChunks { get; set; }
    public int ProcessedLines { get; set; }
    public int ValidCount { get; set; }
    public int ErrorCount { get; set; }
    public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
```

`src/StatementImporter.Data/Entities/ImportChunk.cs`:

```csharp
namespace StatementImporter.Data.Entities;

public sealed class ImportChunk
{
    public long Id { get; set; }
    public Guid ImportId { get; set; }
    public int ChunkIndex { get; set; }
    public long StartOffset { get; set; }
    public int FirstLineNumber { get; set; }
    public int LineCount { get; set; }
    public required string Status { get; set; }
    public int Attempts { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
```

`src/StatementImporter.Data/Entities/BankTransaction.cs`:

```csharp
namespace StatementImporter.Data.Entities;

public sealed class BankTransaction
{
    public long Id { get; set; }
    public required string TransactionId { get; set; }
    public DateOnly Date { get; set; }
    public required string Description { get; set; }
    public decimal Amount { get; set; }
    public Guid ImportId { get; set; }
    public int LineNumber { get; set; }
}
```

`src/StatementImporter.Data/Entities/ImportError.cs`:

```csharp
namespace StatementImporter.Data.Entities;

public sealed class ImportError
{
    public long Id { get; set; }
    public Guid ImportId { get; set; }
    public int LineNumber { get; set; }
    public required string RawLine { get; set; }
    public required string Reason { get; set; }
}
```

- [ ] **Passo 3: criar o DbContext e a factory de design**

`src/StatementImporter.Data/StatementImporterDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using StatementImporter.Data.Entities;

namespace StatementImporter.Data;

public sealed class StatementImporterDbContext(DbContextOptions<StatementImporterDbContext> options) : DbContext(options)
{
    public const int MaxRawLineLength = 500;

    public DbSet<Import> Imports => Set<Import>();
    public DbSet<ImportChunk> ImportChunks => Set<ImportChunk>();
    public DbSet<BankTransaction> Transactions => Set<BankTransaction>();
    public DbSet<ImportError> ImportErrors => Set<ImportError>();

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Import>(e =>
        {
            e.ToTable("imports");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.FileHash).HasMaxLength(64).IsFixedLength();
            e.HasIndex(x => x.FileHash).IsUnique();
            e.Property(x => x.FilePath).HasMaxLength(500);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.FailureReason).HasMaxLength(500);
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });

        modelBuilder.Entity<ImportChunk>(e =>
        {
            e.ToTable("import_chunks");
            e.HasIndex(x => new { x.ImportId, x.ChunkIndex }).IsUnique();
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasOne<Import>().WithMany().HasForeignKey(x => x.ImportId);
        });

        modelBuilder.Entity<BankTransaction>(e =>
        {
            e.ToTable("transactions");
            e.Property(x => x.TransactionId).HasMaxLength(64);
            e.HasIndex(x => x.TransactionId).IsUnique();
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.HasIndex(x => new { x.ImportId, x.LineNumber });
            e.HasOne<Import>().WithMany().HasForeignKey(x => x.ImportId);
        });

        modelBuilder.Entity<ImportError>(e =>
        {
            e.ToTable("import_errors");
            e.Property(x => x.RawLine).HasMaxLength(MaxRawLineLength);
            e.Property(x => x.Reason).HasMaxLength(32);
            e.HasIndex(x => new { x.ImportId, x.LineNumber });
            e.HasOne<Import>().WithMany().HasForeignKey(x => x.ImportId);
        });
    }
}
```

`src/StatementImporter.Data/DesignTimeDbContextFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using StatementImporter.Contracts.Configuration;

namespace StatementImporter.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<StatementImporterDbContext>
{
    public StatementImporterDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddDockerSecrets()
            .Build();

        var options = new DbContextOptionsBuilder<StatementImporterDbContext>();
        StatementImporterDbContext.Configure(options, ServiceConfiguration.BuildDbConnectionString(config));
        return new StatementImporterDbContext(options.Options);
    }
}
```

A factory é usada pelo `dotnet ef` e, no container, pelo migration bundle. Ela lê `Db__*` do ambiente e a senha de `/run/secrets/Db__Password`.

- [ ] **Passo 4: gerar a migration**

O `dotnet ef` precisa do projeto restaurado. Sem o build antes, ele falha com `Unable to retrieve project metadata` (`NETSDK1004`).

```bash
dotnet build StatementImporter.slnx
dotnet ef migrations add InitialSchema --project src/StatementImporter.Data --startup-project src/StatementImporter.Data
```

Esperado: `Done.`, e a pasta `src/StatementImporter.Data/Migrations/` com `*_InitialSchema.cs`, `*_InitialSchema.Designer.cs` e `StatementImporterDbContextModelSnapshot.cs`.

- [ ] **Passo 5: verificar o SQL gerado (sem banco)**

Rodar: `dotnet ef migrations script --project src/StatementImporter.Data --startup-project src/StatementImporter.Data | grep -E "CREATE (TABLE|UNIQUE INDEX)"`
Esperado: 4 `CREATE TABLE` (`imports`, `import_chunks`, `transactions`, `import_errors`) e 3 `CREATE UNIQUE INDEX` (`ix_imports_file_hash`, `ix_import_chunks_import_id_chunk_index`, `ix_transactions_transaction_id`).

- [ ] **Checkpoint** (sem commit).

---

### Task 6: Worker — divisão da importação em blocos

**Arquivos:**
- Criar: `src/StatementImporter.Worker/StatementImporter.Worker.csproj`, `ImportOptions.cs`, `Imports/ImportRequestedHandler.cs`, `Program.cs` (mínimo, substituído na Task 8)
- Criar: `tests/StatementImporter.IntegrationTests/StatementImporter.IntegrationTests.csproj`, `InfrastructureFixture.cs`, `Worker/ImportProcessingTests.cs`

**Interfaces:**
- Consome: `ChunkPlanner.Plan`, `MessagePublisher`, `QueueTopology`, `ContractsJsonContext`, `ImportStatus`, `ChunkStatus` (Tasks 3–4); `StatementImporterDbContext`, `Import`, `ImportChunk` (Task 5)
- Produz:
  - `ImportOptions` (`ChunkSize = 10_000`, `MaxAttempts = 5`, `MaxStoredErrors = 10_000`, `ReconcileInterval = 60s`, `QueuedThreshold = 60s`; seção `Import` da configuração)
  - `ImportRequestedHandler(StatementImporterDbContext, MessagePublisher, IOptions<ImportOptions>, ILogger<ImportRequestedHandler>)` com `HandleAsync(ImportRequested, CancellationToken)`
  - `InfrastructureFixture` com `UploadsPath`, `CreateDbContext()`, `CreateSplitterAsync(db, chunkSize)`, `CreateQueuedImportAsync(IEnumerable<string> dataLines) : Task<Import>`, `static ValidLines(prefix, count)`. A fixture também define as variáveis `Db__*`, `RabbitMq__*` e `Import__UploadsPath`, que a API usa na Task 9.

- [ ] **Passo 1: criar o projeto do worker**

`src/StatementImporter.Worker/StatementImporter.Worker.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\StatementImporter.Contracts\StatementImporter.Contracts.csproj" />
    <ProjectReference Include="..\StatementImporter.Data\StatementImporter.Data.csproj" />
  </ItemGroup>

</Project>
```

`src/StatementImporter.Worker/Program.cs` (temporário, só para o projeto compilar; a Task 8 substitui):

```csharp
await Host.CreateApplicationBuilder(args).Build().RunAsync();
```

`src/StatementImporter.Worker/ImportOptions.cs`:

```csharp
namespace StatementImporter.Worker;

public sealed class ImportOptions
{
    public int ChunkSize { get; set; } = 10_000;
    public int MaxAttempts { get; set; } = 5;
    public int MaxStoredErrors { get; set; } = 10_000;
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan QueuedThreshold { get; set; } = TimeSpan.FromSeconds(60);
}
```

- [ ] **Passo 2: criar o projeto de integração e a fixture**

`tests/StatementImporter.IntegrationTests/StatementImporter.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="Testcontainers.RabbitMq" />
    <PackageReference Include="xunit.v3" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\StatementImporter.Data\StatementImporter.Data.csproj" />
    <ProjectReference Include="..\..\src\StatementImporter.Worker\StatementImporter.Worker.csproj" />
  </ItemGroup>

</Project>
```

```bash
dotnet sln StatementImporter.slnx add src/StatementImporter.Worker/StatementImporter.Worker.csproj tests/StatementImporter.IntegrationTests/StatementImporter.IntegrationTests.csproj
```

`tests/StatementImporter.IntegrationTests/InfrastructureFixture.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Csv;
using StatementImporter.Contracts.Messaging;
using StatementImporter.Data;
using StatementImporter.Data.Entities;
using StatementImporter.Worker;
using StatementImporter.Worker.Imports;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace StatementImporter.IntegrationTests;

public sealed class InfrastructureFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();
    private IConnection? _rabbitConnection;

    public string UploadsPath { get; } = Directory.CreateTempSubdirectory("statement-importer-uploads").FullName;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        await using (var db = CreateDbContext())
            await db.Database.MigrateAsync();

        var pg = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());
        var amqp = new Uri(_rabbit.GetConnectionString());
        var credentials = amqp.UserInfo.Split(':');
        var settings = new Dictionary<string, string?>
        {
            ["Db__Host"] = pg.Host,
            ["Db__Port"] = pg.Port.ToString(),
            ["Db__Database"] = pg.Database,
            ["Db__Username"] = pg.Username,
            ["Db__Password"] = pg.Password,
            ["RabbitMq__Host"] = amqp.Host,
            ["RabbitMq__Port"] = amqp.Port.ToString(),
            ["RabbitMq__Username"] = Uri.UnescapeDataString(credentials[0]),
            ["RabbitMq__Password"] = Uri.UnescapeDataString(credentials[1]),
            ["Import__UploadsPath"] = UploadsPath,
        };
        foreach (var (key, value) in settings)
            Environment.SetEnvironmentVariable(key, value);

        _rabbitConnection = await new ConnectionFactory { Uri = amqp }.CreateConnectionAsync();
    }

    public StatementImporterDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<StatementImporterDbContext>();
        StatementImporterDbContext.Configure(options, _postgres.GetConnectionString());
        return new StatementImporterDbContext(options.Options);
    }

    public async Task<ImportRequestedHandler> CreateSplitterAsync(StatementImporterDbContext db, int chunkSize) =>
        new(db, await MessagePublisher.CreateAsync(_rabbitConnection!), Options(chunkSize), NullLogger<ImportRequestedHandler>.Instance);

    public async Task<Import> CreateQueuedImportAsync(IEnumerable<string> dataLines)
    {
        var id = Guid.CreateVersion7();
        var path = Path.Combine(UploadsPath, $"{id}.csv");
        await File.WriteAllLinesAsync(path, [CsvFormat.Header, .. dataLines]);

        var import = new Import
        {
            Id = id,
            FileName = "teste.csv",
            FileHash = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()).PadRight(64, '0'),
            FilePath = path,
            Status = ImportStatus.Queued,
            CreatedAt = DateTime.UtcNow,
        };
        await using var db = CreateDbContext();
        db.Imports.Add(import);
        await db.SaveChangesAsync();
        return import;
    }

    public static IEnumerable<string> ValidLines(string prefix, int count) =>
        Enumerable.Range(1, count).Select(i => $"{prefix}-{i:D6},2026-03-15,LINHA {i},{i}.50");

    private static IOptions<ImportOptions> Options(int chunkSize, int maxStoredErrors = 10_000) =>
        Microsoft.Extensions.Options.Options.Create(new ImportOptions { ChunkSize = chunkSize, MaxStoredErrors = maxStoredErrors });

    public async ValueTask DisposeAsync()
    {
        if (_rabbitConnection is not null)
            await _rabbitConnection.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }
}

[CollectionDefinition(nameof(InfrastructureCollection))]
public sealed class InfrastructureCollection : ICollectionFixture<InfrastructureFixture>;
```

- [ ] **Passo 3: escrever os testes que falham**

`tests/StatementImporter.IntegrationTests/Worker/ImportProcessingTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Messaging;

namespace StatementImporter.IntegrationTests.Worker;

[Collection(nameof(InfrastructureCollection))]
public class ImportProcessingTests(InfrastructureFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Split_covers_every_line_exactly_once()
    {
        var prefix = $"SPLIT-{Guid.NewGuid():N}";
        var import = await fixture.CreateQueuedImportAsync([.. InfrastructureFixture.ValidLines(prefix, 23), "", "   "]);

        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 5)).HandleAsync(new ImportRequested(import.Id), Ct);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        var chunks = await check.ImportChunks.Where(c => c.ImportId == import.Id).OrderBy(c => c.ChunkIndex).ToListAsync(Ct);

        Assert.Equal(ImportStatus.Processing, saved.Status);
        Assert.Equal(23, saved.TotalLines);
        Assert.Equal(5, saved.TotalChunks);
        Assert.Equal([5, 5, 5, 5, 3], chunks.Select(c => c.LineCount));
        Assert.All(chunks, c => Assert.Equal(ChunkStatus.Pending, c.Status));
    }

    [Fact]
    public async Task Header_only_file_completes_without_chunks()
    {
        var import = await fixture.CreateQueuedImportAsync([]);

        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 10)).HandleAsync(new ImportRequested(import.Id), Ct);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        Assert.Equal((ImportStatus.Completed, 0, 0), (saved.Status, saved.TotalLines, saved.TotalChunks));
    }

    [Fact]
    public async Task Missing_file_fails_the_import()
    {
        var import = await fixture.CreateQueuedImportAsync(InfrastructureFixture.ValidLines($"GONE-{Guid.NewGuid():N}", 2));
        File.Delete(import.FilePath);

        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 10)).HandleAsync(new ImportRequested(import.Id), Ct);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        Assert.Equal(ImportStatus.Failed, saved.Status);
        Assert.NotNull(saved.FailureReason);
    }
}
```

- [ ] **Passo 4: rodar e ver falhar**

Rodar: `dotnet test --project tests/StatementImporter.IntegrationTests`
Esperado: erro de compilação (`ImportRequestedHandler` não existe).

- [ ] **Passo 5: implementar o handler**

`src/StatementImporter.Worker/Imports/ImportRequestedHandler.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Csv;
using StatementImporter.Contracts.Messaging;
using StatementImporter.Data;
using StatementImporter.Data.Entities;

namespace StatementImporter.Worker.Imports;

public sealed class ImportRequestedHandler(
    StatementImporterDbContext db,
    MessagePublisher publisher,
    IOptions<ImportOptions> options,
    ILogger<ImportRequestedHandler> logger)
{
    public async Task HandleAsync(ImportRequested message, CancellationToken cancellationToken)
    {
        string status;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var import = (await db.Imports
                .FromSql($"SELECT * FROM imports WHERE id = {message.ImportId} FOR UPDATE")
                .ToListAsync(cancellationToken)).SingleOrDefault();

            if (import is null)
            {
                logger.LogWarning("Importação {ImportId} não encontrada.", message.ImportId);
                return;
            }

            if (import.Status == ImportStatus.Queued)
                Split(import);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            status = import.Status;
        }

        if (status != ImportStatus.Processing)
            return;

        var pending = await db.ImportChunks
            .Where(c => c.ImportId == message.ImportId && c.Status == ChunkStatus.Pending)
            .OrderBy(c => c.ChunkIndex)
            .Select(c => c.ChunkIndex)
            .ToListAsync(cancellationToken);

        foreach (var index in pending)
        {
            await publisher.PublishAsync(QueueTopology.ProcessChunk, new ProcessChunk(message.ImportId, index),
                ContractsJsonContext.Default.ProcessChunk, cancellationToken);
        }
    }

    private void Split(Import import)
    {
        var now = DateTime.UtcNow;
        ChunkPlan plan;
        try
        {
            using var file = File.OpenRead(import.FilePath);
            plan = ChunkPlanner.Plan(file, options.Value.ChunkSize);
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "Não foi possível ler o arquivo da importação {ImportId}.", import.Id);
            import.Status = ImportStatus.Failed;
            import.FailureReason = $"Arquivo ausente ou ilegível: {ex.Message}";
            import.CompletedAt = now;
            return;
        }

        import.TotalLines = plan.TotalLines;
        import.TotalChunks = plan.Chunks.Count;
        import.StartedAt = now;

        if (plan.TotalLines == 0)
        {
            import.Status = ImportStatus.Completed;
            import.CompletedAt = now;
            return;
        }

        import.Status = ImportStatus.Processing;
        db.ImportChunks.AddRange(plan.Chunks.Select(c => new ImportChunk
        {
            ImportId = import.Id,
            ChunkIndex = c.Index,
            StartOffset = c.StartOffset,
            FirstLineNumber = c.FirstLineNumber,
            LineCount = c.LineCount,
            Status = ChunkStatus.Pending,
        }));
    }
}
```

O `FOR UPDATE` serializa duas entregas simultâneas da mesma importação: a segunda encontra `processing` e só republica os blocos pendentes, o que é inofensivo.

- [ ] **Passo 6: rodar e ver passar** (Docker Desktop precisa estar rodando)

Rodar: `dotnet test --project tests/StatementImporter.IntegrationTests`
Esperado: `falhou: 0` (3 testes). A primeira execução baixa as imagens `postgres:17-alpine` e `rabbitmq:4-management-alpine`.

- [ ] **Checkpoint** (sem commit).

---

### Task 7: Worker — processamento idempotente de um bloco

**Arquivos:**
- Criar: `src/StatementImporter.Worker/Imports/ChunkWriter.cs`, `Imports/ProcessChunkHandler.cs`
- Substituir: `tests/StatementImporter.IntegrationTests/InfrastructureFixture.cs` (ganha `CreateProcessor` e `ProcessAllAsync`)
- Substituir: `tests/StatementImporter.IntegrationTests/Worker/ImportProcessingTests.cs` (ganha 5 testes)

**Interfaces:**
- Consome: `ChunkReader.Read`, `LineValidator.Validate`, `ErrorReasons` (Tasks 2–3); `ImportRequestedHandler` (Task 6)
- Produz:
  - `record AcceptedLine(RawLine Raw, ValidLine Line)`, `record RejectedLine(int LineNumber, string RawText, string Reason)`
  - `ChunkWriter.InsertTransactionsAsync(NpgsqlConnection, Guid importId, IReadOnlyCollection<AcceptedLine>, CancellationToken) : Task<HashSet<int>>`: devolve os `line_number` efetivamente inseridos
  - `ChunkWriter.InsertErrorsAsync(NpgsqlConnection, Guid importId, IEnumerable<RejectedLine>, CancellationToken)`
  - `ProcessChunkHandler(StatementImporterDbContext, IOptions<ImportOptions>, ILogger<ProcessChunkHandler>)` com `HandleAsync(ProcessChunk message, bool redelivered, CancellationToken)`
  - Fixture: `static CreateProcessor(db, chunkSize, maxStoredErrors = 10_000)`, `ProcessAllAsync(Guid importId, int chunkSize)`

- [ ] **Passo 1: substituir a fixture** (igual à Task 6, com `CreateProcessor` e `ProcessAllAsync` a mais)

`tests/StatementImporter.IntegrationTests/InfrastructureFixture.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Csv;
using StatementImporter.Contracts.Messaging;
using StatementImporter.Data;
using StatementImporter.Data.Entities;
using StatementImporter.Worker;
using StatementImporter.Worker.Imports;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace StatementImporter.IntegrationTests;

public sealed class InfrastructureFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();
    private IConnection? _rabbitConnection;

    public string UploadsPath { get; } = Directory.CreateTempSubdirectory("statement-importer-uploads").FullName;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

        await using (var db = CreateDbContext())
            await db.Database.MigrateAsync();

        var pg = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());
        var amqp = new Uri(_rabbit.GetConnectionString());
        var credentials = amqp.UserInfo.Split(':');
        var settings = new Dictionary<string, string?>
        {
            ["Db__Host"] = pg.Host,
            ["Db__Port"] = pg.Port.ToString(),
            ["Db__Database"] = pg.Database,
            ["Db__Username"] = pg.Username,
            ["Db__Password"] = pg.Password,
            ["RabbitMq__Host"] = amqp.Host,
            ["RabbitMq__Port"] = amqp.Port.ToString(),
            ["RabbitMq__Username"] = Uri.UnescapeDataString(credentials[0]),
            ["RabbitMq__Password"] = Uri.UnescapeDataString(credentials[1]),
            ["Import__UploadsPath"] = UploadsPath,
        };
        foreach (var (key, value) in settings)
            Environment.SetEnvironmentVariable(key, value);

        _rabbitConnection = await new ConnectionFactory { Uri = amqp }.CreateConnectionAsync();
    }

    public StatementImporterDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<StatementImporterDbContext>();
        StatementImporterDbContext.Configure(options, _postgres.GetConnectionString());
        return new StatementImporterDbContext(options.Options);
    }

    public async Task<ImportRequestedHandler> CreateSplitterAsync(StatementImporterDbContext db, int chunkSize) =>
        new(db, await MessagePublisher.CreateAsync(_rabbitConnection!), Options(chunkSize), NullLogger<ImportRequestedHandler>.Instance);

    public static ProcessChunkHandler CreateProcessor(StatementImporterDbContext db, int chunkSize, int maxStoredErrors = 10_000) =>
        new(db, Options(chunkSize, maxStoredErrors), NullLogger<ProcessChunkHandler>.Instance);

    public async Task<Import> CreateQueuedImportAsync(IEnumerable<string> dataLines)
    {
        var id = Guid.CreateVersion7();
        var path = Path.Combine(UploadsPath, $"{id}.csv");
        await File.WriteAllLinesAsync(path, [CsvFormat.Header, .. dataLines]);

        var import = new Import
        {
            Id = id,
            FileName = "teste.csv",
            FileHash = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()).PadRight(64, '0'),
            FilePath = path,
            Status = ImportStatus.Queued,
            CreatedAt = DateTime.UtcNow,
        };
        await using var db = CreateDbContext();
        db.Imports.Add(import);
        await db.SaveChangesAsync();
        return import;
    }

    public async Task ProcessAllAsync(Guid importId, int chunkSize)
    {
        await using (var db = CreateDbContext())
            await (await CreateSplitterAsync(db, chunkSize)).HandleAsync(new ImportRequested(importId), CancellationToken.None);

        await using var reader = CreateDbContext();
        var indexes = await reader.ImportChunks.Where(c => c.ImportId == importId).Select(c => c.ChunkIndex).ToListAsync();
        foreach (var index in indexes)
        {
            await using var db = CreateDbContext();
            await CreateProcessor(db, chunkSize).HandleAsync(new ProcessChunk(importId, index), redelivered: false, CancellationToken.None);
        }
    }

    public static IEnumerable<string> ValidLines(string prefix, int count) =>
        Enumerable.Range(1, count).Select(i => $"{prefix}-{i:D6},2026-03-15,LINHA {i},{i}.50");

    private static IOptions<ImportOptions> Options(int chunkSize, int maxStoredErrors = 10_000) =>
        Microsoft.Extensions.Options.Options.Create(new ImportOptions { ChunkSize = chunkSize, MaxStoredErrors = maxStoredErrors });

    public async ValueTask DisposeAsync()
    {
        if (_rabbitConnection is not null)
            await _rabbitConnection.DisposeAsync();
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }
}

[CollectionDefinition(nameof(InfrastructureCollection))]
public sealed class InfrastructureCollection : ICollectionFixture<InfrastructureFixture>;
```

- [ ] **Passo 2: substituir os testes** (os 3 da Task 6, mais 5 novos)

`tests/StatementImporter.IntegrationTests/Worker/ImportProcessingTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Csv;
using StatementImporter.Contracts.Messaging;

namespace StatementImporter.IntegrationTests.Worker;

[Collection(nameof(InfrastructureCollection))]
public class ImportProcessingTests(InfrastructureFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Split_covers_every_line_exactly_once()
    {
        var prefix = $"SPLIT-{Guid.NewGuid():N}";
        var import = await fixture.CreateQueuedImportAsync([.. InfrastructureFixture.ValidLines(prefix, 23), "", "   "]);

        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 5)).HandleAsync(new ImportRequested(import.Id), Ct);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        var chunks = await check.ImportChunks.Where(c => c.ImportId == import.Id).OrderBy(c => c.ChunkIndex).ToListAsync(Ct);

        Assert.Equal(ImportStatus.Processing, saved.Status);
        Assert.Equal(23, saved.TotalLines);
        Assert.Equal(5, saved.TotalChunks);
        Assert.Equal([5, 5, 5, 5, 3], chunks.Select(c => c.LineCount));
        Assert.All(chunks, c => Assert.Equal(ChunkStatus.Pending, c.Status));
    }

    [Fact]
    public async Task Header_only_file_completes_without_chunks()
    {
        var import = await fixture.CreateQueuedImportAsync([]);

        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 10)).HandleAsync(new ImportRequested(import.Id), Ct);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        Assert.Equal((ImportStatus.Completed, 0, 0), (saved.Status, saved.TotalLines, saved.TotalChunks));
    }

    [Fact]
    public async Task Missing_file_fails_the_import()
    {
        var import = await fixture.CreateQueuedImportAsync(InfrastructureFixture.ValidLines($"GONE-{Guid.NewGuid():N}", 2));
        File.Delete(import.FilePath);

        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 10)).HandleAsync(new ImportRequested(import.Id), Ct);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        Assert.Equal(ImportStatus.Failed, saved.Status);
        Assert.NotNull(saved.FailureReason);
    }

    [Fact]
    public async Task Processing_the_same_chunk_twice_does_not_duplicate()
    {
        var prefix = $"TWICE-{Guid.NewGuid():N}";
        var import = await fixture.CreateQueuedImportAsync(InfrastructureFixture.ValidLines(prefix, 25));
        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 10)).HandleAsync(new ImportRequested(import.Id), Ct);

        foreach (var (index, redelivered) in new[] { (0, false), (0, true), (1, false), (2, false), (2, true) })
        {
            await using var db = fixture.CreateDbContext();
            await InfrastructureFixture.CreateProcessor(db, chunkSize: 10)
                .HandleAsync(new ProcessChunk(import.Id, index), redelivered, Ct);
        }

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        Assert.Equal(25, await check.Transactions.CountAsync(t => t.ImportId == import.Id, Ct));
        Assert.Equal((ImportStatus.Completed, 3, 25, 25, 0), (saved.Status, saved.ProcessedChunks, saved.ProcessedLines, saved.ValidCount, saved.ErrorCount));
    }

    [Fact]
    public async Task Invalid_lines_are_reported_and_valid_ones_are_kept()
    {
        var prefix = $"MIX-{Guid.NewGuid():N}";
        var import = await fixture.CreateQueuedImportAsync(
        [
            $"{prefix}-1,2026-03-15,OK,1.00",
            $"{prefix}-2,2026-02-30,DATA RUIM,1.00",
            $"{prefix}-3,2026-03-15,VALOR RUIM,1,00",
            $"{prefix}-4,2026-03-15,OK,2.00",
        ]);

        await fixture.ProcessAllAsync(import.Id, chunkSize: 3);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        var errors = await check.ImportErrors.Where(e => e.ImportId == import.Id).OrderBy(e => e.LineNumber).ToListAsync(Ct);

        Assert.Equal((ImportStatus.CompletedWithErrors, 4, 2, 2), (saved.Status, saved.TotalLines, saved.ValidCount, saved.ErrorCount));
        Assert.Equal([(3, ErrorReasons.InvalidDate), (4, ErrorReasons.ColumnCount)], errors.Select(e => (e.LineNumber, e.Reason)));
        Assert.Equal(saved.TotalLines, saved.ValidCount + saved.ErrorCount);
    }

    [Fact]
    public async Task Transaction_id_repeated_across_files_is_reported_as_duplicate()
    {
        var prefix = $"DUP-{Guid.NewGuid():N}";
        var first = await fixture.CreateQueuedImportAsync(InfrastructureFixture.ValidLines(prefix, 3));
        await fixture.ProcessAllAsync(first.Id, chunkSize: 10);

        var second = await fixture.CreateQueuedImportAsync([$"{prefix}-000002,2026-03-16,REPETIDA,9.99", $"{prefix}-NOVA,2026-03-16,NOVA,1.00"]);
        await fixture.ProcessAllAsync(second.Id, chunkSize: 10);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == second.Id, Ct);
        var error = await check.ImportErrors.SingleAsync(e => e.ImportId == second.Id, Ct);

        Assert.Equal((1, 1), (saved.ValidCount, saved.ErrorCount));
        Assert.Equal((2, ErrorReasons.Duplicate), (error.LineNumber, error.Reason));
    }

    [Fact]
    public async Task Stored_errors_respect_the_limit_but_every_error_is_counted()
    {
        var import = await fixture.CreateQueuedImportAsync(Enumerable.Range(1, 8).Select(i => $"BAD-{i},nao-e-data,X,1.00"));

        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 10)).HandleAsync(new ImportRequested(import.Id), Ct);
        await using (var db = fixture.CreateDbContext())
            await InfrastructureFixture.CreateProcessor(db, chunkSize: 10, maxStoredErrors: 5)
                .HandleAsync(new ProcessChunk(import.Id, 0), redelivered: false, Ct);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        Assert.Equal(8, saved.ErrorCount);
        Assert.Equal(5, await check.ImportErrors.CountAsync(e => e.ImportId == import.Id, Ct));
    }

    [Fact]
    public async Task Chunk_redelivered_beyond_the_limit_fails_the_import()
    {
        var import = await fixture.CreateQueuedImportAsync(InfrastructureFixture.ValidLines($"FAIL-{Guid.NewGuid():N}", 2));
        await using (var db = fixture.CreateDbContext())
            await (await fixture.CreateSplitterAsync(db, chunkSize: 10)).HandleAsync(new ImportRequested(import.Id), Ct);
        await using (var db = fixture.CreateDbContext())
            await db.ImportChunks.Where(c => c.ImportId == import.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, 5), Ct);

        await using (var db = fixture.CreateDbContext())
            await InfrastructureFixture.CreateProcessor(db, chunkSize: 10)
                .HandleAsync(new ProcessChunk(import.Id, 0), redelivered: true, Ct);

        await using var check = fixture.CreateDbContext();
        var saved = await check.Imports.SingleAsync(i => i.Id == import.Id, Ct);
        Assert.Equal(ImportStatus.Failed, saved.Status);
        Assert.Equal(ChunkStatus.Failed, (await check.ImportChunks.SingleAsync(c => c.ImportId == import.Id, Ct)).Status);
        Assert.Equal(0, await check.Transactions.CountAsync(t => t.ImportId == import.Id, Ct));
    }
}
```

- [ ] **Passo 3: rodar e ver falhar**

Rodar: `dotnet test --project tests/StatementImporter.IntegrationTests`
Esperado: erro de compilação (`ProcessChunkHandler` não existe).

- [ ] **Passo 4: implementar a escrita em lote**

`src/StatementImporter.Worker/Imports/ChunkWriter.cs`:

```csharp
using Npgsql;
using NpgsqlTypes;
using StatementImporter.Contracts.Csv;
using StatementImporter.Data;

namespace StatementImporter.Worker.Imports;

public sealed record AcceptedLine(RawLine Raw, ValidLine Line);

public sealed record RejectedLine(int LineNumber, string RawText, string Reason);

public static class ChunkWriter
{
    public static async Task<HashSet<int>> InsertTransactionsAsync(
        NpgsqlConnection connection, Guid importId, IReadOnlyCollection<AcceptedLine> lines, CancellationToken cancellationToken)
    {
        var inserted = new HashSet<int>();
        if (lines.Count == 0)
            return inserted;

        await using (var create = new NpgsqlCommand("""
            CREATE TEMP TABLE staging_transactions (
                transaction_id varchar(64), date date, description varchar(200), amount numeric(18,2), line_number int
            ) ON COMMIT DROP
            """, connection))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var importer = await connection.BeginBinaryImportAsync(
            "COPY staging_transactions (transaction_id, date, description, amount, line_number) FROM STDIN (FORMAT BINARY)",
            cancellationToken))
        {
            foreach (var line in lines)
            {
                await importer.StartRowAsync(cancellationToken);
                await importer.WriteAsync(line.Line.TransactionId, NpgsqlDbType.Varchar, cancellationToken);
                await importer.WriteAsync(line.Line.Date, NpgsqlDbType.Date, cancellationToken);
                await importer.WriteAsync(line.Line.Description, NpgsqlDbType.Varchar, cancellationToken);
                await importer.WriteAsync(line.Line.Amount, NpgsqlDbType.Numeric, cancellationToken);
                await importer.WriteAsync(line.Raw.LineNumber, NpgsqlDbType.Integer, cancellationToken);
            }
            await importer.CompleteAsync(cancellationToken);
        }

        await using var insert = new NpgsqlCommand("""
            INSERT INTO transactions (transaction_id, date, description, amount, import_id, line_number)
            SELECT transaction_id, date, description, amount, @import_id, line_number
            FROM staging_transactions
            ORDER BY transaction_id
            ON CONFLICT (transaction_id) DO NOTHING
            RETURNING line_number
            """, connection);
        insert.Parameters.AddWithValue("import_id", importId);

        await using var reader = await insert.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            inserted.Add(reader.GetInt32(0));

        return inserted;
    }

    public static async Task InsertErrorsAsync(
        NpgsqlConnection connection, Guid importId, IEnumerable<RejectedLine> errors, CancellationToken cancellationToken)
    {
        await using var importer = await connection.BeginBinaryImportAsync(
            "COPY import_errors (import_id, line_number, raw_line, reason) FROM STDIN (FORMAT BINARY)", cancellationToken);

        foreach (var error in errors)
        {
            var raw = error.RawText.Length > StatementImporterDbContext.MaxRawLineLength
                ? error.RawText[..StatementImporterDbContext.MaxRawLineLength]
                : error.RawText;

            await importer.StartRowAsync(cancellationToken);
            await importer.WriteAsync(importId, NpgsqlDbType.Uuid, cancellationToken);
            await importer.WriteAsync(error.LineNumber, NpgsqlDbType.Integer, cancellationToken);
            await importer.WriteAsync(raw, NpgsqlDbType.Varchar, cancellationToken);
            await importer.WriteAsync(error.Reason, NpgsqlDbType.Varchar, cancellationToken);
        }
        await importer.CompleteAsync(cancellationToken);
    }
}
```

O `ORDER BY transaction_id` fixa a ordem em que os workers travam as chaves no índice único. Sem ele, dois blocos com IDs repetidos entre si podem entrar em deadlock ([ADR 0003](../../architecture/decisions/0003-processamento-em-blocos-idempotente.md)).

- [ ] **Passo 5: implementar o handler**

`src/StatementImporter.Worker/Imports/ProcessChunkHandler.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Csv;
using StatementImporter.Contracts.Messaging;
using StatementImporter.Data;

namespace StatementImporter.Worker.Imports;

public sealed class ProcessChunkHandler(
    StatementImporterDbContext db,
    IOptions<ImportOptions> options,
    ILogger<ProcessChunkHandler> logger)
{
    public async Task HandleAsync(ProcessChunk message, bool redelivered, CancellationToken cancellationToken)
    {
        var (importId, index) = (message.ImportId, message.ChunkIndex);

        if (redelivered && await RegisterAttemptAsync(importId, index, cancellationToken) > options.Value.MaxAttempts)
        {
            await FailAsync(importId, index, cancellationToken);
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var chunk = (await db.ImportChunks
            .FromSql($"SELECT * FROM import_chunks WHERE import_id = {importId} AND chunk_index = {index} FOR UPDATE")
            .ToListAsync(cancellationToken)).SingleOrDefault();
        var import = await db.Imports.AsNoTracking().SingleAsync(i => i.Id == importId, cancellationToken);

        if (chunk is null || chunk.Status != ChunkStatus.Pending || import.Status == ImportStatus.Failed)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        List<RawLine> lines;
        await using (var file = File.OpenRead(import.FilePath))
        {
            lines = ChunkReader.Read(file, new ChunkInfo(chunk.ChunkIndex, chunk.StartOffset, chunk.FirstLineNumber, chunk.LineCount));
        }

        var accepted = new List<AcceptedLine>();
        var rejected = new List<RejectedLine>();
        foreach (var line in lines)
        {
            var result = LineValidator.Validate(line.Text);
            if (result.IsValid)
                accepted.Add(new AcceptedLine(line, result.Line!));
            else
                rejected.Add(new RejectedLine(line.LineNumber, line.Text, result.ErrorReason!));
        }

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var inserted = await ChunkWriter.InsertTransactionsAsync(connection, importId, accepted, cancellationToken);
        rejected.AddRange(accepted
            .Where(a => !inserted.Contains(a.Raw.LineNumber))
            .Select(a => new RejectedLine(a.Raw.LineNumber, a.Raw.Text, ErrorReasons.Duplicate)));

        var storeLimit = Math.Max(0, options.Value.MaxStoredErrors - import.ErrorCount);
        await ChunkWriter.InsertErrorsAsync(connection, importId, rejected.OrderBy(r => r.LineNumber).Take(storeLimit), cancellationToken);

        chunk.Status = ChunkStatus.Done;
        chunk.ProcessedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var lineCount = chunk.LineCount;
        var validCount = inserted.Count;
        var errorCount = rejected.Count;
        await db.Imports.Where(i => i.Id == importId).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.ProcessedChunks, i => i.ProcessedChunks + 1)
            .SetProperty(i => i.ProcessedLines, i => i.ProcessedLines + lineCount)
            .SetProperty(i => i.ValidCount, i => i.ValidCount + validCount)
            .SetProperty(i => i.ErrorCount, i => i.ErrorCount + errorCount), cancellationToken);

        var progress = await db.Imports.AsNoTracking()
            .Where(i => i.Id == importId)
            .Select(i => new { i.ProcessedChunks, i.TotalChunks, i.ErrorCount })
            .SingleAsync(cancellationToken);

        if (progress.ProcessedChunks == progress.TotalChunks)
        {
            var finalStatus = progress.ErrorCount > 0 ? ImportStatus.CompletedWithErrors : ImportStatus.Completed;
            var completedAt = DateTime.UtcNow;
            await db.Imports.Where(i => i.Id == importId).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, finalStatus)
                .SetProperty(i => i.CompletedAt, completedAt), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Bloco {ChunkIndex} da importação {ImportId}: {Valid} válidas, {Errors} rejeitadas.",
            index, importId, validCount, errorCount);
    }

    private async Task<int> RegisterAttemptAsync(Guid importId, int index, CancellationToken cancellationToken)
    {
        await db.ImportChunks
            .Where(c => c.ImportId == importId && c.ChunkIndex == index && c.Status == ChunkStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, c => c.Attempts + 1), cancellationToken);

        return await db.ImportChunks
            .Where(c => c.ImportId == importId && c.ChunkIndex == index)
            .Select(c => c.Attempts)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task FailAsync(Guid importId, int index, CancellationToken cancellationToken)
    {
        var reason = $"Bloco {index} falhou após {options.Value.MaxAttempts} tentativas.";
        var completedAt = DateTime.UtcNow;
        logger.LogError("Importação {ImportId}: {Reason}", importId, reason);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.ImportChunks
            .Where(c => c.ImportId == importId && c.ChunkIndex == index)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ChunkStatus.Failed), cancellationToken);
        await db.Imports
            .Where(i => i.Id == importId && i.Status != ImportStatus.Failed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, ImportStatus.Failed)
                .SetProperty(i => i.FailureReason, reason)
                .SetProperty(i => i.CompletedAt, completedAt), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
```

Pontos que garantem a idempotência: o `FOR UPDATE` serializa workers no mesmo bloco, e o bloco `done` é descartado. O `UPDATE` dos contadores trava a linha da importação até o commit, então só um worker vê `processed_chunks = total_chunks` e finaliza. `attempts` só sobe com `redelivered`, então duplicatas novas não consomem tentativas.

- [ ] **Passo 6: rodar e ver passar**

Rodar: `dotnet test --project tests/StatementImporter.IntegrationTests`
Esperado: `falhou: 0` (8 testes).

- [ ] **Checkpoint** (sem commit).

---

### Task 8: Worker — host, consumidores, heartbeat e reconciliador

**Arquivos:**
- Criar: `src/StatementImporter.Worker/Hosting/Heartbeat.cs`, `Hosting/ConsumerHost.cs`, `Hosting/Reconciler.cs`, `appsettings.json`
- Substituir: `src/StatementImporter.Worker/Program.cs`
- Modificar: `tests/StatementImporter.UnitTests/StatementImporter.UnitTests.csproj` (referência ao Worker)
- Criar: `tests/StatementImporter.UnitTests/Hosting/HeartbeatTests.cs`

**Interfaces:**
- Consome: `ImportRequestedHandler`, `ProcessChunkHandler` (Tasks 6–7)
- Produz:
  - `Heartbeat.FilePath` (padrão `/tmp/heartbeat` no container), `Heartbeat.MaxAge = 30s`, `Heartbeat.Beat()`, `Heartbeat.Check(DateTime utcNow) : int` (0 saudável, 1 não)
  - Binário `StatementImporter.Worker --healthcheck` → `Heartbeat.Check` (usado pelo `HEALTHCHECK` na Task 10)
  - Se a conexão com o RabbitMQ não abre na partida, ou cai depois, o processo sai com **código 1**.

- [ ] **Passo 1: escrever o teste que falha**

Substituir `tests/StatementImporter.UnitTests/StatementImporter.UnitTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\StatementImporter.Contracts\StatementImporter.Contracts.csproj" />
    <ProjectReference Include="..\..\src\StatementImporter.Worker\StatementImporter.Worker.csproj" />
  </ItemGroup>

</Project>
```

`tests/StatementImporter.UnitTests/Hosting/HeartbeatTests.cs`:

```csharp
using StatementImporter.Worker.Hosting;

namespace StatementImporter.UnitTests.Hosting;

public class HeartbeatTests
{
    [Fact]
    public void Healthy_only_while_the_heartbeat_is_recent()
    {
        Heartbeat.FilePath = Path.Combine(Path.GetTempPath(), $"heartbeat-{Guid.NewGuid()}");
        Assert.Equal(1, Heartbeat.Check(DateTime.UtcNow));

        Heartbeat.Beat();

        Assert.Equal(0, Heartbeat.Check(DateTime.UtcNow));
        Assert.Equal(1, Heartbeat.Check(DateTime.UtcNow + Heartbeat.MaxAge));
        File.Delete(Heartbeat.FilePath);
    }
}
```

- [ ] **Passo 2: rodar e ver falhar**

Rodar: `dotnet test --project tests/StatementImporter.UnitTests`
Esperado: erro de compilação (`Heartbeat` não existe).

- [ ] **Passo 3: implementar o heartbeat**

`src/StatementImporter.Worker/Hosting/Heartbeat.cs`:

```csharp
namespace StatementImporter.Worker.Hosting;

public static class Heartbeat
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    public static string FilePath { get; set; } = Path.Combine(Path.GetTempPath(), "heartbeat");

    public static void Beat() => File.WriteAllText(FilePath, DateTime.UtcNow.ToString("O"));

    public static int Check(DateTime utcNow) =>
        File.Exists(FilePath) && utcNow - File.GetLastWriteTimeUtc(FilePath) < MaxAge ? 0 : 1;
}
```

- [ ] **Passo 4: rodar e ver passar**

Rodar: `dotnet test --project tests/StatementImporter.UnitTests`
Esperado: `falhou: 0` (39 testes).

- [ ] **Passo 5: implementar consumidores, reconciliador e `Program`**

`src/StatementImporter.Worker/Hosting/ConsumerHost.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Messaging;
using StatementImporter.Worker.Imports;

namespace StatementImporter.Worker.Hosting;

public sealed class ConsumerHost(IConnection connection, IServiceScopeFactory scopes, ILogger<ConsumerHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartConsumerAsync(QueueTopology.ImportRequested, ContractsJsonContext.Default.ImportRequested,
            (services, message, _, ct) => services.GetRequiredService<ImportRequestedHandler>().HandleAsync(message, ct),
            stoppingToken);
        await StartConsumerAsync(QueueTopology.ProcessChunk, ContractsJsonContext.Default.ProcessChunk,
            (services, message, redelivered, ct) => services.GetRequiredService<ProcessChunkHandler>().HandleAsync(message, redelivered, ct),
            stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!connection.IsOpen)
            {
                Environment.ExitCode = 1;
                throw new InvalidOperationException("Conexão com o RabbitMQ perdida; encerrando para o restart policy religar o worker.");
            }
            Heartbeat.Beat();
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task StartConsumerAsync<T>(
        string queue,
        JsonTypeInfo<T> typeInfo,
        Func<IServiceProvider, T, bool, CancellationToken, Task> handle,
        CancellationToken stoppingToken)
    {
        var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await QueueTopology.DeclareAsync(channel, stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            try
            {
                var message = JsonSerializer.Deserialize(delivery.Body.Span, typeInfo)
                    ?? throw new InvalidOperationException($"Mensagem vazia na fila {queue}.");
                await using var scope = scopes.CreateAsyncScope();
                await handle(scope.ServiceProvider, message, delivery.Redelivered, stoppingToken);
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Falha ao processar mensagem da fila {Queue}; devolvendo para a fila.", queue);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
            }
        };

        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);
    }
}
```

`src/StatementImporter.Worker/Hosting/Reconciler.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Messaging;
using StatementImporter.Data;

namespace StatementImporter.Worker.Hosting;

public sealed class Reconciler(
    IServiceScopeFactory scopes,
    MessagePublisher publisher,
    IOptions<ImportOptions> options,
    ILogger<Reconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ReconcileInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RepublishStaleImportsAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Falha no reconciliador; nova tentativa no próximo ciclo.");
            }
        }
    }

    public async Task RepublishStaleImportsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StatementImporterDbContext>();
        var threshold = DateTime.UtcNow - options.Value.QueuedThreshold;

        var stale = await db.Imports
            .Where(i => i.Status == ImportStatus.Queued && i.CreatedAt < threshold)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in stale)
        {
            logger.LogInformation("Republicando importação {ImportId} parada em queued.", id);
            await publisher.PublishAsync(QueueTopology.ImportRequested, new ImportRequested(id),
                ContractsJsonContext.Default.ImportRequested, cancellationToken);
        }
    }
}
```

Substituir `src/StatementImporter.Worker/Program.cs`:

```csharp
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using StatementImporter.Contracts.Configuration;
using StatementImporter.Contracts.Messaging;
using StatementImporter.Data;
using StatementImporter.Worker;
using StatementImporter.Worker.Hosting;
using StatementImporter.Worker.Imports;

if (args.Contains("--healthcheck"))
    return Heartbeat.Check(DateTime.UtcNow);

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddDockerSecrets();

builder.Services.Configure<ImportOptions>(builder.Configuration.GetSection("Import"));

var connectionString = ServiceConfiguration.BuildDbConnectionString(builder.Configuration);
builder.Services.AddDbContext<StatementImporterDbContext>(o => StatementImporterDbContext.Configure(o, connectionString));

IConnection rabbit;
try
{
    rabbit = await ServiceConfiguration.CreateRabbitMqFactory(builder.Configuration).CreateConnectionAsync();
}
catch (BrokerUnreachableException ex)
{
    Console.Error.WriteLine($"RabbitMQ indisponível: {ex.Message}");
    return 1;
}
builder.Services.AddSingleton(rabbit);
builder.Services.AddSingleton(await MessagePublisher.CreateAsync(rabbit));

builder.Services.AddScoped<ImportRequestedHandler>();
builder.Services.AddScoped<ProcessChunkHandler>();
builder.Services.AddHostedService<ConsumerHost>();
builder.Services.AddHostedService<Reconciler>();

await builder.Build().RunAsync();
return Environment.ExitCode;
```

`src/StatementImporter.Worker/appsettings.json` (sem isso, o EF loga cada SQL em `Information`):

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information",
      "Microsoft.EntityFrameworkCore": "Warning"
    }
  }
}
```

- [ ] **Passo 6: verificar**

Rodar: `dotnet build StatementImporter.slnx && dotnet test --project tests/StatementImporter.UnitTests && dotnet test --project tests/StatementImporter.IntegrationTests`
Esperado: `0 Aviso(s)`, `0 Erro(s)`; 39 unitários e 8 de integração com `falhou: 0`. O comportamento em runtime (consumo, restart, reconciliador) é verificado no stack real, na Task 13.

- [ ] **Checkpoint** (sem commit).

---

### Task 9: API — upload, consultas e healthcheck (Native AOT)

**Arquivos:**
- Criar: `src/StatementImporter.Api/StatementImporter.Api.csproj`, `HealthCheck.cs`, `Imports/UploadStorage.cs`, `Imports/Paging.cs`, `Imports/ImportRepository.cs`, `Imports/ImportEndpoints.cs`, `Program.cs`
- Modificar: `tests/StatementImporter.IntegrationTests/StatementImporter.IntegrationTests.csproj` (Mvc.Testing + referência à Api)
- Criar: `tests/StatementImporter.IntegrationTests/Api/ImportEndpointsTests.cs`

**Interfaces:**
- Consome: `ServiceConfiguration`, `MessagePublisher`, `QueueTopology`, `ContractsJsonContext`, DTOs de `Http/Responses.cs`, `CsvFormat` (Tasks 2–4); fixture e `ProcessAllAsync` (Task 7)
- Produz:
  - `POST /api/imports?fileName=` → `202` com `Location: /api/imports/{id}` · `200` mesmo arquivo · `400` cabeçalho inválido ou `fileName` ausente · `413` acima de 200 MB
  - `GET /api/imports`, `GET /api/imports/{id}`, `GET /api/imports/{id}/errors?page=&pageSize=`, `GET /api/transactions?importId=&page=&pageSize=`, `GET /health`, `GET /api/docs` (Scalar) e `GET /openapi/v1.json`
  - Binário `StatementImporter.Api --healthcheck` → `GET http://localhost:8080/health`, saída 0/1
  - `public partial class Program` (para `WebApplicationFactory<Program>`)

- [ ] **Passo 1: criar o projeto**

`src/StatementImporter.Api/StatementImporter.Api.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <PublishAot>true</PublishAot>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <InterceptorsNamespaces>$(InterceptorsNamespaces);Dapper.AOT</InterceptorsNamespaces>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Dapper" />
    <PackageReference Include="Dapper.AOT" />
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
    <PackageReference Include="Scalar.AspNetCore" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\StatementImporter.Contracts\StatementImporter.Contracts.csproj" />
  </ItemGroup>

</Project>
```

A `api` **não** referencia `Microsoft.Extensions.Configuration.KeyPerFile` diretamente: o pacote já vem no shared framework do ASP.NET, e a referência direta gera o aviso `NU1510`, que com `TreatWarningsAsErrors` vira erro.

Substituir `tests/StatementImporter.IntegrationTests/StatementImporter.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="Testcontainers.RabbitMq" />
    <PackageReference Include="xunit.v3" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\StatementImporter.Api\StatementImporter.Api.csproj" />
    <ProjectReference Include="..\..\src\StatementImporter.Data\StatementImporter.Data.csproj" />
    <ProjectReference Include="..\..\src\StatementImporter.Worker\StatementImporter.Worker.csproj" />
  </ItemGroup>

</Project>
```

```bash
dotnet sln StatementImporter.slnx add src/StatementImporter.Api/StatementImporter.Api.csproj
```

- [ ] **Passo 2: escrever os testes que falham**

`tests/StatementImporter.IntegrationTests/Api/ImportEndpointsTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Csv;
using StatementImporter.Contracts.Http;

namespace StatementImporter.IntegrationTests.Api;

[Collection(nameof(InfrastructureCollection))]
public sealed class ImportEndpointsTests(InfrastructureFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _ = fixture;
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    private static StringContent Csv(string content) => new(content, Encoding.UTF8, "text/csv");

    private static string UniqueCsv() =>
        CsvFormat.Header + "\n" + string.Join("\n", InfrastructureFixture.ValidLines($"API-{Guid.NewGuid():N}", 3)) + "\n";

    [Fact]
    public async Task Upload_is_accepted_and_queued()
    {
        var response = await _client.PostAsync("/api/imports?fileName=extrato.csv", Csv(UniqueCsv()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var import = await response.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ImportResponse, TestContext.Current.CancellationToken);
        Assert.Equal(("extrato.csv", ImportStatus.Queued), (import!.FileName, import.Status));
        Assert.Equal($"/api/imports/{import.Id}", response.Headers.Location!.OriginalString);
        Assert.True(File.Exists(Path.Combine(fixture.UploadsPath, $"{import.Id}.csv")));
    }

    [Fact]
    public async Task Uploading_the_same_file_twice_returns_the_same_import()
    {
        var content = UniqueCsv();

        var first = await _client.PostAsync("/api/imports?fileName=a.csv", Csv(content), TestContext.Current.CancellationToken);
        var second = await _client.PostAsync("/api/imports?fileName=b.csv", Csv(content), TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));
        var firstImport = await first.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ImportResponse, TestContext.Current.CancellationToken);
        var secondImport = await second.Content.ReadFromJsonAsync(ContractsJsonContext.Default.ImportResponse, TestContext.Current.CancellationToken);
        Assert.Equal(firstImport!.Id, secondImport!.Id);
        Assert.Single(Directory.GetFiles(fixture.UploadsPath), f => f.EndsWith($"{firstImport.Id}.csv"));
    }

    [Theory]
    [InlineData("id,valor\nTX-1,10.00\n")]
    [InlineData("")]
    public async Task Invalid_header_is_rejected(string content)
    {
        var response = await _client.PostAsync("/api/imports?fileName=ruim.csv", Csv(content), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_file_name_is_rejected()
    {
        var response = await _client.PostAsync("/api/imports", Csv(UniqueCsv()), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_import_returns_not_found()
    {
        var response = await _client.GetAsync($"/api/imports/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Processed_import_exposes_errors_and_transactions()
    {
        var prefix = $"READ-{Guid.NewGuid():N}";
        var import = await fixture.CreateQueuedImportAsync([$"{prefix}-1,2026-03-15,OK,10.50", $"{prefix}-2,data,RUIM,1.00"]);
        await fixture.ProcessAllAsync(import.Id, chunkSize: 10);

        var status = await _client.GetFromJsonAsync($"/api/imports/{import.Id}", ContractsJsonContext.Default.ImportResponse, TestContext.Current.CancellationToken);
        var errors = await _client.GetFromJsonAsync($"/api/imports/{import.Id}/errors", ContractsJsonContext.Default.PagedResponseImportErrorResponse, TestContext.Current.CancellationToken);
        var transactions = await _client.GetFromJsonAsync($"/api/transactions?importId={import.Id}", ContractsJsonContext.Default.PagedResponseTransactionResponse, TestContext.Current.CancellationToken);

        Assert.Equal((ImportStatus.CompletedWithErrors, 1, 1), (status!.Status, status.ValidCount, status.ErrorCount));
        Assert.Equal((3, ErrorReasons.InvalidDate), (errors!.Items.Single().LineNumber, errors.Items.Single().Reason));
        Assert.Equal(new TransactionResponse($"{prefix}-1", "2026-03-15", "OK", 10.50m, import.Id, 2), transactions!.Items.Single());
    }

    [Fact]
    public async Task Health_reports_ok_when_the_database_is_reachable()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }
}
```

O `WebApplicationFactory` executa o `Program` real. Ele lê `Db__*`, `RabbitMq__*` e `Import__UploadsPath` das variáveis de ambiente que a fixture definiu. O limite de 200 MB (`413`) não é testável aqui, porque o `TestServer` não aplica limites do Kestrel; ele é verificado no stack real (Task 13).

- [ ] **Passo 3: rodar e ver falhar**

Rodar: `dotnet test --project tests/StatementImporter.IntegrationTests`
Esperado: erro de compilação (`Program` não encontrado).

- [ ] **Passo 4: implementar**

`src/StatementImporter.Api/HealthCheck.cs`:

```csharp
namespace StatementImporter.Api;

public static class HealthCheck
{
    public static async Task<int> RunAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            var response = await http.GetAsync("http://localhost:8080/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }
}
```

`src/StatementImporter.Api/Imports/UploadStorage.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using StatementImporter.Contracts.Csv;

namespace StatementImporter.Api.Imports;

public sealed record StoredUpload(string Path, string Hash);

public sealed class UploadStorage
{
    public const long MaxUploadBytes = 200L * 1024 * 1024;
    private const int HeaderSniffLimit = 1024;

    private readonly string _root;

    public UploadStorage(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public async Task<StoredUpload?> SaveAsync(Guid importId, Stream body, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var filled = 0;
        var newline = -1;
        while (filled < HeaderSniffLimit)
        {
            var read = await body.ReadAsync(buffer.AsMemory(filled, HeaderSniffLimit - filled), cancellationToken);
            if (read == 0)
                break;
            var index = buffer.AsSpan(filled, read).IndexOf((byte)'\n');
            filled += read;
            if (index >= 0)
            {
                newline = filled - read + index;
                break;
            }
        }

        var header = Encoding.UTF8.GetString(buffer, 0, newline >= 0 ? newline : filled);
        if (!CsvFormat.IsHeader(header))
            return null;

        var path = Path.Combine(_root, $"{importId}.csv");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            hash.AppendData(buffer, 0, filled);
            await file.WriteAsync(buffer.AsMemory(0, filled), cancellationToken);

            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch
        {
            Delete(path);
            throw;
        }

        return new StoredUpload(path, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    public void Delete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
```

O cabeçalho é validado com o primeiro 1 KB do stream, antes de gravar o resto. Um arquivo errado de 200 MB recebe `400` sem ser enviado inteiro para o disco.

`src/StatementImporter.Api/Imports/Paging.cs`:

```csharp
namespace StatementImporter.Api.Imports;

public readonly record struct Paging(int Page, int PageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public int Offset => (Page - 1) * PageSize;

    public static Paging From(int? page, int? pageSize) =>
        new(Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));
}
```

`src/StatementImporter.Api/Imports/ImportRepository.cs`:

```csharp
using Dapper;
using Npgsql;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Http;

namespace StatementImporter.Api.Imports;

public sealed class ImportRepository(NpgsqlDataSource dataSource)
{
    private const string SelectImport = """
        SELECT id AS Id, file_name AS FileName, status AS Status, total_lines AS TotalLines,
               processed_lines AS ProcessedLines, valid_count AS ValidCount, error_count AS ErrorCount,
               failure_reason AS FailureReason, created_at AS CreatedAt, started_at AS StartedAt,
               completed_at AS CompletedAt
        FROM imports
        """;

    public async Task<bool> TryInsertAsync(Guid id, string fileName, string fileHash, string filePath)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        var rows = await connection.ExecuteAsync("""
            INSERT INTO imports (id, file_name, file_hash, file_path, status, total_lines, total_chunks,
                                 processed_chunks, processed_lines, valid_count, error_count, created_at)
            VALUES (@id, @fileName, @fileHash, @filePath, @status, 0, 0, 0, 0, 0, 0, now())
            ON CONFLICT (file_hash) DO NOTHING
            """, new { id, fileName, fileHash, filePath, status = ImportStatus.Queued });
        return rows == 1;
    }

    public async Task<ImportResponse?> GetAsync(Guid id)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<ImportResponse>(SelectImport + " WHERE id = @id", new { id });
    }

    public async Task<ImportResponse?> GetByHashAsync(string fileHash)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<ImportResponse>(SelectImport + " WHERE file_hash = @fileHash", new { fileHash });
    }

    public async Task<IReadOnlyList<ImportResponse>> ListRecentAsync()
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync<ImportResponse>(SelectImport + " ORDER BY created_at DESC LIMIT 20");
        return rows.AsList();
    }

    public async Task<PagedResponse<ImportErrorResponse>> GetErrorsAsync(Guid importId, Paging paging)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        var items = await connection.QueryAsync<ImportErrorResponse>("""
            SELECT line_number AS LineNumber, raw_line AS RawLine, reason AS Reason
            FROM import_errors WHERE import_id = @importId
            ORDER BY line_number LIMIT @limit OFFSET @offset
            """, new { importId, limit = paging.PageSize, offset = paging.Offset });
        var total = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM import_errors WHERE import_id = @importId", new { importId });
        return new PagedResponse<ImportErrorResponse>(items.AsList(), paging.Page, paging.PageSize, total);
    }

    public async Task<PagedResponse<TransactionResponse>> GetTransactionsAsync(Guid? importId, Paging paging)
    {
        const string columns = """
            SELECT transaction_id AS TransactionId, to_char(date, 'YYYY-MM-DD') AS Date, description AS Description,
                   amount AS Amount, import_id AS ImportId, line_number AS LineNumber
            FROM transactions
            """;
        await using var connection = await dataSource.OpenConnectionAsync();

        IEnumerable<TransactionResponse> items;
        long total;
        if (importId is { } id)
        {
            items = await connection.QueryAsync<TransactionResponse>(
                columns + " WHERE import_id = @id ORDER BY line_number LIMIT @limit OFFSET @offset",
                new { id, limit = paging.PageSize, offset = paging.Offset });
            total = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM transactions WHERE import_id = @id", new { id });
        }
        else
        {
            items = await connection.QueryAsync<TransactionResponse>(
                columns + " ORDER BY id LIMIT @limit OFFSET @offset",
                new { limit = paging.PageSize, offset = paging.Offset });
            total = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM transactions");
        }
        return new PagedResponse<TransactionResponse>(items.AsList(), paging.Page, paging.PageSize, total);
    }
}
```

Os aliases (`file_name AS FileName`) mapeiam as colunas snake_case para os construtores dos records. A data sai como texto (`to_char`), o que evita depender do suporte do Dapper.AOT a `DateOnly`. Os dois SQLs de `GetTransactionsAsync` evitam `@importId IS NULL`, que o Postgres rejeita com parâmetro nulo sem tipo.

`src/StatementImporter.Api/Imports/ImportEndpoints.cs`:

```csharp
using RabbitMQ.Client.Exceptions;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Csv;
using StatementImporter.Contracts.Http;
using StatementImporter.Contracts.Messaging;

namespace StatementImporter.Api.Imports;

public static class ImportEndpoints
{
    public static void MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var imports = app.MapGroup("/api/imports");

        imports.MapPost("", UploadAsync)
            .Produces<ImportResponse>(StatusCodes.Status202Accepted)
            .Produces<ImportResponse>(StatusCodes.Status200OK)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        imports.MapGet("", async (ImportRepository repository) => Results.Ok(await repository.ListRecentAsync()))
            .Produces<IReadOnlyList<ImportResponse>>();

        imports.MapGet("{id:guid}", async (Guid id, ImportRepository repository) =>
                await repository.GetAsync(id) is { } import ? Results.Ok(import) : Results.NotFound())
            .Produces<ImportResponse>()
            .Produces(StatusCodes.Status404NotFound);

        imports.MapGet("{id:guid}/errors", async (Guid id, int? page, int? pageSize, ImportRepository repository) =>
                await repository.GetAsync(id) is null
                    ? Results.NotFound()
                    : Results.Ok(await repository.GetErrorsAsync(id, Paging.From(page, pageSize))))
            .Produces<PagedResponse<ImportErrorResponse>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/transactions", async (Guid? importId, int? page, int? pageSize, ImportRepository repository) =>
                Results.Ok(await repository.GetTransactionsAsync(importId, Paging.From(page, pageSize))))
            .Produces<PagedResponse<TransactionResponse>>();
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request,
        string? fileName,
        UploadStorage storage,
        ImportRepository repository,
        MessagePublisher publisher,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255)
            return Results.BadRequest(new ErrorResponse("Informe fileName na query string (até 255 caracteres)."));

        var id = Guid.CreateVersion7();
        StoredUpload? upload;
        try
        {
            upload = await storage.SaveAsync(id, request.Body, cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (upload is null)
            return Results.BadRequest(new ErrorResponse($"Cabeçalho inválido. Esperado: {CsvFormat.Header}"));

        if (!await repository.TryInsertAsync(id, fileName, upload.Hash, upload.Path))
        {
            storage.Delete(upload.Path);
            return Results.Ok(await repository.GetByHashAsync(upload.Hash));
        }

        try
        {
            await publisher.PublishAsync(QueueTopology.ImportRequested, new ImportRequested(id),
                ContractsJsonContext.Default.ImportRequested, cancellationToken);
        }
        catch (Exception ex) when (ex is AlreadyClosedException or OperationInterruptedException)
        {
            loggerFactory.CreateLogger(nameof(ImportEndpoints)).LogWarning(ex,
                "Fila indisponível; a importação {ImportId} ficou em queued e será republicada pelo reconciliador.", id);
        }

        return Results.Accepted($"/api/imports/{id}", await repository.GetAsync(id));
    }
}
```

`src/StatementImporter.Api/Program.cs`:

```csharp
using Dapper;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Scalar.AspNetCore;
using StatementImporter.Api;
using StatementImporter.Api.Imports;
using StatementImporter.Contracts;
using StatementImporter.Contracts.Configuration;
using StatementImporter.Contracts.Messaging;

[module: DapperAot]

if (args.Contains("--healthcheck"))
    return await HealthCheck.RunAsync();

var builder = WebApplication.CreateSlimBuilder(args);
builder.Configuration.AddDockerSecrets();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = UploadStorage.MaxUploadBytes);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ContractsJsonContext.Default));
builder.Services.AddOpenApi();

builder.Services.AddSingleton(NpgsqlDataSource.Create(ServiceConfiguration.BuildDbConnectionString(builder.Configuration)));
builder.Services.AddSingleton<ImportRepository>();
builder.Services.AddSingleton(new UploadStorage(builder.Configuration["Import:UploadsPath"] ?? "/data/uploads"));

IConnection rabbit;
try
{
    rabbit = await ServiceConfiguration.CreateRabbitMqFactory(builder.Configuration).CreateConnectionAsync();
}
catch (BrokerUnreachableException ex)
{
    Console.Error.WriteLine($"RabbitMQ indisponível: {ex.Message}");
    return 1;
}
builder.Services.AddSingleton(rabbit);
builder.Services.AddSingleton(await MessagePublisher.CreateAsync(rabbit));

var app = builder.Build();

rabbit.ConnectionShutdownAsync += (_, e) =>
{
    if (e.Initiator != ShutdownInitiator.Application)
    {
        app.Logger.LogCritical("Conexão com o RabbitMQ perdida ({Reason}); encerrando para o restart policy religar a API.", e.ReplyText);
        Environment.ExitCode = 1;
        app.Lifetime.StopApplication();
    }
    return Task.CompletedTask;
};

app.MapOpenApi();
app.MapScalarApiReference("/api/docs");

app.MapGet("/health", async (NpgsqlDataSource dataSource, IConnection queue, CancellationToken cancellationToken) =>
{
    if (!queue.IsOpen)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return Results.Ok();
    }
    catch (NpgsqlException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapImportEndpoints();

await app.RunAsync();
return Environment.ExitCode;

public partial class Program;
```

Se a conexão com o RabbitMQ cair, a API **encerra** em vez de seguir com um canal morto, que devolveria `500` para sempre (bug encontrado na validação). O restart policy religa a API. Um upload pego no meio da queda responde `202` e fica `queued` até o reconciliador do worker republicar.

- [ ] **Passo 5: rodar e ver passar**

Rodar: `dotnet build StatementImporter.slnx && dotnet test --project tests/StatementImporter.IntegrationTests`
Esperado: `0 Aviso(s)`, `0 Erro(s)`; `falhou: 0` (16 testes: 8 do worker + 8 da API).

- [ ] **Checkpoint** (sem commit). A publicação AOT de verdade (Linux) acontece dentro do Docker, na Task 10.

---

### Task 10: Imagens Docker e stack de produção

**Arquivos:**
- Criar: `src/StatementImporter.Api/Dockerfile`, `src/StatementImporter.Worker/Dockerfile`, `src/StatementImporter.Data/Dockerfile`, `.dockerignore`, `compose.yml`
- Criar (local, fora do git): `secrets/db_password.txt`

**Interfaces:**
- Consome: binários com `--healthcheck` (Tasks 8–9); migration bundle via `DesignTimeDbContextFactory` (Task 5); `dotnet-tools.json` (Task 1)
- Produz: imagens `ghcr.io/yudikojima/statement-importer-{api,worker,migrator}:${TAG:-local}` com target `runtime`; target `dev` em `api` e `worker` (usado na Task 11); stack com redes `internal` (`internal: true`) e `public`, volumes `pgdata` e `uploads`, secret `Db__Password`

- [ ] **Passo 1: `.dockerignore`**

```
**/bin/
**/obj/
**/TestResults/
.git/
.vs/
.vscode/
data/
docs/
secrets/
tests/
.env
**/Dockerfile
compose*.yml
```

- [ ] **Passo 2: `src/StatementImporter.Api/Dockerfile`**

```dockerfile
# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot AS restore
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/StatementImporter.Contracts/StatementImporter.Contracts.csproj src/StatementImporter.Contracts/
COPY src/StatementImporter.Api/StatementImporter.Api.csproj src/StatementImporter.Api/
RUN dotnet restore src/StatementImporter.Api/StatementImporter.Api.csproj -r linux-x64

FROM restore AS build
COPY src/StatementImporter.Contracts/ src/StatementImporter.Contracts/
COPY src/StatementImporter.Api/ src/StatementImporter.Api/
RUN dotnet build src/StatementImporter.Api/StatementImporter.Api.csproj -c Release -r linux-x64 --no-restore

FROM build AS dev
ENV DOTNET_USE_POLLING_FILE_WATCHER=1
WORKDIR /src/src/StatementImporter.Api
ENTRYPOINT ["dotnet", "watch", "run", "--no-launch-profile", "--non-interactive", "-r", "linux-x64"]

FROM build AS publish
RUN dotnet publish src/StatementImporter.Api/StatementImporter.Api.csproj -c Release -r linux-x64 --no-restore -o /out \
 && mkdir /empty

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS runtime
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/YudiKojima/statement-importer" \
      org.opencontainers.image.title="statement-importer-api" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
WORKDIR /app
COPY --from=publish --chown=1654:1654 /empty /data/uploads
COPY --from=publish /out/StatementImporter.Api ./
HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=3 CMD ["/app/StatementImporter.Api", "--healthcheck"]
ENTRYPOINT ["/app/StatementImporter.Api"]
```

`COPY --chown=1654:1654 /empty /data/uploads` cria o diretório com dono `app`. Na primeira montagem, o volume nomeado vazio herda esse dono, e sem isso a API (UID 1654) não conseguiria gravar. O `-r linux-x64` no `dotnet watch` do estágio `dev` reaproveita o restore da imagem: o worker fica numa rede sem internet e não conseguiria restaurar de novo.

- [ ] **Passo 3: `src/StatementImporter.Worker/Dockerfile`**

```dockerfile
# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/StatementImporter.Contracts/StatementImporter.Contracts.csproj src/StatementImporter.Contracts/
COPY src/StatementImporter.Data/StatementImporter.Data.csproj src/StatementImporter.Data/
COPY src/StatementImporter.Worker/StatementImporter.Worker.csproj src/StatementImporter.Worker/
RUN dotnet restore src/StatementImporter.Worker/StatementImporter.Worker.csproj -r linux-x64

FROM restore AS build
COPY src/StatementImporter.Contracts/ src/StatementImporter.Contracts/
COPY src/StatementImporter.Data/ src/StatementImporter.Data/
COPY src/StatementImporter.Worker/ src/StatementImporter.Worker/
RUN dotnet build src/StatementImporter.Worker/StatementImporter.Worker.csproj -c Release -r linux-x64 --no-restore

FROM build AS dev
ENV DOTNET_USE_POLLING_FILE_WATCHER=1
WORKDIR /src/src/StatementImporter.Worker
ENTRYPOINT ["dotnet", "watch", "run", "--no-launch-profile", "--non-interactive", "-r", "linux-x64"]

FROM build AS publish
RUN dotnet publish src/StatementImporter.Worker/StatementImporter.Worker.csproj -c Release -r linux-x64 --self-contained false --no-restore -o /out \
 && mkdir /empty

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS runtime
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/YudiKojima/statement-importer" \
      org.opencontainers.image.title="statement-importer-worker" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
WORKDIR /app
COPY --from=publish --chown=1654:1654 /empty /data/uploads
COPY --from=publish /out ./
HEALTHCHECK --interval=10s --timeout=3s --start-period=15s --retries=3 CMD ["/app/StatementImporter.Worker", "--healthcheck"]
ENTRYPOINT ["/app/StatementImporter.Worker"]
```

- [ ] **Passo 4: `src/StatementImporter.Data/Dockerfile`** (migrator)

```dockerfile
# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY dotnet-tools.json ./
RUN dotnet tool restore
COPY src/StatementImporter.Contracts/StatementImporter.Contracts.csproj src/StatementImporter.Contracts/
COPY src/StatementImporter.Data/StatementImporter.Data.csproj src/StatementImporter.Data/
RUN dotnet restore src/StatementImporter.Data/StatementImporter.Data.csproj -r linux-x64

FROM restore AS publish
COPY src/StatementImporter.Contracts/ src/StatementImporter.Contracts/
COPY src/StatementImporter.Data/ src/StatementImporter.Data/
RUN dotnet ef migrations bundle \
      --project src/StatementImporter.Data --startup-project src/StatementImporter.Data \
      --configuration Release --self-contained -r linux-x64 -o /out/efbundle

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS runtime
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/YudiKojima/statement-importer" \
      org.opencontainers.image.title="statement-importer-migrator" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
WORKDIR /app
COPY --from=publish /out/efbundle ./
ENTRYPOINT ["/app/efbundle"]
```

`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` porque o bundle é gerado por um projeto do próprio EF, que não herda o `InvariantGlobalization`, e a imagem chiseled não tem ICU.

- [ ] **Passo 5: `compose.yml`**

```yaml
name: statement-importer

x-db-env: &db-env
  Db__Host: postgres
  Db__Database: statement_importer
  Db__Username: importer

x-rabbitmq-env: &rabbitmq-env
  RabbitMq__Host: rabbitmq
  RabbitMq__Username: importer
  RabbitMq__Password: importer

x-build-args: &build-args
  VERSION: ${VERSION:-dev}
  REVISION: ${REVISION:-unknown}

services:
  postgres:
    image: postgres:17-alpine
    environment:
      POSTGRES_DB: statement_importer
      POSTGRES_USER: importer
      POSTGRES_PASSWORD_FILE: /run/secrets/Db__Password
    secrets: [Db__Password]
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U importer -d statement_importer"]
      interval: 5s
      timeout: 3s
      retries: 10
    networks: [internal]

  rabbitmq:
    image: rabbitmq:4-management-alpine
    environment:
      RABBITMQ_DEFAULT_USER: importer
      RABBITMQ_DEFAULT_PASS: importer
    healthcheck:
      test: ["CMD", "rabbitmq-diagnostics", "-q", "ping"]
      interval: 10s
      timeout: 5s
      retries: 10
      start_period: 20s
    networks: [internal]

  migrator:
    build:
      context: .
      dockerfile: src/StatementImporter.Data/Dockerfile
      target: runtime
      args: *build-args
    image: ghcr.io/yudikojima/statement-importer-migrator:${TAG:-local}
    environment: *db-env
    secrets: [Db__Password]
    depends_on:
      postgres:
        condition: service_healthy
    restart: "no"
    networks: [internal]

  api:
    build:
      context: .
      dockerfile: src/StatementImporter.Api/Dockerfile
      target: runtime
      args: *build-args
    image: ghcr.io/yudikojima/statement-importer-api:${TAG:-local}
    environment:
      <<: [*db-env, *rabbitmq-env]
    secrets: [Db__Password]
    volumes:
      - uploads:/data/uploads
    ports:
      - "8080:8080"
    depends_on:
      migrator:
        condition: service_completed_successfully
      rabbitmq:
        condition: service_healthy
    restart: unless-stopped
    networks: [internal, public]

  worker:
    build:
      context: .
      dockerfile: src/StatementImporter.Worker/Dockerfile
      target: runtime
      args: *build-args
    image: ghcr.io/yudikojima/statement-importer-worker:${TAG:-local}
    environment:
      <<: [*db-env, *rabbitmq-env]
    secrets: [Db__Password]
    volumes:
      - uploads:/data/uploads:ro
    depends_on:
      migrator:
        condition: service_completed_successfully
      rabbitmq:
        condition: service_healthy
    restart: unless-stopped
    deploy:
      resources:
        limits:
          cpus: "0.5"
          memory: 256M
    networks: [internal]

secrets:
  Db__Password:
    file: ./secrets/db_password.txt

volumes:
  pgdata:
  uploads:

networks:
  internal:
    internal: true
  public:
```

O RabbitMQ usa o usuário `importer`, porque o `guest` padrão só aceita conexões de `localhost`. A `api` fica em `public` e publica a 8080 só nesta fase; a fase 02 move a porta para o `proxy`.

- [ ] **Passo 6: criar o secret local e buildar**

```bash
cp secrets/db_password.txt.example secrets/db_password.txt
docker compose build --progress plain 2>&1 | tee /tmp/build.log | grep -cE "warning IL[0-9]{4}"
```

Esperado: `0` (nenhum warning de trimming/AOT no publish Linux) e build terminando sem erro (~1 min sem cache).

- [ ] **Passo 7: subir e verificar**

```bash
docker compose up -d --wait
docker compose ps -a --format '{{.Service}}\t{{.Status}}'
```

Esperado: `up --wait` sai com 0 em ~15 s; `api`, `worker`, `postgres` e `rabbitmq` com `(healthy)`; `migrator` com `Exited (0)`; `docker compose logs migrator` termina em `Done.`

- [ ] **Passo 8: tamanhos e usuário**

```bash
for s in api worker migrator; do i=ghcr.io/yudikojima/statement-importer-$s:local; echo "$s: $(( $(docker save $i | gzip -c | wc -c)/1000000 ))MB user=$(docker image inspect $i --format '{{.Config.User}}')"; done
```

Esperado (medido no rascunho): `api: 17MB user=1654`, `worker: 45MB user=1654`, `migrator: 50MB user=1654`. Metas: < 25, < 70 e < 60 MB.

- [ ] **Checkpoint** (sem commit). Deixar o stack de pé para a Task 13, ou `docker compose down` (sem `-v`).

---

### Task 11: Ambiente de desenvolvimento com Compose Watch

**Arquivos:**
- Criar: `compose.dev.yml`

**Interfaces:**
- Consome: targets `dev` dos Dockerfiles (Task 10)
- Produz: `docker compose -f compose.yml -f compose.dev.yml watch`. Sincroniza `src/` para dentro dos containers, e o `dotnet watch` recompila. Postgres em `localhost:5432`, painel do RabbitMQ em `localhost:15672`.

O nome é `compose.dev.yml` de propósito: um `compose.override.yml` seria carregado por **qualquer** `docker compose up`, e o CI e as provas da fase 03 rodariam as imagens de dev.

- [ ] **Passo 1: criar `compose.dev.yml`**

```yaml
# Desenvolvimento: docker compose -f compose.yml -f compose.dev.yml watch
services:
  postgres:
    ports:
      - "5432:5432"

  rabbitmq:
    ports:
      - "15672:15672"

  api:
    build:
      target: dev
    image: statement-importer-api:dev
    healthcheck:
      test: ["CMD", "curl", "-fs", "http://localhost:8080/health"]
      start_period: 60s
    develop:
      watch:
        - action: sync
          path: ./src/StatementImporter.Api
          target: /src/src/StatementImporter.Api
          ignore: [bin/, obj/]
        - action: sync
          path: ./src/StatementImporter.Contracts
          target: /src/src/StatementImporter.Contracts
          ignore: [bin/, obj/]
        - action: rebuild
          path: ./Directory.Packages.props

  worker:
    build:
      target: dev
    image: statement-importer-worker:dev
    healthcheck:
      test: ["CMD-SHELL", "test $$(( $$(date +%s) - $$(stat -c %Y /tmp/heartbeat) )) -lt 30"]
      start_period: 60s
    deploy:
      resources:
        limits:
          cpus: "2"
          memory: 1G
    develop:
      watch:
        - action: sync
          path: ./src/StatementImporter.Worker
          target: /src/src/StatementImporter.Worker
          ignore: [bin/, obj/]
        - action: sync
          path: ./src/StatementImporter.Contracts
          target: /src/src/StatementImporter.Contracts
          ignore: [bin/, obj/]
        - action: sync
          path: ./src/StatementImporter.Data
          target: /src/src/StatementImporter.Data
          ignore: [bin/, obj/]
        - action: rebuild
          path: ./Directory.Packages.props
```

Os healthchecks de dev usam `curl`/`stat`, que existem na imagem SDK. O worker de dev ganha 2 CPUs e 1 GB, porque o `dotnet watch` não cabe em 256 MB.

- [ ] **Passo 2: subir o ambiente de dev**

```bash
docker compose -f compose.yml -f compose.dev.yml up -d --build --wait
docker compose ps -a --format '{{.Service}}\t{{.Image}}\t{{.Status}}'
```

Esperado: `api` e `worker` com as imagens `statement-importer-*:dev` e `(healthy)`.

- [ ] **Passo 3: verificar o hot reload**

Num terminal: `docker compose -f compose.yml -f compose.dev.yml watch --no-up`

Em outro:

```bash
curl -s -X POST localhost:8080/api/imports
sed -i 's/Informe fileName na query string/HOT-RELOAD OK: informe fileName/' src/StatementImporter.Api/Imports/ImportEndpoints.cs
sleep 15; curl -s -X POST localhost:8080/api/imports
sed -i 's/HOT-RELOAD OK: informe fileName/Informe fileName na query string/' src/StatementImporter.Api/Imports/ImportEndpoints.cs
```

Esperado: a segunda resposta contém `HOT-RELOAD OK` (no rascunho, ~9 s), e o `watch` mostra `Syncing service "api"`. Depois de reverter o arquivo, encerrar o `watch` com Ctrl+C e rodar `docker compose -f compose.yml -f compose.dev.yml down` (sem `-v`).

- [ ] **Checkpoint** (sem commit).

---

### Task 12: Gerador de CSV para demonstrações

**Arquivos:**
- Criar: `tools/generate-csv.cs`

**Interfaces:**
- Produz: `dotnet run tools/generate-csv.cs -- --lines N --error-rate R --seed S --out caminho [--prefix P]`. A saída é determinística por `--seed`. Os IDs são `{prefix}-{i:D8}`, com prefixo padrão `TX{seed}`, então seeds diferentes não colidem. Os erros propositais giram entre coluna extra, ID vazio, data inválida, descrição vazia, valor com vírgula e ID duplicado.

- [ ] **Passo 1: criar `tools/generate-csv.cs`**

```csharp
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
```

- [ ] **Passo 2: verificar**

```bash
dotnet run tools/generate-csv.cs -- --lines 1000000 --error-rate 0.01 --seed 42 --out data/1m.csv
ls -la data/1m.csv
```

Esperado: `data/1m.csv: 1000000 linhas, ~9962 com erro proposital (seed 42).` e um arquivo de ~50 MB. A pasta `data/` está no `.gitignore` e no `.dockerignore`.

- [ ] **Checkpoint** (sem commit).

---

### Task 13: Verificação dos critérios de aceite

**Arquivos:** nenhum (só verificação). Usa o stack da Task 10 e os CSVs da Task 12.

Cada passo corresponde a um critério de [`design.md`](design.md#critérios-de-aceite). Valores entre parênteses são os medidos no rascunho.

- [ ] **Critério 1:** `docker compose up -d --wait`, depois `docker compose ps -a`. Esperado: quatro serviços `healthy`, `migrator` com `Exited (0)`.

- [ ] **Critérios 2 e 3: upload de 1M de linhas e contagem exata**

```bash
R=$(curl -s -w '\n%{http_code} %{time_total}s' -X POST --data-binary @data/1m.csv -H 'Content-Type: text/csv' "localhost:8080/api/imports?fileName=1m.csv")
echo "$R" | tail -1
ID=$(echo "$R" | head -1 | grep -o '"id":"[^"]*"' | cut -d'"' -f4)
until curl -s localhost:8080/api/imports/$ID | grep -qE '"status":"(completed|completed_with_errors|failed)"'; do sleep 2; done
curl -s localhost:8080/api/imports/$ID
docker compose exec -T postgres psql -U importer -d statement_importer -tAc "select count(*) from transactions where import_id='$ID'"
```

Esperado: `202` em menos de 1 s (0,22 s, contando o envio de 50 MB); `completed_with_errors` em ~40 s com 1 worker; `validCount + errorCount = 1000000`, e o `count(*)` igual a `validCount` (990055 + 9945).

- [ ] **Critério 4: reenvio do mesmo arquivo**

```bash
curl -s -o /dev/null -w '%{http_code}\n' -X POST --data-binary @data/1m.csv "localhost:8080/api/imports?fileName=de-novo.csv"
```

Esperado: `200`, e o corpo (sem `-o /dev/null`) traz o mesmo `id`.

- [ ] **Critério 5: 3 workers e `docker kill` no meio**

```bash
dotnet run tools/generate-csv.cs -- --lines 1000000 --error-rate 0.01 --seed 43 --out data/1m-b.csv
docker compose up -d --wait --scale worker=3
ID=$(curl -s -X POST --data-binary @data/1m-b.csv "localhost:8080/api/imports?fileName=1m-b.csv" | grep -o '"id":"[^"]*"' | cut -d'"' -f4)
sleep 6; docker kill statement-importer-worker-2
until curl -s localhost:8080/api/imports/$ID | grep -qE '"status":"(completed|completed_with_errors|failed)"'; do sleep 2; done
curl -s localhost:8080/api/imports/$ID
docker compose exec -T postgres psql -U importer -d statement_importer -tAc "select count(*) from transactions where import_id='$ID'"
docker compose exec -T postgres psql -U importer -d statement_importer -tAc "select count(*) from import_chunks where import_id='$ID' and attempts>0"
docker compose ps -a worker
```

Esperado: `count(*)` igual a `validCount` (990009), pelo menos 1 bloco com `attempts>0` (o reentregue) e `worker-2` em `Exited (137)`, porque `docker kill` não dispara restart. Tempo total ~15 s com 3 workers, contra ~38 s com 1.

- [ ] **Critério 6:** os tamanhos e usuários do Passo 8 da Task 10.

- [ ] **Critério 7: RabbitMQ parado**

```bash
docker compose up -d --scale worker=1
docker compose stop rabbitmq; sleep 15; docker compose ps -a api worker
docker compose start rabbitmq
sleep 60; for c in api worker; do docker inspect statement-importer-$c-1 --format "$c {{.State.Status}} {{.State.Health.Status}} restarts={{.RestartCount}}"; done
docker compose logs api | grep -E "perdida|indisponível" | head -2
```

Esperado: com o RabbitMQ parado, `worker` e `api` em `Restarting (1)`. Depois, os dois `running healthy` com `restarts > 0`, e os logs mostram `Conexão com o RabbitMQ perdida` e `RabbitMQ indisponível`.

- [ ] **Critério 8: reconciliador.** Simula a API caindo entre gravar a importação e publicar a mensagem: o arquivo vai para o volume e a linha entra como `queued`, sem nenhuma mensagem na fila. Não dá para provocar isso por upload com o RabbitMQ parado, porque nessa situação a API está reiniciando e não aceita requisições.

```bash
export MSYS_NO_PATHCONV=1
ID=$(powershell -NoProfile -Command "[guid]::NewGuid().ToString()" | tr -d '\r')
printf 'transaction_id,date,description,amount\nRECONCILE-1,2026-03-15,TESTE,1.00\n' > data/reconcile.csv
docker cp data/reconcile.csv statement-importer-api-1:/data/uploads/$ID.csv
docker compose exec -T postgres psql -U importer -d statement_importer -qc "insert into imports (id,file_name,file_hash,file_path,status,total_lines,total_chunks,processed_chunks,processed_lines,valid_count,error_count,created_at) values ('$ID','reconcile.csv',repeat('a',64),'/data/uploads/$ID.csv','queued',0,0,0,0,0,0, now() - interval '2 minutes')"
sleep 70; docker compose exec -T postgres psql -U importer -d statement_importer -tAc "select status from imports where id='$ID'"
docker compose logs worker | grep Republicando | tail -1
```

Esperado: `completed` (~40 s na execução da fase) e o log `Republicando importação ... parada em queued.`

- [ ] **Critério 9:** `dotnet test --project tests/StatementImporter.UnitTests && dotnet test --project tests/StatementImporter.IntegrationTests`. Esperado: 39 e 16 testes, `falhou: 0`.

- [ ] **Critério 10:** a contagem `0` de `warning IL` no Passo 6 da Task 10.

- [ ] **Limite de upload (413)**

```bash
{ echo 'transaction_id,date,description,amount'; head -c 210000000 /dev/zero | tr '\0' 'a'; } > data/grande.csv
curl -s -o /dev/null -w '%{http_code}\n' -X POST --data-binary @data/grande.csv "localhost:8080/api/imports?fileName=grande.csv"
docker compose exec -T postgres psql -U importer -d statement_importer -tAc "select count(*) from imports where file_name='grande.csv'"
rm data/grande.csv
```

Esperado: `413` e `0` (nenhuma importação criada; o arquivo parcial é apagado do volume).

- [ ] **Encerrar:** `docker compose down` (sem `-v` para manter os dados; `-v` para zerar).

- [ ] **Fim da fase:** seguir o fluxo do [`CLAUDE.md`](../../../CLAUDE.md): `/verification-before-completion` → `/requesting-code-review` → `/finishing-a-development-branch`. Este último marca a fase como `✅ Concluída` no `project-overview.md` e faz o único commit da fase.

---

## Ajustes do code review (2026-10-21)

Aplicados depois da execução das 13 tasks. O código no repositório é a referência; os blocos de código das tasks acima mostram a versão anterior a estes ajustes.

| # | Problema encontrado | Ajuste |
|---|---|---|
| 1 | Com o banco fora, `nack` imediato fazia a mensagem passar das 20 entregas do `delivery-limit` e ser descartada sem `attempts` subir. O bloco ficava `pending` para sempre. | O reconciliador virou `Imports/ImportReconciler.cs` (testável) e passou a republicar blocos `pending` de importações sem progresso há `StalledThreshold` (2 min). `NackDelay` de 1 s antes do `nack`. |
| 8 | Cada réplica republicava todas as importações `queued`, amplificando mensagens durante backlog. | O reconciliador só age quando a fila correspondente está vazia (`MessageCountAsync`). |
| 2 | `UnauthorizedAccessException` na leitura do arquivo não levava a `failed`. | `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)` no divisor. |
| 3 | `FailAsync` podia marcar como `failed` um bloco que outro worker acabara de concluir. | Só falha bloco ainda `pending`; se nada mudou, não toca na importação. |
| 4 | Com o mesmo `transaction_id` duas vezes no bloco, a linha que vencia era arbitrária. | `ORDER BY transaction_id, line_number` (a primeira linha vence). |
| 5 | Arquivo alterado depois da divisão quebrava o invariante de contagem em silêncio. | `InvalidDataException` se o bloco ler menos linhas que o planejado. |
| 6 | `PublishException` (confirm negativo) virava `500` no upload. | Incluída no filtro que responde `202`. |
| 7 | `page` muito grande estourava o offset. | `page` limitado a `int.MaxValue / 100`. |
| 9 | Portas de dev não respondiam: serviço só em rede `internal: true` não publica porta (verificado). | `postgres` e `rabbitmq` entram também na rede `public` no `compose.dev.yml`. |
| 10 | Canal fechado (sem a conexão cair) parava consumo/publicação em silêncio. | Worker checa conexão, canais de consumo e canal de publicação; a API encerra também no `ChannelShutdownAsync` do publisher (`MessagePublisher.IsOpen` e `ShutdownAsync`). |
| 11 | Faltavam testes de concorrência e do reconciliador; a asserção de deduplicação era fraca. | +5 testes de integração (21 no total): 20 entregas simultâneas com duplicatas entre blocos, arquivo truncado, 3 do reconciliador; contagem de arquivos no teste de deduplicação. |

Fora desta fase: usuário de banco com privilégio mínimo (#13), registrado em "Fora do escopo" do system design.
