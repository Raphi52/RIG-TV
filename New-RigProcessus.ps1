#requires -Version 5.1
<#
.SYNOPSIS
    Scaffolds a new WPF native Processus Rig.Wpf.Processus.<CODE>.

.DESCRIPTION
    Creates the full structure (project, ViewModels, Views, Tests, sln/csproj wiring).
    After scaffolding, the skeleton compiles and 2-3 stub tests pass.
    Edit the generated files to add your business logic in TDD style.

.PARAMETER Code
    Processus code in uppercase (e.g. MANDATAIRE, IPE).

.PARAMETER Libelle
    Display label (used in tab and view header).

.EXAMPLE
    .\New-RigProcessus.ps1 -Code MANDATAIRE -Libelle "Gestion des mandataires"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Code,
    [Parameter(Mandatory)] [string] $Libelle
)

$ErrorActionPreference = 'Stop'

$Code = $Code.Trim().ToUpperInvariant()
if ($Code -notmatch '^[A-Z][A-Z0-9_]*$') {
    throw "Invalid code '$Code' (expected: UPPERCASE + digits + underscore)."
}

$pascal = ($Code.ToLowerInvariant() -split '_' | ForEach-Object {
    if ($_.Length -gt 0) { $_.Substring(0,1).ToUpperInvariant() + $_.Substring(1) }
}) -join ''

$root = Split-Path -Parent $PSCommandPath
$slnPath = Join-Path $root 'Rig.Wpf.sln'
if (-not (Test-Path $slnPath)) {
    throw "Rig.Wpf.sln not found in $root - run from Source\Wpf\."
}

$projDir = Join-Path $root "Rig.Wpf.Processus.$Code"
$testDir = Join-Path $root "Rig.Wpf.Processus.$Code.Tests"

if (Test-Path $projDir) { throw "Folder already exists: $projDir" }
if (Test-Path $testDir) { throw "Folder already exists: $testDir" }

Write-Host "Scaffolding Rig.Wpf.Processus.$Code..." -ForegroundColor Cyan

New-Item -ItemType Directory -Path $projDir, "$projDir\Etapes", "$projDir\Views",
    $testDir, "$testDir\Etapes" -Force | Out-Null

function Write-Utf8([string]$path, [string]$content) {
    [System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))
}

Write-Utf8 "$projDir\Rig.Wpf.Processus.$Code.csproj" @"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Library</OutputType>
    <RootNamespace>Rig.Wpf.Processus.$pascal</RootNamespace>
    <AssemblyName>Rig.Wpf.Processus.$Code</AssemblyName>
    <UseWPF>true</UseWPF>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rig.Wpf.Core\Rig.Wpf.Core.csproj" />
    <ProjectReference Include="..\Rig.Wpf.Mvvm\Rig.Wpf.Mvvm.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="8.0.2" />
  </ItemGroup>

</Project>
"@

Write-Utf8 "$projDir\${pascal}ProcessusViewModel.cs" @"
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Mvvm.Processus;
using Rig.Wpf.Processus.${pascal}.Etapes;

namespace Rig.Wpf.Processus.$pascal;

public sealed class ${pascal}ProcessusViewModel : ProcessusViewModelBase
{
    private readonly ${pascal}Etape1ViewModel _etape1;

    public ${pascal}ProcessusViewModel()
        : this(new ${pascal}Etape1ViewModel()) { }

