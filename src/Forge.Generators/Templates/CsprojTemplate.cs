namespace GalA11yForge.Generators.Templates;

/// <summary>
/// 生成的插件工程文件（netstandard2.1 / Unity Mono / BepInEx 5）。
/// 刻意不引用 Assembly-CSharp：钩子目标用字符串 + AccessTools 解析，
/// 这样换游戏版本只影响运行时命中，不会让工程直接编译不过。
/// </summary>
internal static class CsprojTemplate
{
    internal const string Text = """
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <AssemblyName>{{ASSEMBLY}}</AssemblyName>
    <RootNamespace>{{NS}}</RootNamespace>
    <Version>{{VERSION}}</Version>
    <LangVersion>9.0</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <Deterministic>true</Deterministic>
    <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
    <EnableSourceLink>false</EnableSourceLink>
    <NoWarn>CS0169;CS0414;CS0649</NoWarn>

    <!-- 游戏安装目录：生成时写入本机路径；换机器构建时用 -p:GameDir=... 覆盖。 -->
    <GameDir Condition="'$(GameDir)' == ''">{{GAME_PATH_XML}}</GameDir>
    <!-- Unity 托管程序集目录；数据目录名不叫 <游戏名>_Data 时用 -p:ManagedDir=... 覆盖。 -->
    <ManagedDir Condition="'$(ManagedDir)' == ''">$(GameDir)\{{DATADIR}}\Managed</ManagedDir>
    <!-- BepInEx 5 的核心目录；默认用游戏里已装的那份。 -->
    <BepInExDir Condition="'$(BepInExDir)' == ''">$(GameDir)\BepInEx\core</BepInExDir>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="BepInEx">
      <HintPath>$(BepInExDir)\BepInEx.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="0Harmony">
      <HintPath>$(BepInExDir)\0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <ItemGroup>
    <Reference Include="UnityEngine">
      <HintPath>$(ManagedDir)\UnityEngine.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>$(ManagedDir)\UnityEngine.CoreModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.UIModule">
      <HintPath>$(ManagedDir)\UnityEngine.UIModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.UI">
      <HintPath>$(ManagedDir)\UnityEngine.UI.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.InputLegacyModule">
      <HintPath>$(ManagedDir)\UnityEngine.InputLegacyModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.TextRenderingModule">
      <HintPath>$(ManagedDir)\UnityEngine.TextRenderingModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Unity.TextMeshPro">
      <HintPath>$(ManagedDir)\Unity.TextMeshPro.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

</Project>
""";
}
