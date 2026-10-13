using System.Collections.Generic;

namespace Hephaestus.Features.Workshop;

/// <summary>One material a workshop phase needs, with the total quantity to deliver (set quantity × sets).</summary>
public sealed record WorkshopMaterial(uint ItemId, string Name, uint Count);

/// <summary>One phase of a workshop build — the materials to deliver before the next phase unlocks.</summary>
public sealed record WorkshopPhase(int Number, IReadOnlyList<WorkshopMaterial> Materials);

/// <summary>A FC-workshop part and its phases in build order, read from the CompanyCraft sheets.</summary>
public sealed record WorkshopBuild(uint PartItemId, string PartName, IReadOnlyList<WorkshopPhase> Phases);

/// <summary>A workshop part's catalogue entry for the picker: the part item, its name, and its type label.</summary>
public sealed record WorkshopPartInfo(uint ItemId, string Name, string TypeName);
