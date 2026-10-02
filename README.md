# GqlGateway.Extensions

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-Proprietary%20%2F%20Internal-lightgrey)](#)

Enterprise extensions, adapters, and connectors for the **GqlGateway GraphQL Enterprise Gateway**.

This library houses external integrations that connect GqlGateway with enterprise catalog systems, ITSM approval ticketing platforms, dbt transformation workflows, and legacy data protocols.

---

## 📚 Documentation Index

The documentation is cleanly organized by integration domain in the [`docs/`](file:///root/gql_extensions/docs) directory:

- 🏛️ **[Architecture Overview](file:///root/gql_extensions/docs/architecture-overview.md)**: Layering, decoupling from the core gateway host, DI lifetimes, and resilient outbound I/O.
- 🗂️ **[Enterprise Data Catalog Integration Guide](file:///root/gql_extensions/docs/data-catalog-guide.md)**: Mirror vs. Reference mode, Microsoft Purview, Collibra, Alation, OpenMetadata, and automated GDPR Art. 9 tag enforcement.
- 🔄 **[dbt Integration Guide](file:///root/gql_extensions/docs/dbt-integration-guide.md)**: `manifest.json` streaming ingestion, lineage graph synchronization, and dbt exposure publishing.
- 🎫 **[ITSM Approval & Webhook Guide](file:///root/gql_extensions/docs/itsm-webhook-guide.md)**: ServiceNow and Jira Service Management integrations, HMAC-SHA256 verification, and 5-minute replay protection.
- 📊 **[OData v4 Connector Guide](file:///root/gql_extensions/docs/odata-connector-guide.md)**: Serving CSDL metadata, Keyset paging, filter pushdown, and Power BI / Excel direct adapters.
- ❄️ **[Apache Iceberg Lakehouse Connector Guide](file:///root/gql_extensions/docs/lakehouse-connector-guide.md)**: Native in-process Iceberg & Parquet querying, partition pruning, and in-memory PII masking.

---

## 🧭 Architektur: Kern vs. Extensions

Alle Anbindungen an Fremdsysteme leben in **diesem einen Projekt** (`src/GqlGateway.Extensions`, ein Ordner je Anbindung). Der Kern (`gql/src`) enthält nur Schnittstellen (Application/Domain), Orchestrierung und Governance-Logik und bindet die Extensions genau einmal über `services.AddGatewayExtensions(gatewayOptions)` ein (aufgerufen in `AddGatewayInfrastructure`). Abhängigkeitsrichtung: Extensions → Application/Domain, Api → Extensions (durch Architekturtests abgesichert).

| Ordner | Registrierung | Inhalt | Aktivierung |
|---|---|---|---|
| `DataCatalog/` | `AddDataCatalogIntegration` | Purview, Collibra, Alation, OpenMetadata-Adapter, Factory, Sync, Katalog-Webhook | Clients/Sync/Webhook immer; Hintergrund-Sync nur bei `Gateway:Catalog:Enabled` |
| `Itsm/` | `AddItsmIntegration` | ServiceNow-/Jira-Client, ITSM-Webhook-Handler | immer (Dispatcher/Outbox-Worker bleiben im Kern, `Gateway:Itsm:Enabled`) |
| `OpenMetadata/` | `AddOpenMetadataIntegration` | `IOpenMetadataClient`, Policy-Sync | Client/Sync immer; Hintergrund-Sync nur bei `Gateway:OpenMetadata:Enabled` |
| `Dbt/` | `AddDbtIntegration` | Manifest-Ingestion, Exposures, Contracts, Webhook | immer |
| `OData/` | `AddODataIntegration` | OData-v4-Handler | immer (Endpunkte im Kern) |
| `Lakehouse/` | `AddLakehouseIntegration` | Iceberg-Reader, Storage-Provider, Executor | immer |
| `Lineage/` | `AddLineageExportIntegration` | OpenLineage-Export, OpenJEV-Klassifikator | immer (Graph-Store bleibt im Kern) |
| `Backstage/` | `AddBackstageIntegration` | Backstage-Katalog-Export, YAML-Serializer | immer (Endpunkte nur bei `Gateway:Backstage:Enabled`) |
| `Cdc/` | `AddCdcSourceIntegration` | MSSQL-Change-Tracking-Poller, Debezium-Parser | Poller-Dienst immer; Hintergrund-Polling nur bei `Gateway:MssqlChangeTracking:Enabled` |

Alle `Add*Integration`-Methoden sind öffentlich, idempotent und hängen jeden ausgehenden HttpClient an den `SsrfProtectionHandler` (`GqlGateway.Application.Security`).

---

## 📦 Included Extensions

### 1. Enterprise Data Catalogs (`DataCatalog/`)
One client per catalog behind `IDataCatalogClient`, selected by `DataCatalogClientFactory` via `Gateway:Catalog:Provider`, and a single `DataCatalogSyncService` (Mirror sync into the governance repository, governance ratchet `CatalogGovernanceRatchet` from the core, epoch invalidation):
- **Microsoft Purview** (`PurviewDataCatalogClient`): Atlas search API with OAuth2 client-credentials token.
- **Collibra** (`CollibraDataCatalogClient`): REST Core API v2 with bearer token or basic auth.
- **Alation** (`AlationCatalogClient`): Integration API v2 with `TOKEN` header (optionally resolved via `IKeyVaultSecretProvider`), errors are propagated.
- **OpenMetadata** (`OpenMetadataCatalogAdapter`): reuses the hardened `IOpenMetadataClient` (paging, secret provider, bounded reads).
- All catalog responses are read with a 10 MB cap (`CatalogHttpContent`), all HttpClients run through the core `SsrfProtectionHandler`.
- **GDPR Art. 9 enforcement**: tables tagged with `Catalog:GdprArticle9Tags` get `Sensitivity = "HIGH"` and `RequiresFourEyes = true`; column masking via `Catalog:TagToMaskingRuleMap`. Existing protection is never weakened by a sync.
- Real-time webhooks (`CatalogWebhookHandler`) with timestamp-bound HMAC signatures and replay protection.

### 2. dbt Integration (`Dbt/`)
- Ingestion of dbt `manifest.json` artifacts via [`DbtMetadataIngestionService`](file:///root/gql_extensions/src/GqlGateway.Extensions/Dbt/DbtMetadataIngestionService.cs).
- Extracts models, seeds, column PII classifications, and dependency graphs into the gateway's [`LineageGraphStore`](file:///root/gql/src/GqlGateway.Application/Interfaces/ILineageGraphStore.cs).
- Zero-Trust proposal approval lifecycle (`/api/extensions/dbt/proposals`) with automatic column masking rule synchronization and policy epoch invalidation.
- dbt Model Contract Enforcement & Breaking Change CI Gate via [`DbtContractValidator`](file:///root/gql_extensions/src/GqlGateway.Extensions/Dbt/DbtContractValidator.cs) (`POST /api/extensions/dbt/validate-contract`).
- Automated exposure publishing via [`DbtExposurePublisher`](file:///root/gql_extensions/src/GqlGateway.Extensions/Dbt/DbtExposurePublisher.cs) (`GET /api/extensions/dbt/exposures`).

### 3. ITSM Approval Workflows (`Itsm/`)
- Outbound clients `ServiceNowTableApiClient` (Table API) and `JiraCloudRestClient` (REST v3, ADF payload) with basic auth from `Gateway:Itsm:*`; no client-side retries (the core outbox dispatcher owns retries).
- Inbound `ItsmWebhookHandler`: per-instance HMAC-SHA256 secrets, 5-minute timestamp window and replay cache (`ItsmWebhookReplayCache`).

### 4. OData v4 Data Source (`OData/`)
- Declarative OData connector enabling GraphQL queries over SAP and Microsoft OData v4 services with filter pushdown and keyset paging.

### 5. Apache Iceberg Lakehouse Connector (`Lakehouse/`)
- In-process execution of queries on Apache Iceberg v2 tables and Parquet files in Object Storage via [`CompositeLakehouseStorageProvider`](file:///root/gql_extensions/src/GqlGateway.Extensions/Lakehouse/Services/CompositeLakehouseStorageProvider.cs) (AWS S3 SigV4, Azure ADLS Gen2 / Blob, MinIO, Local).
- Vectorized partition pruning and Min/Max column statistics skipping via [`IcebergPartitionPruner`](file:///root/gql_extensions/src/GqlGateway.Extensions/Lakehouse/Services/IcebergPartitionPruner.cs) (up to 95% I/O reduction).
- Zero-Trust tenant isolation and in-memory PII / GDPR Art. 9 masking.

---

## 🚀 Building & Testing

```bash
# Build the extensions solution
dotnet build GqlExtensions.slnx -c Release

# Run automated tests (43 / 43 tests green)
dotnet test GqlExtensions.slnx -c Release
```


*Note: Enforces `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (0 warnings, 0 errors).*
