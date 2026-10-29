namespace InsightFlow.Logic;

/// <summary>An expected failure with an HTTP status and a stable code (the API host maps it to ProblemDetails).</summary>
public sealed class ApiProblemException : Exception
{
    public ApiProblemException(int status, string code, string message)
        : base(message)
    {
        Status = status;
        Code = code;
    }

    public ApiProblemException()
        : this(StatusCodes.Status400BadRequest, "bad_request", "The request is invalid.")
    {
    }

    public ApiProblemException(string message)
        : this(StatusCodes.Status400BadRequest, "bad_request", message)
    {
    }

    public ApiProblemException(string message, Exception innerException)
        : base(message, innerException)
    {
        Status = StatusCodes.Status400BadRequest;
        Code = "bad_request";
    }

    public int Status { get; }

    public string Code { get; }

    public static ApiProblemException NotFound(string what) => new(StatusCodes.Status404NotFound, "not_found", $"{what} was not found.");

    public static ApiProblemException Forbidden(string message) => new(StatusCodes.Status403Forbidden, "forbidden", message);

    public static ApiProblemException Conflict(string message) => new(StatusCodes.Status409Conflict, "conflict", message);
}
