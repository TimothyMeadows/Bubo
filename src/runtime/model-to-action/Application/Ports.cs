using System.Text.Json.Nodes;
namespace Hephaera.ModelAction.Application;
public interface IEngineResolver { Task<JsonObject> Resolve(CancellationToken cancellation); }
