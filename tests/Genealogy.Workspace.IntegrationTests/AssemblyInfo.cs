using Xunit;

// Each test class currently owns a WorkspaceEnvironmentFixture. On a clean
// machine those fixtures would otherwise race to create the same Compose
// service before Docker has recorded the first container. The databases used
// by individual tests remain isolated; only environment bootstrap is serialized.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

