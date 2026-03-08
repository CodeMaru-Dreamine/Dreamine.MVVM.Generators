<!--!
\file README.md
\brief Dreamine.MVVM.Generators - Roslyn source generators for Dreamine MVVM.
\details Documents package purpose, installation, architecture, supported generators, usage examples, packaging behavior, and notes.
\author Dreamine
\date 2026-03-08
\version 1.0.6
-->

# Dreamine.MVVM.Generators

**Dreamine.MVVM.Generators** is the **Roslyn source-generator package** used by the Dreamine MVVM ecosystem.

It generates repetitive MVVM code at compile time based on declarative attributes such as:

- `DreamineProperty`
- `RelayCommand`
- `DreamineEntry`
- `DreamineModel`
- `DreamineEvent`
- `DreamineCommand`
- `DreamineModelProperty`

This package is designed to reduce boilerplate while keeping the architecture **explicit, generator-driven, and easy to reason about**.

[➡️ 한국어 문서 보기](README_KO.md)

---

## What this library solves

In MVVM projects, the same code patterns appear repeatedly:

- backing field → property exposure
- method → `ICommand` property generation
- model / event object exposure
- application bootstrap code
- proxy properties that forward to model state
- command methods that call event or service targets

Dreamine.MVVM.Generators moves those patterns into the **compile-time generation layer**, so ViewModels stay smaller and cleaner.

---

## Key Features

- Roslyn **incremental source generators**
- Works as a **.NET analyzer package**
- Generates MVVM boilerplate from Dreamine attributes
- Supports automatic command property generation
- Supports auto-wiring for model / event / property declarations
- Supports entry-point bootstrap generation
- Supports command forwarding generation
- Packs generator DLL into `analyzers/dotnet/cs`
- Provides `buildTransitive` registration so consumers get the analyzer automatically

---

## Requirements

- **Target Framework**: `netstandard2.0`
- **Roslyn dependencies**:
  - `Microsoft.CodeAnalysis.Common`
  - `Microsoft.CodeAnalysis.CSharp`
  - `Microsoft.CodeAnalysis.Analyzers`
- Usually used with:
  - `Dreamine.MVVM.Attributes`
  - `Dreamine.MVVM.Core`
  - WPF / .NET MVVM applications

---

## Installation

### Option A) NuGet

```bash
dotnet add package Dreamine.MVVM.Generators
```

### Option B) PackageReference

```xml
<ItemGroup>
  <PackageReference Include="Dreamine.MVVM.Generators" Version="1.0.6" />
</ItemGroup>
```

This package is built as an **Analyzer package**, and the project already includes a `buildTransitive` target file:

```xml
<Project>
  <ItemGroup>
    <Analyzer Include="$(MSBuildThisFileDirectory)..\analyzers\dotnet\cs\Dreamine.MVVM.Generators.dll" />
  </ItemGroup>
</Project>
```

That means consumers typically do **not** need to add a manual `<Analyzer Include="...">` entry when the NuGet package is set up correctly.

---

## Project Structure

```text
Dreamine.MVVM.Generators
├── DreamineAutoWiringGenerator.cs
├── DreamineCommandSourceGenerator.cs
├── DreamineEntryGenerator.cs
├── RelayCommandSourceGenerator.cs
├── buildTransitive/
│   └── Dreamine.MVVM.Generators.targets
├── Dreamine.MVVM.Generators.csproj
└── LICENSE
```

---

## Architecture Role

This package belongs to the **generation layer** of the Dreamine MVVM stack.

```text
ViewModel Source Code
        │
        ├─ Dreamine.MVVM.Attributes
        │     (markers / metadata)
        │
        ├─ Dreamine.MVVM.Generators
        │     (compile-time code generation)
        │
        └─ Dreamine.MVVM.Core
              (runtime MVVM infrastructure)
```

The basic responsibility split is:

- **Attributes** declare intent
- **Generators** emit source code
- **Core** executes runtime MVVM behavior

---

## Supported Generators

### 1) `RelayCommandSourceGenerator`

Generates `ICommand` properties from methods marked with `[RelayCommand]`.

Input:

```csharp
using Dreamine.MVVM.Attributes;

public partial class MainViewModel
{
    [RelayCommand]
    private void Save()
    {
    }
}
```

Generated intent:

```csharp
public ICommand SaveCommand => _SaveCommand ??= new RelayCommand(Save);
```

Behavior summary:

- scans attributed methods
- generates `{MethodName}Command`
- relies on `Dreamine.MVVM.Core.RelayCommand`

---

### 2) `DreamineAutoWiringGenerator`

