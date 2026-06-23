using System;
using System.Collections.Generic;
using LimbusRuntimeUIInspector.Contracts.Elements;

namespace LimbusRuntimeUIInspector.Contracts.Scanning;

internal sealed record ScannedUiElement(UiElement Element, IntPtr RectTransform);

internal sealed record ScanResult(
    int RootCount,
    int VisitedCount,
    int MatchedCount,
    int TransformOnlyCount,
    int ReadFailureCount,
    bool Truncated,
    List<ScannedUiElement> Elements);
