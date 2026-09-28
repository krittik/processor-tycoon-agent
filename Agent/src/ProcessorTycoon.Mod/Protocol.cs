using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ProcessorTycoonMod;

internal sealed class Request
{
    public string Command { get; set; } = "status";
    public string Session { get; set; } = "cli";
    public string Target { get; set; } = "";
    public string Scope { get; set; } = "";
    public JToken? Value { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 60;
    public long Since { get; set; }
    public bool Changes { get; set; }
    public bool Hidden { get; set; }
    public bool ExplicitUserRequest { get; set; }
    public string? ObserveAfter { get; set; }
    public JObject? Parameters { get; set; }
    // Internal, compiled adapters only. Never deserialized from the local API.
    [JsonIgnore] public Action? NativeAction { get; set; }
    [JsonIgnore] public bool NativeVisualHandled { get; set; }
    [JsonIgnore] public Component? NativeVisualTarget { get; set; }
    [JsonIgnore] public bool NativeVisualClick { get; set; }
    [JsonIgnore] public int SettleFrames { get; set; } = 3;
    [JsonIgnore] public TaskCompletionSource<object> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class AgentError : Exception
{
    public string Code { get; }
    public AgentError(string code, string message) : base(message) => Code = code;
}

internal sealed class Operation
{
    private static long nextSequence;
    [JsonIgnore] public readonly long sequence = System.Threading.Interlocked.Increment(ref nextSequence);
    public string id = Guid.NewGuid().ToString("N");
    public string state = "accepted";
    public string command = "";
    public object? result;
    public object? error;
}

internal static class Wire
{
    public static readonly JsonSerializerSettings Settings = new() { NullValueHandling = NullValueHandling.Ignore };
    public static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);
    public static object Error(string code, string message) => new { ok = false, error = new { code, message } };
}
