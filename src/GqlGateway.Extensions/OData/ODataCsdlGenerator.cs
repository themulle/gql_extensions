namespace GqlGateway.Extensions.OData;

using System;
using System.Collections.Generic;
using System.Text;
using GqlGateway.Domain.Model;

public static class ODataCsdlGenerator
{
    public static string GenerateMetadataXml(IReadOnlyList<TableMetadata> tables, string serviceNamespace = "GqlGateway.OData")
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<edmx:Edmx Version=\"4.0\" xmlns:edmx=\"http://docs.oasis-open.org/odata/ns/edmx\">");
        sb.AppendLine("  <edmx:DataServices>");
        sb.AppendLine($"    <Schema Namespace=\"{serviceNamespace}\" xmlns=\"http://docs.oasis-open.org/odata/ns/edm\">");

        // 1. Generate EntityTypes
        foreach (var table in tables)
        {
            var entityName = GetEntityName(table);
            sb.AppendLine($"      <EntityType Name=\"{entityName}\">");

            // Keys
            var keys = table.PrimaryKeyColumns.Count > 0 ? table.PrimaryKeyColumns : ["id"];
            sb.AppendLine("        <Key>");
            foreach (var key in keys)
            {
                sb.AppendLine($"          <PropertyRef Name=\"{key}\" />");
            }
            sb.AppendLine("        </Key>");

            // Properties
            if (table.Columns.Count > 0)
            {
                foreach (var col in table.Columns)
                {
                    var edmType = MapToEdmType(col.DataType);
                    var nullable = !keys.Contains(col.ColumnName, StringComparer.OrdinalIgnoreCase);
                    var nullStr = nullable ? "" : " Nullable=\"false\"";
                    sb.AppendLine($"        <Property Name=\"{col.ColumnName}\" Type=\"{edmType}\"{nullStr} />");
                }
            }
            else
            {
                // Fallback id property if no columns defined
                sb.AppendLine("        <Property Name=\"id\" Type=\"Edm.Int32\" Nullable=\"false\" />");
            }

            sb.AppendLine("      </EntityType>");
        }

        // 2. Generate EntityContainer & EntitySets
        sb.AppendLine("      <EntityContainer Name=\"Container\">");
        foreach (var table in tables)
        {
            var entityName = GetEntityName(table);
            sb.AppendLine($"        <EntitySet Name=\"{entityName}\" EntityType=\"{serviceNamespace}.{entityName}\" />");
        }
        sb.AppendLine("      </EntityContainer>");

        sb.AppendLine("    </Schema>");
        sb.AppendLine("  </edmx:DataServices>");
        sb.AppendLine("</edmx:Edmx>");

        return sb.ToString();
    }

    public static string GetEntityName(TableMetadata table)
    {
        var id = table.Identifier;
        return $"{id.Domain}_{id.Schema}_{id.TableName}";
    }

    public static string MapToEdmType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return "Edm.String";
        }

        var normalized = dataType.Trim().ToLowerInvariant();

        return normalized switch
        {
            "int" or "integer" or "int4" or "serial" => "Edm.Int32",
            "bigint" or "int8" or "bigserial" => "Edm.Int64",
            "smallint" or "int2" => "Edm.Int16",
            "decimal" or "numeric" or "money" => "Edm.Decimal",
            "float" or "real" or "float4" => "Edm.Single",
            "double" or "float8" or "double precision" => "Edm.Double",
            "bool" or "boolean" or "bit" => "Edm.Boolean",
            "date" => "Edm.Date",
            "timestamp" or "timestamptz" or "datetime" or "datetime2" or "datetimeoffset" => "Edm.DateTimeOffset",
            "guid" or "uuid" => "Edm.Guid",
            _ => "Edm.String"
        };
    }
}
