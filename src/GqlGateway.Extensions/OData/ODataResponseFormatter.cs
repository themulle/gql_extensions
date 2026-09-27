namespace GqlGateway.Extensions.OData;

using System;
using System.Collections.Generic;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;

public static class ODataResponseFormatter
{
    public static object FormatServiceDocument(string serviceRootUrl, IReadOnlyList<TableMetadata> tables)
    {
        var cleanRoot = serviceRootUrl.TrimEnd('/');
        var entitySets = new List<object>(tables.Count);

        foreach (var table in tables)
        {
            var id = table.Identifier;
            var name = $"{id.Domain}_{id.Schema}_{id.TableName}";
            var relativeUrl = $"{id.Domain}/{id.Schema}/{id.TableName}";

            entitySets.Add(new
            {
                name,
                kind = "EntitySet",
                url = relativeUrl
            });
        }

        return new
        {
            odataContext = $"{cleanRoot}/$metadata",
            value = entitySets
        };
    }

    public static object FormatEntitySetResponse(
        string serviceRootUrl,
        TableIdentifier table,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        int? totalCount = null)
    {
        var cleanRoot = serviceRootUrl.TrimEnd('/');
        var entitySetName = $"{table.Domain}_{table.Schema}_{table.TableName}";
        var context = $"{cleanRoot}/$metadata#{entitySetName}";

        if (totalCount.HasValue)
        {
            return new
            {
                odataContext = context,
                odataCount = totalCount.Value,
                value = rows
            };
        }

        return new
        {
            odataContext = context,
            value = rows
        };
    }

    public static object FormatErrorResponse(string code, string message)
    {
        return new
        {
            error = new
            {
                code,
                message
            }
        };
    }
}
