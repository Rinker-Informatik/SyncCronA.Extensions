# Application-Agent-Test-Web-API

Diese Beispielanwendung integriert die Agent-Library über `AddSyncCronA` und bietet drei Handler an:
`cleanup` (Erfolg), `failure` (Exception) und `slow` (zehn Sekunden Laufzeit, Cancellation-fähig).
Sie registriert sich im Core-Dashboard als „SyncCronA Test Web API“.

## Lokaler End-to-End-Ablauf

Alle Befehle werden im Verzeichnis `src/backend/SyncCronA` ausgeführt. Einmalig das
Entwicklungszertifikat vertrauen:

```bash
dotnet dev-certs https --trust
```

Den Core in einem eigenen Terminal starten. Im Development-Modus verwendet er die vorhandene
In-Memory-Datenbank und temporäre Client-CA:

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile --project SyncCronA.Core --urls https://localhost:7255
```

In zwei weiteren Terminals je eine Instanz starten. Die getrennten Instance-IDs sind erforderlich:

```bash
ASPNETCORE_ENVIRONMENT=Development \
ApplicationAgent__InstanceId=11111111-1111-1111-1111-111111111111 \
dotnet run --no-launch-profile --project SyncCronA.ApplicationAgent.Tests/TestWebApi --urls http://localhost:5180
```

```bash
ASPNETCORE_ENVIRONMENT=Development \
ApplicationAgent__InstanceId=22222222-2222-2222-2222-222222222222 \
dotnet run --no-launch-profile --project SyncCronA.ApplicationAgent.Tests/TestWebApi --urls http://localhost:5181
```

Die Beispielkonfiguration aktiviert nur unter Development die automatische Enrollment-Freigabe.
Über `GET http://localhost:5180/status` bzw. Port 5181 warten, bis beide Instanzen `Connected` melden.
Weitere Beispielendpunkte sind `/health` und `/api/test/ping`.

Einen Handler manuell starten:

```bash
curl --request POST https://localhost:7255/api/applications/5b6d7657-1cfd-48ca-a8f6-d1db831f5964/handlers/cleanup/executions
```

Die Antwort ist `202 Accepted` mit `executionId` und einem `Location`-Header. Das Ergebnis abrufen
(`EXECUTION_ID` durch die zurückgegebene ID ersetzen):

```bash
curl https://localhost:7255/api/executions/EXECUTION_ID
curl https://localhost:7255/api/applications/5b6d7657-1cfd-48ca-a8f6-d1db831f5964/executions
```

Die Historie liefert die neuesten 100 Ausführungen mit Instanzzuordnung, Status, Zeitstempeln,
Dauer und Fehlerdetails. Wiederholte Starts verteilen sich abwechselnd auf geeignete Instanzen.
Mit `failure` wird ein Fehler sichtbar; `slow` erlaubt das Beobachten einer laufenden Ausführung.
Weitere Handler können währenddessen auf derselben Instanz starten.

Fehlt eine verbundene gesunde Instanz mit dem Handler, liefert der Start `409 Conflict`.
Die drei Verwaltungsendpunkte sind außerhalb von Development nicht registriert (`404`).
Ein HTTP-Request erzeugt jeweils eine neue Execution; manuelles Wiederholen des POST ist kein Retry
mit derselben Execution-ID.

## PostgreSQL und Container

Bei PostgreSQL zunächst die Migration anwenden; die Verbindung wird über
`SYNCCRONA_CONNECTION_STRING` für das EF-Tool konfiguriert:

```bash
dotnet ef database update --project SyncCronA.Core.Persistence
```

Das Dockerfile der Library baut die ausführbare Beispielanwendung. Build-Kontext ist
`src/backend/SyncCronA`:

```bash
docker build -f SyncCronA.ApplicationAgent/Dockerfile -t synccrona-application-example .
```

Für den Betrieb sind erreichbare Control-Plane-URL, IDs, Secret und gegebenenfalls Server-CA
über `ApplicationAgent__...` zu konfigurieren. Die Containerkonfiguration aktiviert keine
automatische Freigabe. Details und Grenzen stehen in der [Library-Dokumentation](../../SyncCronA.ApplicationAgent/README.md).

## Tests

```bash
dotnet test SyncCronA.ApplicationAgent.Unittests
dotnet test SyncCronA.Core.Unittests
dotnet test SyncCronA.Core.ApplicationAgent.IntegrationTests
dotnet test SyncCronA.Core.Persistence.IntegrationTests
```

Die PostgreSQL-Tests benötigen Docker. Die mTLS-Integrationstests erzeugen eigene temporäre
Zertifikate und prüfen insbesondere parallele Ausführung und Ergebnisübermittlung nach Disconnect.

## Logging

Core und TestWebApi verwenden Serilog für `ILogger`-Meldungen und HTTP-Request-Logging.
Die Konsolenausgabe enthält Anwendung, Logger-Kategorie, zusätzliche strukturierte Eigenschaften
(z. B. `ExecutionId`, Handler und Instanz) sowie Exceptions.
Die Log-Level stehen im Abschnitt `Serilog:MinimumLevel` der jeweiligen `appsettings.json`.
Beispiel für ausführlichere lokale Logs: `Serilog__MinimumLevel__Default=Debug`.

## Swagger UI im Core

Beim lokalen Core-Start im Development-Modus öffnet sich auf macOS automatisch Google Chrome
mit `https://localhost:7255/swagger/index.html` (bei anderer Start-URL entsprechend dort).
Unter **Executions** den POST-Endpunkt aufklappen, die Application-ID
`5b6d7657-1cfd-48ca-a8f6-d1db831f5964` und `cleanup`, `failure` oder `slow` eintragen und
**Execute** anklicken. Die TestWebApi muss verbunden sein. Mit der zurückgegebenen
`executionId` lässt sich das Ergebnis über den GET-Endpunkt abrufen.

Swagger ist ausschließlich in Development verfügbar. Für Tests oder einen Start ohne Browser:
`Swagger__OpenInChrome=false`. Auf anderen Betriebssystemen die Swagger-URL manuell öffnen.
