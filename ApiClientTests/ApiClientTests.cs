using System.Net;
using System.Text;
using Brain2CPU.ApiClient;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ApiClientTests;

public class ApiClientTests
{
    [Fact]
    public async Task TestWithRealClient()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(); // Registers IHttpClientFactory

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        var apiClient = new ApiClient(factory);
        
        var exception = await Record.ExceptionAsync(async () => 
        {
            _ = await apiClient.GetAsync<string>("http://example.com");
        });
        Assert.Null(exception);

        using var request = new ApiRequest("https://practice.expandtesting.com/download/cdct.jpg");
        var response = await apiClient.SendRequestAsync<Stream>(request);

        string path = Path.GetTempFileName();
        await using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write);
        if (response.Data.CanSeek)
            response.Data.Seek(0, SeekOrigin.Begin);

        await response.Data.CopyToAsync(fileStream);
        response.Data.Dispose();
    }

    [Fact]
    public async Task HttpClient_Should_Echo_Request_Body()
    {
        // Arrange
        var handler = new EchoMessageHandler();
        var httpClient = new HttpClient(handler);

        var mockFactory = new Mock<IHttpClientFactory>();
        mockFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
        
        var client = new ApiClient(mockFactory.Object);

        using var request = new ApiRequest("https://example.com")
        {
            Method = HttpMethod.Post
        };
        var payload = new Payload { Title = "title", Body = "body", UserId = 1 };
        request.BuildXmlContent(payload);

        // Act
        var response = await client.SendRequestAsync<Payload>(request);

        // Assert
        Assert.Equal(payload, response.Data);
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

