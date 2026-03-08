<!--!
\file README_KO.md
\brief Dreamine.MVVM.Generators - Dreamine MVVM용 Roslyn 소스 제너레이터 패키지.
\details 패키지 목적, 설치, 아키텍처, 제너레이터 구성, 사용 예제, 패키징 방식, 주의사항을 정리합니다.
\author Dreamine
\date 2026-03-08
\version 1.0.6
-->

# Dreamine.MVVM.Generators

**Dreamine.MVVM.Generators**는 Dreamine MVVM 생태계에서 사용하는 **Roslyn 소스 제너레이터 패키지**입니다.

이 패키지는 다음과 같은 선언용 Attribute를 기반으로, 반복되는 MVVM 코드를 **컴파일 타임에 자동 생성**합니다.

- `DreamineProperty`
- `RelayCommand`
- `DreamineEntry`
- `DreamineModel`
- `DreamineEvent`
- `DreamineCommand`
- `DreamineModelProperty`

즉, 사람이 직접 반복해서 작성하던 MVVM 보일러플레이트를 **Generator 계층으로 이동**시키는 역할을 합니다.

[➡️ English Version](README.md)

---

## 이 라이브러리가 해결하는 문제

MVVM 프로젝트에서는 다음과 같은 패턴이 반복적으로 등장합니다.

- backing field → public property 노출
- 메서드 → `ICommand` 프로퍼티 생성
- Model / Event 객체 노출
- 앱 부트스트랩 코드 생성
- Model 상태를 프록시하는 속성 생성
- Event / Service 대상으로 위임되는 커맨드 메서드 생성

Dreamine.MVVM.Generators는 이런 패턴을 **컴파일 시점 코드 생성 계층**으로 이동시켜, ViewModel 코드를 더 작고 명확하게 유지하게 해줍니다.

---

## 주요 기능

- Roslyn 기반 **Incremental Source Generator**
- **.NET Analyzer 패키지** 형태로 동작
- Dreamine Attribute를 기반으로 MVVM 보일러플레이트 생성
- 커맨드 프로퍼티 자동 생성 지원
- Model / Event / Property 선언에 대한 Auto-Wiring 지원
- Entry 타입 기반 부트스트랩 코드 생성 지원
- 명령 위임형 커맨드 생성 지원
- `analyzers/dotnet/cs` 경로에 패키징
- `buildTransitive`를 통해 소비 프로젝트에 자동 Analyzer 등록

---

## 요구사항

- **Target Framework**: `netstandard2.0`
- **Roslyn 의존성**:
  - `Microsoft.CodeAnalysis.Common`
  - `Microsoft.CodeAnalysis.CSharp`
  - `Microsoft.CodeAnalysis.Analyzers`
- 일반적으로 아래와 함께 사용합니다.
  - `Dreamine.MVVM.Attributes`
  - `Dreamine.MVVM.Core`
  - WPF / .NET MVVM 애플리케이션

---

## 설치

### 옵션 A) NuGet

```bash
dotnet add package Dreamine.MVVM.Generators
```

### 옵션 B) PackageReference

```xml
<ItemGroup>
  <PackageReference Include="Dreamine.MVVM.Generators" Version="1.0.6" />
</ItemGroup>
```

이 패키지는 **Analyzer 패키지**로 구성되어 있으며, 프로젝트 내부에 `buildTransitive` 타깃 파일이 포함되어 있습니다.

```xml
<Project>
  <ItemGroup>
    <Analyzer Include="$(MSBuildThisFileDirectory)..\analyzers\dotnet\cs\Dreamine.MVVM.Generators.dll" />
  </ItemGroup>
</Project>
```

즉, NuGet 패키징이 정상적으로 적용되면 일반적으로 소비 프로젝트에서 별도의 수동 `<Analyzer Include="...">` 등록이 항상 필요한 구조는 아닙니다.

---

## 프로젝트 구조

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

## 아키텍처 역할

이 패키지는 Dreamine MVVM 스택에서 **생성 계층(Generation Layer)** 역할을 담당합니다.

```text
ViewModel Source Code
        │
        ├─ Dreamine.MVVM.Attributes
        │     (마커 / 메타데이터)
        │
        ├─ Dreamine.MVVM.Generators
        │     (컴파일 타임 코드 생성)
        │
        └─ Dreamine.MVVM.Core
              (런타임 MVVM 인프라)
```

책임 분리는 다음과 같습니다.

- **Attributes**: 의도 선언
- **Generators**: 소스 코드 생성
- **Core**: 런타임 동작 제공

---

## 포함된 제너레이터

### 1) `RelayCommandSourceGenerator`

`[RelayCommand]`가 붙은 메서드로부터 `ICommand` 프로퍼티를 생성합니다.

입력 예:

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

생성 의도:

```csharp
public ICommand SaveCommand => _SaveCommand ??= new RelayCommand(Save);
```

동작 요약:

- Attribute가 붙은 메서드를 스캔
- `{MethodName}Command` 생성
- `Dreamine.MVVM.Core.RelayCommand`에 의존

---

### 2) `DreamineAutoWiringGenerator`

다음 Attribute가 붙은 필드/프로퍼티를 스캔합니다.

- `[DreamineModel]`
- `[DreamineEvent]`
- `[DreamineProperty]`

주요 역할:

