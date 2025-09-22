using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Serialization;

namespace Brain2CPU.ApiClient;

public class ApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public int Retries { get; set; } = 3;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    // keep it null or empty for the default client
    // register named client as:
    /* 
        builder.Services.AddHttpClient("CustomClient")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                {
                    // Custom validation logic
                    return true;
                }
            });
    */
    public string? HttpClientName { get; set; }
    
    public delegate Task<object> ContentHandler(HttpContent content, Type type);

    public Dictionary<string, ContentHandler> ResponseHandlers { get; } = new()
    {
        { "application/json", FromJson },
        { "text/json", FromJson },
        { "text/x-json", FromJson },
        { "application/xml", FromXml },
        { "text/xml", FromXml },
        { "text/x-xml", FromXml },
        { "application/x-www-form-urlencoded", FromXml },
        { "application/octet-stream", Download }
    };

    //408Request Timeout429Too Many Requests (rate limit)500Internal Server Error502Bad Gateway503Service Unavailable504Gateway Timeout
    public List<int> TransientErrorCodes { get; } = [408, 429, 500, 502, 503, 504];

    public async Task<OpResult<T>> SendRequestAsync<T>(ApiRequest apiRequest, CancellationToken? cancellationToken = null)
    {
        var requestBuilder = PrepareRequest(apiRequest);
        if (!requestBuilder.IsSuccess)
            return OpResult<T>.Error(requestBuilder.Exception, "Invalid request", (int)HttpStatusCode.BadRequest);

        var client = string.IsNullOrEmpty(HttpClientName) ? _httpClientFactory.CreateClient() : _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = Timeout > TimeSpan.Zero ? Timeout : System.Threading.Timeout.InfiniteTimeSpan;

        var response = await SendAsync<OpResult<string>>(client, requestBuilder.Data, Retries, cancellationToken);

        requestBuilder.Data.Dispose();

        if (!response.IsSuccess)
            return OpResult<T>.Error(response.Exception, response.Message, response.StatusCode);

        var result = await ProcessResponseAsync<T>(response.Data);

        response.Data.Dispose();

        return result;
    }

    public Task<OpResult<T>> GetAsync<T>(string url, CancellationToken? cancellationToken = null)
        => SendRequestAsync<T>(new ApiRequest(url) { Method = HttpMethod.Get }, cancellationToken);

    private static OpResult<HttpRequestMessage> PrepareRequest(ApiRequest apiRequest)
    {
        var requestMessage = new HttpRequestMessage();
        try
        {
            if (apiRequest.UrlParameters.Count > 0)
            {
                var nv = new NameValueCollection();
                foreach (var param in apiRequest.UrlParameters)
                    nv.Add(param.Key, param.Value);

                var uriBuilder = new UriBuilder(apiRequest.Url)
                {
                    Query = nv.ToString()
                };

                requestMessage.RequestUri = uriBuilder.Uri;
            }
            else
                requestMessage.RequestUri = apiRequest.Url;

            requestMessage.Method = apiRequest.Method;

            foreach (var header in apiRequest.Headers)
            {
                requestMessage.Headers.Add(header.Key, header.Value);
            }

            if (apiRequest.Content != null)
                requestMessage.Content = apiRequest.Content;

            return OpResult<HttpRequestMessage>.Success(requestMessage);
        }
        catch (Exception xcp)
        {
            requestMessage.Dispose();
            return OpResult<HttpRequestMessage>.Error(xcp);
        }
    }

    private async Task<OpResult<HttpResponseMessage>> SendAsync<T>(HttpClient client,
        HttpRequestMessage requestMessage,
        int retry,
        CancellationToken? cancellationToken)
    {
        try
        {
            HttpResponseMessage? responseMessage = null;
            try
            {
                responseMessage = cancellationToken.HasValue
                    ? await client.SendAsync(requestMessage, cancellationToken.Value)
                    : await client.SendAsync(requestMessage);

                responseMessage.EnsureSuccessStatusCode();
            }
            catch
            {
                responseMessage?.Dispose();
                throw;
            }

            return OpResult<HttpResponseMessage>.Success(responseMessage);
        }
        catch (TaskCanceledException tex1) when (tex1.CancellationToken.IsCancellationRequested)
        {
            // cancellation was requested
            return OpResult<HttpResponseMessage>.Cancelled();
        }
        catch (TaskCanceledException tex2) when (!tex2.CancellationToken.IsCancellationRequested && retry > 0)
        {
            // This means the request timed out, retry after a wait
            await Task.Delay(RetryInterval * (Retries - retry + 1));
            return await SendAsync<T>(client, requestMessage, retry - 1, cancellationToken);
        }
        catch (TaskCanceledException tex3)
        {
            return OpResult<HttpResponseMessage>.Error(tex3, "Timeout", (int)HttpStatusCode.RequestTimeout);
        }
        catch (HttpRequestException rex1) when (retry > 0 && IsTransientCode(rex1.StatusCode))
        {
            await Task.Delay(RetryInterval * (Retries - retry + 1));
            return await SendAsync<T>(client, requestMessage, retry - 1, cancellationToken);
        }
        catch (HttpRequestException rex2)
        {
            return OpResult<HttpResponseMessage>.Error(rex2,
                statusCode: (int)(rex2.StatusCode ?? HttpStatusCode.InternalServerError));
        }
        catch (Exception xcp)
        {
            return OpResult<HttpResponseMessage>.Error(xcp, statusCode: (int)HttpStatusCode.InternalServerError);
        }
    }

    private static JsonSerializerOptions? _jsonSerializerOptions = null;
    private static async Task<object> FromJson(HttpContent content, Type type)
    {
        var contentString = await content.ReadAsStringAsync();

        _jsonSerializerOptions ??= new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        return JsonSerializer.Deserialize(contentString, type, _jsonSerializerOptions);
    }
    
    private static async Task<object> FromXml(HttpContent content, Type type)
    {
        var contentString = await content.ReadAsStringAsync();
        
        var serializer = new XmlSerializer(type);
        using var reader = new StringReader(contentString);
        return serializer.Deserialize(reader);
    }

    private static async Task<object> Download(HttpContent content, Type type)
    {
        return await content.ReadAsByteArrayAsync();
    }
    
    private async Task<OpResult<T>> ProcessResponseAsync<T>(HttpResponseMessage response)
    {
        try
        {
            object? contentObject = typeof(T) switch
            {
                // ReSharper disable ConvertTypeCheckPatternToNullCheck
                Type t when t == typeof(string) => await response.Content.ReadAsStringAsync(),
                Type t when t == typeof(byte[]) => await response.Content.ReadAsByteArrayAsync(),
                Type t when t == typeof(Stream) => await response.Content.ReadAsStreamAsync(),
                // ReSharper restore ConvertTypeCheckPatternToNullCheck
                _ => null
            };

            if (contentObject != null)
            {
                // without this response.Data.Dispose(); will destroy the stream
                if (contentObject is Stream stream)
                {
                    var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    buffer.Position = 0;

                    contentObject = buffer;
                }
                return OpResult<T>.Success((T)contentObject, (int)response.StatusCode);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            // assume default json
            if (string.IsNullOrEmpty(contentType))
                contentType = "application/json";

            var handler = ResponseHandlers.SingleOrDefault(x
                => contentType.Contains(x.Key, StringComparison.OrdinalIgnoreCase)).Value;
            
            if(handler != null)
                return OpResult<T>.Success((T)await handler(response.Content, typeof(T)), (int)response.StatusCode);
           
            return OpResult<T>.Error($"Error processing response, {contentType} not handled", (int)HttpStatusCode.InternalServerError);
        }
        catch (Exception xcp)
        {
            return OpResult<T>.Error(xcp, "Failed processing response", (int)HttpStatusCode.InternalServerError);
        }
    }

    private bool IsTransientCode(HttpStatusCode? code)
    {
        if(!code.HasValue)
            return false;
        
        return TransientErrorCodes.Contains((int)code.Value);
    }
}

