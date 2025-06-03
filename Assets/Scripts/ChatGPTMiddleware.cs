using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
#if !UNITY_2019_1_OR_NEWER
using System.Net.Http;
#endif

public class ChatGPTMiddleware : MonoBehaviour
{
    private string apiKey;
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        Converters = { new Vector3Converter() }
    };

    private void Awake()
    {
        apiKey = GetApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            Debug.LogError("OpenAI API key not found. Set environment variable OPENAI_API_KEY or store it in PlayerPrefs.");
        }
    }

    private string GetApiKey()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(key))
            return key;

        if (PlayerPrefs.HasKey("OPENAI_API_KEY"))
        {
            // Ideally the key should be stored encrypted.
            return PlayerPrefs.GetString("OPENAI_API_KEY");
        }
        return null;
    }

    public async Task<string> SendPromptAsync(string prompt)
    {
        if (string.IsNullOrEmpty(apiKey))
            return null;

#if UNITY_2019_1_OR_NEWER
        var request = new UnityWebRequest("https://api.openai.com/v1/chat/completions", "POST");
        string jsonBody = "{\"model\":\"gpt-3.5-turbo\",\"messages\":[{\"role\":\"user\",\"content\":\"" + prompt + "\"}] }";
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Authorization", "Bearer " + apiKey);
        await AwaitRequest(request);
        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("OpenAI request failed: " + request.error);
            return null;
        }
        return request.downloadHandler.text;
#else
        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);
            var content = new StringContent("{\"model\":\"gpt-3.5-turbo\",\"messages\":[{\"role\":\"user\",\"content\":\"" + prompt + "\"}] }", System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync("https://api.openai.com/v1/chat/completions", content);
            if (!response.IsSuccessStatusCode)
            {
                Debug.LogError("OpenAI request failed: " + response.StatusCode);
                return null;
            }
            return await response.Content.ReadAsStringAsync();
        }
#endif
    }

    private async Task AwaitRequest(UnityWebRequest request)
    {
        var op = request.SendWebRequest();
        var tcs = new TaskCompletionSource<bool>();
        op.completed += _ => tcs.SetResult(true);
        await tcs.Task;
    }

    public void ExecuteInstruction(string json)
    {
        try
        {
            using (var doc = System.Text.Json.JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var element in doc.RootElement.EnumerateArray())
                    {
                        ExecuteAction(element);
                    }
                }
                else
                {
                    ExecuteAction(doc.RootElement);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("Failed to parse instruction: " + ex.Message);
        }
    }

    private void ExecuteAction(System.Text.Json.JsonElement element)
    {
        if (!element.TryGetProperty("type", out var typeProp))
        {
            Debug.LogError("Action missing type field");
            return;
        }
        string type = typeProp.GetString();
        switch (type)
        {
            case "CreateObject":
                var create = System.Text.Json.JsonSerializer.Deserialize<CreateObjectAction>(element.GetRawText(), Options);
                create.Execute();
                break;
            case "ModifyObject":
                var modify = System.Text.Json.JsonSerializer.Deserialize<ModifyObjectAction>(element.GetRawText(), Options);
                modify.Execute();
                break;
            default:
                Debug.LogError("Unknown action type: " + type);
                break;
        }
    }
}

public interface IGameAction
{
    void Execute();
}

[Serializable]
public class CreateObjectAction : IGameAction
{
    public string type;
    public string objectType;
    public string name;
    public Vector3 position;
    public Vector3 scale = Vector3.one;
    public Vector3 rotation;
    public string color;

    public void Execute()
    {
        if (!Enum.TryParse(objectType, true, out PrimitiveType primitive))
        {
            Debug.LogError("Invalid object type: " + objectType);
            return;
        }

        GameObject obj = GameObject.CreatePrimitive(primitive);
        if (!string.IsNullOrEmpty(name))
            obj.name = name;
        obj.transform.position = position;
        obj.transform.localScale = scale;
        obj.transform.eulerAngles = rotation;
        if (!string.IsNullOrEmpty(color) && ColorUtility.TryParseHtmlString(color, out var col))
        {
            var renderer = obj.GetComponent<Renderer>();
            if (renderer != null)
                renderer.material.color = col;
        }
        Debug.Log($"Created {objectType} '{obj.name}' at {position}");
    }
}

[Serializable]
public class ModifyObjectAction : IGameAction
{
    public string type;
    public string targetName;
    public Vector3 position;
    public Vector3 scale = Vector3.one;
    public Vector3 rotation;
    public string color;

    public void Execute()
    {
        var obj = GameObject.Find(targetName);
        if (obj == null)
        {
            Debug.LogError("Object not found: " + targetName);
            return;
        }
        obj.transform.position = position;
        obj.transform.localScale = scale;
        obj.transform.eulerAngles = rotation;
        if (!string.IsNullOrEmpty(color) && ColorUtility.TryParseHtmlString(color, out var col))
        {
            var renderer = obj.GetComponent<Renderer>();
            if (renderer != null)
                renderer.material.color = col;
        }
        Debug.Log($"Modified object '{targetName}'");
    }
}

public class Vector3Converter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var obj = JsonDocument.ParseValue(ref reader).RootElement;
        float x = obj.GetProperty("x").GetSingle();
        float y = obj.GetProperty("y").GetSingle();
        float z = obj.GetProperty("z").GetSingle();
        return new Vector3(x, y, z);
    }

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.x);
        writer.WriteNumber("y", value.y);
        writer.WriteNumber("z", value.z);
        writer.WriteEndObject();
    }
}