Scans fields or properties marked with:

- `[DreamineModel]`
- `[DreamineEvent]`
- `[DreamineProperty]`

Typical responsibilities:

- expose fields as generated properties
- initialize model instances
- resolve event instances through `DMContainer`
- generate property wrappers for field-backed declarations

Example:

```csharp
using Dreamine.MVVM.Attributes;

public partial class MainViewModel
{
    [DreamineProperty]
    private string _title;

    [DreamineModel]
    private MainModel _model;

    [DreamineEvent]
    private MainEvent _event;
}
```

Typical generated intent:

- `_title` → `Title`
- `_model` → `Model`
- `_event` → `Event`

Notes:

- model declarations are initialized with `new {Type}()`
- event declarations are resolved via `DMContainer.Resolve<T>()`
- generated partial class inherits `ViewModelBase`

---

### 3) `DreamineEntryGenerator`

Generates bootstrap and application wiring code for types marked with `[DreamineEntry]`.

Typical responsibilities observed from the generator:

- application startup hookup
- DI auto-registration
- `ViewModelLocator` registration
- `FrameworkElement.Loaded` event hook
- automatic View ↔ ViewModel attachment
- late registration via dispatcher callback

Example:

```csharp
using Dreamine.MVVM.Attributes;

[DreamineEntry]
public partial class AppEntry
{
}
```

This generator is intended for **application entry / bootstrap scenarios**, not ordinary property generation.

---

### 4) `DreamineCommandSourceGenerator`

Processes `[DreamineCommand]` and supports command-forwarding scenarios such as:

- target method path declaration
- generated command property
- forwarding to `Event.*` or `Service.*` style targets
- optional `BindTo` behavior for assigning returned values

Example:

```csharp
using Dreamine.MVVM.Attributes;

public partial class MainViewModel
{
    [DreamineCommand("Event.ReadmeClick", BindTo = "Readme")]
    partial void LoadReadme();
}
```

This is more advanced than a basic relay command because it represents **declarative forwarding intent**.

---

## Quick Start

### 1) Add the required packages

```xml
<ItemGroup>
  <PackageReference Include="Dreamine.MVVM.Attributes" Version="1.0.4" />
  <PackageReference Include="Dreamine.MVVM.Core" Version="1.0.0" />
  <PackageReference Include="Dreamine.MVVM.Generators" Version="1.0.6" />
</ItemGroup>
```

---

### 2) Declare a ViewModel with attributes

```csharp
using Dreamine.MVVM.Attributes;

public partial class MainViewModel
{
    [DreamineProperty]
    private string _title;

    [RelayCommand]
    private void Save()
    {
    }
}
```

---

### 3) Build the project

At build time, the generator emits the required partial source files.

Typical outcomes:

- `Title` property generation
- `SaveCommand` property generation
- reduced handwritten MVVM boilerplate

---

## Packaging Notes

The project file is configured as:

- `PackageType=Analyzer`
- `OutputItemType=Analyzer`
- `IncludeBuildOutput=false`
- generator DLL packed into `analyzers/dotnet/cs`
- transitive analyzer registration included through `buildTransitive`

This is the correct packaging direction for a reusable source generator NuGet package.

---

## Important Notes

### 1) This package does not provide runtime behavior by itself

It generates source code, but runtime types still come from packages such as:

- `Dreamine.MVVM.Core`
- `Dreamine.MVVM.Attributes`

---

### 2) The generated code assumes Dreamine runtime conventions

For example, generated code references:

- `ViewModelBase`
- `RelayCommand`
- `DMContainer`
- `ViewModelLocator`

So the generator is intended to be used inside the **Dreamine MVVM stack**, not as a fully standalone general-purpose generator.

---

### 3) Existing older README statements may be outdated

Some earlier descriptions referenced names such as:

- `VsProperty`
- manual analyzer registration as always required

The current project structure indicates the package has moved to Dreamine naming and already includes `buildTransitive` analyzer registration support.

---

## Comparison

| Package | Role | Runtime Logic | Compile-Time Generation |
|---|---|---:|---:|
| Dreamine.MVVM.Attributes | declaration layer | No | No |
| Dreamine.MVVM.Generators | generation layer | No | Yes |
| Dreamine.MVVM.Core | runtime MVVM layer | Yes | No |

This separation keeps the system modular and aligned with SOLID-oriented layering.

---

## Recommended Package Pairing

This package is most useful when used together with:

```text
Dreamine.MVVM.Attributes
Dreamine.MVVM.Core
Dreamine WPF / UI / app packages
```

---

## License

MIT License
