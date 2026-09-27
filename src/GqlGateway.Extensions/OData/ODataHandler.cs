namespace GqlGateway.Extensions.OData;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using Microsoft.Extensions.Logging;

public sealed class ODataHandler : IODataHandler
{
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly IGatewayExecutionService _executionService;
    private readonly ILogger<ODataHandler> _logger;

    public ODataHandler(
        ITableMetadataRepository metadataRepo,
        IGatewayExecutionService executionService,
        ILogger<ODataHandler> logger)
    {
        _metadataRepo = metadataRepo;
        _executionService = executionService;
        _logger = logger;
    }

    public async Task<string> GetMetadataCsdlAsync(CancellationToken ct = default)
    {
        var tables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        return ODataCsdlGenerator.GenerateMetadataXml(tables);
    }

    public async Task<object> GetServiceDocumentAsync(string serviceRootUrl, CancellationToken ct = default)
    {
        var tables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        return ODataResponseFormatter.FormatServiceDocument(serviceRootUrl, tables);
    }

    public async Task<ODataQueryResult> ExecuteEntitySetQueryAsync(
        ClaimsPrincipal? principal,
        string serviceRootUrl,
        TableIdentifier table,
        int? top,
        int? skip,
        string? select,
        bool includeCount,
        IReadOnlyDictionary<string, string[]>? headers,
        CancellationToken ct = default)
    {
        // Safe limit handling: default top 100, max 1000
        var effectiveTop = top.HasValue ? Math.Clamp(top.Value, 1, 1000) : 100;
        var effectiveSkip = skip.HasValue ? Math.Max(0, skip.Value) : 0;

        IReadOnlyList<string>? requestedFields = null;
        if (!string.IsNullOrWhiteSpace(select))
        {
            requestedFields = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
        TableAccessDecision decision;

        try
        {
            (rows, decision) = await _executionService.ExecuteTableQueryAsync(
                principal: principal,
                table: table,
                first: effectiveTop,
                after: effectiveSkip,
                queryArguments: null,
                requestedFields: requestedFields,
                requestHeaders: headers,
                ct: ct
            ).ConfigureAwait(false);
        }
        catch (GqlGateway.Domain.Exceptions.TableNotFoundException nfEx)
        {
            _logger.LogWarning("OData query for {Table} not found: {Message}", table, nfEx.Message);
            return new ODataQueryResult(
                Success: false,
                StatusCode: 404,
                Payload: ODataResponseFormatter.FormatErrorResponse("NOT_FOUND", nfEx.Message),
                ErrorCode: "NOT_FOUND",
                ErrorMessage: nfEx.Message
            );
        }
        catch (GqlGateway.Domain.Exceptions.GatewayUnauthorizedException unEx)
        {
            _logger.LogWarning("OData query for {Table} unauthorized: {Message}", table, unEx.Message);
            return new ODataQueryResult(
                Success: false,
                StatusCode: 401,
                Payload: ODataResponseFormatter.FormatErrorResponse("UNAUTHORIZED", unEx.Message),
                ErrorCode: "UNAUTHORIZED",
                ErrorMessage: unEx.Message
            );
        }
        catch (GqlGateway.Domain.Exceptions.GatewaySecurityException secEx)
        {
            _logger.LogWarning("OData query for {Table} forbidden: {Message}", table, secEx.Message);
            return new ODataQueryResult(
                Success: false,
                StatusCode: 403,
                Payload: ODataResponseFormatter.FormatErrorResponse("ACCESS_DENIED", secEx.Message),
                ErrorCode: "ACCESS_DENIED",
                ErrorMessage: secEx.Message
            );
        }

        if (!decision.IsAllowed)
        {
            var reasons = decision.DeniedReasons.Count > 0 ? string.Join("; ", decision.DeniedReasons) : "Access denied by gateway governance policy.";
            _logger.LogWarning("OData query for {Table} denied: {Reasons}", table, reasons);

            return new ODataQueryResult(
                Success: false,
                StatusCode: 403,
                Payload: ODataResponseFormatter.FormatErrorResponse("ACCESS_DENIED", reasons),
                ErrorCode: "ACCESS_DENIED",
                ErrorMessage: reasons
            );
        }

        int? totalCount = includeCount ? rows.Count : null;
        var payload = ODataResponseFormatter.FormatEntitySetResponse(serviceRootUrl, table, rows, totalCount);

        return new ODataQueryResult(
            Success: true,
            StatusCode: 200,
            Payload: payload
        );
    }
}
