using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LimbusRuntimeUIInspector.Contracts;

internal sealed record ScannedUiElement(UiElement Element, IntPtr RectTransform);

internal sealed record ScanResult(
    int RootCount,
    int VisitedCount,
    int MatchedCount,
    int TransformOnlyCount,
    int ReadFailureCount,
    bool Truncated,
    List<ScannedUiElement> Elements);

internal sealed record ElementList(int Count, IReadOnlyList<UiElement> Elements);

internal sealed record JobSnapshot(
    int JobId,
    string Kind,
    string State,
    string? Error,
    ElementList? Result,
    UiElement? Element,
    long ElapsedMs);

internal sealed record UiElement(
    int Id,
    string Name,
    string Path,
    string Kind,
    bool ActiveSelf,
    bool ActiveInHierarchy,
    float AnchoredX,
    float AnchoredY,
    float Width,
    float Height,
    float AnchorMinX,
    float AnchorMinY,
    float AnchorMaxX,
    float AnchorMaxY,
    float PivotX,
    float PivotY,
    float ScaleX,
    float ScaleY,
    float ScaleZ);

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

