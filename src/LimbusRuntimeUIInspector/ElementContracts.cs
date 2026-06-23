namespace LimbusRuntimeUIInspector.Contracts.Elements;

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
