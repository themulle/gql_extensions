# Security Review – gql_extensions, Stand 2026-10-02

**Scope:** `gql_extensions/src/GqlGateway.Extensions` (Lakehouse, OData, Dbt, DataCatalog, OpenMetadata, Itsm, DI-Registrierung) auf dem Branch `security/review-2026-10-02`, inkl. der Fixes aus dem Gateway-Review (H-18, H-19, H-20, M-32 bis M-35). Kerncode im Repo `gql` nur so weit, wie er für Datenfluss und Ausnutzbarkeit nötig ist.
**Methode:** statische Whitebox-Analyse, Datenfluss vom Fremdsystem bzw. Request bis in Governance und Ausgabe; keine Laufzeit-PoCs.

---

## 1. Kernaussage

Die Fixes aus der ersten Runde halten:

- Azure-Signatur nur für den konfigurierten Account (H-18)
- S3-Host-/Bucket-/Präfix-Prüfung (M-33)
- dbt-Dateinamen und Direktiven (H-20)
- Webhook-HMAC mit Timestamp (M-34)
- Byte- und Tiefenlimits, Symlink-Abwehr

Neue Schwerpunkte:

1. **OpenMetadata als Vertrauensanker für Zugriffsrechte:** `AutoCreateConsents` ist jetzt eine in Produktion erlaubte WARN-Option. Damit kann ein OM-Admin Gateway-Zugriff ohne Four-Eyes vergeben, auch auf Art.-9-Tabellen, und das über Identitäten, die aus OM-Namen abgeleitet werden (EX-01).
2. **Reconcile und Mandanten-Zuordnung beim OM-Sync sind unvollständig** (EX-02, EX-03, EX-06).
3. **Lakehouse-RLS wird auf bereits maskierten Werten ausgewertet** (EX-04).
4. **Mehr als die Hälfte des Extension-Codes ist nicht aktiv:** Katalog-Sync, Katalog-Clients, Jira/ServiceNow-Clients. Der Kern registriert eigene Implementierungen zuerst (`TryAdd`). Fixes in diesem Code wirken deshalb nicht, und er enthält Altlasten (EX-15).

| Schweregrad | Anzahl |
|---|---|
| 🔴 Kritisch | 0 |
| 🟠 Hoch | 1 |
| 🟡 Mittel | 7 |
| 🟢 Niedrig | 6 |
| ℹ️ Info | 4 |

---

## 2. Welcher Code ist aktiv?

| Komponente | Status | Grund |
|---|---|---|
| `OpenMetadataClient`, `OpenMetadataSyncService`, `OpenMetadataSyncBackgroundService` | **aktiv** | |
| `CatalogWebhookHandler` | **aktiv** | ruft aber den **Kern**-`DataCatalogSyncService` auf |
| Lakehouse (Storage-Provider, Iceberg-Reader/Pruner, Executor) | **aktiv** | Executor liefert derzeit Beispieldaten plus Partitionswerte |
| OData-Handler/CSDL, Dbt (Webhook, Parser, Ingestion, Contract/Exposure) | **aktiv** | |
| Extension-`DataCatalogSyncService`, Collibra/Purview/Alation-Clients, `OpenMetadataCatalogAdapter` | **inaktiv** | Kern registriert `IDataCatalogSyncService` vorher per `TryAddScoped` (Core `GatewayServiceCollectionExtensions.cs:283`) |
| Extension-`JiraClient`/`ServiceNowClient` | **praktisch inaktiv** | `ItsmWorkflowDispatcher` nimmt per `FirstOrDefault` die zuerst registrierten Kern-Clients |

---

## 3. Befunde

### EX-01 🟠 OM-Auto-Consents ohne Four-Eyes; Grantee-Identität aus Namen, die OM kontrolliert
`OpenMetadata/OpenMetadataSyncService.cs:186, 290-308, 655-659, 741-755`
```csharp
if (primaryName.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
{ sid = new Sid(primaryName); return true; }
...
AddDesired(CreateConsentForRole(role.Name, tableMeta, effect));
```
Bei `AutoCreateConsents=true` entstehen Allow-Consents für ein Jahr, ohne jede Freigabe. Vier Punkte kommen zusammen:
1. Ein OM-User oder -Team, dessen **Name** mit `S-1-` beginnt, wird ohne Mapping als Gateway-SID übernommen.
2. Jede OM-Rolle wird 1:1 zu einem Gateway-Role-Claim, ohne Allowlist und auch ohne zugeordnete User.
3. `UserToUserSidMap` wird per Name **oder** E-Mail und case-insensitiv abgefragt. Ein OM-User mit dem *Namen* einer gemappten E-Mail erbt deren SID.
4. `RequiresFourEyes`, `HIGH` und Art. 9 werden nicht ausgenommen. Der Kern erzwingt Four-Eyes nur im Antragsweg, nicht bei der Consent-Auswertung.

