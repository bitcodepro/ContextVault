using System.Diagnostics;
using KnowledgeMemory.Core;
using Xunit.Abstractions;

namespace KnowledgeMemory.Tests;

/// <summary>
/// Opt-in measurement (set KM_PERF=1): 10 000 cards of 2-10 KB, cold and warm search p50/p95.
/// This is a measurement, not a guarantee; results depend on hardware.
/// </summary>
public sealed class PerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Search_latency_on_10k_documents()
    {
        if (Environment.GetEnvironmentVariable("KM_PERF") != "1")
        {
            output.WriteLine("Skipped: set KM_PERF=1 to run the 10k-document measurement.");
            return;
        }
        using var t = new TempStore();
        var rnd = new Random(7);
        var vocab = Enumerable.Range(0, 5000).Select(i => "слово" + i).Concat(Enumerable.Range(0, 3000).Select(i => "word" + i)).ToArray();
        var projects = new[] { "Revqora", "Typlo", "Reqvane", "Schemora", "KeyStudio", "Gulo" };
        var load = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
        {
            var len = rnd.Next(2000, 10_000);
            var sb = new System.Text.StringBuilder(len + 20);
            while (sb.Length < len) sb.Append(vocab[rnd.Next(vocab.Length)]).Append(' ');
            await t.Store.SaveAsync(new SaveRequest
            {
                RequestId = "perf-" + i.ToString("D6"), Project = projects[i % projects.Length], ExternalKey = "T-" + i,
                Title = $"Задача {i} {vocab[rnd.Next(vocab.Length)]}", Summary = "Краткое резюме " + vocab[rnd.Next(vocab.Length)],
                Content = sb.ToString(),
            }, CancellationToken.None);
        }
        output.WriteLine($"Inserted 10 000 cards in {load.Elapsed.TotalSeconds:F1} s; db = {t.Disk.WorkingBytes() / 1024 / 1024} MB");

        var queries = Enumerable.Range(0, 200).Select(i => (i % 4) switch
        {
            0 => new SearchQuery { Query = vocab[rnd.Next(vocab.Length)] },
            1 => new SearchQuery { Query = vocab[rnd.Next(vocab.Length)] + " " + vocab[rnd.Next(vocab.Length)] },
            2 => new SearchQuery { Query = "T-" + rnd.Next(10_000) },
            _ => new SearchQuery { Query = vocab[rnd.Next(vocab.Length)], Project = "revqora" },
        }).ToList();

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // "cold": new connections, OS cache still warm
        var cold = new List<double>();
        foreach (var q in queries.Take(20)) cold.Add(Time(() => t.Store.SearchAsync(q, CancellationToken.None).Wait()));
        var warm = new List<double>();
        foreach (var q in queries) warm.Add(Time(() => t.Store.SearchAsync(q, CancellationToken.None).Wait()));

        output.WriteLine($"cold (first 20): p50={P(cold, 50):F1} ms p95={P(cold, 95):F1} ms");
        output.WriteLine($"warm (200):      p50={P(warm, 50):F1} ms p95={P(warm, 95):F1} ms");
        output.WriteLine($"CPU: {Environment.ProcessorCount} logical cores, OS: {Environment.OSVersion}");
        Assert.True(P(warm, 95) < 500, "warm p95 should stay within hundreds of milliseconds");
    }

    private static double Time(Action a)
    {
        var sw = Stopwatch.StartNew();
        a();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static double P(List<double> v, int p)
    {
        var s = v.OrderBy(x => x).ToList();
        return s[Math.Min(s.Count - 1, (int)Math.Ceiling(p / 100.0 * s.Count) - 1)];
    }
}
