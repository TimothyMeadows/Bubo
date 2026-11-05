using System.Text;
using System.Text.Json.Nodes;
using Hephaera.ModelAction.Application;

// Original data-only protocol checks. These authenticate no reviewer or asset.
static class RuntimeDataNullabilityTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        JsonObject Parse(string json, ActionRuntimeDataKind kind) =>
            ActionEffectDependencies.ParseRuntimeData<JsonObject>(Encoding.UTF8.GetBytes(json), kind);
        var engine = OwnedEffectFixture.EngineIdentity(full: true);
        var packet = new JsonObject { ["engine"] = engine.DeepClone(),
            ["effectSources"] = new JsonArray(new JsonObject { ["nativePreparationSha256"] = null }) };
        var parsed = Parse(packet.ToJsonString(), ActionRuntimeDataKind.ReceivingPacket);
        check(JsonNode.DeepEquals(parsed, packet), "receiving parser preserves complete nullable engine and authored preparation fields exactly");
        var receipt = new JsonObject { ["runtime"] = new JsonObject { ["engineIdentity"] = engine.DeepClone() } };
        check(JsonNode.DeepEquals(Parse(receipt.ToJsonString(), ActionRuntimeDataKind.RuntimeAcceptanceReceipt), receipt),
            "acceptance parser preserves full canonical engine identity without removing nulls");
        reject(() => Parse(receipt.ToJsonString(), ActionRuntimeDataKind.ActionReceipt),
            "action-only receipt keeps strict null rejection without runtime acceptance authority");
        var sources = new JsonArray(new JsonObject { ["ancestry"] = new JsonObject { ["nativePreparationSha256"] = null } });
        var sourceData = ActionEffectDependencies.ParseRuntimeData<JsonArray>(Encoding.UTF8.GetBytes(sources.ToJsonString()), ActionRuntimeDataKind.EffectSources);
        check(JsonNode.DeepEquals(sources, sourceData), "runtime source parser preserves explicitly absent authored preparation identity");
        foreach (var (json, kind) in new[]
        {
            ("null", ActionRuntimeDataKind.ReceivingPacket),
            ("{\"engine\":null}", ActionRuntimeDataKind.ReceivingPacket),
            ("{\"runtime\":null}", ActionRuntimeDataKind.RuntimeAcceptanceReceipt),
            ("{\"runtime\":{\"engineIdentity\":null}}", ActionRuntimeDataKind.RuntimeAcceptanceReceipt),
            ("{\"engineIdentity\":{\"task\":null}}", ActionRuntimeDataKind.RuntimeAcceptanceReceipt),
            ("{\"runtime\":{\"other\":{\"task\":null}}}", ActionRuntimeDataKind.RuntimeAcceptanceReceipt),
            ("{\"engine\":{\"task\":null},\"modelSha256\":null}", ActionRuntimeDataKind.ReceivingPacket),
            ("{\"engineInputs\":[null]}", ActionRuntimeDataKind.ReceivingPacket),
            ("{\"effectSources\":[null]}", ActionRuntimeDataKind.ReceivingPacket),
            ("{\"effectSources\":[{\"rightsSha256\":null}]}", ActionRuntimeDataKind.ReceivingPacket),
            ("{\"effectSources\":[{\"unknown\":null}]}", ActionRuntimeDataKind.ReceivingPacket),
            ("{\"nativePreparationSha256\":null}", ActionRuntimeDataKind.ReceivingPacket),
            ("{\"engine\":{\"task\":null,\"task\":null}}", ActionRuntimeDataKind.ReceivingPacket),
            ("{\"runtime\":{\"engineIdentity\":{\"task\":null,\"task\":null}}}", ActionRuntimeDataKind.RuntimeAcceptanceReceipt)
        }) reject(() => Parse(json, kind), "runtime parser rejects null/duplicate outside its exact declared context: " + json);
        foreach (var json in new[] { "[null]", "[{\"id\":null}]", "[{\"ancestry\":null}]", "[{\"ancestry\":{\"rightsSha256\":null}}]", "[{\"nativePreparationSha256\":null}]" })
            reject(() => ActionEffectDependencies.ParseRuntimeData<JsonArray>(Encoding.UTF8.GetBytes(json), ActionRuntimeDataKind.EffectSources),
                "runtime source parser rejects unsupported null: " + json);
        reject(() => Parse("{}", (ActionRuntimeDataKind)999), "runtime parser requires a registered explicit context");
        reject(() => Parse("{\"engine\":{\"task\":\"" + new string('a', 4097) + "\"}}", ActionRuntimeDataKind.ReceivingPacket),
            "nullable engine identity retains finite text guard");
        reject(() => Parse("{\"engine\":{\"items\":[" + string.Join(',', Enumerable.Repeat("null", 16385)) + "]}}", ActionRuntimeDataKind.ReceivingPacket),
            "nullable engine identity retains finite array guard");
        reject(() => Parse("{\"engine\":" + new string('[', 33) + "null" + new string(']', 33) + "}", ActionRuntimeDataKind.ReceivingPacket),
            "nullable engine identity retains finite depth guard");
        var row = "[" + string.Join(',', Enumerable.Repeat("null", 16384)) + "]";
        reject(() => Parse("{\"engine\":{\"items\":[" + string.Join(',', Enumerable.Repeat(row, 17)) + "]}}", ActionRuntimeDataKind.ReceivingPacket),
            "nullable engine identity retains finite cumulative node guard");
        reject(() => ActionEffectDependencies.ParseRuntimeData<JsonObject>(new byte[ActionEffectDependencies.MaximumJsonBytes + 1], ActionRuntimeDataKind.ReceivingPacket),
            "nullable parsing retains finite byte guard");
    }
}
