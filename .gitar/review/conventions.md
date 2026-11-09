# Project conventions

- This is one ASP.NET Core service on .NET 10: Razor Pages and htmx for the UI, Minimal APIs for JSON. Flag new services, JavaScript frameworks or front-end build steps.
- Elasticsearch is the only data store. Flag state kept in process memory, because the app runs as two replicas.
- Provider clients in `Backends` are null until credentials exist. Code that uses them must handle null and fail gracefully, not at startup.
- Wrap each OpenAI call in a span from `AppTelemetry.Source`, with the model and token counts as attributes.
- Pass `CancellationToken` through every call to Elasticsearch or OpenAI.
- Razor output must stay HTML-encoded. Flag `Html.Raw` on anything that came from a user, a model or a search result.
- Never log a credential or a full provider request.