    private ${pascal}ProcessusViewModel(${pascal}Etape1ViewModel etape1)
        : base(`"$Code`", `"$Libelle`", new[] { etape1 })
    {
        _etape1 = etape1;
        SaveCommand = new RelayCommand(Save, () => _etape1.IsValid);
        _etape1.IsValidChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
    }

    public RelayCommand SaveCommand { get; }

    private void Save()
    {
        // TODO: persistance via IDemandeRepository / mapper
    }
}
"@

Write-Utf8 "$projDir\Etapes\${pascal}Etape1ViewModel.cs" @"
using Rig.Wpf.Mvvm.Processus;

namespace Rig.Wpf.Processus.${pascal}.Etapes;

public sealed class ${pascal}Etape1ViewModel : EtapeViewModelBase
{
    public ${pascal}Etape1ViewModel()
        : base(`"${Code}_E1`", `"Etape 1`")
    {
        // TODO: add bindable properties; call SetIsValid(...) when relevant.
        SetIsValid(true);
    }
}
"@

Write-Utf8 "$projDir\${pascal}ServiceCollectionExtensions.cs" @"
using Microsoft.Extensions.DependencyInjection;
using Rig.Wpf.Mvvm.DependencyInjection;

namespace Rig.Wpf.Processus.$pascal;

public static class ${pascal}ServiceCollectionExtensions
{
    public static IServiceCollection Add${pascal}Processus(this IServiceCollection services)
        => services.AddNativeProcessus<${pascal}ProcessusViewModel>(`"$Code`");
}
"@

Write-Utf8 "$projDir\Views\${pascal}Etape1View.xaml" @"
<UserControl x:Class=`"Rig.Wpf.Processus.${pascal}.Views.${pascal}Etape1View`"
             xmlns=`"http://schemas.microsoft.com/winfx/2006/xaml/presentation`"
             xmlns:x=`"http://schemas.microsoft.com/winfx/2006/xaml`"
             xmlns:etapes=`"clr-namespace:Rig.Wpf.Processus.${pascal}.Etapes`"
             d:DataContext=`"{d:DesignInstance etapes:${pascal}Etape1ViewModel}`"
             xmlns:d=`"http://schemas.microsoft.com/expression/blend/2008`"
             xmlns:mc=`"http://schemas.openxmlformats.org/markup-compatibility/2006`"
             mc:Ignorable=`"d`">
    <StackPanel Margin=`"16`">
        <TextBlock Text=`"{Binding Libelle}`" FontSize=`"16`" FontWeight=`"SemiBold`" />
        <TextBlock Text=`"(scaffolded - edit this View to add your controls)`"
                   Foreground=`"#888`" Margin=`"0,8,0,0`" />
    </StackPanel>
</UserControl>
"@

Write-Utf8 "$projDir\Views\${pascal}Etape1View.xaml.cs" @"
using System.Windows.Controls;

namespace Rig.Wpf.Processus.${pascal}.Views;

public partial class ${pascal}Etape1View : UserControl
{
    public ${pascal}Etape1View() => InitializeComponent();
}
"@

Write-Utf8 "$projDir\Views\${pascal}ProcessusView.xaml" @"
<UserControl x:Class=`"Rig.Wpf.Processus.${pascal}.Views.${pascal}ProcessusView`"
             xmlns=`"http://schemas.microsoft.com/winfx/2006/xaml/presentation`"
             xmlns:x=`"http://schemas.microsoft.com/winfx/2006/xaml`"
             xmlns:vm=`"clr-namespace:Rig.Wpf.Processus.${pascal}`"
             xmlns:etapes=`"clr-namespace:Rig.Wpf.Processus.${pascal}.Etapes`"
             xmlns:views=`"clr-namespace:Rig.Wpf.Processus.${pascal}.Views`"
             d:DataContext=`"{d:DesignInstance vm:${pascal}ProcessusViewModel}`"
             xmlns:d=`"http://schemas.microsoft.com/expression/blend/2008`"
             xmlns:mc=`"http://schemas.openxmlformats.org/markup-compatibility/2006`"
             mc:Ignorable=`"d`">
    <UserControl.Resources>
        <DataTemplate DataType=`"{x:Type etapes:${pascal}Etape1ViewModel}`">
            <views:${pascal}Etape1View />
        </DataTemplate>
    </UserControl.Resources>

