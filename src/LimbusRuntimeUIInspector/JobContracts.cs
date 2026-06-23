using System.Collections.Generic;
using LimbusRuntimeUIInspector.Contracts.Elements;

namespace LimbusRuntimeUIInspector.Contracts.Jobs;

internal sealed record ElementList(int Count, IReadOnlyList<UiElement> Elements);

internal sealed record JobSnapshot(
    int JobId,
    string Kind,
    string State,
    string? Error,
    ElementList? Result,
    UiElement? Element,
    long ElapsedMs);
