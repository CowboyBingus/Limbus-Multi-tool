using System.Collections.Generic;
using LimbusRuntimeUIInspector.Contracts.Api;
using LimbusRuntimeUIInspector.Contracts.Elements;
using LimbusRuntimeUIInspector.Contracts.Jobs;
using LimbusRuntimeUIInspector.Contracts.Scanning;

namespace LimbusRuntimeUIInspector.Contracts;

internal static class InspectorContracts
{
    public static ApiResponse<JobSnapshot> Success(JobSnapshot snapshot) => InspectorApiContracts.Success(snapshot);

    public static ApiResponse<object> Failure(string error) => InspectorApiContracts.Failure<object>(error);

    public static bool HasEditTarget(EditRequest? request) => InspectorApiContracts.HasEditTarget(request);

    public static IReadOnlyList<UiElement> ElementsFromScan(ScanResult scan)
    {
        return scan.Elements.ConvertAll(item => item.Element);
    }

    public static ElementList ElementList(IReadOnlyList<UiElement> elements)
    {
        return new ElementList(elements.Count, elements);
    }
}