- 필드를 생성 프로퍼티로 노출
- Model 인스턴스 자동 초기화
- Event 인스턴스를 `DMContainer`에서 Resolve
- 필드 기반 선언을 프로퍼티 래퍼로 생성

예제:

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

전형적인 생성 의도:

- `_title` → `Title`
- `_model` → `Model`
- `_event` → `Event`

추가 메모:

- Model 선언은 `new {Type}()`로 초기화
- Event 선언은 `DMContainer.Resolve<T>()` 사용
- 생성된 partial class는 `ViewModelBase`를 상속하도록 구성

---

### 3) `DreamineEntryGenerator`

`[DreamineEntry]`가 부여된 타입을 기준으로 **앱 시작 / 부트스트랩 코드**를 생성합니다.

제너레이터 내부 흐름상 확인되는 역할:

- 애플리케이션 시작 훅 연결
- DI 자동 등록
- `ViewModelLocator` 등록
- `FrameworkElement.Loaded` 이벤트 훅
- View ↔ ViewModel 자동 연결
- Dispatcher를 통한 지연 등록 호출

예제:

```csharp
using Dreamine.MVVM.Attributes;

[DreamineEntry]
public partial class AppEntry
{
}
```

이 제너레이터는 일반 프로퍼티 생성용이 아니라 **앱 진입점 / 초기화 시나리오**를 위한 제너레이터입니다.

---

### 4) `DreamineCommandSourceGenerator`

`[DreamineCommand]`를 처리하며, 다음과 같은 **명령 위임형 시나리오**를 지원합니다.

- 대상 메서드 경로 선언
- 커맨드 프로퍼티 생성
- `Event.*`, `Service.*` 형식 대상 호출
- 반환값을 특정 프로퍼티에 연결하는 `BindTo`

예제:

```csharp
using Dreamine.MVVM.Attributes;

public partial class MainViewModel
{
    [DreamineCommand("Event.ReadmeClick", BindTo = "Readme")]
    partial void LoadReadme();
}
```

이것은 단순 RelayCommand보다 더 상위 개념의 **선언적 포워딩 명령**입니다.

---

## 빠른 시작

### 1) 필요한 패키지 추가

```xml
<ItemGroup>
  <PackageReference Include="Dreamine.MVVM.Attributes" Version="1.0.4" />
  <PackageReference Include="Dreamine.MVVM.Core" Version="1.0.0" />
  <PackageReference Include="Dreamine.MVVM.Generators" Version="1.0.6" />
</ItemGroup>
```

---

### 2) Attribute 기반 ViewModel 선언

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

### 3) 프로젝트 빌드

빌드 시점에 제너레이터가 partial 소스를 생성합니다.

전형적인 결과:

- `Title` 프로퍼티 생성
- `SaveCommand` 프로퍼티 생성
- 수동 MVVM 반복 코드 감소

---

## 패키징 메모

이 프로젝트는 다음처럼 설정되어 있습니다.

- `PackageType=Analyzer`
- `OutputItemType=Analyzer`
- `IncludeBuildOutput=false`
- Generator DLL을 `analyzers/dotnet/cs`에 포함
- `buildTransitive`를 통한 Analyzer 자동 등록

즉, **재사용 가능한 Source Generator NuGet 패키지 방향**으로 올바르게 구성되어 있습니다.

---

## 중요 참고 사항

### 1) 이 패키지 자체가 런타임 동작을 제공하지는 않습니다

이 패키지는 코드를 생성하지만, 실제 런타임 타입은 보통 아래 패키지에서 옵니다.

- `Dreamine.MVVM.Core`
- `Dreamine.MVVM.Attributes`

---

### 2) 생성된 코드는 Dreamine 런타임 규약을 전제로 합니다

예를 들어 생성 코드가 다음 타입을 참조합니다.

- `ViewModelBase`
- `RelayCommand`
- `DMContainer`
- `ViewModelLocator`

즉, 이 Generator는 완전 독립 범용 Generator보다는 **Dreamine MVVM 스택 내부용 Generator**에 가깝습니다.

---

### 3) 기존 README의 일부 설명은 현재 구조와 맞지 않을 수 있습니다

이전 설명에는 예를 들어:

- `VsProperty`
- Analyzer를 항상 수동 등록해야 한다는 가정

같은 표현이 있었지만, 현재 프로젝트 구조를 보면 Dreamine 네이밍으로 이미 전환되어 있고 `buildTransitive`를 통해 Analyzer 자동 등록도 고려된 상태입니다.

---

## 비교

| 패키지 | 역할 | 런타임 로직 | 컴파일 타임 생성 |
|---|---|---:|---:|
| Dreamine.MVVM.Attributes | 선언 계층 | 아니오 | 아니오 |
| Dreamine.MVVM.Generators | 생성 계층 | 아니오 | 예 |
| Dreamine.MVVM.Core | 런타임 계층 | 예 | 아니오 |

이 구조는 모듈 분리를 명확하게 유지한다는 점에서 SOLID 관점에도 잘 맞습니다.

---

## 함께 쓰면 좋은 패키지

이 패키지는 보통 아래와 함께 사용할 때 가장 유용합니다.

```text
Dreamine.MVVM.Attributes
Dreamine.MVVM.Core
Dreamine WPF / UI / App 패키지
```

---

## License

MIT License