public class ApiRequest : IDisposable
{
    public ApiRequest(string url) => Url = new Uri(url);
    public ApiRequest(Uri url) => Url = url;
    public ApiRequest(string scheme, string host, string? path, int? port = null)
    {
        var uriBuilder = new UriBuilder(scheme, host, port ?? -1, path);
        Url = uriBuilder.Uri;
    }
    public HttpMethod Method { get; set; } = HttpMethod.Get;
    public Uri Url { get; }
    public Dictionary<string, string> UrlParameters { get; set; } = [];
    public Dictionary<string, string> Headers { get; set; } = [];
    public HttpContent? Content { get; set; } = null;

    public void BuildStringContent(string s)
        => Content = new StringContent(s);
    
    public void BuildJsonContent<T>(T o)
        => Content = JsonContent.Create(o);

    public void BuildXmlContent<T>(T o)
    {
        var xmlSerializer = new XmlSerializer(typeof(T));
        using var stringWriter = new StringWriter();
        xmlSerializer.Serialize(stringWriter, o);

        Content = new StringContent(stringWriter.ToString(), Encoding.UTF8, "application/xml");
    }

    public void BuildFormContent(IEnumerable<KeyValuePair<string,string>> d)
        => Content = new FormUrlEncodedContent(d);
    
    public void BuildUploadContent(Stream stream, string formName, string fileName)
    {
        var content = new MultipartFormDataContent
        {
            { new StreamContent(stream), formName, fileName }
        };

        Content = content;
    }
    
    public string MethodString
    {
        get => Method.ToString();
        set => Method = (HttpMethod)Enum.Parse(typeof(HttpMethod), value, true);
    }

    public void Dispose()
    {
        Content?.Dispose();
        GC.SuppressFinalize(this);
    }
}
