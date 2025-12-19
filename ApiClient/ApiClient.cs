using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Serialization;
using Microsoft.Extensions.Logging;

namespace Brain2CPU.ApiClient;

public class ApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ApiClient>? _logger;

    public ApiClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _logger = null;
    }

    public ApiClient(IHttpClientFactory httpClientFactory, ILogger<ApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public int Retries { get; set; } = 3;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromMilliseconds(250);

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

    //408 Request Timeout, 429 Too Many Requests, 500 Internal Server Error, 502 Bad Gateway, 503 Service Unavailable, 504 Gateway Timeout
    public List<int> TransientErrorCodes { get; } = [408, 429, 500, 502, 503, 504];

    // Common headers to be added to every request, will be overridden by ApiRequest headers if specified
    public Dictionary<string, string> CommonHeaders { get; } = [];

    public async Task<OpResult<T>> SendRequestAsync<T>(ApiRequest apiRequest, CancellationToken? cancellationToken = null)
    {
        _logger?.LogInformation("Sending request to {Url} with method {Method}", apiRequest.Url, apiRequest.Method);
        
        var requestBuilder = PrepareRequest(apiRequest);
        if (!requestBuilder.IsSuccess)
        {
            _logger?.LogError(requestBuilder.Exception, "Failed to prepare request for {Url}", apiRequest.Url);
            return OpResult<T>.Error(requestBuilder.Exception, "Invalid request", (int)HttpStatusCode.BadRequest);
        }

        var client = string.IsNullOrEmpty(HttpClientName) ? _httpClientFactory.CreateClient() : _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = Timeout > TimeSpan.Zero ? Timeout : System.Threading.Timeout.InfiniteTimeSpan;

        var response = await SendAsync(client, requestBuilder.Data, Retries, cancellationToken);

        if (!response.IsSuccess)
        {
            _logger?.LogError("Request to {Url} failed with status {Status} - {StatusCode}: {Message}", 
                apiRequest.Url, response.Status, response.StatusCode, response.Message);
            return OpResult<T>.Error(response.Status, response.Exception, response.Message, response.StatusCode);
        }

        _logger?.LogDebug("Request to {Url} completed with status {StatusCode}", apiRequest.Url, response.Data.StatusCode);
        var result = await ProcessResponseAsync<T>(response.Data);

        response.Data.Dispose();

        return result;
    }

    public async Task<OpResult> SendRequestAsync(ApiRequest apiRequest, CancellationToken? cancellationToken = null)
        => await SendRequestAsync<string>(apiRequest, cancellationToken);

    public Task<OpResult<T>> GetAsync<T>(string url, CancellationToken? cancellationToken = null)
        => SendRequestAsync<T>(new ApiRequest(url) { Method = HttpMethod.Get }, cancellationToken);

    public async Task<OpResult<string>> DownloadAsync(ApiRequest apiRequest, string downloadDirectory, CancellationToken? cancellationToken = null)
    {
        _logger?.LogInformation("Starting download from {Url} to directory {DownloadDirectory}", apiRequest.Url, downloadDirectory);
        
        if(string.IsNullOrEmpty(downloadDirectory))
        {
            _logger?.LogError("Download directory is null or empty");
            return OpResult<string>.Error("Download directory must be specified");
        }

        try
        {
            if (!Directory.Exists(downloadDirectory))
                Directory.CreateDirectory(downloadDirectory);
        }
        catch (Exception xcp)
        {
            _logger?.LogError(xcp, "Cannot access download directory {DownloadDirectory}", downloadDirectory);
            return OpResult<string>.Error(xcp, $"Cannot access download directory {downloadDirectory}");
        }

        var requestBuilder = PrepareRequest(apiRequest);
        if (!requestBuilder.IsSuccess)
        {
            _logger?.LogError(requestBuilder.Exception, "Failed to prepare download request for {Url}", apiRequest.Url);
            return OpResult<string>.Error(requestBuilder.Exception, "Invalid request", (int)HttpStatusCode.BadRequest);
        }

        var client = string.IsNullOrEmpty(HttpClientName) ? _httpClientFactory.CreateClient() : _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = Timeout > TimeSpan.Zero ? Timeout : System.Threading.Timeout.InfiniteTimeSpan;

        var response = await SendAsync(client, requestBuilder.Data, Retries, cancellationToken);
        if (!response.IsSuccess)
        {
            _logger?.LogError("Download from {Url} failed with status {StatusCode}: {Message}", apiRequest.Url, response.StatusCode, response.Message);
            return OpResult<string>.Error(response.Exception, response.Message, response.StatusCode);
        }

        var tmpPath = Path.GetTempFileName();
        var fileName = GetFileNameFrom(response.Data.Content.Headers, apiRequest.Url);
        _logger?.LogDebug("Download file name determined: {FileName}", fileName);
        
        try
        {
            await using var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write);
            await using var stream = await response.Data.Content.ReadAsStreamAsync();

            await stream.CopyToAsync(fileStream);

            await fileStream.FlushAsync();
            fileStream.Close();

            var destinationPath = Path.Combine(downloadDirectory, fileName);
            var counter = 1;
            while (File.Exists(destinationPath))
            {
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                destinationPath = Path.Combine(downloadDirectory, $"{fileNameWithoutExt}({counter}){ext}");
                counter++;
            }
            File.Move(tmpPath, destinationPath);

            _logger?.LogInformation("Download completed successfully: {DestinationPath}", destinationPath);
            return OpResult<string>.Success(destinationPath);
        }
        catch (Exception xcp)
        {
            _logger?.LogError(xcp, "Download to {DownloadDirectory} failed", downloadDirectory);
            return OpResult<string>.Error(xcp, "Download failed");
        }
        finally
        {
            response.Data.Dispose();

            try
            {
                if(File.Exists(tmpPath))
                    File.Delete(tmpPath);
            }
            catch
            {
                // we can ignore this
            }
        }
    }

    private static string GetFileNameFrom(HttpContentHeaders headers, Uri url)
    {
        if (headers.ContentDisposition != null)
        {
            if (!string.IsNullOrEmpty(headers.ContentDisposition.FileNameStar))
                return headers.ContentDisposition.FileNameStar.Trim('"');

            if (!string.IsNullOrEmpty(headers.ContentDisposition.FileName))
                return headers.ContentDisposition.FileName.Trim('"');
        }

        try
        {
            return Path.GetFileName(url.LocalPath);
        }
        catch
        {
            return Path.GetRandomFileName();
        }
    }

    private OpResult<HttpRequestMessage> PrepareRequest(ApiRequest apiRequest)
    {
        var requestMessage = new HttpRequestMessage();
        try
        {
            if (apiRequest.UrlParameters.Count > 0)
            {
                var uriBuilder = new UriBuilder(apiRequest.Url)
                {
                    Query = string.Join("&", apiRequest.UrlParameters.Select(kv => $"{WebUtility.UrlEncode(kv.Key)}={WebUtility.UrlEncode(kv.Value)}"))
                };

                requestMessage.RequestUri = uriBuilder.Uri;
                _logger?.LogDebug("Request URI with parameters: {Uri}", requestMessage.RequestUri);
            }
            else
                requestMessage.RequestUri = apiRequest.Url;

            requestMessage.Method = apiRequest.Method;

            foreach (var header in CommonHeaders.Where(x => !apiRequest.Headers.ContainsKey(x.Key)))
            {
                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            foreach (var header in apiRequest.Headers)
            {
                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
                // the following will throw if the header value contains invalid characters as +=/ but those are valid in some headers like Authorization 
                //requestMessage.Headers.Add(header.Key, header.Value);
            }

            if (apiRequest.Content != null)
                requestMessage.Content = apiRequest.Content;

            _logger?.LogDebug("Request prepared successfully with {HeaderCount} headers", requestMessage.Headers.Count());
            return OpResult<HttpRequestMessage>.Success(requestMessage);
        }
        catch (Exception xcp)
        {
            _logger?.LogError(xcp, "Error preparing request for {Url}", apiRequest.Url);
            requestMessage.Dispose();
            return OpResult<HttpRequestMessage>.Error(xcp);
        }
    }

    private async Task<OpResult<HttpResponseMessage>> SendAsync(HttpClient client,
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

            _logger?.LogDebug("Request succeeded with status code {StatusCode}", responseMessage.StatusCode);
            return OpResult<HttpResponseMessage>.Success(responseMessage);
        }
        catch (TaskCanceledException) when (cancellationToken.HasValue && cancellationToken.Value.IsCancellationRequested)
        {
            _logger?.LogWarning("Request was cancelled by the caller");
            // cancellation was requested
            return OpResult<HttpResponseMessage>.Cancelled();
        }
        catch (TaskCanceledException) when (retry > 0)
        {
            _logger?.LogWarning("Request timeout. Retrying {RetryCount} more time(s) after delay", retry);
            // This means the request timed out, retry after a wait
            await Task.Delay(RetryInterval * (Retries - retry + 1));
            return await SendAsync(client, await CloneRequestAsync(requestMessage), retry - 1, cancellationToken);
        }
        catch (TaskCanceledException tex)
        {
            _logger?.LogError("Request timeout after {Retries} retries", Retries);
            return OpResult<HttpResponseMessage>.Error(tex, "Timeout", (int)HttpStatusCode.RequestTimeout);
        }
        catch (HttpRequestException rex1) when (retry > 0 && IsTransientCode(rex1.StatusCode))
        {
            _logger?.LogWarning("Transient error {StatusCode} received. Retrying {RetryCount} more time(s)", rex1.StatusCode, retry);
            await Task.Delay(RetryInterval * (Retries - retry + 1));
            return await SendAsync(client, await CloneRequestAsync(requestMessage), retry - 1, cancellationToken);
        }
        catch (HttpRequestException rex2)
        {
            _logger?.LogError(rex2, "HTTP request failed with status {StatusCode}", rex2.StatusCode ?? HttpStatusCode.InternalServerError);
            return OpResult<HttpResponseMessage>.Error(rex2,
                statusCode: (int)(rex2.StatusCode ?? HttpStatusCode.InternalServerError));
        }
        catch (Exception xcp)
        {
            _logger?.LogError(xcp, "Unexpected error during request");
            return OpResult<HttpResponseMessage>.Error(xcp, statusCode: (int)HttpStatusCode.InternalServerError);
        }
        finally
        {
            requestMessage.Dispose();
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version
        };

        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (original.Content != null)
        {
            var ms = new MemoryStream();
            await original.Content.CopyToAsync(ms);
            ms.Position = 0;

            var contentClone = new StreamContent(ms);

            // Copy content headers
            foreach (var header in original.Content.Headers)
                contentClone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            clone.Content = contentClone;
        }

        return clone;
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
                _logger?.LogDebug("Response processed successfully as {Type}", typeof(T).Name);
                return OpResult<T>.Success((T)contentObject, (int)response.StatusCode);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            // assume default json
            if (string.IsNullOrEmpty(contentType))
            {
                _logger?.LogDebug("Content type not specified, assuming application/json");
                contentType = "application/json";
            }

            _logger?.LogDebug("Processing response with content type {ContentType}", contentType);
            var handler = ResponseHandlers.SingleOrDefault(x
                => contentType.Contains(x.Key, StringComparison.OrdinalIgnoreCase)).Value;
            
            if(handler != null)
            {
                _logger?.LogDebug("Using handler for content type {ContentType}", contentType);
                return OpResult<T>.Success((T)await handler(response.Content, typeof(T)), (int)response.StatusCode);
            }
           
            _logger?.LogError("No handler found for content type {ContentType}", contentType);
            return OpResult<T>.Error($"Error processing response, {contentType} not handled", (int)HttpStatusCode.InternalServerError);
        }
        catch (Exception xcp)
        {
            _logger?.LogError(xcp, "Error processing response of type {Type}", typeof(T).Name);
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
