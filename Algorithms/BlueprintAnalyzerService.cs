using ReflectionMagic;
using SpaceEditor.Data;
using System.Diagnostics;
using System.IO;

namespace SpaceEditor.Algorithms;

public class BlueprintAnalysisResult
{
    public string BlueprintName { get; set; } = "Unknown";
    public int TotalBlocks { get; set; } = 0;
    public Dictionary<string, int> TotalComponents { get; set; } = new();
    public List<string> UnknownBlocks { get; set; } = new();
}

public class BlueprintAnalyzerService
{
    private readonly GameProxy _gameProxy;

    public BlueprintAnalyzerService(GameProxy gameProxy)
    {
        this._gameProxy = gameProxy;
    }

    public async Task<BlueprintAnalysisResult> AnalyzeAsync(string vrbFilePath)
    {
        var result = new BlueprintAnalysisResult();
        result.BlueprintName = Path.GetFileNameWithoutExtension(vrbFilePath);

        Debug.WriteLine($"\n[Analyzer] ==========================================");
        Debug.WriteLine($"[Analyzer] Starting analysis for: {result.BlueprintName}");

        Debug.WriteLine($"[Analyzer] Awaiting BlockDefinitions cache...");
        var definitions = await this._gameProxy.BlockDefinitions;

        Debug.WriteLine($"[Analyzer] BlockDefinitions cache acquired. Total known blocks: {definitions.Blocks.Count}");

        Debug.WriteLine($"[Analyzer] Attempting to deserialize VRB file via GameProxy...");
        dynamic blueprint;
        try
        {
            // We just pass the file path now. The proxy handles the context injection internally.
            blueprint = this._gameProxy.DeserializeFile(vrbFilePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Analyzer] FATAL ERROR during DeserializeFile: {ex.Message}");
            throw new Exception("Failed to deserialize the blueprint file.", ex);
        }

        if (blueprint == null)
        {
            Debug.WriteLine("[Analyzer] DeserializeFile returned null.");
            throw new Exception("Blueprint deserialization returned null.");
        }

        Debug.WriteLine($"[Analyzer] VRB deserialized successfully. Extracting CubeGrids...");

        System.Collections.IEnumerable grids;
        try
        {
            grids = blueprint.CubeGrids;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Analyzer] Failed to access CubeGrids property. Object type might be incorrect. Error: {ex.Message}");
            return result;
        }

        if (grids == null)
        {
            Debug.WriteLine("[Analyzer] No CubeGrids found in blueprint.");
            return result;
        }

        int gridCount = 0;
        foreach (dynamic grid in grids)
        {
            gridCount++;
            System.Collections.IEnumerable blocks;
            try
            {
                blocks = grid.CubeBlocks;
            }
            catch
            {
                Debug.WriteLine($"[Analyzer] Failed to access CubeBlocks on grid {gridCount}. Skipping.");
                continue;
            }

            if (blocks == null) continue;

            foreach (dynamic block in blocks)
            {
                result.TotalBlocks++;

                string subtypeId = null;
                try
                {
                    var blockType = (Type)block.GetType();
                    if (blockType.GetProperty("SubtypeName") != null || blockType.GetField("SubtypeName") != null)
                    {
                        subtypeId = DynamicHelper.Unwrap(block.SubtypeName);
                    }

                    if (string.IsNullOrEmpty(subtypeId) && (blockType.GetProperty("TypeId") != null || blockType.GetField("TypeId") != null))
                    {
                        string typeId = DynamicHelper.Unwrap(block.TypeId).ToString();
                        subtypeId = typeId.Replace("MyObjectBuilder_", "");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Analyzer] Error reading block identity at block index {result.TotalBlocks}: {ex.Message}");
                    continue;
                }

                if (string.IsNullOrEmpty(subtypeId)) continue;

                if (definitions.Blocks.TryGetValue(subtypeId, out var recipe))
                {
                    foreach (var comp in recipe.Components)
                    {
                        if (result.TotalComponents.ContainsKey(comp.Key))
                            result.TotalComponents[comp.Key] += comp.Value;
                        else
                            result.TotalComponents[comp.Key] = comp.Value;
                    }
                }
                else
                {
                    if (!result.UnknownBlocks.Contains(subtypeId))
                    {
                        result.UnknownBlocks.Add(subtypeId);
                    }
                }
            }
        }

        Debug.WriteLine($"[Analyzer] Analysis complete. Grids parsed: {gridCount}. Total Blocks: {result.TotalBlocks}. Unknown Blocks: {result.UnknownBlocks.Count}");
        Debug.WriteLine($"[Analyzer] ==========================================\n");
        return result;
    }
}

public class ComponentCost
{
    public string ComponentName { get; set; }
    public int Amount { get; set; }
}