**Angriff:** Ein OM-Admin (oder ein kompromittiertes OM) legt eine Rolle mit `ViewAll` auf `table` an und erstellt einen User mit dem Namen einer Ziel-SID. Nach dem nächsten Sync (oder einem signierten `role`/`user`-Webhook) erhält diese SID Allow auf alle synchronisierten Tabellen.
**Fix:**
- S-1-Fallback und Lookup über den Namen entfernen, nur explizite Maps (`UserToUserSidMap` per stabiler OM-ID oder E-Mail).
- Neue `RoleToGatewayRoleMap` als Allowlist.
- Tabellen mit `RequiresFourEyes`, `IsHighlySensitive` oder Art. 9 nie automatisch freigeben, dafür einen ConsentRequest im Four-Eyes-Workflow erzeugen.
- Optional eine Allowlist von Tabellen bzw. Domains für Auto-Consents.
- Entscheidung nötig: Solange diese Fixes fehlen, sollte `AutoCreateConsents` als **DANGER** eingestuft werden.

### EX-02 🟡 Reconcile widerruft veraltete Sync-Consents nicht zuverlässig
`OpenMetadataSyncService.cs:121-129, 266-280, 321-346`
- **Ursache:** Widerrufen wird nur für Grantees und Tabellen, die aktuell in OM existieren.
- **Folge:** Folgende Consents bleiben bis zu ein Jahr gültig:
  - Consents gelöschter Rollen und gelöschter User (inkl. S-1-Fallback).
  - Consents gelöschter oder herausgefilterter Tabellen.
  - Alle bisherigen Allow-Consents nach dem Abschalten von `AutoCreateConsents`.
- Jede Exception im Policy-, Rollen- oder User-Block bricht den Reconcile still ab, der Sync meldet trotzdem `Success=true`. Auslöser sind z. B. OM-5xx, eine Users-Seite über 10 MB (kann ein OM-Admin herbeiführen) oder `ToDictionary` bei Namen, die sich nur in der Groß-/Kleinschreibung unterscheiden.
- **Fix:**
  - Alle Consents mit dem Sync-Marker laden, ohne Filter auf Subjekt oder Tabelle, und alles widerrufen, was nicht gewünscht ist.
  - Fehler als `Success=false` melden und alarmieren.
  - Kurze Gültigkeit (z. B. 2× Sync-Intervall, bei jedem Lauf verlängert), damit Fehler den Zugriff schließen statt offen lassen.
  - Beim Abschalten der Option die Marker-Consents mit Allow widerrufen.

### EX-03 🟡 OM-Consents ohne Mandant → Deny-Regeln wirkungslos im Mandantenbetrieb
`OpenMetadataSyncService.cs:280, 757-770`; Kern `GatewayExecutionService.cs:179`
- **Ursache:** `CreateConsentFor*` setzt keinen `TenantId`, der Wert bleibt `LegacySingleTenant`. Die Auswertung filtert `tenant_id = @tenantId`.
- **Folge:** Deny-Consents aus OM, die auch ohne AutoCreate angelegt werden, greifen für Nicht-Legacy-Mandanten **nie**. Das ist fail-open für einen Schutzmechanismus.
- **Fix:** Ziel-Mandant per Konfiguration pro OM-Service bzw. Datenbank. Fehlt er, Import ablehnen. Reconcile und Dedup pro Mandant ausführen.

