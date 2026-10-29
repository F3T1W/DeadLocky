namespace DeadLocky.Features.ManageMods.Model;

public sealed record CameraRepairResult(Guid PackageId, string Name, bool Repaired, string Detail);
