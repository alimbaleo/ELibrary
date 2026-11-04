namespace ELibrary.Core.Models
{
    public class Response<T>
    {
        public T Data { get; set; }
        public string Message { get; set; }
        public bool Success { get; set; }

        public Response(T data, string message = "", bool success = true)
        {
            Data = data;
            Message = message;
            Success = success;
        }

        public static Response<T> Ok(T data, string message = "") => new(data, message, true);

        public static Response<T> Fail(string message) => new(default!, message, false);
    }
}
