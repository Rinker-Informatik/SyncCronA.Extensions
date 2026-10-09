# SyncCronA Application Agent

.NET-10-Library für Anwendungen, die registrierte Handler durch den SyncCronA Core ausführen lassen.
Die startbare Referenzanwendung liegt unter `SyncCronA.ApplicationAgent.Tests/TestWebApi`.

```bash
dotnet add package SyncCronA.Extensions
```

Das NuGet-Paket heißt `SyncCronA.Extensions`; die C#-Namespaces heißen weiterhin
`SyncCronA.ApplicationAgent.*`.

## Integration

```csharp
using SyncCronA.ApplicationAgent.DependencyInjection;

builder.Services.AddSyncCronA(builder.Configuration)
    .AddHandler<CleanupHandler>("cleanup");

public sealed class CleanupHandler : ISyncCronAHandler
{
    public Task ExecuteAsync(
        SyncCronA.ApplicationAgent.Runtime.ExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Businesslogik; context.ExecutionId zur Korrelation bzw. Idempotenz verwenden.
        return Task.CompletedTask;
    }
}
```

`AddSyncCronA` bindet den Abschnitt `ApplicationAgent`. Benötigt werden `ControlPlaneUrl` (HTTPS),
`ApplicationId` und das zum Enrollment gehörende `BootstrapSecret`. `ApplicationName` setzt den
lesbaren Namen der Application im Core-Dashboard bei der authentifizierten Registrierung. Optional:
`Environment`, `ControlPlaneServerCertificatePem`, `HeartbeatInterval` und `RetryInterval`.
Die Instanz-ID wird bei jedem Prozessstart neu erzeugt.
`AutoApproveEnrollment` ist standardmäßig aus und nur für die lokale Testanwendung gedacht.

Handler-Namen sind explizit, unterscheiden Groß-/Kleinschreibung und dürfen pro Anwendung nur
auf einen Handler zeigen. Doppelte Namen werden bei der Service-Registrierung abgewiesen.
Jede Ausführung erhält einen eigenen asynchron freigegebenen DI-Scope. Es gibt kein konfiguriertes
Parallelitätslimit. Handler erhalten zunächst keine Eingabeparameter und keinen Rückgabewert:
erfolgreiche Rückkehr bedeutet `Success`, eine Exception `Failed`.

## Verbindung und Ausführung

- Der Agent führt das bestehende Enrollment aus und verbindet sich über mTLS/gRPC (Protokoll v2).
- Er meldet Handler, Version und Environment und sendet Heartbeats.
- Der Core hält `AgentId → AgentConnection` für aktuell offene Streams im Speicher.
  Die Connection ist keine zusätzliche fachliche Entität. Handler und Health-Zustand liegen an
  der bestehenden Application-Instanz; Agent-ID, Instanzschlüssel und Datenbank-Instanz-ID sind verschieden.
- Pro Stream schreibt genau eine Sendeschleife. Empfang und Handler-Ausführung laufen unabhängig davon.
- `Started`, `Running` und `Success`/`Failed` tragen eine Sequenznummer und werden erst nach
  Datenbankspeicherung bestätigt. Fehlertexte werden auf die 8000 Zeichen des Datenbankfelds begrenzt.
- Bei Disconnect laufen Handler weiter. Unbestätigte Meldungen werden nach Reconnect in Reihenfolge
  nachgeliefert. Wiederholte Aufträge derselben Execution-ID starten im selben Agent-Prozess nicht erneut.
- Beim Host-Shutdown erhalten Handler Cancellation. Innerhalb des Host-Shutdown-Zeitfensters wartet
  der Agent auf Handler-Abschluss und Ergebnisbestätigungen; eine Cancellation muss vom Handler beachtet werden.

## Grenzen dieses Ausbaus

Der Core läuft als einzelne Instanz; die Verbindungsverwaltung liegt im Prozessspeicher.
Die Auswahl erfolgt per Round-Robin unter verbundenen Instanzen, die den Handler anbieten und deren
letzter Heartbeat höchstens 60 Sekunden alt ist. Ein Hintergrunddienst markiert abgelaufene
Verbindungen alle fünf Sekunden offline. Das Heartbeat-Intervall sollte deutlich unter 60 Sekunden liegen.

Ergebnispuffer und Duplikaterkennung überleben keinen Agent-Prozessabsturz. Die Duplikaterkennung
behält die Execution-IDs für die Prozesslaufzeit. Eine bereits zugeordnete Execution wird bei unklarem
Versand oder Disconnect nicht automatisch neu verteilt. Nach einem Prozessabsturz kann sie ohne
Endergebnis verbleiben. Automatische Retries, dauerhafte Outbox und Timeout-Reconciliation folgen separat.
Es gibt keine Exactly-once-Garantie. Logs enthalten Execution-ID, Handler und Instanz; zentrales
Log-Streaming gehört noch nicht zum Funktionsumfang.

Core und Agent müssen für Protokoll v2 gemeinsam aktualisiert werden. Protokoll v1 wird abgewiesen.
Für PostgreSQL ist vor dem Core-Start die Migration `ApplicationHandlerExecution` anzuwenden.

Zum lokalen Nachvollziehen: [Beispiel und manuelle Ausführung](../SyncCronA.ApplicationAgent.Tests/TestWebApi/README.md).
