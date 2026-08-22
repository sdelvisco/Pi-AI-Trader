# Intelligence.Tests

Normal run (mocked HTTP only, never touches the network):

```
dotnet test strategies/csharp/Intelligence.Tests/Intelligence.Tests.csproj --filter Category!=LiveSmoke
```

To deliberately run the one live smoke test against the real Azure AI Foundry endpoint (see `AzureLlmClientLiveSmokeTest.cs`):

```
dotnet test strategies/csharp/Intelligence.Tests/Intelligence.Tests.csproj --filter Category=LiveSmoke
```
