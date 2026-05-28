# ORIS TPS Writer — WPF Desktop აპლიკაცია (.NET 8)

ქართული ბუღალტრული .tps ფაილების გენერატორი desktop აპლიკაციის სახით.
Python ვერსიის სრული C# პორტი, დადასტურებული ფორმატით.

## მოთხოვნები

- **.NET 8 SDK** (ან უფრო ახალი) — https://dotnet.microsoft.com/download
- **Windows** (WPF-ის გამო)
- Visual Studio 2022 ან `dotnet` CLI

## აშენება და გაშვება

Visual Studio-ში: გახსენი `OrisTpsWriter.sln` და დააჭირე F5.

ან CLI-დან:
```bash
# WPF desktop აპლიკაცია (Windows)
cd OrisTpsWriter
dotnet run

# ან მხოლოდ Core library (cross-platform, backend-ისთვის)
cd OrisTpsWriter.Core
dotnet build
```

## solution სტრუქტურა

- **OrisTpsWriter.Core** — net8.0 class library, UI-ს გარეშე (Linux/Mac/Windows).
  გამოიყენე შენს ASP.NET Core backend-ში.
- **OrisTpsWriter** — net8.0-windows WPF desktop აპლიკაცია, references Core.

## პროექტის სტრუქტურა

```
OrisTpsWriter/
├── Core/                    ← ბირთვი (UI-დან დამოუკიდებელი)
│   ├── OrisEncoding.cs      ქართული single-byte codec
│   ├── TpsRle.cs            TopSpeed RLE codec
│   ├── TpsDefinitions.cs    TpsField, TpsIndex
│   ├── TpsWriter.cs         low-level writer (multi-page, blocks)
│   └── OrisTable.cs         მაღალი დონის API
├── MainWindow.xaml(.cs)     UI
├── App.xaml(.cs)            entry point
└── _reference/VerifyTest.cs ვალიდაციის ნიმუში (build-ში არ ერთვის)
```

## გამოყენება UI-დან

1. **ცხრილის სახელი** — შეიყვანე (default UNNAMED)
2. **ფილდები** — დაამატე სახელი, ტიპი (STRING/LONG/ULONG/SHORT), სიგრძე
3. **Keys** — დაამატე key, მონიშნე მისი ფილდები
4. **მონაცემები** — შეიყვანე ხელით, ან CSV იმპორტი, ან Demo ღილაკი
5. **შენახვა** — 💾 ღილაკით .tps ფაილი

დააჭირე "Demo მონაცემები" სწრაფი ტესტისთვის (ARN-ის მსგავსი ქართული გვარები).

## გამოყენება კოდიდან (Core library)

შენი React + .NET Core 8 backend-ში პირდაპირ:

```csharp
using OrisTpsWriter.Core;

var table = new OrisTable("PARTNERS");
table.AddField(new ULongField("PRT:ID"));
table.AddField(new StringField("PRT:NAME", 60));
table.AddField(new LongField("PRT:BALANCE"));
table.AddKey("PRT:K1", new[] { "PRT:ID" });

foreach (var p in partnersFromSql)
    table.AddRow(new Dictionary<string, object>
    {
        ["PRT:ID"] = p.Id,
        ["PRT:NAME"] = p.Name,        // ქართული — ავტომატურად ORIS encoding
        ["PRT:BALANCE"] = p.Balance,
    });

table.Save(@"C:\ORIS\Data\partners.tps");
```

**Core ფოლდერი UI-სგან დამოუკიდებელია** — შეგიძლია ცალკე class library-ად
გამოყo (net8.0 target, WPF-ის გარეშე) და ASP.NET backend-ში გამოიყენო.

## ფორმატის discovery-ები (ამ კოდში ჩაშენებული)

1. pageStart/pageEnd = page references: `(ref << 8) + 0x200`
2. blocks იწყება index [2]-დან (პირველი ორი slot რეზერვირებული)
3. driverVersion = 1
4. tableNumber/recordNumber/lastIssuedRow = BIG-endian
5. ORIS single-byte encoding (0xC0=ა ... 0xE4=ჰ)
6. Field flags ≠ index (index = display sequence)
7. Index def: separate arrays
8. data/index/definitions ცალკე blocks, consecutive pages consolidated
9. RLE-wrapped pages

## შეზღუდვები

- **ცარიელი ცხრილების შექმნა** — ნულიდან .tps გენერაცია (export workflow)
- არსებულ ფაილში INSERT ჯერ არ არის (ცალკე პროექტია)
- ცხრილი regenerated უნდა იყოს მთლიანად ცვლილებისას

## License

Apache 2.0 (tps-parse-ის ცოდნაზე დაფუძნებული reverse engineering).
