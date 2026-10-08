# Installation

## Requirements

- **Unity 2021.3** or newer for the Unity package. The source generator that writes [`Init` for you](../guides/source-generator.md) is picked up by Unity 2021.2+, so it works on every supported Unity version.
- **.NET**: any runtime that supports **.NET Standard 2.1**, or .NET 8.

## Unity

In the Package Manager choose **Add package from git URL** and paste:

```
https://github.com/KenseiTheOne/KenseiECS.git?path=/KenseiECS
```

To pin a version, append the tag:

```
https://github.com/KenseiTheOne/KenseiECS.git?path=/KenseiECS#v2.1.0
```

The package ships two assembly definitions, `KenseiECS` and `KenseiECS.Editor`; both are auto-referenced, so your code in `Assembly-CSharp` sees the framework without extra setup. Dropping the `KenseiECS/` folder into `Assets/` works too.

The package also contains the `Samples~/BasicGame` sample (Package Manager -> Samples); see [BasicGame Sample](../unity/sample.md).

::: tip IL2CPP
IL2CPP builds are supported. The hot types carry `Il2CppSetOption` to drop null and bounds checks, the `IAutoReset`/`IAutoCopy` bridges avoid runtime generic instantiation, and nothing uses `Reflection.Emit` or runtime code generation. Burst is not supported: `World`, pools and filters are managed classes. See the [FAQ](../faq.md#does-it-work-with-il2cpp-with-burst).
:::

## .NET

### From source

There is no NuGet package; build against the repository sources. Use one of these:

- Reference the project `KenseiECS.NET/KenseiECS.csproj`. It targets `netstandard2.1` and `net8.0` and compiles the package sources (everything under `KenseiECS/` except the editor code and the samples; the Unity-only files compile to nothing outside Unity).
- Or compile the `KenseiECS/Core` and `KenseiECS/Systems` sources directly into your project, as the test project does.

```xml
<ItemGroup>
  <ProjectReference Include="path/to/KenseiECS/KenseiECS.NET/KenseiECS.csproj" />
</ItemGroup>
```

A project reference does not bring the source generator along. To use it, reference the prebuilt generator as an analyzer:

```xml
<ItemGroup>
  <Analyzer Include="path/to/KenseiECS/KenseiECS/Plugins/KenseiECS.Generators.dll" />
</ItemGroup>
```

Building from source is also how you turn on [debug validation](#debug-validation) in .NET.

## Debug validation

Misuse checks (dead or stale handles, double visits in filter loops, runner misuse) are compiled only when the `KENSEI_DEBUG` define is set. See [Release vs. KENSEI_DEBUG](../guides/debug-mode.md) for what changes.

- **Unity**: toggle **KenseiECS -> Debug Mode**; it adds the define to every build target.
- **.NET, sources compiled into your project**: add the define to that project.

  ```xml
  <PropertyGroup>
    <DefineConstants>$(DefineConstants);KENSEI_DEBUG</DefineConstants>
  </PropertyGroup>
  ```

- **.NET, `KenseiECS.NET/KenseiECS.csproj` referenced**: the define must be set where the framework itself is compiled. That project adds it when built with the `KenseiDebug` property, e.g. `dotnet build -p:KenseiDebug=true`.

Next: the [Quick Start](./quick-start.md).
