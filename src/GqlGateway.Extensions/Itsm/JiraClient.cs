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

public sealed class JiraClient : IItsmWorkflowClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<JiraClient> _logger;
    private readonly IHostEnvironment? _environment;

    private static int _consecutiveFailures;
    private static DateTimeOffset _circuitBreakerUntil = DateTimeOffset.MinValue;
    private static readonly object _circuitLock = new();

    public ItsmSystemType SystemType => ItsmSystemType.Jira;

    public JiraClient(HttpClient httpClient, ILogger<JiraClient> logger, IHostEnvironment? environment = null)
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
                return new ItsmTicketResult(false, null, "ITSM_UNAVAILABLE", "Jira circuit breaker is currently OPEN.");
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

                bool isDev = _environment != null && _environment.IsDevelopment();
                using HttpResponseMessage response = _httpClient.BaseAddress != null
                    ? await _httpClient.PostAsJsonAsync("/rest/api/2/issue", payload, ct).ConfigureAwait(false)
                    : isDev
                        ? new HttpResponseMessage(System.Net.HttpStatusCode.Created)
                        {
                            Content = JsonContent.Create(new { key = $"SEC-{RandomNumberGenerator.GetInt32(1000, 9999)}" })
                        }
                        : null!;

                if (response == null)
                {
                    _logger.LogError("Jira endpoint URL is not configured in non-development environment.");
                    return new ItsmTicketResult(false, null, "ITSM_NOT_CONFIGURED", "Jira endpoint URL is not configured in non-development environment.");
                }

                if (response.IsSuccessStatusCode)
                {
                    lock (_circuitLock)
                    {
                        _consecutiveFailures = 0;
                    }

                    var ticketId = $"SEC-{RandomNumberGenerator.GetInt32(1000, 9999)}";
                    var ticketUrl = $"https://jira.corp.local/browse/{ticketId}";

                    return new ItsmTicketResult(
                        true,
                        new ItsmTicketReference(ItsmSystemType.Jira, ticketId, ticketUrl),
                        null,
                        null);
                }

                _logger.LogWarning("Jira ticket creation attempt {Attempt} failed with status {StatusCode}", attempt, response.StatusCode);
            }
            catch (Exception ex) when (attempt < maxRetries && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Jira ticket creation attempt {Attempt} threw exception: {Message}", attempt, ex.Message);
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
                _logger.LogError("Jira consecutive failures >= 5. Circuit breaker OPEN for 30s.");
            }
        }

        return new ItsmTicketResult(false, null, "ITSM_UNAVAILABLE", "Failed to create Jira ticket after 3 attempts.");
    }
}
