namespace KnowledgeMemory.Core;

/// <summary>
/// The four operations exposed to AI clients. Implemented in-process by the service
/// (HTTP MCP endpoint) and by a REST client in the stdio adapter, so MCP tool code is shared.
/// There is intentionally no delete here: deletion is a user action in the web UI.
/// </summary>
public interface IKnowledgeApi
{
    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct);
    Task<DocumentPage> GetAsync(string id, int? offset, int? maxChars, CancellationToken ct);
    Task<SaveResult> SaveAsync(SaveRequest request, CancellationToken ct);
    Task<IReadOnlyList<ProjectInfo>> ProjectsAsync(CancellationToken ct);
}
