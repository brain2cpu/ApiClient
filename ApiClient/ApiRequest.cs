using System.Net.Http.Json;
using System.Text;
using System.Xml.Serialization;

namespace Brain2CPU.ApiClient;

public class ApiRequest : IDisposable
{
    public ApiRequest(string url) => Url = new Uri(url);
    public ApiRequest(Uri url) => Url = url;
    public ApiRequest(string scheme, string host, string? path, int? port = null)
    {
        var uriBuilder = new UriBuilder(scheme, host, port ?? -1, path);
        Url = uriBuilder.Uri;
    }
    
    public Uri Url { get; }

    public HttpMethod Method { get; set; } = HttpMethod.Get;
    public Dictionary<string, string> UrlParameters { get; } = [];
    public Dictionary<string, string> Headers { get; } = [];
    public HttpContent? Content { get; set; } = null;

    public ApiRequest AddUrlParams(params string[] kv)
    {
        if (kv.Length % 2 != 0)
            throw new ArgumentException("URL parameters must be in key value pairs");

        for (int i = 0; i < kv.Length; i += 2)
            UrlParameters[kv[i]] = kv[i + 1];

        return this;
    }

    public ApiRequest AddHeaders(params string[] kv)
    {
        if (kv.Length % 2 != 0)
            throw new ArgumentException("Headers must be in key value pairs");

        for (int i = 0; i < kv.Length; i += 2)
            Headers[kv[i]] = kv[i + 1];

        return this;
    }

    public ApiRequest BuildStringContent(string s)
    {
        Content = new StringContent(s);

        return this;
    }

    public ApiRequest BuildJsonContent<T>(T o)
    {
        Content = JsonContent.Create(o);
        
        return this;
    }

    public ApiRequest BuildXmlContent<T>(T o)
    {
        var xmlSerializer = new XmlSerializer(typeof(T));
        using var stringWriter = new StringWriter();
        xmlSerializer.Serialize(stringWriter, o);

        Content = new StringContent(stringWriter.ToString(), Encoding.UTF8, "application/xml");

        return this;
    }

    public ApiRequest BuildFormContent(IEnumerable<KeyValuePair<string, string>> d)
    {
        Content = new FormUrlEncodedContent(d);

        return this;
    }

    public ApiRequest BuildUploadContent(Stream stream, string formName, string fileName)
    {
        var content = new MultipartFormDataContent
        {
            { new StreamContent(stream), formName, fileName }
        };

        Content = content;

        return this;
    }
    
    public string MethodString
    {
        get => Method.ToString();
        set => Method = new HttpMethod(value);
    }

    public static string CombineUrl(params string[] segments)
        => string.Join("/", segments.Select(s => s.Trim('/')));

    public static void AddBearerTokenToHeader(Dictionary<string, string> headers, string token)
    {
        headers["Authorization"] = $"Bearer {token}";
    }

    public static void AddBasicAuthenticationToHeader(Dictionary<string, string> headers, string user, string pass)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        headers["Authorization"] = $"Basic {credentials}";
    }

    public void Dispose()
    {
        Content?.Dispose();
        GC.SuppressFinalize(this);
    }
}