### EX-04 🟡 Lakehouse: Consent-Zeilenfilter wird auf maskierten Werten ausgewertet
`Lakehouse/Services/LakehouseDataSourceExecutor.cs:65-90`; Kern `GatewayExecutionService.cs:370-373`
- **Ursache:** Der Executor maskiert Werte, ohne `RlsPushdownExecuted` bzw. `InDbColumnMaskingExecuted` zu setzen. Die Pipeline wertet danach `CombinedRowFilterSql` per `DataTable.Select` gegen die **maskierten** Werte aus (REDACT → `"REDACTED"` bzw. `0`).
- **Folge:** Regeln mit Negation oder Bereichsvergleich auf einer für den Nutzer maskierten Spalte (`region <> 'US'`, `band < 3`) werden wahr. Die eigentlich ausgeschlossenen Zeilen werden ausgeliefert, mit den übrigen Spalten im Klartext. Bei Deny-Spalten (NULL) ist das Verhalten fail-closed.
- **Fix:** Der Executor liefert Rohwerte und überlässt das Maskieren der zentralen Pipeline. Oder er wertet RLS selbst vor dem Maskieren aus und setzt die Flags.

### EX-05 🟡 OData `$metadata` / Service-Dokument zeigen gesperrte Tabellen und Spalten
`OData/ODataHandler.cs:40-54`, `OData/ODataCsdlGenerator.cs:308-339`
- **Ursache:** Gefiltert wird nur nach Domain bzw. Tenant (bei `LegacySingleTenant` alle Domains), nicht nach Consent oder Spaltenzugriff.
- **Folge:** Jeder angemeldete Nutzer sieht das vollständige Schema inkl. Deny-Spalten und Beschreibungen. Das ist inkonsistent zu `$openapi` (dort sind Rollen Pflicht).
- **Fix:** Zugriffsentscheidung pro Tabelle auflösen, nicht erlaubte Tabellen und Deny-Spalten weglassen. Alternativ `$metadata` hinter dieselbe Rolle wie `$openapi`.

### EX-06 🟡 OM-Import: Datenbank fällt aus der Identität, ServiceFilter im Webhook-Pfad umgangen
`OpenMetadataSyncService.cs:477-488, 531-535`
```csharp
var domain = table.Service?.Name ?? table.Database?.Name;
var schema = table.DatabaseSchema?.Name;   // Database wird ignoriert
```
- **Kollision:** `svc.sandbox.public.users` und `svc.prod.public.users` bilden dieselbe Gateway-Tabelle. Ein Sandbox-Owner kann DisplayName und DataType der Prod-Tabelle setzen und Phantomspalten anlegen. Zusammen mit EX-01 entstehen Consents auf die Prod-Identität.
- **Webhook-Pfad:** Er importiert jede FQN ohne `ServiceFilter`. Neue Tabellen werden mit `IsActive=true` und `NORMAL` angelegt.
- **Fix:**
  - Datenbank in die Zuordnung aufnehmen.
  - Explizite Map von OM-Service/Datenbank auf Gateway-Domain; Tabellen ohne Mapping ablehnen.
  - ServiceFilter auch im Webhook-Pfad anwenden.
  - Neue Tabellen optional mit `IsActive=false` anlegen, bis ein Gateway-Admin sie freigibt.

### EX-07 🟡 Prompt-Injection über Katalog-/OM-Metadaten in MCP-Agentenbeschreibungen
`OpenMetadataSyncService.cs:577, 625-626`; Kern `DataCatalogSyncService.cs:221, 254` (Ratchet), `SemanticMcpCompiler.cs:40-46, 91, 138`
- **Ursache:** DisplayName, Spaltennamen und DataType aus OM werden ungeprüft übernommen. Der Ratchet **überschreibt** den vom Gateway gepflegten DisplayName.
- **Folge:** Die Werte landen in MCP-Tool-Descriptions, im Glossar und im JSON-Schema. Anführungszeichen in Domain bzw. Tabellenname brechen den per String gebauten `TargetGraphQLOperation`. SQL bleibt geschützt, weil `QuoteIdentifier` validiert.
- **Angreiferkreis:** Data Stewards bzw. Table-Owner in OM, ein deutlich größerer Kreis als OM-Admins.
- **Fix:**
  - Gateway-DisplayName nicht überschreiben; sonst Länge und Zeichensatz begrenzen.
  - Spaltennamen per `ValidateIdentifier` prüfen, DataType gegen eine Allowlist.
  - Den GraphQL-Operationstext mit Variablen statt Interpolation bauen.
  - MCP-Metadaten als Daten kennzeichnen.

