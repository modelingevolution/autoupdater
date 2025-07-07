using Xunit;

namespace ModelingEvolution.AutoUpdater.IntegrationTests;

/// <summary>
/// Test collection to ensure AutoUpdater tests don't run in parallel
/// </summary>
[CollectionDefinition("AutoUpdater Tests")]
public class AutoUpdaterTestCollection : ICollectionFixture<AutoUpdaterTestFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the ICollectionFixture<> interfaces.
}