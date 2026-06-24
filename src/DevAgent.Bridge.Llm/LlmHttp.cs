namespace DevAgent.Bridge.Llm;

using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>Small helper that POSTs a JSON body and parses the JSON response.</summary>
internal static class LlmHttp
{
    public static async Task<JsonObject> PostJsonAsync(
        HttpClient http,
        string url,
        JsonObject body,
        Action<HttpRequestMessage> configureHeaders,
        string providerName,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        configureHeaders(request);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not LlmClientException)
        {
            throw new LlmClientException($"{providerName} request failed: {ex.Message}", ex);
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new LlmClientException(
                $"{providerName} returned HTTP {(int)response.StatusCode}: {Truncate(payload)}");
        }

        try
        {
            return JsonNode.Parse(payload) as JsonObject
                   ?? throw new LlmClientException($"{providerName} returned a non-object JSON response.");
        }
        catch (Exception ex) when (ex is not LlmClientException)
        {
            throw new LlmClientException($"{providerName} returned malformed JSON: {Truncate(payload)}", ex);
        }
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500] + "…";
}
