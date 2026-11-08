using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Devolutions.Hosting;

internal sealed class DevolutionsApiClient(HttpClient httpClient, ILogger logger)
{
    internal const string HttpClientName = "Devolutions.Secrets";

    public async Task<string> FetchAsync(
        DevolutionsServerResource server, string secretName, Guid vaultId, Guid entryId,
        string jsonPointer, CancellationToken cancellationToken)
    {
        var operation = "resolve API credentials";
        string? token = null;
        var failed = false;
        try
        {
            var appKey = await server.AppKey.GetValueAsync(cancellationToken).ConfigureAwait(false);
            var appSecret = await server.AppSecret.GetValueAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(appSecret))
            {
                throw new InvalidOperationException("API credentials must not be empty.");
            }

            operation = "login";
            using var login = new HttpRequestMessage(HttpMethod.Post, new Uri(server.ServerUri, "api/v1/login"))
            {
                Content = JsonContent.Create(new { appKey, appSecret })
            };
            using var loginResponse = await SendAsync(login, operation, cancellationToken).ConfigureAwait(false);
            using var authentication = await ReadJsonAsync(loginResponse, cancellationToken).ConfigureAwait(false);
            if (!authentication.RootElement.TryGetProperty("tokenId", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(token = tokenElement.GetString()))
            {
                throw new InvalidOperationException("Login did not return a tokenId.");
            }

            operation = "read credential entry";
            using var request = new HttpRequestMessage(
                HttpMethod.Get, new Uri(server.ServerUri, $"api/v1/vault/{vaultId:D}/entry/{entryId:D}"));
            request.Headers.Add("tokenId", token);
            using var response = await SendAsync(request, operation, cancellationToken).ConfigureAwait(false);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return JsonSecretSelector.Read(document.RootElement, jsonPointer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failed = true;
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            or InvalidOperationException or ArgumentException or FormatException or IOException or OperationCanceledException)
        {
            failed = true;
            // Provider responses and exception messages can contain secrets. Never forward them.
            logger.LogError("Devolutions resource {ResourceName}, secret {SecretName}: {Operation} failed ({FailureType}).",
                server.Name, secretName, operation, exception.GetType().Name);
            throw new InvalidOperationException(
                $"Devolutions resource '{server.Name}', secret '{secretName}': {operation} failed. " +
                "Check connectivity, application permissions, vault/entry IDs and the configured JSON Pointer.");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                // Logout needs an independent, bounded token even if the caller was cancelled.
                using var logoutTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    using var logout = new HttpRequestMessage(HttpMethod.Post, new Uri(server.ServerUri, "api/v1/logout"))
                    {
                        Content = new ByteArrayContent([])
                    };
                    logout.Headers.Add("tokenId", token);
                    using var response = await SendAsync(logout, "logout", logoutTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException
                    or ArgumentException or FormatException or IOException or OperationCanceledException)
                {
                    logger.LogError("Devolutions resource {ResourceName}, secret {SecretName}: logout failed ({FailureType}).",
                        server.Name, secretName, exception.GetType().Name);
                    if (!failed)
                    {
                        throw new InvalidOperationException(
                            $"Devolutions resource '{server.Name}', secret '{secretName}': logout failed.");
                    }
                }
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, string operation, CancellationToken cancellationToken)
    {
        var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            logger.LogError("Devolutions {Operation} returned HTTP {StatusCode}.", operation, status);
            throw new InvalidOperationException($"Devolutions {operation} returned HTTP {status}.");
        }

        return response;
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
