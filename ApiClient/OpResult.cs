namespace Brain2CPU.ApiClient;

public enum OpStatus
{
    Success,
    Cancelled,
    Failed
}

public class OpResult<T>
{
    public OpStatus Status { get; }
    public bool IsSuccess => Status == OpStatus.Success;
    public int StatusCode { get; }
    public Exception? Exception { get; }
    public string Message { get; }
    public T Data { get; }

    private OpResult(T data, OpStatus status, Exception? exception, string message, int statusCode)
    {
        Data = data;
        Status = status;
        Exception = exception;
        Message = message;
        StatusCode = statusCode;
    }

    public static OpResult<T> Success(int statusCode = 0) 
        => new(default, OpStatus.Success, null, "",statusCode);
    
    public static OpResult<T> Success(T data, int statusCode = 1) 
        => new(data, OpStatus.Success, null, "", statusCode);
    
    public static OpResult<T> Cancelled(string message = "", int statusCode = -1) 
        => new(default, OpStatus.Cancelled, null, message, statusCode);

    public static OpResult<T> Error(Exception? exception, string message = "", int statusCode = 1) 
        => new(default, OpStatus.Failed, exception, message, statusCode);
    
    public static OpResult<T> Error(string message, int statusCode = 1) 
        => new(default, OpStatus.Failed, null, message, statusCode);

    public string GetFullMessage()
    {
        if (string.IsNullOrWhiteSpace(Message))
            return GetFullMessage(Exception);
        
        if(Exception == null)
            return Message;

        return $"{Message}{Environment.NewLine}{GetFullMessage(Exception)}";
    }

    private static string GetFullMessage(Exception? ex)
    {
        if(ex == null)
            return "";

        if (ex.InnerException == null)
            return ex.Message;
        
        return GetFullMessage(ex.InnerException) + Environment.NewLine + ex.Message;
    }
}
