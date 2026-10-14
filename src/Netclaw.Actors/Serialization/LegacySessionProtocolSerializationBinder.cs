// -----------------------------------------------------------------------
// <copyright file="LegacySessionProtocolSerializationBinder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using Newtonsoft.Json.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Security;

namespace Netclaw.Actors.Serialization;

/// <summary>
/// Reads event identities that the session protocol refactor moved.
/// </summary>
internal sealed class LegacySessionProtocolSerializationBinder : ISerializationBinder
{
    private const string LegacyAssemblyName = "Netclaw.Actors";

    private static readonly DefaultSerializationBinder DefaultBinder = new();

    private static readonly IReadOnlyDictionary<string, Type> LegacyTypes =
        new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            ["Netclaw.Actors.Protocol.TurnRecorded"] = typeof(SessionProtocol.TurnRecorded),
            ["Netclaw.Actors.Protocol.ToolBatchStarted"] = typeof(SessionProtocol.ToolBatchStarted),
            ["Netclaw.Actors.Protocol.ToolCallRecorded"] = typeof(SessionProtocol.ToolCallRecorded),
            ["Netclaw.Actors.Protocol.TurnContextRecord"] = typeof(SessionProtocol.TurnContextRecord),
            ["Netclaw.Actors.Protocol.ToolApprovalRequested"] = typeof(SessionProtocol.ToolApprovalRequested),
            ["Netclaw.Actors.Protocol.ToolApprovalRequested+ApprovalCandidateRecord"] = typeof(ApprovalCandidate),
            ["Netclaw.Actors.Protocol.ToolApprovalResolved"] = typeof(SessionProtocol.ToolApprovalResolved),
            ["Netclaw.Actors.Protocol.ToolBatchAbandoned"] = typeof(SessionProtocol.ToolBatchAbandoned),
            ["Netclaw.Actors.Protocol.SessionBackgroundJobsReaped"] = typeof(SessionProtocol.SessionBackgroundJobsReaped),
            ["Netclaw.Actors.Protocol.AdoptedContextRecorded"] = typeof(SessionProtocol.AdoptedContextRecorded),
            ["Netclaw.Actors.Protocol.AdoptedContextRecorded+AdoptedMessageRecord"] = typeof(SessionProtocol.AdoptedContextRecorded.AdoptedMessageRecord),
            ["Netclaw.Actors.Protocol.SessionTitleSet"] = typeof(SessionProtocol.SessionTitleSet),
            ["Netclaw.Actors.Protocol.SessionCompacted"] = typeof(SessionProtocol.SessionCompacted)
        };

    /// <summary>
    /// Resolves only exact identities from the former persisted protocol namespace.
    /// </summary>
    public Type BindToType(string? assemblyName, string typeName)
    {
        if (assemblyName is not null
            && string.Equals(new AssemblyName(assemblyName).Name, LegacyAssemblyName, StringComparison.Ordinal)
            && LegacyTypes.TryGetValue(typeName, out var currentType))
        {
            return currentType;
        }

        return DefaultBinder.BindToType(assemblyName, typeName);
    }

    /// <summary>
    /// Keeps the existing type names for new JSON writes.
    /// </summary>
    public void BindToName(Type serializedType, out string? assemblyName, out string? typeName)
        => DefaultBinder.BindToName(serializedType, out assemblyName, out typeName);
}
