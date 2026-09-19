namespace Soenneker.Flywheel.Core.Stores.Librarian;

internal readonly record struct VersionKey(string Name, string Version, string InstanceId);
