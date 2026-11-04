namespace Bubo.Vfx.Domain;

public static class PreparationValidation
{
    public const int MaximumFrames = 256;
    public const int MaximumSources = 256;
    public const int MaximumDimension = 8192;
    public const long MaximumPixels = 16777216;
    public const long MaximumSourceBytes = 134217728;
    public const long MaximumTotalSourceBytes = 536870912;
    public const long MaximumWorkingBytes = 536870912;
    public const long MaximumOutputBytes = 1073741824;
    public const long MaximumPixelWork = 1073741824;

    public static void RequireValid(VfxPreparationRequest request)
    {
        var report = Validate(request);
        if (!report.IsValid)
            throw new InvalidOperationException("Invalid VFX preparation: " + string.Join("; ", report.Issues.Select(issue => issue.Path + ": " + issue.Message)));
    }

    public static ValidationReport Validate(VfxPreparationRequest request)
    {
        var issues = new List<ValidationIssue>();
        void Issue(string path, string message, string code = "invalid-preparation")
        {
            if (issues.Count < 256) issues.Add(new(code, path, message));
        }
        ValidationReport Report() => new(issues.Count == 0, issues.AsReadOnly());
        void Positive(long value, long ceiling, string path)
        {
            if (value <= 0 || value > ceiling) Issue(path, "Expected a positive integer at most " + ceiling);
        }
        if (request is null) { Issue("$", "Request is required"); return Report(); }
        if (request.SchemaVersion != "bubo-vfx-preparation/v1") Issue("schemaVersion", "Expected bubo-vfx-preparation/v1");
        if (!VfxContractGuards.Id(request.Id)) Issue("id", "Expected a stable portable identifier");
        if (!VfxContractGuards.Id(request.Version)) Issue("version", "Expected a stable portable identifier");
        if (!VfxContractGuards.Hash(request.StyleSha256)) Issue("styleSha256", "Expected a lowercase SHA-256 identity");
        if (request.Engine is null) Issue("engine", "Exact selected engine identity is required");
        else
        {
            if (request.Engine.Id is not ("THREEJS" or "BABYLONJS" or "PHASERJS" or "UNITY" or "UNREAL" or "GODOT")) Issue("engine.id", "Expected a registered engine");
            if (!VfxContractGuards.Hash(request.Engine.ContractSha256)) Issue("engine.contractSha256", "Exact contract hash is required");
            if (!VfxContractGuards.Hash(request.Engine.DefinitionSha256)) Issue("engine.definitionSha256", "Exact definition hash is required");
            if (!VfxContractGuards.Hash(request.Engine.RegistrySha256)) Issue("engine.registrySha256", "Exact registry hash is required");
        }
        if (request.Profile is not ("sheet" or "sequence" or "math-mask")) Issue("profile", "Only sheet, sequence and math-mask profiles are admitted");
        if (request.Budgets is null || request.Timing is null || request.Atlas is null || request.Sources is null)
        {
            Issue("$", "Sources, timing, atlas and budgets are required");
            return Report();
        }
        var b = request.Budgets;
        Positive(b.MaxSourceBytes, MaximumSourceBytes, "budgets.maxSourceBytes");
        Positive(b.MaxTotalSourceBytes, MaximumTotalSourceBytes, "budgets.maxTotalSourceBytes");
        Positive(b.MaxChunkBytes, MaximumSourceBytes, "budgets.maxChunkBytes");
        Positive(b.MaxCompressedBytes, MaximumSourceBytes, "budgets.maxCompressedBytes");
        Positive(b.MaxFrames, MaximumFrames, "budgets.maxFrames");
        Positive(b.MaxDecodedPixels, MaximumPixels, "budgets.maxDecodedPixels");
        Positive(b.MaxAtlasPixels, MaximumPixels, "budgets.maxAtlasPixels");
        Positive(b.MaxPixelWork, MaximumPixelWork, "budgets.maxPixelWork");
        Positive(b.MaxWorkingBytes, MaximumWorkingBytes, "budgets.maxWorkingBytes");
        Positive(b.MaxOutputBytes, MaximumOutputBytes, "budgets.maxOutputBytes");
        Positive(b.MaxOutputFiles, MaximumSources + 6, "budgets.maxOutputFiles");
        Positive(b.MaxSeconds, 3600, "budgets.maxSeconds");
        if (b.MinimumFreeDiskBytes < 0 || b.MinimumFreeDiskBytes > 4294967296L) Issue("budgets.minimumFreeDiskBytes", "Disk reserve must be between zero and 4 GiB");
        if (b.MaxSourceBytes > b.MaxTotalSourceBytes || b.MaxChunkBytes > b.MaxSourceBytes || b.MaxCompressedBytes > b.MaxSourceBytes)
            Issue("budgets", "Chunk/compressed/source byte limits must fit their enclosing source limits");
        if (b.HardProcessMemoryBytes is not null)
            Issue("budgets.hardProcessMemoryBytes", "A hard process-memory worker is unavailable for this processing profile", "unsupported-capability");
        if (request.Sources.Count > MaximumSources) { Issue("sources", "At most 256 exact source references are admitted"); return Report(); }
        long sourceBytes = 0;
        var sourcePaths = new Dictionary<string, VfxPreparationSource>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < request.Sources.Count; i++)
        {
            var source = request.Sources[i]; var path = $"sources[{i}]";
            if (source is null) { Issue(path, "Source references must not be null"); continue; }
            if (!VfxContractGuards.Path(source.Path) || !source.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                Issue(path + ".path", "Expected a canonical project-relative PNG path");
            if (!VfxContractGuards.Hash(source.Sha256)) Issue(path + ".sha256", "Expected an exact lowercase SHA-256 identity");
            if (source.Bytes < 1 || source.Bytes > b.MaxSourceBytes || source.Bytes > MaximumSourceBytes) Issue(path + ".bytes", "Source must fit its finite byte allowance");
            if (source.Bytes > 0 && sourceBytes <= long.MaxValue - source.Bytes) sourceBytes += source.Bytes;
            else sourceBytes = long.MaxValue;
            if (source.Path is not null && sourcePaths.TryGetValue(source.Path, out var previous))
            {
                if (source.Path != previous.Path || source.Sha256 != previous.Sha256 || source.Bytes != previous.Bytes)
                    Issue(path, "Repeated source paths must have exactly identical spelling and byte identity");
            }
            else if (source.Path is not null) sourcePaths.Add(source.Path, source);
        }
        if (sourceBytes > b.MaxTotalSourceBytes) Issue("sources", "Aggregate exact source bytes exceed their budget");
        if (sourceBytes > b.MaxOutputBytes) Issue("budgets.maxOutputBytes", "Output budget cannot hold immutable source snapshots");
        if (request.Sources.Count + 6 > b.MaxOutputFiles) Issue("budgets.maxOutputFiles", "Output budget must include source snapshots, permanent reservation and five processing artifacts");

        var t = request.Timing;
        Positive(t.FrameCount, MaximumFrames, "timing.frameCount");
        Positive(t.FrameStep, MaximumFrames, "timing.frameStep");
        Positive(t.RateNumerator, 1000000, "timing.rateNumerator");
        Positive(t.RateDenominator, 1000000, "timing.rateDenominator");
        if (t.FirstFrame < 0 || t.FirstFrame >= MaximumFrames) Issue("timing.firstFrame", "First frame must be an explicit nonnegative index below 256");
        if (t.FrameCount > b.MaxFrames) Issue("timing.frameCount", "Selected frames exceed the declared count budget");
        if (t.Loop is not ("once" or "repeat")) Issue("timing.loop", "Loop intent must be once or repeat; seamless quality is separately reviewed");
        if (t.RateNumerator > 0 && t.RateDenominator > 0 && t.FrameCount > 0 && (double)t.FrameCount * t.RateDenominator / t.RateNumerator > 3600)
            Issue("timing", "Selected duration must not exceed one hour");
        var a = request.Atlas;
        Positive(a.Columns, MaximumFrames, "atlas.columns");
        if (a.Columns > t.FrameCount) Issue("atlas.columns", "Atlas columns must not exceed the selected frame count");
        if (a.Padding is < 0 or > 64 || a.Extrusion is < 0 or > 64) Issue("atlas", "Padding and extrusion must each be between zero and 64 pixels");
        if (a.Alpha != "straight") Issue("atlas.alpha", "Only straight alpha is supported; no implicit conversion");
        if (a.ColorSpace is not ("srgb" or "linear")) Issue("atlas.colorSpace", "Explicit srgb or linear interpretation is required");

        long available = 0; int width = 0, height = 0;
        if (request.Profile == "sheet")
        {
            if (request.Sources.Count != 1 || request.Sheet is null || request.Mask is not null) Issue("sheet", "Sheet profile requires exactly one PNG, a sheet declaration and no mask");
            if (request.Sheet is not null)
            {
                var s = request.Sheet;
                Positive(s.Columns, MaximumFrames, "sheet.columns"); Positive(s.Rows, MaximumFrames, "sheet.rows");
                Positive(s.CellWidth, MaximumDimension, "sheet.cellWidth"); Positive(s.CellHeight, MaximumDimension, "sheet.cellHeight");
                if (s.OriginX is < 0 or > MaximumDimension || s.OriginY is < 0 or > MaximumDimension) Issue("sheet", "Grid origin must fit bounded source coordinates");
                if (s.StrideX < s.CellWidth || s.StrideX > MaximumDimension || s.StrideY < s.CellHeight || s.StrideY > MaximumDimension) Issue("sheet", "Grid strides must preserve nonoverlapping complete cells");
                if ((long)(s.Columns - 1) * s.StrideX + s.OriginX + s.CellWidth > MaximumDimension || (long)(s.Rows - 1) * s.StrideY + s.OriginY + s.CellHeight > MaximumDimension)
                    Issue("sheet", "Declared grid exceeds bounded image dimensions");
                if ((long)s.CellWidth * s.CellHeight > b.MaxDecodedPixels) Issue("sheet", "One cell exceeds the decoded pixel budget");
                if (s.FrameIndices is null || s.FrameIndices.Count is < 1 or > MaximumFrames) Issue("sheet.frameIndices", "An explicit ordered list of 1 to 256 cell indices is required");
                else
                {
                    available = s.FrameIndices.Count;
                    foreach (var index in s.FrameIndices) if (index < 0 || index >= (long)s.Columns * s.Rows) { Issue("sheet.frameIndices", "Frame index lies outside the declared grid"); break; }
                }
                width = s.CellWidth; height = s.CellHeight;
            }
        }
        else if (request.Profile == "sequence")
        {
            if (request.Sources.Count == 0 || request.Sheet is not null || request.Mask is not null) Issue("sources", "Sequence profile requires an explicit ordered PNG list, with no sheet or mask");
            available = request.Sources.Count;
        }
        else if (request.Profile == "math-mask")
        {
            if (request.Sources.Count != 0 || request.Mask is null || request.Sheet is not null) Issue("mask", "Math-mask requires its registered typed formula and no external inputs or sheet");
            available = 1;
            if (t.FirstFrame != 0 || t.FrameCount != 1 || t.FrameStep != 1) Issue("timing", "Math-mask produces exactly one explicit frame");
            if (request.Mask is not null)
            {
                var m = request.Mask;
                if (m.Kind is not ("disc" or "ring" or "ramp")) Issue("mask.kind", "Only disc, ring and ramp formulas are registered");
                Positive(m.Width, MaximumDimension, "mask.width"); Positive(m.Height, MaximumDimension, "mask.height");
                if ((long)m.Width * m.Height > b.MaxDecodedPixels) Issue("mask", "Mask exceeds decoded pixel budget");
                if (!double.IsFinite(m.InnerRadius) || !double.IsFinite(m.OuterRadius) || !double.IsFinite(m.Softness) || !double.IsFinite(m.AngleDegrees)
                    || m.InnerRadius < 0 || m.OuterRadius <= 0 || m.OuterRadius > 1 || m.InnerRadius >= m.OuterRadius || m.Softness < 0 || m.Softness > 1 || Math.Abs(m.AngleDegrees) > 360)
                    Issue("mask", "Formula parameters must be finite and normalized with 0 <= inner < outer <= 1, softness 0..1 and angle -360..360");
                if (m.Kind == "disc" && m.InnerRadius != 0) Issue("mask.innerRadius", "Disc formula requires zero inner radius");
                width = m.Width; height = m.Height;
            }
        }
        if (t.FirstFrame >= 0 && t.FrameCount > 0 && t.FrameStep > 0 && (long)t.FirstFrame + (long)(t.FrameCount - 1) * t.FrameStep >= available)
            Issue("timing", "Selected frame span exceeds the exact declared order");
        if (width > 0 && height > 0 && a.Columns > 0 && t.FrameCount > 0 && a.Padding >= 0 && a.Extrusion >= 0)
        {
            var border = 2L * (a.Padding + a.Extrusion);
            var atlasWidth = (width + border) * a.Columns;
            var atlasHeight = (height + border) * (((long)t.FrameCount + a.Columns - 1) / a.Columns);
            if (atlasWidth > MaximumDimension || atlasHeight > MaximumDimension || atlasWidth * atlasHeight > b.MaxAtlasPixels)
                Issue("atlas", "Atlas dimensions exceed finite dimension/pixel limits");
        }
        return Report();
    }
}

internal static class VfxContractGuards
{
    internal static bool Id(string? value) => value is not null && value.Length is >= 1 and <= 80
        && char.IsAsciiLetterOrDigit(value[0]) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    internal static bool Hash(string? value) => value is not null && value.Length == 64
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool Path(string? path)
    {
        if (path is null || path.Length is < 1 or > 1024 || path.Any(c => char.IsControl(c) || c is '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*')) return false;
        foreach (var part in path.Split('/'))
        {
            if (part is "" or "." or ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.EndsWith('.') || part.EndsWith(' ')) return false;
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9')) return false;
        }
        return true;
    }
}
