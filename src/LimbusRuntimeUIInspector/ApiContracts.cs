namespace LimbusRuntimeUIInspector.Contracts.Api;

internal sealed class EditRequest
{
    public int Id { get; set; }
    public bool? Active { get; set; }
    public float? AnchoredX { get; set; }
    public float? AnchoredY { get; set; }
    public float? Width { get; set; }
    public float? Height { get; set; }
    public float? AnchorMinX { get; set; }
    public float? AnchorMinY { get; set; }
    public float? AnchorMaxX { get; set; }
    public float? AnchorMaxY { get; set; }
    public float? PivotX { get; set; }
    public float? PivotY { get; set; }
    public float? ScaleX { get; set; }
    public float? ScaleY { get; set; }
    public float? ScaleZ { get; set; }
}

internal sealed record ApiResponse<T>(bool Ok, T? Data, string? Error);
