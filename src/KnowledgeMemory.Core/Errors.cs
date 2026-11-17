namespace KnowledgeMemory.Core;

public static class ErrorCodes
{
    public const string Validation = "validation_error";
    public const string NotFound = "not_found";
    public const string VersionConflict = "version_conflict";
    public const string DuplicateKey = "duplicate_key";
    public const string IdempotencyMismatch = "idempotency_mismatch";
    public const string Busy = "busy";
    public const string ServiceUnavailable = "service_unavailable";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string Internal = "internal_error";
}

/// <summary>Expected domain error. Message never contains knowledge content.</summary>
public sealed class KnowledgeException : Exception
{
    public KnowledgeException(string code, string message, IReadOnlyDictionary<string, object?>? details = null)
        : base(message)
    {
        Code = code;
        Details = details ?? new Dictionary<string, object?>();
    }

    public string Code { get; }
    public IReadOnlyDictionary<string, object?> Details { get; }

    public int HttpStatus => Code switch
    {
        ErrorCodes.Validation => 400,
        ErrorCodes.NotFound => 404,
        ErrorCodes.VersionConflict or ErrorCodes.DuplicateKey or ErrorCodes.IdempotencyMismatch => 409,
        ErrorCodes.Busy or ErrorCodes.ServiceUnavailable => 503,
        ErrorCodes.Unauthorized => 401,
        ErrorCodes.Forbidden => 403,
        _ => 500,
    };

    public static KnowledgeException Invalid(string message) => new(ErrorCodes.Validation, message);
}

/// <summary>Wire format of an error, shared by REST API and MCP tool results.</summary>
public sealed record ErrorBody(string Code, string Message, IReadOnlyDictionary<string, object?>? Details = null);

public sealed record ErrorEnvelope(ErrorBody Error);
