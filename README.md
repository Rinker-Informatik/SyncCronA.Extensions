# SyncCronA.Extensions

`SyncCronA.Extensions` ist ein NuGet-Paket für .NET-10-Anwendungen, die Handler über den SyncCronA Core ausführen lassen. Die Extension registriert die Handler, übernimmt das Enrollment, hält eine mTLS/gRPC-Verbindung zum Core und meldet Ausführungsstatus und Ergebnisse. Der Core und die gemeinsam genutzten Verträge liegen in eigenen Projekten; `SyncCronA.Contracts` wird als NuGet-Abhängigkeit mitinstalliert.

## In einer Anwendung verwenden

### 1. Paket installieren

```bash
dotnet add package SyncCronA.Extensions
```

Die Anwendung benötigt .NET 10 und einen erreichbaren SyncCronA Core mit Application-Agent-Protokoll v2. Für das Enrollment brauchst du vom Core die `ApplicationId` und das dazugehörige `BootstrapSecret`. Die erste Registrierung muss im Core freigegeben werden.

### 2. Verbindung konfigurieren

In `appsettings.json` den Abschnitt `ApplicationAgent` ergänzen:

```json
{
  "ApplicationAgent": {
    "ControlPlaneUrl": "https://core.example.com/",
    "ApplicationId": "00000000-0000-0000-0000-000000000001",
    "ApplicationName": "Meine Anwendung"
  }
}
```

Das `BootstrapSecret` über eine sichere Konfigurationsquelle bereitstellen, zum Beispiel als Umgebungsvariable:

```bash
export ApplicationAgent__BootstrapSecret='<bootstrap-secret-mit-mindestens-32-zeichen>'
```

`ControlPlaneUrl` muss eine absolute HTTPS-URL sein. `ApplicationId` darf nicht leer sein; `BootstrapSecret` braucht mindestens 32 Zeichen. `ApplicationName` ist der lesbare Name im Core-Dashboard.

Weitere Einstellungen im selben Abschnitt:

| Schlüssel | Zweck und Standardwert |
| --- | --- |
| `Environment` | Kennzeichnung der Anwendungsumgebung; standardmäßig leer. |
| `ControlPlaneServerCertificatePem` | Vertrauenswürdiges Server-CA-Zertifikat als PEM, falls der Core eine eigene CA verwendet. |
| `HeartbeatInterval` | Abstand der Heartbeats; standardmäßig 15 Sekunden. |
| `RetryInterval` | Wartezeit vor einem erneuten Verbindungsversuch; standardmäßig 5 Sekunden. |
| `AutoApproveEnrollment` | Standardmäßig `false`; nur für die lokale Testanwendung gedacht. |

### 3. Handler registrieren

In einer ASP.NET-Core-Anwendung die Extension beim Host registrieren und jedem Handler einen eindeutigen Namen geben:

```csharp
using SyncCronA.ApplicationAgent.Agent;
using SyncCronA.ApplicationAgent.DependencyInjection;
using ExecutionContext = SyncCronA.ApplicationAgent.Runtime.ExecutionContext;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSyncCronA(builder.Configuration)
    .AddHandler<CleanupHandler>("cleanup");

var app = builder.Build();
app.MapGet("/agent-status", (ApplicationAgentStatusService status) => status.Get());
app.Run();

public sealed class CleanupHandler : ISyncCronAHandler
{
    public Task ExecuteAsync(
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Hier die eigene Arbeit ausführen. context.ExecutionId kann zur
        // Korrelation und für idempotente Verarbeitung verwendet werden.
        return Task.CompletedTask;
    }
}
```

`AddSyncCronA` registriert den Agenten als Hosted Service. Beim Start führt er das Enrollment aus, meldet die registrierten Handler und verbindet sich mit dem Core. Der Core kann anschließend `cleanup` starten. Jeder Auftrag erhält einen eigenen DI-Scope. Der Handler sollte den `CancellationToken` beachten; eine Exception wird als fehlgeschlagene Ausführung gemeldet.

### 4. Verbindung prüfen

Nach dem Start zeigt `GET /agent-status` im obigen Beispiel den aktuellen Zustand. `AwaitingApproval` bedeutet, dass die Registrierung noch im Core freigegeben werden muss; `Connected` zeigt die aktive Verbindung. Bei einem Verbindungsfehler versucht der Agent nach `RetryInterval` erneut, sich anzumelden.

Die [startbare Test-Web-API](dotnet/SyncCronA.ApplicationAgent.Tests/TestWebApi/README.md) enthält drei Handler für Erfolg, Fehler und langsame Ausführung sowie einen lokalen Ablauf mit dem Core. Weitere Details zur Ausführung und ihren Grenzen stehen in der [Bibliotheksdokumentation](dotnet/SyncCronA.ApplicationAgent/README.md).
