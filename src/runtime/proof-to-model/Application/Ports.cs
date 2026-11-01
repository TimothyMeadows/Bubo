using System.Text.Json;
using Hephaera.ProofToModel.Domain;
namespace Hephaera.ProofToModel.Application;

public interface IWorkspace : Hephaera.AssetProduction.IWorkspace;
public interface INativeProcess : Hephaera.AssetProduction.INativeProcess;
public interface IAlphaProcessor
{
    AlphaMatte? Inspect(string path);
    void SaveMask(string path, AlphaMatte matte);
    void PadSquare(string input, string output, int side);
}
public static class Serialization
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true };
}
