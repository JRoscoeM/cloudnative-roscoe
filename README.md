# Conference LiveVision
## Demo Link:
https://www.youtube.com/watch?v=XElMRD25W90
## Cloud Native Hackathon

**Conference LiveVision** shows how busy a conference is and where to go next, at `https://roscoe.cnh.radarhq.io`. It's a .NET 10 web app built for the Cloud Native Hackathon.

- **Person count:** a phone on `/sensor` runs a person detector on the device and posts only the number. Video never leaves the phone.
- **Photo reports:** an attendee photographs a room on `/snapshot`. OpenAI estimates how full it is, and the photo is discarded.
- **Queue timer:** a sensor counts people inside a box drawn over the line, multiplied by hand-timed seconds per person.
- **Fill forecast:** a straight-line trend over recent sensor readings, adjusted for when talks end.
- **Live map:** a floor plan colored by fullness, refreshed every 5 seconds.

- **Talk search:** hybrid keyword and semantic search in Elasticsearch, and "try instead" suggestions when a room is packed.
- **Organizer dashboard:** `/organizer` edits rooms and lines, creates sensor links, and shows site health from the cluster's Prometheus.

Rooms and lines start from `app/Venue.cs` and are then managed from the dashboard. Talks come from the public KubeCon + CloudNativeCon North America 2026 schedule, stored in `app/talks.json` (titles, times and rooms only). A room shows its talks when its name matches the schedule, such as `Hall DE`.

| Part | Choice |
|---|---|
| App | ASP.NET Core in `app/`: Razor Pages and htmx, with Minimal APIs for JSON |
| Data | Elasticsearch |
| Detection | MediaPipe EfficientDet-Lite2, in the browser, served from `app/wwwroot/vendor` |
| Photo analysis | OpenAI, used only for `/snapshot` |
| Telemetry | OpenTelemetry to Dash0 over OTLP, and `/metrics` for the cluster's Prometheus |
| Delivery | Pull requests reviewed by Gitar, then GitHub Actions, Argo CD and Radar |

### Run it locally

You need the .NET 10 SDK.

```bash
dotnet test App.slnx
```

```bash
dotnet run --project app
```

The app runs without credentials and then shows clearly labelled sample data. `/api/status` shows which providers are configured. To connect a provider locally, set the variables from the next section in your shell.

### Provider credentials

Create Secret `app-credentials` in namespace `team-roscoe` yourself, in Radar Cloud under **Resources** > **Secrets**, and never commit it. Include only the providers you have keys for:

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: app-credentials
  namespace: team-roscoe
type: Opaque
stringData:
  ELASTICSEARCH_URL: <your Elasticsearch endpoint>
  ELASTICSEARCH_API_KEY: <your Elasticsearch API key>
  SENSOR_KEY: <a long random string you make up>
  OPENAI_API_KEY: <your OpenAI key>
  OPENAI_MODEL: <optional, overrides the default vision model>
  OTEL_EXPORTER_OTLP_ENDPOINT: <your Dash0 OTLP endpoint>
  OTEL_EXPORTER_OTLP_HEADERS: Authorization=Bearer <your Dash0 token>
```

Restart the app after creating or changing the Secret, because it's read when a pod starts.

`SENSOR_KEY` is the setup key you enter once on each sensor phone. The phone exchanges it for a token that can only report for the space it watches.

The organizer dashboard uses `SENSOR_KEY` too, unless you also set `ORGANIZER_KEY`. Set it if people who run sensors shouldn't be able to edit the venue.

### Use of AI tools

This project was built on Nov 9th with the help of Claude Code.