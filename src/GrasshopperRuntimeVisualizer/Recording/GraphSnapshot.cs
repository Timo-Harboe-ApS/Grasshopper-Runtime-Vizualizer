using System.Text.Json;
using System.Text.Json.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace GrasshopperRuntimeVisualizer.Recording;

public static class GraphSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static void Write(GH_Document document, string path)
    {
        var groups = document.Objects
            .OfType<GH_Group>()
            .Select(group => new GroupRecord(
                group.InstanceGuid,
                group.NickName,
                group.ObjectIDs.ToArray(),
                RectRecord.From(group.Attributes?.Bounds)))
            .ToArray();

        var groupLookup = groups
            .SelectMany(group => group.ObjectIds.Select(id => (ObjectId: id, GroupId: group.Id)))
            .GroupBy(pair => pair.ObjectId)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.GroupId).ToArray());

        var objects = document.Objects
            .Select(obj => ObjectRecord.From(obj, groupLookup.TryGetValue(obj.InstanceGuid, out var groupIds) ? groupIds : []))
            .ToArray();

        var parameters = document.Objects
            .SelectMany(ObjectParameters)
            .DistinctBy(param => param.Id)
            .ToArray();

        var connections = AllParams(document)
            .SelectMany(ParamConnections)
            .ToArray();

        var snapshot = new GraphRecord(
            document.DocumentID,
            document.DisplayName,
            DateTimeOffset.Now,
            objects,
            parameters,
            connections,
            groups);

        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    private static IEnumerable<ParamRecord> ObjectParameters(IGH_DocumentObject obj)
    {
        if (obj is IGH_Param looseParam)
        {
            yield return ParamRecord.From(looseParam, obj.InstanceGuid, "loose", 0);
        }

        if (obj is not IGH_Component component)
        {
            yield break;
        }

        for (var index = 0; index < component.Params.Input.Count; index++)
        {
            yield return ParamRecord.From(component.Params.Input[index], component.InstanceGuid, "input", index);
        }

        for (var index = 0; index < component.Params.Output.Count; index++)
        {
            yield return ParamRecord.From(component.Params.Output[index], component.InstanceGuid, "output", index);
        }
    }

    private static IEnumerable<IGH_Param> AllParams(GH_Document document)
    {
        foreach (var obj in document.Objects)
        {
            if (obj is IGH_Param looseParam)
            {
                yield return looseParam;
            }

            if (obj is not IGH_Component component)
            {
                continue;
            }

            foreach (var input in component.Params.Input)
            {
                yield return input;
            }

            foreach (var output in component.Params.Output)
            {
                yield return output;
            }
        }
    }

    private static IEnumerable<ConnectionRecord> ParamConnections(IGH_Param targetParam)
    {
        var targetObject = targetParam.Attributes?.GetTopLevel.DocObject ?? targetParam;

        for (var sourceIndex = 0; sourceIndex < targetParam.Sources.Count; sourceIndex++)
        {
            var sourceParam = targetParam.Sources[sourceIndex];
            var sourceObject = sourceParam.Attributes?.GetTopLevel.DocObject ?? sourceParam;

            yield return new ConnectionRecord(
                sourceObject.InstanceGuid,
                sourceParam.InstanceGuid,
                sourceParam.NickName,
                targetObject.InstanceGuid,
                targetParam.InstanceGuid,
                targetParam.NickName,
                sourceIndex);
        }
    }
}

public sealed record GraphRecord(
    Guid DocumentId,
    string Name,
    DateTimeOffset CapturedAt,
    ObjectRecord[] Objects,
    ParamRecord[] Parameters,
    ConnectionRecord[] Connections,
    GroupRecord[] Groups);

public sealed record ObjectRecord(
    Guid Id,
    Guid ComponentGuid,
    string Name,
    string Nickname,
    string Type,
    string Category,
    string SubCategory,
    RectRecord Bounds,
    Guid[] GroupIds)
{
    public static ObjectRecord From(IGH_DocumentObject obj, Guid[] groupIds)
    {
        return new ObjectRecord(
            obj.InstanceGuid,
            obj.ComponentGuid,
            obj.Name,
            obj.NickName,
            obj.GetType().FullName ?? obj.GetType().Name,
            obj.Category,
            obj.SubCategory,
            RectRecord.From(obj.Attributes?.Bounds),
            groupIds);
    }
}

public sealed record ParamRecord(
    Guid Id,
    Guid OwnerObject,
    string Name,
    string Nickname,
    string Kind,
    int Index,
    RectRecord Bounds,
    PointRecord? InputGrip,
    PointRecord? OutputGrip)
{
    public static ParamRecord From(IGH_Param param, Guid ownerObject, string kind, int index)
    {
        var attributes = param.Attributes;

        return new ParamRecord(
            param.InstanceGuid,
            ownerObject,
            param.Name,
            param.NickName,
            kind,
            index,
            RectRecord.From(attributes?.Bounds),
            attributes is { HasInputGrip: true } ? PointRecord.From(attributes.InputGrip) : null,
            attributes is { HasOutputGrip: true } ? PointRecord.From(attributes.OutputGrip) : null);
    }
}

public sealed record ConnectionRecord(
    Guid SourceObject,
    Guid SourceParam,
    string SourceParamName,
    Guid TargetObject,
    Guid TargetParam,
    string TargetParamName,
    int SourceIndex);

public sealed record GroupRecord(Guid Id, string Nickname, Guid[] ObjectIds, RectRecord Bounds);

public sealed record PointRecord(float X, float Y)
{
    public static PointRecord From(System.Drawing.PointF point)
    {
        return new PointRecord(point.X, point.Y);
    }
}

public sealed record RectRecord(float X, float Y, float Width, float Height)
{
    public static RectRecord From(System.Drawing.RectangleF? rect)
    {
        var value = rect ?? System.Drawing.RectangleF.Empty;
        return new RectRecord(value.X, value.Y, value.Width, value.Height);
    }
}
