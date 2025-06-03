using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class CodexServer : MonoBehaviour
{
    private const int Port = 5000;
    private HttpListener _listener;
    private ConcurrentQueue<(string json, string apiKey)> _queue = new ConcurrentQueue<(string, string)>();
    private CancellationTokenSource _cts;
    private string _expectedApiKey;
    private string _logDir;

    private void Awake()
    {
        _expectedApiKey = Environment.GetEnvironmentVariable("CODEX_API_KEY");
        _logDir = Path.Combine(Application.dataPath, "Logs");
        Directory.CreateDirectory(_logDir);
        StartServer();
    }

    private void OnDestroy()
    {
        StopServer();
    }

    private void StartServer()
    {
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        try
        {
            _listener.Start();
            Log($"Server started on port {Port}");
            _listener.BeginGetContext(OnRequest, null);
            Task.Run(() => ProcessQueueAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to start server: {ex.Message}");
        }
    }

    private void StopServer()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch {}
    }

    private void OnRequest(IAsyncResult ar)
    {
        if (!_listener.IsListening) return;
        HttpListenerContext ctx = null;
        try
        {
            ctx = _listener.EndGetContext(ar);
        }
        catch { return; }
        finally
        {
            if (_listener.IsListening)
                _listener.BeginGetContext(OnRequest, null);
        }

        if (ctx.Request.HttpMethod != "POST")
        {
            ctx.Response.StatusCode = 405;
            ctx.Response.Close();
            return;
        }

        var apiKey = ctx.Request.Headers["X-API-Key"];
        if (!string.IsNullOrEmpty(_expectedApiKey) && apiKey != _expectedApiKey)
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Close();
            return;
        }

        using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
        {
            var json = reader.ReadToEnd();
            _queue.Enqueue((json, apiKey));
        }
        ctx.Response.StatusCode = 202;
        ctx.Response.Close();
    }

    private async Task ProcessQueueAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_queue.TryDequeue(out var item))
            {
                await HandleRequestAsync(item.json);
            }
            else
            {
                await Task.Delay(100, token);
            }
        }
    }

    private async Task HandleRequestAsync(string json)
    {
        CodexPayload payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<CodexPayload>(json);
        }
        catch (Exception ex)
        {
            Log($"Invalid JSON: {ex.Message}");
            return;
        }

        string scriptPath = Path.Combine("Assets/Scripts/AI_generated", payload.fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
        File.WriteAllText(scriptPath, payload.code);
#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
        string resultMessage = "Success";
        bool success = true;
        try
        {
            if (payload.instructions != null)
            {
                foreach (var goInst in payload.instructions.gameObjects)
                {
                    ProcessGameObject(goInst);
                }
            }
        }
        catch (Exception ex)
        {
            success = false;
            resultMessage = ex.Message;
            Log($"Error processing instructions: {ex.Message}");
        }
        await SendCallbackAsync(payload.callbackUrl, success, resultMessage);
    }

    private void ProcessGameObject(GameObjectInstruction inst)
    {
        var obj = new GameObject(inst.name);
        if (inst.components != null)
        {
            foreach (var comp in inst.components)
            {
                var type = Type.GetType(comp);
                if (type != null)
                {
                    obj.AddComponent(type);
                }
                else
                {
                    Log($"Component not found: {comp}");
                }
            }
        }
        if (inst.properties != null)
        {
            foreach (var kv in inst.properties)
            {
                foreach (var comp in obj.GetComponents<Component>())
                {
                    var prop = comp.GetType().GetProperty(kv.Key);
                    if (prop != null)
                    {
                        try
                        {
                            object value = Convert.ChangeType(kv.Value.GetDouble(), prop.PropertyType);
                            prop.SetValue(comp, value);
                        }
                        catch
                        {
                            // fallback to string
                            prop.SetValue(comp, kv.Value.GetString());
                        }
                    }
                }
            }
        }
        Log($"Created GameObject {inst.name}");
    }

    private async Task SendCallbackAsync(string url, bool success, string message)
    {
        if (string.IsNullOrEmpty(url)) return;
        var payload = new CallbackPayload { status = success ? "success" : "error", message = message };
        var json = JsonSerializer.Serialize(payload);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using (var request = new UnityWebRequest(url, "POST"))
            {
                byte[] body = Encoding.UTF8.GetBytes(json);
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                await AwaitRequest(request);
                if (request.result == UnityWebRequest.Result.Success)
                {
                    Log($"Callback sent: {json}");
                    break;
                }
                else if (attempt < 2)
                {
                    await Task.Delay(5000);
                }
            }
        }
    }

    private async Task AwaitRequest(UnityWebRequest request)
    {
        var op = request.SendWebRequest();
        var tcs = new TaskCompletionSource<bool>();
        op.completed += _ => tcs.SetResult(true);
        await tcs.Task;
    }

    private void Log(string message)
    {
        string logPath = Path.Combine(_logDir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
        File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {message}\n");
    }
}

[Serializable]
public class CodexPayload
{
    public string fileName;
    public string code;
    public Instruction instructions;
    public string callbackUrl;
}

[Serializable]
public class Instruction
{
    public List<GameObjectInstruction> gameObjects;
}

[Serializable]
public class GameObjectInstruction
{
    public string name;
    public string[] components;
    public Dictionary<string, JsonElement> properties;
}

public class CallbackPayload
{
    public string status;
    public string message;
}
