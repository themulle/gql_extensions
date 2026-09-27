# GqlGateway.Extensions

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-Proprietary%20%2F%20Internal-lightgrey)](#)

Enterprise extensions, adapters, and connectors for the **GqlGateway GraphQL Enterprise Gateway**.

This library houses external integrations that connect GqlGateway with enterprise catalog systems, ITSM approval ticketing platforms, dbt transformation workflows, and legacy data protocols.

---

## 📦 Included Extensions

### 1. Enterprise Data Catalogs (`DataCatalog/`)
Unified multi-catalog synchronization supporting both **Mirror** (persistent SQLite ingestion) and **Reference** (federated on-demand lookup) modes:
- **Microsoft Purview**: Apache Atlas REST client for synchronizing Azure-native data assets, glossary terms, classifications, and contact owners.
- **Collibra Data Intelligence Cloud**: REST Core API v2 integration for enterprise data governance assets, domains, and communities.
- **Alation**: Integration API v2 client for catalog tables, custom fields, and steward assignments.
- **OpenMetadata**: Real-time webhook and batch sync client mapping OpenMetadata entities to GqlGateway governance schemas.
- **Automated GDPR Art. 9 Enforcement**: Automatic detection of sensitive categories (health, genetic, biometric, religious, political) enforcing `Sensitivity = "HIGH"`, mandatory four-eyes approval (`RequiresFourEyes = true`), and redaction (`REDACT` with `[REDACTED-GDPR-ART9]`).
- **PII Tag Mapping**: Automatic mapping from catalog tags to column masking algorithms (`MASK_EMAIL`, `HMAC_SHA256`, `REDACT`).

### 2. dbt Integration (`Dbt/`)
- Ingestion of dbt `manifest.json` and `catalog.json` artifacts via `DbtManifestIngestService`.
- Extracts models, sources, tests, column classifications, and exposure lineage directly into the gateway's graph store.

### 3. ITSM Approval Workflows (`Itsm/`)
- Inbound webhook handlers for **ServiceNow** and **Jira Service Management**.
- Secures two-phase approval workflows for sensitive data access requests.
- Protected by timing-safe HMAC-SHA256 signature verification and 5-minute replay prevention.

### 4. OData v4 Data Source (`OData/`)
- Declarative OData connector enabling GraphQL queries over SAP and Microsoft OData v4 services with filter pushdown and keyset paging.

---

## 🚀 Building & Testing

```bash
# Build the extensions solution
dotnet build GqlExtensions.slnx -c Release

# Run automated tests (24 / 24 tests green)
dotnet test GqlExtensions.slnx -c Release
```

*Note: Enforces `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (0 warnings, 0 errors).*
