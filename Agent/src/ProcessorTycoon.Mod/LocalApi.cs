using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ProcessorTycoonMod;

internal sealed class LocalApi : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly ConcurrentQueue<Request> requests;
    private readonly string discoveryFile;
    private bool disposed;
    public string Endpoint { get; }
    public string Instance { get; } = Guid.NewGuid().ToString("N");

    public LocalApi(int port, string gameRoot, ConcurrentQueue<Request> requests)
    {
        this.requests = requests;
        Endpoint = $"http://127.0.0.1:{port}/";
        discoveryFile = Path.Combine(gameRoot, "tools", "endpoint.json");
        listener.Prefixes.Add(Endpoint);
        listener.Start();
        Directory.CreateDirectory(Path.GetDirectoryName(discoveryFile)!);
        File.WriteAllText(discoveryFile, Wire.Serialize(new { endpoint = Endpoint, instance = Instance, apiVersion = 1 }));
        _ = Listen();
    }

    private async Task Listen()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (disposed || !listener.IsListening) { return; }
            _ = Handle(context);
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        object response;
        try
        {
            if (context.Request.HttpMethod != "POST" || context.Request.Url.AbsolutePath != "/v1/command") throw new AgentError("invalid_route", "POST JSON to /v1/command.");
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);
            var request = JsonConvert.DeserializeObject<Request>(body) ?? throw new AgentError("invalid_request", "A JSON request is required.");
            requests.Enqueue(request);
            // Read requests cannot silently mutate after a network timeout; mutations return operation IDs promptly.
            response = await request.Completion.Task.ConfigureAwait(false);
        }
        catch (AgentError error) { response = Wire.Error(error.Code, error.Message); }
        catch (JsonException error) { response = Wire.Error("invalid_json", error.Message); }
        catch (Exception error) { response = Wire.Error("server_error", error.Message); }
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Wire.Serialize(response));
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception) when (disposed || !context.Response.OutputStream.CanWrite) { }
        catch (HttpListenerException) { }
        catch (IOException) { }
    }

    public void Dispose()
    {
        disposed = true;
        listener.Close();
        while (requests.TryDequeue(out var request)) request.Completion.TrySetResult(Wire.Error("shutting_down", "Game bridge is shutting down."));
        if (File.Exists(discoveryFile) && File.ReadAllText(discoveryFile).Contains(Instance)) File.Delete(discoveryFile);
    }
}
