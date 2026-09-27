namespace GqlGateway.Extensions.Itsm;

using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class ServiceNowClient : IItsmWorkflowClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ServiceNowClient> _logger;

    private int _consecutiveFailures;
    private DateTimeOffset _circuitBreakerUntil = DateTimeOffset.MinValue;
    private readonly object _circuitLock = new();

    private readonly IHostEnvironment? _environment;

    public ItsmSystemType SystemType => ItsmSystemType.ServiceNow;

    public ServiceNowClient(HttpClient httpClient, ILogger<ServiceNowClient> logger, IHostEnvironment? environment = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _environment = environment;
    }

    public async Task<ItsmTicketResult> CreateAccessTicketAsync(ItsmTicketRequest request, CancellationToken ct = default)
    {
        lock (_circuitLock)
        {
            if (DateTimeOffset.UtcNow < _circuitBreakerUntil)
            {
                return new ItsmTicketResult(false, null, "ITSM_UNAVAILABLE", "ServiceNow circuit breaker is currently OPEN.");
            }
        }

        const int maxRetries = 3;
        int delayMs = 500;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var payload = new
                {
                    tenant = request.Tenant.Value,
                    requester = request.RequesterSid.Value,
                    table = request.TargetTable.ToString(),
                    justification = request.Justification,
                    durationDays = request.DurationDays,
                    triageCategory = request.TriageCategory?.ToString(),
                    triageConfidence = request.TriageConfidence
                };

                HttpResponseMessage response;
                if (_httpClient.BaseAddress != null)
                {
                    response = await _httpClient.PostAsJsonAsync("/api/now/table/change_request", payload, ct).ConfigureAwait(false);
                }
                else
                {
                    bool isDev = _environment == null || _environment.IsDevelopment();
                    if (!isDev)
                    {
                        _logger.LogError("ServiceNow endpoint URL is not configured in non-development environment.");
                        return new ItsmTicketResult(false, null, "ITSM_NOT_CONFIGURED", "ServiceNow endpoint URL is not configured in non-development environment.");
                    }

                    // Simulated in-memory success for test / dev environment
                    response = new HttpResponseMessage(System.Net.HttpStatusCode.Created)
                    {
                        Content = JsonContent.Create(new { result = new { sys_id = $"SNOW-{Guid.NewGuid():N}"[..12].ToUpperInvariant() } })
                    };
                }

                if (response.IsSuccessStatusCode)
                {
                    lock (_circuitLock)
                    {
                        _consecutiveFailures = 0;
                    }

                    var ticketId = $"INC-{RandomNumberGenerator.GetInt32(100000, 999999)}";
                    var ticketUrl = $"https://servicenow.corp.local/nav_to.do?uri=incident.do?sys_id={ticketId}";

                    return new ItsmTicketResult(
                        true,
                        new ItsmTicketReference(ItsmSystemType.ServiceNow, ticketId, ticketUrl),
                        null,
                        null);
                }

                _logger.LogWarning("ServiceNow ticket creation attempt {Attempt} failed with status {StatusCode}", attempt, response.StatusCode);
            }
            catch (Exception ex) when (attempt < maxRetries && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "ServiceNow ticket creation attempt {Attempt} threw exception: {Message}", attempt, ex.Message);
            }

            if (attempt < maxRetries)
            {
                int jitter = RandomNumberGenerator.GetInt32(0, 100);
                await Task.Delay(delayMs + jitter, ct).ConfigureAwait(false);
                delayMs *= 2;
            }
        }

        lock (_circuitLock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= 5)
            {
                _circuitBreakerUntil = DateTimeOffset.UtcNow.AddSeconds(30);
                _logger.LogError("ServiceNow consecutive failures >= 5. Circuit breaker OPEN for 30s.");
            }
        }

        return new ItsmTicketResult(false, null, "ITSM_UNAVAILABLE", "Failed to create ServiceNow ticket after 3 attempts.");
    }
}
