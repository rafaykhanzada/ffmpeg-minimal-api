namespace PinterestAPI.Models
{
    public class ResultModel
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public object Data { get; set; }
        public static ResultModel Ok(object? data = null) => new() { Success = true, Data = data };
        public static ResultModel Failure(string error) => new() { Success = false, Message = error };
    }
}
