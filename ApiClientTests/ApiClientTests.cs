using System.Net;
using System.Text;
using System.Text.Json;
using Brain2CPU.ApiClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace ApiClientTests;

#pragma warning disable CA1873

public class ApiClientTests
{
    [Fact]
    public async Task TestWithRealClient()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var apiClient = new ApiClient(factory);
        
        OpResult<string>? result = null;
        var exception = await Record.ExceptionAsync(async () => 
        {
            result = await apiClient.GetAsync<string>("http://example.com");
        });
        Assert.Null(exception);
        Assert.True(result?.IsSuccess);
        Assert.NotEmpty(result!.Data);

        var downloaded = await apiClient.DownloadAsync(new ApiRequest("https://practice.expandtesting.com/download/cdct.jpg"), Path.GetTempPath());
        Assert.True(downloaded.IsSuccess);
        Assert.True(File.Exists(downloaded.Data));

        File.Delete(downloaded.Data);
    }

    [Fact]
    public async Task HttpClient_Should_Echo_Request_Body()
    {
        var handler = new EchoMessageHandler();
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        
        var client = new ApiClient(mockFactory.Object);

        using var request = new ApiRequest("https://example.com") { Method = HttpMethod.Post };
        var payload = new Payload { Title = "title", Body = "body", UserId = 1 };
        request.BuildXmlContent(payload);

        var response = await client.SendRequestAsync<Payload>(request);
        Assert.True(response.IsSuccess);
        Assert.Equal(payload.Title, response.Data.Title);
    }

    [Fact]
    public async Task SendRequestAsync_WithLogging_ShouldLogRequestInitiation()
    {
        var handler = new SuccessMessageHandler();
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var mockLogger = new Mock<ILogger<ApiClient>>();
        var client = new ApiClient(mockFactory.Object, mockLogger.Object);
        var request = new ApiRequest("https://api.example.com/data") { Method = HttpMethod.Get };

        await client.SendRequestAsync<string>(request);

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Sending request")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task SendRequestAsync_WithSuccessResponse_ShouldLogDebug()
    {
        var handler = new SuccessMessageHandler();
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var mockLogger = new Mock<ILogger<ApiClient>>();
        var client = new ApiClient(mockFactory.Object, mockLogger.Object);
        var request = new ApiRequest("https://api.example.com/test") { Method = HttpMethod.Get };

        var result = await client.SendRequestAsync<string>(request);

        Assert.True(result.IsSuccess);
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("completed with status")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task SendRequestAsync_WithJsonContent_ShouldDeserializeCorrectly()
    {
        var jsonData = new { Id = 1, Name = "Test" };
        var jsonString = JsonSerializer.Serialize(jsonData);
        
        var handler = new JsonMessageHandler(jsonString);
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var client = new ApiClient(mockFactory.Object);
        var request = new ApiRequest("https://api.example.com/data");

        var result = await client.SendRequestAsync<dynamic>(request);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public async Task SendRequestAsync_WithTimeout_ShouldRetryAndEventuallyFail()
    {
        var handler = new TimeoutMessageHandler();
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var client = new ApiClient(mockFactory.Object)
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            Retries = 2,
            RetryInterval = TimeSpan.FromMilliseconds(10)
        };

        var request = new ApiRequest("https://api.example.com/slow");
        var result = await client.SendRequestAsync<string>(request);

        Assert.False(result.IsSuccess);
        Assert.Equal((int)HttpStatusCode.RequestTimeout, result.StatusCode);
    }

    [Fact]
    public async Task SendRequestAsync_WithTransientError_ShouldRetry()
    {
        var handler = new TransientErrorMessageHandler();
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var mockLogger = new Mock<ILogger<ApiClient>>();
        var client = new ApiClient(mockFactory.Object, mockLogger.Object)
        {
            Retries = 2,
            RetryInterval = TimeSpan.FromMilliseconds(10)
        };

        var request = new ApiRequest("https://api.example.com/transient");
        var result = await client.SendRequestAsync<string>(request);

        Assert.True(result.IsSuccess);
        
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("Retrying")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task SendRequestAsync_WithCancellation_ShouldCancel()
    {
        var handler = new DelayMessageHandler(TimeSpan.FromSeconds(10));
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var mockLogger = new Mock<ILogger<ApiClient>>();
        var client = new ApiClient(mockFactory.Object, mockLogger.Object);

        var request = new ApiRequest("https://api.example.com/slow");
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await client.SendRequestAsync<string>(request, cts.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(OpStatus.Cancelled, result.Status);
        
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("cancelled")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task SendRequestAsync_WithCommonHeaders_ShouldIncludeThemInRequest()
    {
        var capturedRequest = new HttpRequestMessage();
        var handler = new CaptureRequestHandler(capturedRequest);
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var client = new ApiClient(mockFactory.Object);
        client.CommonHeaders.Add("Authorization", "Bearer token123");
        client.CommonHeaders.Add("X-Custom-Header", "custom-value");

        var request = new ApiRequest("https://api.example.com/data");
        await client.SendRequestAsync<string>(request);

        Assert.Contains(capturedRequest.Headers, h => h.Key == "Authorization" && h.Value.First() == "Bearer token123");
        Assert.Contains(capturedRequest.Headers, h => h.Key == "X-Custom-Header" && h.Value.First() == "custom-value");
    }

    [Fact]
    public async Task SendRequestAsync_RequestHeadersShouldOverrideCommonHeaders()
    {
        var capturedRequest = new HttpRequestMessage();
        var handler = new CaptureRequestHandler(capturedRequest);
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var client = new ApiClient(mockFactory.Object);
        client.CommonHeaders.Add("Authorization", "Bearer common-token");

        var request = new ApiRequest("https://api.example.com/data");
        request.Headers["Authorization"] = "Bearer request-token";

        await client.SendRequestAsync<string>(request);

        Assert.Contains(capturedRequest.Headers, h => h.Key == "Authorization" && h.Value.First() == "Bearer request-token");
    }

    [Fact]
    public async Task SendRequestAsync_WithUrlParameters_ShouldBuildCorrectUri()
    {
        var capturedRequest = new HttpRequestMessage();
        var handler = new CaptureRequestHandler(capturedRequest);
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var client = new ApiClient(mockFactory.Object);
        var request = new ApiRequest("https://api.example.com/search");
        request.UrlParameters["query"] = "test";
        request.UrlParameters["page"] = "1";

        await client.SendRequestAsync<string>(request);

        var uri = capturedRequest.RequestUri?.ToString() ?? "";
        Assert.Contains("query=test", uri);
        Assert.Contains("page=1", uri);
    }

    [Fact]
    public async Task SendRequestAsync_WithSpecialCharactersInUrlParameters_ShouldEncodeCorrectly()
    {
        var capturedRequest = new HttpRequestMessage();
        var handler = new CaptureRequestHandler(capturedRequest);
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var client = new ApiClient(mockFactory.Object);
        var request = new ApiRequest("https://api.example.com/search");
        request.UrlParameters["search"] = "hello world & test";

        await client.SendRequestAsync<string>(request);

        var uri = capturedRequest.RequestUri?.ToString() ?? "";
        Assert.Contains("search=", uri);
        Assert.DoesNotContain(" ", uri);
    }

    [Fact]
    public async Task GetAsync_ShouldUseGetMethod()
    {
        var capturedRequest = new HttpRequestMessage();
        var handler = new CaptureRequestHandler(capturedRequest);
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var client = new ApiClient(mockFactory.Object);
        await client.GetAsync<string>("https://api.example.com/data");

        Assert.Equal(HttpMethod.Get, capturedRequest.Method);
    }

    [Fact]
    public async Task SendRequestAsync_WithPostContent_ShouldIncludeContent()
    {
        var handler = new EchoMessageHandler();
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var client = new ApiClient(mockFactory.Object);
        var request = new ApiRequest("https://api.example.com/data") { Method = HttpMethod.Post };
        
        var payload = new Payload { Title = "test", Body = "content", UserId = 42 };
        request.BuildJsonContent(payload);

        var result = await client.SendRequestAsync<Payload>(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(payload.UserId, result.Data.UserId);
    }

    [Fact]
    public async Task DownloadAsync_ShouldDownloadFileSuccessfully()
    {
        var fileContent = Encoding.UTF8.GetBytes("test file content");
        var handler = new FileDownloadMessageHandler(fileContent, "test.txt");
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var client = new ApiClient(mockFactory.Object);
        var tempDir = Path.GetTempPath();
        var request = new ApiRequest("https://api.example.com/download");

        var result = await client.DownloadAsync(request, tempDir);

        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(result.Data));
        
        var downloadedContent = await File.ReadAllBytesAsync(result.Data);
        Assert.Equal(fileContent, downloadedContent);

        File.Delete(result.Data);
    }

    [Fact]
    public async Task DownloadAsync_WithDuplicateFileName_ShouldCreateNewFileWithCounter()
    {
        var fileContent = Encoding.UTF8.GetBytes("test content");
        var handler = new FileDownloadMessageHandler(fileContent, "test.txt");
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var client = new ApiClient(mockFactory.Object);
        var tempDir = Path.Combine(Path.GetTempPath(), "download_test");
        Directory.CreateDirectory(tempDir);

        var initialFile = Path.Combine(tempDir, "test.txt");
        await File.WriteAllBytesAsync(initialFile, fileContent);

        var request = new ApiRequest("https://api.example.com/download");
        var result = await client.DownloadAsync(request, tempDir);

        Assert.True(result.IsSuccess);
        Assert.Contains("(1)", result.Data);
        Assert.True(File.Exists(result.Data));

        File.Delete(initialFile);
        File.Delete(result.Data);
        Directory.Delete(tempDir);
    }

    [Fact]
    public async Task DownloadAsync_WithInvalidDirectory_ShouldReturnError()
    {
        var handler = new SuccessMessageHandler();
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var mockLogger = new Mock<ILogger<ApiClient>>();
        var client = new ApiClient(mockFactory.Object, mockLogger.Object);
        var request = new ApiRequest("https://api.example.com/download");

        var result = await client.DownloadAsync(request, "");

        Assert.False(result.IsSuccess);
        
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString().Contains("null or empty")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task SendRequestAsync_WithByteArrayResponse_ShouldReturnBytes()
    {
        var testBytes = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new BytesMessageHandler(testBytes);
        var httpClient = new HttpClient(handler);
        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var client = new ApiClient(mockFactory.Object);
        var request = new ApiRequest("https://api.example.com/binary");

        var result = await client.SendRequestAsync<byte[]>(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(testBytes, result.Data);
    }

    [Fact]
    public async Task SendRequestAsync_WithNamedClient_ShouldUseNamedClient()
    {
        var namedHandler = new SuccessMessageHandler();
        var namedHttpClient = new HttpClient(namedHandler);

        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient("CustomClient")).Returns(namedHttpClient);

        var client = new ApiClient(mockFactory.Object) { HttpClientName = "CustomClient" };
        var request = new ApiRequest("https://api.example.com/data");

        var result = await client.SendRequestAsync<string>(request);

        Assert.True(result.IsSuccess);
        mockFactory.Verify(f => f.CreateClient("CustomClient"), Times.Once);
    }

    [Fact]
    public void ApiClient_Constructor_WithLogger_ShouldInitializeLogger()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var mockLogger = new Mock<ILogger<ApiClient>>();

        var client = new ApiClient(mockFactory.Object, mockLogger.Object);

        Assert.NotNull(client);
    }

    [Fact]
    public void ApiClient_Constructor_WithoutLogger_ShouldInitializeWithoutLogger()
    {
        var mockFactory = new Mock<IHttpClientFactory>();

        var client = new ApiClient(mockFactory.Object);

        Assert.NotNull(client);
    }

    [Fact]
    public void ApiClient_TransientErrorCodes_ShouldContainExpectedCodes()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var client = new ApiClient(mockFactory.Object);

        Assert.Contains(408, client.TransientErrorCodes);
        Assert.Contains(429, client.TransientErrorCodes);
        Assert.Contains(500, client.TransientErrorCodes);
        Assert.Contains(502, client.TransientErrorCodes);
        Assert.Contains(503, client.TransientErrorCodes);
        Assert.Contains(504, client.TransientErrorCodes);
    }

    [Fact]
    public void ApiClient_ResponseHandlers_ShouldContainAllContentTypes()
    {
        var mockFactory = new Mock<IHttpClientFactory>();
        var client = new ApiClient(mockFactory.Object);

        Assert.Contains("application/json", client.ResponseHandlers.Keys);
        Assert.Contains("text/json", client.ResponseHandlers.Keys);
        Assert.Contains("application/xml", client.ResponseHandlers.Keys);
        Assert.Contains("text/xml", client.ResponseHandlers.Keys);
        Assert.Contains("application/octet-stream", client.ResponseHandlers.Keys);
    }
}

public record Payload
{
    public string Title { get; set; }
    public string Body { get; set; }
    public int UserId { get; set; }
}

public class EchoMessageHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestBody = await request.Content.ReadAsStringAsync();

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, request.Content?.Headers.ContentType?.MediaType ?? "application/json")
        };

        return response;
    }
}