### EX-08 🟡 dbt `/sync`: DataOwner ändern ohne Review globale Metadaten, Lineage und SQL-Endpoints
Kern `DbtEndpoints.cs:24-27`; `Dbt/DbtMetadataIngestionService.cs:322-408`
- **Ursache:** Der Sync schreibt Beschreibungen und `Meta` **beliebiger** Tabellen, ohne Domain-Ownership des Aufrufers zu prüfen. Er aktualisiert den globalen Lineage-Graph und legt bei `AutoSyncFromDbt` SQL-Endpoints gegen eine frei wählbare `database` an bzw. überschreibt dbt-generierte Endpoints anderer Teams.
- **Folge:** Beschreibungen und `Meta` fließen in MCP und OpenAPI, das ist ein Weg für Prompt-Injection (vgl. EX-07). Bei Freigaben wurde unter M-11 dagegen festgelegt: nur globale Admins.
- **Fix:**
  - Domain-Ownership pro Modell prüfen.
  - Metadaten-Änderungen als Vorschläge mit Review führen.
  - `dataSource` gegen eine Positivliste prüfen.
  - Sync nur für globale Admins oder nur für eigene Domains.

---

## 4. Niedrig

| ID | Befund | Fundstelle | Fix |
|---|---|---|---|
| EX-09 | Lakehouse-Tenant-Nachweis über min/max-Bounds akzeptiert Mischdateien (`[acme, zeta]` passt auf `beta`). Die Tenant-Spalte wird mit dem Aufrufer-Tenant **überschrieben** statt geprüft. Der Pruner vergleicht `OrdinalIgnoreCase`, Iceberg sortiert Bounds aber binär. Wirkt voll erst mit echtem Scan. | `IcebergPartitionPruner.cs:84-91, 160`; `LakehouseDataSourceExecutor.cs:259-270` | Nur `lower == upper == tenant` oder Partitionswert gelten lassen; zeilenweise filtern; ordinal vergleichen; Tenant-Spalte prüfen statt setzen |
| EX-10 | S3/Azure: `?` bzw. `#` im Objektschlüssel aus Manifesten wird zur signierten Subresource-Anfrage (`?versionId=`, `?acl`, `comp=list`) | `S3LakehouseStorageProvider.cs:204, 279`; `AzureBlobStorageProvider.cs:197` | `?`, `#` und `%` ablehnen oder Segmente per `EscapeDataString` kodieren; Query/Fragment in `ValidateManifestLocation` ablehnen |
| EX-11 | Präfix-Ableitung `GetUriTableDirectory` zu großzügig: leeres Präfix gibt den ganzen Bucket frei, Verzeichnis mit Punkt gilt als Datei | `IcebergMetadataReader.cs:377-383, 463-482` | Leeres Präfix ablehnen; Tabellenverzeichnis explizit konfigurieren |
| EX-12 | HttpClients: Redirects nicht abgeschaltet, `SsrfProtectionHandler` (DelegatingHandler) sieht Redirect-Ziele und DNS-Rebinding nicht. `IsAmazonS3Host` akzeptiert jeden `*.amazonaws.com`-Host. Der Typed-Client `IOpenMetadataClient` läuft ganz **ohne** SSRF-Handler (Handler hängt am Namen „OpenMetadataClient“). | Kern `GatewayServiceCollectionExtensions.cs:512-537`; `ExtensionsServiceCollectionExtensions.cs:51`; `S3LakehouseStorageProvider.cs:163-175` | `SocketsHttpHandler` mit `AllowAutoRedirect=false` und IP-prüfendem `ConnectCallback`; Handler an der Extension-Registrierung anhängen; S3-Host-Regex |
| EX-13 | `Dbt.danger_bypass_webhook_signature_validation` fehlt in `IsWebhookSignatureBypassed`, also kein Prod-Block, keine Health-Meldung. Der Endpoint ist anonym. | `DbtWebhookReceiver.cs:34`; Kern `GatewayOptions.cs:80` | In `IsWebhookSignatureBypassed` aufnehmen (DANGER) |
| EX-14 | Webhooks: Katalog nutzt bei leerem `Catalog.WebhookSecret` das OM-Secret (Cross-Endpoint-Replay, besonders im Legacy-Modus). Dedup nur lokal im Prozess und **vor** der Verarbeitung (Retry nach Fehler wird verworfen). Keine Obergrenze für Tabellen pro Payload. Jedes OM-Policy/Role/User-Event startet einen vollen Sync. EventGrid-Handshake beantwortet unsignierte Anfragen. | `CatalogWebhookHandler.cs:34, 79-144`; `OpenMetadataSyncService.cs:454, 493-499`; Kern `WebhookEndpoints.cs:166-188` | Secret pro Quelle; verteilte Dedup (Redis), Eintrag erst nach Erfolg; Payload-Limit; Sync-Trigger entprellen; Handshake nur mit konfiguriertem Validierungs-Token |

