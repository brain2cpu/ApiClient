# ApiClient Documentation

The `ApiClient` is a robust HTTP client that provides built-in support for retries, timeout handling, content type negotiation, and error handling. 

## Features

- Automatic retries for transient failures
- Configurable timeout handling
- Content type negotiation (JSON, XML, Form data)
- File upload support
- File donwload support with unique file names
- Custom client configuration
- Strong typing with `OpResult<T>`
- Extensible response handlers

## Setup

### Basic Configuration

```csharp
// In your MauiProgram.cs or similar startup code
builder.Services.AddHttpClient(); // Required for IHttpClientFactory
builder.Services.AddSingleton<ApiClient>();
```

### With Logging Support

```csharp
builder.Services.AddHttpClient(); // Required for IHttpClientFactory
builder.Services.AddLogging(); // Enable logging
builder.Services.AddSingleton<ApiClient>(); // Will automatically use ILogger if available
```

### Custom Client Configuration

```csharp
builder.Services.AddHttpClient("CustomClient")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
        {
            // Custom certificate validation logic
            return true;
        }
    });
```

## Usage Examples

### Basic GET Request

```csharp
public class WeatherService
{
    private readonly ApiClient _apiClient;
    
    public WeatherService(ApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public async Task<OpResult<WeatherData>> GetWeatherAsync()
    {
        return await _apiClient.GetAsync<WeatherData>("https://api.weather.com/current");
    }
}
```

### POST Request with JSON Data

```csharp
var request = new ApiRequest("https://api.example.com/data");
request.Method = HttpMethod.Post;
request.BuildJsonContent(new { 
    name = "test", 
    value = 123 
});

var response = await _apiClient.SendRequestAsync<ResponseType>(request);
```

### Handling Query Parameters

```csharp
var request = new ApiRequest("https://api.example.com/search");
request.UrlParameters["query"] = "searchTerm";
request.UrlParameters["page"] = "1";
request.UrlParameters["limit"] = "10";

var response = await _apiClient.SendRequestAsync<SearchResults>(request);
```

### File Upload

```csharp
var request = new ApiRequest("https://api.example.com/upload");
request.Method = HttpMethod.Post;

using var fileStream = File.OpenRead("myfile.jpg");
request.BuildUploadContent(fileStream, "file", "myfile.jpg");

var response = await _apiClient.SendRequestAsync<UploadResult>(request);
```

### Form Data Submission

```csharp
var request = new ApiRequest("https://api.example.com/form");
request.Method = HttpMethod.Post;
request.BuildFormContent(new Dictionary<string, string>
{
    ["username"] = "john_doe",
    ["email"] = "john@example.com"
});

var response = await _apiClient.SendRequestAsync<FormSubmissionResult>(request);
```

## Configuration Options

### Client Settings

```csharp
_apiClient.Retries = 3;                              // Number of retry attempts
_apiClient.Timeout = TimeSpan.FromSeconds(30);       // Request timeout
_apiClient.RetryInterval = TimeSpan.FromSeconds(1);  // Delay between retries
_apiClient.HttpClientName = "CustomClient";          // Named client configuration
```

For headers to be included with every request:
```csharp
_apiClient.CommonHeaders.Add("Name", "Value);
```
If the request already has a header with the same name it will be used instead of the common one.

### Logging Configuration

The ApiClient uses `ILogger<ApiClient>` from Microsoft.Extensions.Logging for structured logging. To enable logging:

```csharp
// In your MAUI/ASP.NET Core startup
builder.Services
    .AddLogging(config =>
    {
        config.AddConsole(); // or AddDebug(), AddFile(), etc.
        config.SetMinimumLevel(LogLevel.Information);
    })
    .AddHttpClient()
    .AddSingleton<ApiClient>();
```

The ApiClient logs at different levels:
- **Information**: Request initiation, completion, and downloads
- **Warning**: Request timeouts, cancellations, and transient errors with retries
- **Error**: Failed requests, processing errors, and unhandled exceptions
- **Debug**: Request details, headers count, content type handling, and response processing


## Error Handling

The ApiClient uses `OpResult<T>` to provide detailed error information:

```csharp
var result = await _apiClient.SendRequestAsync<MyData>(request);
if (result.IsSuccess)
{
    var data = result.Data;
    // Process successful response
}
else
{
    // Handle error cases
    switch (result.Status)
    {
        case OpStatus.Cancelled:
            // Request was cancelled
            break;
        case OpStatus.Error:
            var errorMessage = result.Message;
            var statusCode = result.StatusCode;
            var exception = result.Exception;
            // Handle error
            break;
    }
}
```

## Transient Error Handling

By default, the ApiClient handles the following transient error codes:
- 408 Request Timeout
- 429 Too Many Requests (rate limit)
- 500 Internal Server Error
- 502 Bad Gateway
- 503 Service Unavailable
- 504 Gateway Timeout

These can be customized by modifying the `TransientErrorCodes` list:

```csharp
_apiClient.TransientErrorCodes.Add(520); // Add custom error code
```

## Best Practices

1. **Use Dependency Injection**
```csharp
   public class MyService
   {
       private readonly ApiClient _apiClient;
       
       public MyService(ApiClient apiClient)
       {
           _apiClient = apiClient;
       }
   }
```

2. **Always Dispose ApiRequest**
```csharp
   using var request = new ApiRequest("https://api.example.com");
   // Use request...
```

3. **Handle Cancellation**
```csharp
   using var cts = new CancellationTokenSource();
   var result = await _apiClient.SendRequestAsync<T>(request, cts.Token);
```

4. **Set Appropriate Timeouts**
```csharp
   // For long-running operations
   _apiClient.Timeout = TimeSpan.FromMinutes(5);
```

## Advanced Scenarios

### Custom Request Headers

```csharp
var request = new ApiRequest("https://api.example.com");
ApiRequest.AddBearerTokenHeader(request.Headers, token);
request.Headers["Custom-Header"] = "value";
```

### XML Serialization

```csharp
var request = new ApiRequest("https://api.example.com");
request.BuildXmlContent(myXmlData);
```

### Stream Response Handling

```csharp
var result = await _apiClient.SendRequestAsync<Stream>(request);
if (result.IsSuccess)
{
    using var stream = result.Data;
    // Process stream
}