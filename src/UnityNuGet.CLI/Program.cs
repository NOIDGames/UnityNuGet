using System.CommandLine;
using NuGet.Configuration;
using NuGet.PackageManagement;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using UnityNuGet;
using static NuGet.Frameworks.FrameworkConstants;

var output = new Option<string>(name: "--output", description: "Output directory", getDefaultValue: () => ".");
var scope =
    new Option<string>(name: "--scope", description: "Scope prefix for the name", getDefaultValue: () => "org.nuget");
var minUnityVersion =
    new Option<string>(name: "--unity", description: "Minimum unity version", getDefaultValue: () => "2022.2");
var configfile = new Option<string?>("--configfile", description: "The NuGet configuration file to use.");
var packageName = new Argument<string>(name: "name", description: "Nuget package name");
var packageVersion = new Argument<string>(name: "version", description: "Nuget package version");
var defineConstraints =
    new Option<List<string>>(name: "--define-constraints", description: "Add extra defineConstraints to the assembly") {
        Arity = ArgumentArity.ZeroOrMore,
    };
var analyzer = new Option<bool>(name: "--analyzer", description: "The package contains a Roslyn analyzer",
                                getDefaultValue: () => false);

var targetFrameworks = new RegistryTargetFramework[] {
    new() { Name = "netstandard2.1", DefineConstraints = Array.Empty<string>(),
            Framework = CommonFrameworks.NetStandard21 },
};

RootCommand rootCommand = new("Nuget -> UPM package converter");
rootCommand.AddOption(output);
rootCommand.AddOption(scope);
rootCommand.AddOption(minUnityVersion);
rootCommand.AddOption(configfile);
rootCommand.AddOption(defineConstraints);
rootCommand.AddOption(analyzer);
rootCommand.AddArgument(packageName);
rootCommand.AddArgument(packageVersion);

rootCommand.SetHandler(
    async (outDir, scope, unityVersion, config, packageName, packageVersion, defineConstraints, analyzer) =>
    {
        SourceCacheContext cacheContext = new();
        NuGetConsoleLogger logger = new();
        string? root = config switch {
            null => null,
            string r when Path.IsPathRooted(r) => "/",
            _ => ".",
        };
        var nugetSettings = Settings.LoadDefaultSettings(root: root, configFileName: config, machineWideSettings: null);

        Func<string, string> convertName = name => $"{scope}.{name.ToLowerInvariant()}";
        var npmPackageId = convertName(packageName);

        logger.LogInformation("Searching for NuGet package...");

        var repositories =
            new SourceRepositoryProvider(new PackageSourceProvider(nugetSettings), Repository.Provider.GetCoreV3())
                .GetRepositories();

        IPackageSearchMetadata? nugetMetadata = null;
        SourceRepository? nugetSource = null;

        foreach (var source in repositories)
        {
            nugetMetadata = await(await source.GetResourceAsync<PackageMetadataResource>())
                                .GetMetadataAsync(new PackageIdentity(packageName, new(packageVersion)), cacheContext,
                                                  logger, CancellationToken.None);
            if (nugetMetadata is not null)
            {
                nugetSource = source;
                break;
            }
        }
        if (nugetMetadata is null)
        {
            throw new Exception($"Unable to find nuget package: {packageName}-{packageVersion}");
        }

        UnityPackage manifest = new() {
            Name = npmPackageId,
            Version = packageVersion,
            Description = nugetMetadata.Description,
            Dependencies =
                NuGetHelper.GetCompatiblePackageDependencyGroups(nugetMetadata.DependencySets, targetFrameworks)
                    .SelectMany(dg => dg.Packages)
                    .ToDictionary(p => convertName(p.Id), p => p.VersionRange.MinVersion.OriginalVersion),
            Unity = unityVersion,
        };

        PackageBuilder converter = new(targetFrameworks, logger) { OutputDir = outDir };

        logger.LogInformation("Downloading Nuget package...");
        using var downloadResult = await PackageDownloader.GetDownloadResourceResultAsync(
            nugetSource, nugetMetadata.Identity, new PackageDownloadContext(cacheContext),
            SettingsUtility.GetGlobalPackagesFolder(nugetSettings), logger, CancellationToken.None);

        logger.LogInformation($"Converting NuGet package {nugetMetadata.Identity}");
        var result = await converter.BuildUPM(nugetMetadata.Identity, downloadResult.PackageReader, manifest,
                                              extraDefineConstraints: defineConstraints, isAnalyzer: analyzer);
        if (result is not null)
        {
            logger.LogInformation($"Wrote: {result}");
            if (manifest.Dependencies.Count > 0)
            {
                logger.LogInformation("With following dependencies:");
                foreach (var dep in manifest.Dependencies)
                {
                    logger.LogInformation($"{dep.Key} {dep.Value}");
                }
            }
        }
    },
    output, scope, minUnityVersion, configfile, packageName, packageVersion, defineConstraints, analyzer);

return rootCommand.InvokeAsync(args).Result;
