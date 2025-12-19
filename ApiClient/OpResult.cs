namespace Brain2CPU.ApiClient;

public enum OpStatus
{
    Success,
    Cancelled,
    Failed
}

public class OpResult
{
    public OpStatus Status { get; }
    public bool IsSuccess => Status == OpStatus.Success;
    public int StatusCode { get; }
    public Exception? Exception { get; }
    public string Message { get; }

    protected OpResult(OpStatus status, Exception? exception, string message, int statusCode)
    {
        Status = status;
        Exception = exception;
        Message = message;
        StatusCode = statusCode;
    }

    public static OpResult Success(int statusCode = 0) 
        => new(OpStatus.Success, null, "",statusCode);
    
    public static OpResult Cancelled(string message = "", int statusCode = -1) 
        => new(OpStatus.Cancelled, null, message, statusCode);

    public static OpResult Error(OpStatus status, Exception? exception, string message = "", int statusCode = 1) 
        => new(status, exception, message, statusCode);

    public static OpResult Error(Exception? exception, string message = "", int statusCode = 1) 
        => new(OpStatus.Failed, exception, message, statusCode);
    
    public static OpResult Error(string message, int statusCode = 1) 
        => new(OpStatus.Failed, null, message, statusCode);

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

public class OpResult<T> : OpResult
{
    public T Data { get; }

    private OpResult(T data, OpStatus status, Exception? exception, string message, int statusCode) : base(status, exception, message, statusCode)
    {
        Data = data;
    }

    public static OpResult<T> Success(T data, int statusCode = 1) 
        => new(data, OpStatus.Success, null, "", statusCode);
    
    public new static OpResult<T> Cancelled(string message = "", int statusCode = -1) 
        => new(default, OpStatus.Cancelled, null, message, statusCode);

    public new static OpResult<T> Error(OpStatus status, Exception? exception, string message = "", int statusCode = 1) 
        => new(default, status, exception, message, statusCode);

    public new static OpResult<T> Error(Exception? exception, string message = "", int statusCode = 1) 
        => new(default, OpStatus.Failed, exception, message, statusCode);
    
    public new static OpResult<T> Error(string message, int statusCode = 1) 
        => new(default, OpStatus.Failed, null, message, statusCode);
}

public static class OpResultExtensions
{
    public static OpResult<T2> ContinueWith<T1, T2>(this OpResult<T1> result, Func<T1, OpResult<T2>> next)
    {
        if (!result.IsSuccess)
            return OpResult<T2>.Error(result.Exception, result.Message, result.StatusCode);
        
        return next(result.Data);
    }

    public static async Task<OpResult<T2>> ContinueWith<T1, T2>(this Task<OpResult<T1>> task, Func<T1, OpResult<T2>> next)
    {
        var result = await task;
        if (!result.IsSuccess)
            return OpResult<T2>.Error(result.Exception, result.Message, result.StatusCode);
        
        return next(result.Data);
    }

    public static async Task<OpResult<T2>> ContinueWith<T1, T2>(this Task<OpResult<T1>> task, Func<T1, Task<OpResult<T2>>> next)
    {
        var result = await task;
        if (!result.IsSuccess)
            return OpResult<T2>.Error(result.Exception, result.Message, result.StatusCode);
        
        return await next(result.Data);
    }

    public static async Task<OpResult<T2>> ContinueWith<T1, T2>(this OpResult<T1> result, Func<T1, Task<OpResult<T2>>> next)
    {
        if (!result.IsSuccess)
            return OpResult<T2>.Error(result.Exception, result.Message, result.StatusCode);

        return await next(result.Data);
    }
}
