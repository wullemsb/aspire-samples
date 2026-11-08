using System.Net;
using System.Text.Json;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Devolutions.Hosting.Tests;

public sealed class DevolutionsApiClientTests
{
    private static readonly Guid VaultId = Guid.Parse("c6eca48a-0cda-4f52-94dc-d02fe0146896");
    private static readonly Guid EntryId = Guid.Parse("996aa495-f206-4a17-b7ac-82005b06e557");

    [Fact]
    public async Task UsesDocumentedLoginEntryAndLogoutProtocol()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = DevolutionsResourceTests.AddServer(builder);
        var requests = 0;
        using var httpClient = new HttpClient(new TestHttpHandler(async (request, token) =>
        {
            switch (++requests)
            {
                case 1:
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal("https://example.test/dps/api/v1/login", request.RequestUri!.AbsoluteUri);
                    using (var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)))
                    {
                        Assert.Equal("test-key", json.RootElement.GetProperty("appKey").GetString());
                        Assert.Equal("test-app-secret", json.RootElement.GetProperty("appSecret").GetString());
                    }
                    return TestHttpHandler.Json("""{"tokenId":"session-token"}""");
                case 2:
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.Equal($"https://example.test/dps/api/v1/vault/{VaultId:D}/entry/{EntryId:D}", request.RequestUri!.AbsoluteUri);
                    Assert.Equal("session-token", Assert.Single(request.Headers.GetValues("tokenId")));
                    return TestHttpHandler.Json("""{"payload":{"password":"test-password"}}""");
                default:
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal("https://example.test/dps/api/v1/logout", request.RequestUri!.AbsoluteUri);
                    Assert.Equal("session-token", Assert.Single(request.Headers.GetValues("tokenId")));
                    Assert.Empty(await request.Content!.ReadAsByteArrayAsync(token));
                    return TestHttpHandler.Json("{}");
            }
        }));
        var logger = new RecordingLogger();
        Assert.Equal("test-password", await new DevolutionsApiClient(httpClient, logger).FetchAsync(
            server.Resource, "password", VaultId, EntryId, "/payload/password", TestContext.Current.CancellationToken));
        Assert.Equal(3, requests);
        Assert.Empty(logger.Messages);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task EntryHttpFailuresLogoutAndNeverExposeResponseBodies(HttpStatusCode status)
    {
        await AssertFailureAsync(
            TestHttpHandler.Json("""{"tokenId":"session-token"}"""),
            TestHttpHandler.Json("""{"error":"test-password test-app-secret session-token"}""", status),
            "read credential entry", expectedRequests: 3);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task LoginHttpFailuresNeverAttemptEntryOrLogout(HttpStatusCode status) =>
        await AssertFailureAsync(
            TestHttpHandler.Json("""{"error":"test-app-secret session-token"}""", status),
            TestHttpHandler.Json("{}"), "login", expectedRequests: 1);

    [Fact]
    public async Task InvalidTokenHeadersAreSanitized() =>
        await AssertFailureAsync(
            TestHttpHandler.Json("""{"tokenId":"session-token\r\ninvalid-header"}"""),
            TestHttpHandler.Json("{}"), "read credential entry", expectedRequests: 1);

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"tokenId":null}""")]
    [InlineData("""{"tokenId":""}""")]
    [InlineData("""{"tokenId":123}""")]
    [InlineData("test-password is not JSON")]
    public async Task InvalidLoginResponsesFailWithoutReadingEntry(string json) =>
        await AssertFailureAsync(TestHttpHandler.Json(json), TestHttpHandler.Json("{}"), "login", expectedRequests: 1);

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"payload":{"password":null}}""")]
    [InlineData("test-password is not JSON")]
    public async Task InvalidEntryResponsesLogoutAndFailSafely(string json) =>
        await AssertFailureAsync(TestHttpHandler.Json("""{"tokenId":"session-token"}"""),
            TestHttpHandler.Json(json), "read credential entry", expectedRequests: 3);

    [Fact]
    public async Task LogoutFailureFailsTheFetchWithoutReturningSecret() =>
        await AssertFailureAsync(TestHttpHandler.Json("""{"tokenId":"session-token"}"""),
            TestHttpHandler.Json("""{"payload":{"password":"test-password"}}"""),
            "logout", expectedRequests: 3, logoutStatus: HttpStatusCode.InternalServerError);

    [Fact]
    public async Task LogoutFailureDoesNotReplaceTheOriginalFailure() =>
        await AssertFailureAsync(TestHttpHandler.Json("""{"tokenId":"session-token"}"""),
            TestHttpHandler.Json("{}", HttpStatusCode.Forbidden),
            "read credential entry", expectedRequests: 3, logoutStatus: HttpStatusCode.InternalServerError);

    [Fact]
    public async Task CancellationDuringEntryReadStillLogsOutWithIndependentToken()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = DevolutionsResourceTests.AddServer(builder);
        using var cancellation = new CancellationTokenSource();
        var requests = 0;
        using var httpClient = new HttpClient(new TestHttpHandler((request, token) =>
        {
            switch (++requests)
            {
                case 1:
                    return Task.FromResult(TestHttpHandler.Json("""{"tokenId":"session-token"}"""));
                case 2:
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("Expected cancellation");
                default:
                    Assert.False(token.IsCancellationRequested);
                    Assert.Equal("/dps/api/v1/logout", request.RequestUri!.AbsolutePath);
                    return Task.FromResult(TestHttpHandler.Json("{}"));
            }
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DevolutionsApiClient(httpClient, new RecordingLogger())
            .FetchAsync(server.Resource, "password", VaultId, EntryId, "/payload/password", cancellation.Token));
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task NetworkErrorsAreSanitized()
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = DevolutionsResourceTests.AddServer(builder);
        using var httpClient = new HttpClient(new TestHttpHandler((_, _) =>
            throw new HttpRequestException("test-password test-app-secret session-token")));
        var logger = new RecordingLogger();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new DevolutionsApiClient(httpClient, logger)
            .FetchAsync(server.Resource, "password", VaultId, EntryId, "/payload/password", TestContext.Current.CancellationToken));
        Assert.DoesNotContain("test-password", error.ToString());
        Assert.DoesNotContain("test-app-secret", string.Join(" ", logger.Messages));
        Assert.Null(error.InnerException);
    }

    private static async Task AssertFailureAsync(
        HttpResponseMessage login, HttpResponseMessage entry, string operation, int expectedRequests,
        HttpStatusCode logoutStatus = HttpStatusCode.OK)
    {
        var builder = DistributedApplication.CreateBuilder();
        var server = DevolutionsResourceTests.AddServer(builder);
        var requests = 0;
        using var httpClient = new HttpClient(new TestHttpHandler((_, _) => Task.FromResult(++requests switch
        {
            1 => login,
            2 => entry,
            _ => TestHttpHandler.Json("test-password", logoutStatus)
        })));
        using (login)
        using (entry)
        {
            var logger = new RecordingLogger();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new DevolutionsApiClient(httpClient, logger)
                .FetchAsync(server.Resource, "password", VaultId, EntryId, "/payload/password", TestContext.Current.CancellationToken));
            Assert.Contains("devolutions", error.Message);
            Assert.Contains(operation, error.Message);
            Assert.Null(error.InnerException);
            Assert.Equal(expectedRequests, requests);
            var output = error + " " + string.Join(" ", logger.Messages);
            Assert.DoesNotContain("test-password", output);
            Assert.DoesNotContain("test-app-secret", output);
            Assert.DoesNotContain("session-token", output);
            Assert.NotEmpty(logger.Messages);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Messages.Add(formatter(state, exception));
        }
    }
}
