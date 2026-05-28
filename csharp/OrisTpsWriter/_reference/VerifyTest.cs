// ============================================================================
// VerifyTest.cs — ვალიდაციის console პროგრამა
//
// ეს ფაილი დამოუკიდებლად უნდა გაეშვას (ცალკე console პროექტში ან dotnet-script).
// ის ქმნის იგივე ცხრილს, რასაც Python-ის partners მაგალითი, რომ შევადაროთ
// output ბაიტ-ბაიტ.
//
// გამოყენება (ცალკე console პროექტში):
//   dotnet run
// შემდეგ შეადარე partners_csharp.tps ↔ Python-ის partners_100_v2.tps
// ============================================================================

using System;
using System.Collections.Generic;
using OrisTpsWriter.Core;

namespace OrisTpsWriter.Tests
{
    public static class VerifyTest
    {
        public static void Run()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // --- Test 1: encoding round-trip ---
            Console.WriteLine("=== Test 1: ORIS encoding ===");
            string[] samples = { "შპს ალფა", "ოქრიაშვილი გ.", "ABC 123" };
            foreach (var s in samples)
            {
                byte[] enc = OrisEncoding.Encode(s);
                string dec = OrisEncoding.Decode(enc);
                bool ok = dec == s;
                Console.WriteLine($"  {(ok ? "✓" : "✗")} {s} → {BitConverter.ToString(enc).Replace("-", " ")}");
            }

            // --- Test 2: RLE round-trip ---
            Console.WriteLine("\n=== Test 2: RLE codec ===");
            byte[] testData = System.Text.Encoding.ASCII.GetBytes("Hello World, RLE test data here!");
            byte[] wrapped = TpsRle.Wrap(testData);
            byte[] unwrapped = TpsRle.Unwrap(wrapped);
            bool rleOk = System.Linq.Enumerable.SequenceEqual(testData, unwrapped);
            Console.WriteLine($"  {(rleOk ? "✓" : "✗")} RLE wrap/unwrap round-trip");

            // --- Test 3: ARN-like table (matches Python WORKING_FORMAT_v11) ---
            Console.WriteLine("\n=== Test 3: ARN-like table ===");
            var table = new OrisTable("UNNAMED");
            table.AddField(new StringField("ARN:KADR", 50));
            table.AddField(new StringField("ARN:SECT", 20));
            table.AddKey("ARN:K1", new[] { "ARN:SECT", "ARN:KADR" });

            string[] names = {
                "ოქრიაშვილი გ. .", "გოგიჩაძე ა. .", "კაპანაძე ე. .",
                "კიკვაძე დ. .", "ახვლედიანი ზ. ."
            };
            foreach (var n in names)
                table.AddRow(new Dictionary<string, object>
                {
                    ["ARN:KADR"] = n,
                    ["ARN:SECT"] = ""
                });

            int size = table.Save("arn_csharp.tps");
            Console.WriteLine($"  ✓ arn_csharp.tps: {size} bytes ({table.RecordCount} records)");
            Console.WriteLine($"    შეადარე Python-ის WORKING_FORMAT_v11.tps-ს (1280 bytes უნდა იყოს)");

            // --- Test 4: large multi-page table ---
            Console.WriteLine("\n=== Test 4: 1000-record multi-page ===");
            var big = new OrisTable("PARTNERS");
            big.AddField(new ULongField("PRT:ID"));
            big.AddField(new StringField("PRT:NAME", 60));
            big.AddField(new LongField("PRT:BALANCE"));
            big.AddKey("PRT:K1", new[] { "PRT:ID" });

            string[] cn = { "ალფა", "ბეტა", "გამა", "დელტა", "ომეგა" };
            for (int i = 0; i < 1000; i++)
            {
                big.AddRow(new Dictionary<string, object>
                {
                    ["PRT:ID"] = i + 1,
                    ["PRT:NAME"] = $"შპს {cn[i % 5]}-{i + 1}",
                    ["PRT:BALANCE"] = ((i * 73) % 100 - 50) * 10000
                });
            }
            int bigSize = big.Save("partners_csharp_1000.tps");
            Console.WriteLine($"  ✓ partners_csharp_1000.tps: {bigSize} bytes ({big.RecordCount} records)");

            Console.WriteLine("\nყველა ტესტი დასრულდა. შეადარე .tps ფაილები Python output-ს ან გახსენი Clarion Viewer-ით.");
        }
    }
}
