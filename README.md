# Codex Unity Middleware

This repository contains a simple Unity Editor plugin that exposes a local HTTP API for receiving scripts and game instructions from an external Codex server. It automatically saves incoming scripts, refreshes the Unity project so they compile, executes the provided instructions and reports the result back via callback.

## Installation

1. Copy `Assets/Scripts/CodexServer.cs` and `Assets/Scripts/ChatGPTMiddleware.cs` into the `Assets/Scripts` folder of your Unity project.
2. Set environment variables:
   - `CODEX_API_KEY` – key required in `X-API-Key` header for incoming requests.
   - `OPENAI_API_KEY` – (optional) used by `ChatGPTMiddleware` if you still want to send prompts to OpenAI.
3. Open the project in the Unity Editor. When you press Play the middleware starts listening on `http://localhost:5000/`.

## API Usage

Send a POST request to `http://localhost:5000/` with header `X-API-Key` and JSON payload:

```json
{
  "fileName": "EnemyFollowPlayer.cs",
  "code": "// C# script code here...",
  "instructions": {
    "gameObjects": [
      {
        "name": "Enemy",
        "components": ["EnemyFollowPlayer"],
        "properties": {
          "speed": 5.0,
          "detectionRange": 10.0
        }
      }
    ]
  },
  "callbackUrl": "http://localhost:6000/codex_callback"
}
```

Scripts are stored under `Assets/Scripts/AI_generated` and Unity automatically compiles them. After compilation the middleware creates the requested GameObjects, attaches the generated components and sets their public properties using reflection.

## Callback

The middleware posts a JSON response to `callbackUrl` after processing:

```json
{ "status": "success", "message": "details" }
```

If the request fails the status is `error`. The callback is retried up to three times with five seconds between attempts.

## Logs

A log file is created for each request in `Assets/Logs` containing time stamped messages about compilation and instruction execution.

## Extending

The middleware is implemented with extensibility in mind. New action types for `ChatGPTMiddleware` can implement the `IGameAction` interface and be dispatched in `ExecuteAction`. Additional API endpoints or instruction formats can be added by modifying `CodexServer`.
