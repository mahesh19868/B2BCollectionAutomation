namespace Collections.Web.Services;

public sealed record ServiceError(string Field, string Message);

/// <summary>Outcome of a validated command. Field is a model property name, or empty for form-level errors.</summary>
public sealed class ServiceResult
{
    private ServiceResult(int? id, IReadOnlyList<ServiceError> errors)
    {
        Id = id;
        Errors = errors;
    }

    public int? Id { get; }
    public IReadOnlyList<ServiceError> Errors { get; }
    public bool Succeeded => Errors.Count == 0;

    /// <summary>The target record does not exist in the current business (treated as 404).</summary>
    public bool NotFound { get; private init; }

    public static ServiceResult Ok(int? id = null) => new(id, Array.Empty<ServiceError>());
    public static ServiceResult Fail(string field, string message) => new(null, new[] { new ServiceError(field, message) });
    public static ServiceResult Fail(IEnumerable<ServiceError> errors) => new(null, errors.ToList());
    public static ServiceResult Missing() => new(null, new[] { new ServiceError(string.Empty, "Not found.") }) { NotFound = true };
}
