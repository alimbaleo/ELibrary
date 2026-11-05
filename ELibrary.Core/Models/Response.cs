using System.Net;

namespace ELibrary.Core.Models
{
    public enum ErrorType
    {
        None,
        BadRequest,
        NotFound,
        Upstream
    }

    public class Response<T>
    {
        public T Data { get; set; }
        public string Message { get; set; }
        public bool Success { get; set; }
        public ErrorType ErrorType { get; set; }

        public Response(T data, string message = "", bool success = true, ErrorType errorType = ErrorType.None)
        {
            Data = data;
            Message = message;
            Success = success;
            ErrorType = success ? ErrorType.None : (errorType == ErrorType.None ? ErrorType.BadRequest : errorType);
        }

        public static Response<T> Ok(T data, string message = "") => new(data, message, true);

        public static Response<T> Fail(string message) => new(default!, message, false, ErrorType.BadRequest);

        public static Response<T> NotFound(string message) => new(default!, message, false, ErrorType.NotFound);

        public static Response<T> UpstreamError(string message) => new(default!, message, false, ErrorType.Upstream);

        public static Response<T> Failed<TOther>(Response<TOther> other) => new(default!, other.Message, false, other.ErrorType);

        // Says what actually went wrong with a provider (down, blocked, timed out) instead of a generic
        // "not found". Upstream bodies are not included; the caller logs the exception for details.
        public static Response<T> FromProviderException(Exception ex, string provider, string notFoundMessage)
        {
            return ex switch
            {
                HttpRequestException { StatusCode: HttpStatusCode.NotFound } => NotFound(notFoundMessage),
                HttpRequestException { StatusCode: not null } httpEx =>
                    UpstreamError($"{provider} returned HTTP {(int)httpEx.StatusCode} ({httpEx.StatusCode})"),
                TaskCanceledException or TimeoutException => UpstreamError($"{provider} timed out"),
                HttpRequestException => UpstreamError($"{provider} could not be reached"),
                _ => UpstreamError($"{provider} request failed")
            };
        }
    }
}