    <DockPanel>
        <Border DockPanel.Dock=`"Top`" Background=`"#F5F5F5`" Padding=`"16,8`">
            <TextBlock Text=`"{Binding Libelle}`" FontSize=`"18`" FontWeight=`"Bold`" />
        </Border>
        <Border DockPanel.Dock=`"Bottom`" Background=`"#EEEEEE`" Padding=`"16,8`">
            <Button Content=`"Enregistrer`" HorizontalAlignment=`"Right`" Padding=`"12,4`"
                    Command=`"{Binding SaveCommand}`" />
        </Border>
        <ContentControl Content=`"{Binding CurrentEtape}`" />
    </DockPanel>
</UserControl>
"@

Write-Utf8 "$projDir\Views\${pascal}ProcessusView.xaml.cs" @"
using System.Windows.Controls;

namespace Rig.Wpf.Processus.${pascal}.Views;

public partial class ${pascal}ProcessusView : UserControl
{
    public ${pascal}ProcessusView() => InitializeComponent();
}
"@

Write-Utf8 "$testDir\Rig.Wpf.Processus.$Code.Tests.csproj" @"
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Library</OutputType>
    <RootNamespace>Rig.Wpf.Processus.${pascal}.Tests</RootNamespace>
    <AssemblyName>Rig.Wpf.Processus.$Code.Tests</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Rig.Wpf.Processus.$Code\Rig.Wpf.Processus.$Code.csproj" />
  </ItemGroup>

</Project>
"@

Write-Utf8 "$testDir\${pascal}ProcessusViewModelTests.cs" @"
using FluentAssertions;
using Rig.Wpf.Processus.$pascal;
using Xunit;

namespace Rig.Wpf.Processus.${pascal}.Tests;

public class ${pascal}ProcessusViewModelTests
{
    [Fact]
    public void NewInstance_HasCorrectCode()
    {
        var sut = new ${pascal}ProcessusViewModel();

        sut.Code.Should().Be(`"$Code`");
    }

    [Fact]
    public void NewInstance_HasOneEtape()
    {
        var sut = new ${pascal}ProcessusViewModel();

        sut.Etapes.Should().ContainSingle();
        sut.CurrentEtape.Should().BeSameAs(sut.Etapes[0]);
    }
}
"@

Write-Utf8 "$testDir\Etapes\${pascal}Etape1ViewModelTests.cs" @"
using FluentAssertions;
using Rig.Wpf.Processus.${pascal}.Etapes;
using Xunit;

namespace Rig.Wpf.Processus.${pascal}.Tests.Etapes;

public class ${pascal}Etape1ViewModelTests
{
    [Fact]
    public void NewInstance_HasCorrectCodeAndLibelle()
    {
        var sut = new ${pascal}Etape1ViewModel();

        sut.Code.Should().Be(`"${Code}_E1`");
        sut.Libelle.Should().NotBeNullOrEmpty();
    }
}
"@

Push-Location $root
try {
    dotnet sln Rig.Wpf.sln add `
        "Rig.Wpf.Processus.$Code\Rig.Wpf.Processus.$Code.csproj" `
        "Rig.Wpf.Processus.$Code.Tests\Rig.Wpf.Processus.$Code.Tests.csproj" | Out-Null
} finally {
    Pop-Location
}

$shellCsproj = Join-Path $root "Rig.Wpf.Shell\Rig.Wpf.Shell.csproj"
$shellContent = Get-Content $shellCsproj -Raw
$ref = "    <ProjectReference Include=`"..\Rig.Wpf.Processus.$Code\Rig.Wpf.Processus.$Code.csproj`" />"
if ($shellContent -notlike "*$ref*") {
    $shellContent = $shellContent -replace `
        '(\s*<ProjectReference Include="\.\.\\Rig\.Wpf\.Processus\.TESTNLH\\Rig\.Wpf\.Processus\.TESTNLH\.csproj" />)', `
        "`$1`r`n$ref"
    Write-Utf8 $shellCsproj $shellContent
}

Write-Host ""
Write-Host "OK - Rig.Wpf.Processus.$Code created." -ForegroundColor Green
Write-Host ""
Write-Host "Manual steps remaining:" -ForegroundColor Yellow
Write-Host "  1. App.xaml: add the DataTemplate"
Write-Host "       <DataTemplate DataType=`"{x:Type p:${pascal}ProcessusViewModel}`">"
Write-Host "         <pviews:${pascal}ProcessusView />"
Write-Host "       </DataTemplate>"
Write-Host "       (with xmlns:p / xmlns:pviews pointing to the new project)"
Write-Host "  2. App.xaml.cs: call services.Add${pascal}Processus(); in ConfigureServices."
Write-Host "  3. appsettings.json: add `"$Code`" to RigWpf:UseNativeFor (to enable native mode)."
Write-Host "  4. Run tests: dotnet test Rig.Wpf.Processus.$Code.Tests"
Write-Host ""
