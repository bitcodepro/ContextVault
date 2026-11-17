using KnowledgeMemory.Core;

namespace KnowledgeMemory.Tests;

/// <summary>
/// Search quality set: natural RU/UK/EN queries with known correct cards.
/// Documents known limits too (no morphology, no typo tolerance) so regressions and improvements are visible.
/// </summary>
public sealed class SearchQualityTests : IAsyncLifetime
{
    private readonly TempStore _t = new();
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly (string Project, string? Key, string Title, string Summary, string Content, string[] Tags)[] Corpus =
    [
        ("Revqora", "245", "Импорт задач из CSV", "Потоковый импорт CSV с проверкой дубликатов", "## Решения\n- Парсер CsvHelper отклонён из-за памяти.\n- Дубликаты ищем по хэшу строки.", ["import", "csv"]),
        ("Typlo", "245", "Раскладка клавиатуры для иврита", "Поддержка RTL раскладки", "Добавили раскладку иврита и проверку направления текста.", ["keyboard", "rtl"]),
        ("Revqora", "ET-6449", "Ошибка синхронизации веток", "Сбой при rebase после force push", "Причина: устаревший кэш refs. Решение: инвалидировать кэш после fetch.", ["git", "sync"]),
        ("Revqora", "PR-71", "Drag and drop of commits", "Reordering commits by dragging in the history view", "Implemented drag and drop with keyboard fallback. Rejected: custom canvas list.", ["ui", "history"]),
        ("Reqvane", "12", "Хранение истории запросов", "История HTTP-запросов в SQLite", "Решили хранить историю в SQLite с ограничением 10 000 записей.", ["storage", "history"]),
        ("Reqvane", "13", "Експорт колекцій у Postman", "Експорт колекцій запитів у формат Postman v2.1", "Зроблено експорт; імпорт відкладено. Обмеження: без скриптів.", ["export", "postman"]),
        ("Schemora", null, "Цветовые токены интерфейса", "Утверждённая палитра и токены темы", "Основной акцент синий, опасные действия красные. Тёмная тема отложена.", ["design", "colors"]),
        ("KeyStudio", null, "No telemetry policy", "The extension never collects analytics", "Decision: no telemetry or analytics ever, including opt-in.", ["privacy", "policy"]),
        ("Revqora", "300", "Производительность диффа больших файлов", "Ускорение отображения диффа", "Виртуализация строк, дифф считается в фоне. Проверено на файле 50 МБ.", ["perf", "diff"]),
        ("Typlo", "0007", "Тихие ошибки при запуске", "Ошибки старта не показывались пользователю", "Добавлен журнал и уведомление при сбое запуска.", ["errors", "startup"]),
        ("Gulo", null, "SPRT testing rules", "Do not stop SPRT before LLR bounds", "Let tests finish: stop only when LLR reaches plus or minus 2.94.", ["engine", "testing"]),
        ("Revqora", "A-1", "Выбор кодировки файлов", "Поддержка любых кодировок вместо отказа", "Определяем кодировку автоматически; бинарные файлы показываем hex.", ["encoding"]),
    ];

    public async Task InitializeAsync()
    {
        foreach (var c in Corpus)
            await _t.Store.SaveAsync(new SaveRequest
            {
                RequestId = "seed-" + Guid.NewGuid().ToString("N"), Project = c.Project, ExternalKey = c.Key, Title = c.Title,
                Summary = c.Summary, Content = c.Content, Tags = c.Tags,
            }, Ct);
    }

    public Task DisposeAsync()
    {
        _t.Dispose();
        return Task.CompletedTask;
    }

    // (query, project filter, expected title in top 3; null = expect no results)
    public static TheoryData<string, string?, string?> Cases => new()
    {
        // exact keys
        { "245", "revqora", "Импорт задач из CSV" },
        { "#245", "typlo", "Раскладка клавиатуры для иврита" },
        { "ET-6449", null, "Ошибка синхронизации веток" },
        { "et-6449", null, "Ошибка синхронизации веток" },
        { "PR-71", null, "Drag and drop of commits" },
        { "задача 300", "revqora", "Производительность диффа больших файлов" },
        { "0007", null, "Тихие ошибки при запуске" },
        { "A-1", null, "Выбор кодировки файлов" },
        // Russian wording
        { "импорт csv", null, "Импорт задач из CSV" },
        { "дубликаты", null, "Импорт задач из CSV" },
        { "rebase force push", null, "Ошибка синхронизации веток" },
        { "кэш refs", null, "Ошибка синхронизации веток" },
        { "история запросов", null, "Хранение истории запросов" },
        { "sqlite история", "reqvane", "Хранение истории запросов" },
        { "цветовые токены", null, "Цветовые токены интерфейса" },
        { "тёмная тема", null, "Цветовые токены интерфейса" },
        { "виртуализация строк", null, "Производительность диффа больших файлов" },
        { "кодировк", null, "Выбор кодировки файлов" },      // prefix match
        { "иврит", null, "Раскладка клавиатуры для иврита" }, // prefix of "иврита"
        // Ukrainian
        { "експорт колекцій", null, "Експорт колекцій у Postman" },
        { "postman", null, "Експорт колекцій у Postman" },
        { "імпорт відкладено", null, "Експорт колекцій у Postman" },
        // English
        { "drag and drop", null, "Drag and drop of commits" },
        { "keyboard fallback", null, "Drag and drop of commits" },
        { "telemetry", null, "No telemetry policy" },
        { "analytics opt-in", null, "No telemetry policy" },
        { "SPRT LLR", null, "SPRT testing rules" },
        { "privacy", null, "No telemetry policy" },                // tag
        { "rtl", null, "Раскладка клавиатуры для иврита" },        // tag
        // partial fallback: one word matches, one does not
        { "импорт несуществующееслово", null, "Импорт задач из CSV" },
        // no result
        { "квантовый компьютер", null, null },
        { "245", "schemora", null },
        { "blockchain", null, null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Query_finds_expected_card(string query, string? project, string? expectedTitle)
    {
        var r = await _t.Store.SearchAsync(new SearchQuery { Query = query, Project = project }, Ct);
        if (expectedTitle is null)
        {
            Assert.Empty(r.Items);
            Assert.NotNull(r.Note); // honest "not found"
            return;
        }
        var top = r.Items.Take(3).Select(h => h.Title).ToList();
        Assert.Contains(expectedTitle, top);
    }

    [Fact]
    public async Task Exact_key_comes_first_and_lists_all_projects()
    {
        var r = await _t.Store.SearchAsync(new SearchQuery { Query = "245" }, Ct);
        Assert.Equal(2, r.Items.Count(h => h.MatchType == "exactKey"));
        Assert.True(r.Items.Take(2).All(h => h.MatchType == "exactKey"));
    }

    [Fact]
    public async Task Typos_are_not_matched_but_other_word_forms_are()
    {
        // No fuzzy matching: a typo is a documented limit.
        Assert.Empty((await _t.Store.SearchAsync(new SearchQuery { Query = "имопрт" }, Ct)).Items);
        // Stem column: another case of the same word is found.
        Assert.Contains((await _t.Store.SearchAsync(new SearchQuery { Query = "клавиатурой" }, Ct)).Items, h => h.Project == "typlo");
    }

    [Fact]
    public async Task Ranking_is_stable_for_repeated_queries()
    {
        var a = await _t.Store.SearchAsync(new SearchQuery { Query = "история" }, Ct);
        var b = await _t.Store.SearchAsync(new SearchQuery { Query = "история" }, Ct);
        Assert.Equal(a.Items.Select(x => x.Id), b.Items.Select(x => x.Id));
    }
}