public class SuccessMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("success", Encoding.UTF8, "text/plain")
        };

        return Task.FromResult(response);
    }
}

public class JsonMessageHandler : HttpMessageHandler
{
    private readonly string _jsonContent;

    public JsonMessageHandler(string jsonContent)
    {
        _jsonContent = jsonContent;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_jsonContent, Encoding.UTF8, "application/json")
        };

        return Task.FromResult(response);
    }
}

public class TimeoutMessageHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(10000, cancellationToken);
        
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("delayed response", Encoding.UTF8, "text/plain")
        };
    }
}

public class TransientErrorMessageHandler : HttpMessageHandler
{
    private int _attemptCount = 0;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _attemptCount++;

        if (_attemptCount == 1)
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("service unavailable", Encoding.UTF8, "text/plain")
            };

            return Task.FromResult(response);
        }

        var successResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("success after retry", Encoding.UTF8, "text/plain")
        };

        return Task.FromResult(successResponse);
    }
}

public class DelayMessageHandler : HttpMessageHandler
{
    private readonly TimeSpan _delay;

    public DelayMessageHandler(TimeSpan delay)
    {
        _delay = delay;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("delayed response", Encoding.UTF8, "text/plain")
        };
    }
}

public class CaptureRequestHandler : HttpMessageHandler
{
    private readonly HttpRequestMessage _captureTarget;

    public CaptureRequestHandler(HttpRequestMessage captureTarget)
    {
        _captureTarget = captureTarget;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _captureTarget.Method = request.Method;
        _captureTarget.RequestUri = request.RequestUri;
        foreach (var header in request.Headers)
        {
            _captureTarget.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("captured", Encoding.UTF8, "text/plain")
        };

        return Task.FromResult(response);
    }
}

public class BytesMessageHandler : HttpMessageHandler
{
    private readonly byte[] _bytes;

    public BytesMessageHandler(byte[] bytes)
    {
        _bytes = bytes;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(_bytes)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        return Task.FromResult(response);
    }
}

public class FileDownloadMessageHandler : HttpMessageHandler
{
    private readonly byte[] _fileContent;
    private readonly string _fileName;

    public FileDownloadMessageHandler(byte[] fileContent, string fileName)
    {
        _fileContent = fileContent;
        _fileName = fileName;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(_fileContent)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        response.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileName = _fileName
        };

        return Task.FromResult(response);
    }
}
