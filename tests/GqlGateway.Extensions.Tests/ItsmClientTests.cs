using System.Net;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using GqlGateway.Extensions.Itsm;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace GqlGateway.Extensions.Tests;

public sealed class ItsmClientTests
{
    private sealed class DummyHostEnvironment(string envName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = envName;
        public string ApplicationName { get; set; } = "GqlGateway.Extensions.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handlerFunc(request));
        }
    }

    [Fact]
    public async Task ServiceNowClient_InProductionWithoutBaseAddress_ReturnsError()
    {
        var prodEnv = new DummyHostEnvironment("Production");
        using var httpClient = new HttpClient(); // BaseAddress is null
        var logger = NullLogger<ServiceNowClient>.Instance;
        var client = new ServiceNowClient(httpClient, logger, prodEnv);

        var result = await client.CreateAccessTicketAsync(new ItsmTicketRequest(
            new TenantId("tenant-a"),
            new Sid("S-1-5-21-1234"),
            new TableIdentifier("finance", "dbo", "invoices"),
            "Need access",
            7,
            null,
            null));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ITSM_NOT_CONFIGURED");
    }

    [Fact]
    public async Task JiraClient_InProductionWithoutBaseAddress_ReturnsError()
    {
        var prodEnv = new DummyHostEnvironment("Production");
        using var httpClient = new HttpClient(); // BaseAddress is null
        var logger = NullLogger<JiraClient>.Instance;
        var client = new JiraClient(httpClient, logger, prodEnv);

        var result = await client.CreateAccessTicketAsync(new ItsmTicketRequest(
            new TenantId("tenant-a"),
            new Sid("S-1-5-21-1234"),
            new TableIdentifier("finance", "dbo", "invoices"),
            "Need access",
            7,
            null,
            null));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ITSM_NOT_CONFIGURED");
    }

    [Fact]
    public async Task ServiceNowClient_CreatesTicketSuccessfully_InNonProduction()
    {
        var devEnv = new DummyHostEnvironment("Development");
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"result\":{\"sys_id\":\"REQ000123\",\"number\":\"REQ000123\"}}")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://servicenow.corp.local") };
        var logger = NullLogger<ServiceNowClient>.Instance;
        var client = new ServiceNowClient(httpClient, logger, devEnv);

        var result = await client.CreateAccessTicketAsync(new ItsmTicketRequest(
            new TenantId("tenant-a"),
            new Sid("S-1-5-21-1234"),
            new TableIdentifier("finance", "dbo", "invoices"),
            "Need access",
            7,
            null,
            null));

        result.Success.ShouldBeTrue();
        result.TicketReference?.TicketId.ShouldStartWith("INC-");
    }

    [Fact]
    public async Task JiraClient_CreatesTicketSuccessfully_InNonProduction()
    {
        var devEnv = new DummyHostEnvironment("Development");
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"key\":\"SEC-999\",\"id\":\"10001\"}")
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://jira.corp.local") };
        var logger = NullLogger<JiraClient>.Instance;
        var client = new JiraClient(httpClient, logger, devEnv);

        var result = await client.CreateAccessTicketAsync(new ItsmTicketRequest(
            new TenantId("tenant-a"),
            new Sid("S-1-5-21-1234"),
            new TableIdentifier("finance", "dbo", "invoices"),
            "Need access",
            7,
            null,
            null));

        result.Success.ShouldBeTrue();
        result.TicketReference?.TicketId.ShouldStartWith("SEC-");
    }

    [Fact]
    public async Task ServiceNowClient_WithNullEnvironmentWithoutBaseAddress_FailsClosed()
    {
        using var httpClient = new HttpClient();
        var logger = NullLogger<ServiceNowClient>.Instance;
        var client = new ServiceNowClient(httpClient, logger, environment: null);

        var result = await client.CreateAccessTicketAsync(new ItsmTicketRequest(
            new TenantId("tenant-a"),
            new Sid("S-1-5-21-1234"),
            new TableIdentifier("finance", "dbo", "invoices"),
            "Need access",
            7,
            null,
            null));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ITSM_NOT_CONFIGURED");
    }

    [Fact]
    public async Task JiraClient_WithNullEnvironmentWithoutBaseAddress_FailsClosed()
    {
        using var httpClient = new HttpClient();
        var logger = NullLogger<JiraClient>.Instance;
        var client = new JiraClient(httpClient, logger, environment: null);

        var result = await client.CreateAccessTicketAsync(new ItsmTicketRequest(
            new TenantId("tenant-a"),
            new Sid("S-1-5-21-1234"),
            new TableIdentifier("finance", "dbo", "invoices"),
            "Need access",
            7,
            null,
            null));

        result.Success.ShouldBeFalse();
        result.ErrorCode.ShouldBe("ITSM_NOT_CONFIGURED");
    }
}
