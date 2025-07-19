# SoC

## DockerComposeConfiguration

It looks as if it being used for 2 purposes:
1) As a configuration for the Docker Compose file
2) As state for UI (INotifyPropertyChanged)

Solution:
- Split into two classes: `DockerComposeConfiguration` for configuration 
- and `PackageState` for UI state

Implementation details:

Most likely all these properties:

- OperationMessage, 
- AvailableUpgrade, 
- IsUpgradeAvailable, 
- IsPackageValid, 
- StatusText 
- StatusColor

Should be extracted to PackageState (INotifyPropertyChanged) class.

In other to make this work we would follow CQRS pattern. 
Services, that work on DockerComposeConfiguration will publish events through EventHub,
Please take a look at ./micro-plumberd/ for documentation to use similar patterns and names.
That will be consumed by a ReadModel (similiar to how EventHandlers/ReadModels are written in rocket-welder2 using micro-plumberd).

The read model would have Given(TEent) methods that would update the PackageState.
The read model shall have clastered in-memory index ConcurrentDictionary<PackageName, Item>;
record Item(PackageState State, DockerComposeConfiguration Config);
And bindable ObservableCollection<Item>;