---

## 5. Info

- **EX-15 Inaktiver Code mit Altlasten** (siehe Abschnitt 2). Katalog-Clients: kein Authorization-Header, `ReadFromJsonAsync` ohne Größenlimit, Fehler werden zu leerer Liste, unbegrenzt wachsender statischer Cache. Jira/ServiceNow: keine Authentifizierung, Payload passt zu keiner der APIs, POST wird dreimal wiederholt (doppelte Tickets), erfundene IDs in Development. **Empfehlung: entfernen** oder als bewusst inaktiv markieren. Der M-32-Fix im Extension-Sync wirkt nicht; wirksam ist der Kern-Ratchet.
- **EX-16 OM-Token im Log:** Steht in `AuthToken` ein Roh-Token statt einer Referenz, landet er über die Exception-Message des Secret-Providers im Log. Fix: Exception ohne Message loggen, Referenzformat (z. B. `kv:`) erzwingen.
- **EX-17 Filter-Orakel über Partitionstransformationen:** Filter auf ein Partitionsfeld wie `email_trunc` steht nicht im Katalog und gilt als Clear. Erreichbar derzeit nur über MCP-`queryArguments`. Fix: nur Katalogspalten zulassen.
- **EX-18 Sonstiges:**
  - dbt-RLS/Casbin-Vorschläge werden bei Freigabe als unbekannte MaskingRule gespeichert (fail-closed REDACT, aber keine Zeilenregel); abgelehnte Vorschläge lassen sich erneut freigeben.
  - `Lakehouse.MaxScanRowsLimit` wird nicht ausgewertet.
  - `LocalStorageProvider.OpenReadStreamAsync` liest ohne Größenlimit (ungenutzt).
  - TOCTOU zwischen Symlink-Prüfung und Öffnen.

---

## 6. Geprüft ohne Befund

- **H-18:** exakter Azure-Host; `@`, Punkt am Hostende, Port und http abgewiesen; Composite-Routing per geparstem Host.
- **M-33:** `%2e%2e`, `%2f`, Backslash und doppeltes `@` abgedeckt; `s3a`/`abfss`/`wasbs` fail-closed.
- **H-20:** Name-Regex, gequotete Identifier, keine `-- @`-Direktiven, Überschreiben nur mit Marker.
- **M-34 dbt:** `FixedTimeEquals`, Timestamp und EventId Pflicht, 5-min-Fenster.
- **Grenzen:** 64-MB-Read, JSON-`MaxDepth` 64 bei Iceberg und dbt.
- **OM-Client:** Paging-Cursor escaped, keine `next`-URLs aus Antworten verfolgt, begrenztes Lesen.
- **CSDL:** per `SecurityElement.Escape` maskiert; `$top` geklemmt; `$select` per Regex.
- **DI-Lifetimes:** Singletons ohne Scoped-Abhängigkeiten; keine Secrets in Logs außer EX-16.
- **Ratchet (Kern):** Sensitivity, FourEyes, IsActive, IsSensitive und Masking nur verschärfend; Datenquellen-Felder bleiben erhalten; Owner, Delegates und Approver werden nicht importiert.

---

## 7. Empfohlene Reihenfolge

1. **Sofort:** EX-01 (S-1-Fallback und Lookup über den Namen entfernen, Four-Eyes-Tabellen ausnehmen); bis dahin `AutoCreateConsents` nicht aktivieren bzw. als DANGER führen. EX-13 (dbt-Bypass in die Bypass-Liste).
2. **Kurzfristig:** EX-02 (Reconcile), EX-03 (Mandant), EX-04 (Lakehouse-RLS vor dem Maskieren), EX-06 (OM-Identität mit Datenbank, ServiceFilter im Webhook).
3. **Mittelfristig:** EX-05 (OData `$metadata` nach Rechten), EX-07/EX-08 (Metadaten-Vertrauensgrenze zu MCP, dbt-Ownership), EX-12 (HttpClient-Härtung).
4. **Aufräumen:** EX-15 (inaktiven Extension-Code entfernen).
