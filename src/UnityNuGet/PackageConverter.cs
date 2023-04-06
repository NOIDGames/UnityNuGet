using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NUglify.Html;

namespace UnityNuGet
{
public class PackageBuilder
{
    private static readonly Encoding Utf8EncodingNoBom = new UTF8Encoding(false, false);

    public RegistryTargetFramework[] TargetFrameworks { get; set; }

    public string OutputDir { get; set; } = "";

    private readonly ILogger logger;

    public PackageBuilder(RegistryTargetFramework[] targetFrameworks, ILogger logger)
    {
        this.logger = logger;
        this.TargetFrameworks = targetFrameworks;
    }

    /// <summary>
    /// Converts a NuGet package to a Unity package.
    /// </summary>
    public async Task<string?> BuildUPM(PackageIdentity nugetPackage, PackageReaderBase packageReader,
                                        UnityPackage unityPackage, bool isAnalyzer = false,
                                        List<string>? extraDefineConstraints = null, string? license = null)
    {
        var outputPath = Path.Combine(OutputDir, $"{unityPackage.Name}-{unityPackage.Version}.tgz");

        try
        {
            using var memStream = new MemoryStream();

            using (var outStream = File.Create(outputPath)) using (
                var gzoStream = new GZipOutputStream(
                    outStream)) using (var tarArchive = new TarOutputStream(gzoStream, Encoding.UTF8))
            {
                // Select the framework version that is the closest or equal to the latest configured framework version
                var versions = await packageReader.GetLibItemsAsync(CancellationToken.None);

                var closestVersions = NuGetHelper.GetClosestFrameworkSpecificGroups(versions, TargetFrameworks);

                var collectedItems = new Dictionary<FrameworkSpecificGroup, HashSet<RegistryTargetFramework>>();

                foreach (var closestVersion in closestVersions)
                {
                    if (!collectedItems.TryGetValue(closestVersion.Item1, out var frameworksPerGroup))
                    {
                        frameworksPerGroup = new HashSet<RegistryTargetFramework>();
                        collectedItems.Add(closestVersion.Item1, frameworksPerGroup);
                    }
                    frameworksPerGroup.Add(closestVersion.Item2);
                }

                if (!isAnalyzer && collectedItems.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"The package does not contain a compatible .NET assembly {string.Join(",", TargetFrameworks.Select(x => x.Name))}");
                }

                var isPackageNetStandard21Assembly = DotNetHelper.IsNetStandard21Assembly(nugetPackage.Id);
                var hasMultiNetStandard = collectedItems.Count > 1;
                var hasOnlyNetStandard21 =
                    collectedItems.Count == 1 && collectedItems.Values.First().All(x => x.Name == "netstandard2.1");

                if (isPackageNetStandard21Assembly)
                {
                    logger.LogInformation(
                        $"Package {nugetPackage.Id} is a system package for netstandard2.1 and will be only used for netstandard 2.0");
                }

                if (isAnalyzer)
                {
                    var packageFiles =
                        await packageReader.GetItemsAsync(PackagingConstants.Folders.Analyzers, CancellationToken.None);

                    var analyzerFiles =
                        packageFiles.SelectMany(p => p.Items).Where(p => p.StartsWith("analyzers/dotnet/cs")).ToArray();

                    if (analyzerFiles.Length == 0)
                    {
                        analyzerFiles =
                            packageFiles.SelectMany(p => p.Items).Where(p => p.StartsWith("analyzers")).ToArray();
                    }

                    var createdDirectoryList = new List<string>();

                    foreach (var analyzerFile in analyzerFiles)
                    {
                        var folderPrefix =
                            $"{Path.GetDirectoryName(analyzerFile)!.Replace($"analyzers{Path.DirectorySeparatorChar}", string.Empty)}{Path.DirectorySeparatorChar}";

                        // Write folder meta
                        if (!string.IsNullOrEmpty(folderPrefix))
                        {
                            var directoryNameBuilder = new StringBuilder();

                            foreach (var directoryName in folderPrefix.Split(Path.DirectorySeparatorChar,
                                                                             StringSplitOptions.RemoveEmptyEntries))
                            {
                                directoryNameBuilder.Append(directoryName);
                                directoryNameBuilder.Append(Path.DirectorySeparatorChar);

                                var processedDirectoryName = directoryNameBuilder.ToString()[0.. ^ 1];

                                if (createdDirectoryList.Any(d => d.Equals(processedDirectoryName)))
                                {
                                    continue;
                                }

                                createdDirectoryList.Add(processedDirectoryName);

                                // write meta file for the folder
                                await WriteTextFileToTar(
                                    tarArchive, $"{processedDirectoryName}.meta",
                                    UnityMeta.GetMetaForFolder(GetStableGuid(nugetPackage, processedDirectoryName)));
                            }
                        }

                        var fileInUnityPackage = $"{folderPrefix}{Path.GetFileName(analyzerFile)}";
                        string ? meta;

                        string fileExtension = Path.GetExtension(fileInUnityPackage);
                        if (fileExtension == ".dll")
                        {
                            meta = UnityMeta.GetMetaForDll(GetStableGuid(nugetPackage, fileInUnityPackage), false,
                                                           new string[] { "RoslynAnalyzer" }, Array.Empty<string>());
                        }
                        else
                        {
                            meta = UnityMeta.GetMetaForExtension(GetStableGuid(nugetPackage, fileInUnityPackage),
                                                                 fileExtension);
                        }

                        if (meta == null)
                        {
                            continue;
                        }

                        memStream.Position = 0;
                        memStream.SetLength(0);

                        using var stream = await packageReader.GetStreamAsync(analyzerFile, CancellationToken.None);
                        await stream.CopyToAsync(memStream);
                        var buffer = memStream.ToArray();

                        // write content
                        await WriteBufferToTar(tarArchive, fileInUnityPackage, buffer);

                        // write meta file
                        await WriteTextFileToTar(tarArchive, $"{fileInUnityPackage}.meta", meta);
                    }
                }

                foreach (var groupToFrameworks in collectedItems)
                {
                    var item = groupToFrameworks.Key;
                    var frameworks = groupToFrameworks.Value;

                    var folderPrefix = hasMultiNetStandard ? $"{frameworks.First().Name}/" : "";
                    foreach (var file in item.Items)
                    {
                        var fileInUnityPackage = $"{folderPrefix}{Path.GetFileName(file)}";
                        string ? meta;

                        string fileExtension = Path.GetExtension(fileInUnityPackage);
                        if (fileExtension == ".dll")
                        {
                            // If we have multiple .NETStandard supported or there is just netstandard2.1 or the package
                            // can only be used when it is not netstandard 2.1 We will use the define coming from the
                            // configuration file Otherwise, it means that the assembly is compatible with whatever
                            // netstandard, and we can simply use NET_STANDARD
                            var defineConstraints =
                                hasMultiNetStandard || hasOnlyNetStandard21 || isPackageNetStandard21Assembly
                                    ? frameworks.First(x => x.Framework == item.TargetFramework).DefineConstraints
                                    : Array.Empty<string>();
                            meta = UnityMeta.GetMetaForDll(
                                GetStableGuid(nugetPackage, fileInUnityPackage), true, Array.Empty<string>(),
                                defineConstraints != null
                                    ? defineConstraints.Concat(extraDefineConstraints?.AsEnumerable() ??
                                                               Array.Empty<string>())
                                    : Array.Empty<string>());
                        }
                        else
                        {
                            meta = UnityMeta.GetMetaForExtension(GetStableGuid(nugetPackage, fileInUnityPackage),
                                                                 fileExtension);
                        }

                        if (meta == null)
                        {
                            continue;
                        }

                        memStream.Position = 0;
                        memStream.SetLength(0);

                        using var stream = await packageReader.GetStreamAsync(file, CancellationToken.None);
                        await stream.CopyToAsync(memStream);
                        var buffer = memStream.ToArray();

                        // write content
                        await WriteBufferToTar(tarArchive, fileInUnityPackage, buffer);

                        // write meta file
                        await WriteTextFileToTar(tarArchive, $"{fileInUnityPackage}.meta", meta);
                    }

                    // Write folder meta
                    if (!string.IsNullOrEmpty(folderPrefix) && item.Items.Any())
                    {
                        // write meta file for the folder
                        await WriteTextFileToTar(tarArchive, $"{folderPrefix[0..^1]}.meta",
                                                 UnityMeta.GetMetaForFolder(GetStableGuid(nugetPackage, folderPrefix)));
                    }
                }

                if (!isAnalyzer && collectedItems.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"The package does not contain a compatible .NET assembly {string.Join(",", TargetFrameworks.Select(x => x.Name))}");
                }

                // Write the native libraries
                var nativeFiles = NativeLibraries.GetSupportedNativeLibsAsync(packageReader, logger);
                var nativeFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                await foreach (var (file, folders, platform, architecture) in nativeFiles)
                {
                    string extension = Path.GetExtension(file);
                    var guid = GetStableGuid(nugetPackage, file);
                    string? meta = extension switch { ".dll" or ".so" or ".dylib" => NativeLibraries.GetMetaForNative(
                                                          guid, platform, architecture, Array.Empty<string>()),
                                                      _ => UnityMeta.GetMetaForExtension(guid, extension) };

                    if (meta == null)
                    {
                        logger.LogInformation($"Skipping file without meta: {file} ...");
                        continue;
                    }

                    memStream.SetLength(0);
                    using var stream = await packageReader.GetStreamAsync(file, CancellationToken.None);
                    await stream.CopyToAsync(memStream);
                    var buffer = memStream.ToArray();

                    await WriteBufferToTar(tarArchive, file, buffer);
                    await WriteTextFileToTar(tarArchive, $"{file}.meta", meta);

                    // Remember all folders for meta files
                    string folder = "";

                    foreach (var relative in folders)
                    {
                        folder = Path.Combine(folder, relative);
                        nativeFolders.Add(folder);
                    }
                }

                foreach (var folder in nativeFolders)
                {
                    await WriteTextFileToTar(tarArchive, $"{folder}.meta",
                                             UnityMeta.GetMetaForFolder(GetStableGuid(nugetPackage, folder)));
                }

                // Write the package,json
                var unityPackageAsJson = unityPackage.ToJson();
                const string packageJsonFileName = "package.json";
                await WriteTextFileToTar(tarArchive, packageJsonFileName, unityPackageAsJson);
                await WriteTextFileToTar(
                    tarArchive, $"{packageJsonFileName}.meta",
                    UnityMeta.GetMetaForExtension(GetStableGuid(nugetPackage, packageJsonFileName), ".json")!);

                // Write the license to the package if any
                if (!string.IsNullOrEmpty(license))
                {
                    const string licenseMdFile = "License.md";
                    await WriteTextFileToTar(tarArchive, licenseMdFile, license);
                    await WriteTextFileToTar(
                        tarArchive, $"{licenseMdFile}.meta",
                        UnityMeta.GetMetaForExtension(GetStableGuid(nugetPackage, licenseMdFile), ".md")!);
                }
            }
            return outputPath;
        }
        catch (Exception ex)
        {
            try
            {
                File.Delete(outputPath);
            }
            catch
            {
                // ignored
            }

            logger.LogWarning($"Error while processing package `{nugetPackage}`. Reason: {ex}");
            return null;
        }
    }

    public static async Task<string?> GetLicenseText(IPackageSearchMetadata packageMeta)
    {
        string? license = null;
        if (!string.IsNullOrEmpty(packageMeta.LicenseMetadata?.License))
        {
            license = packageMeta.LicenseMetadata.License;
        }
        var licenseUrl = packageMeta.LicenseMetadata?.LicenseUrl.ToString() ?? packageMeta.LicenseUrl?.ToString();
        if (!string.IsNullOrEmpty(licenseUrl))
        {
            try
            {
                // Try to fetch the license from an URL
                using var httpClient = new HttpClient();
                var licenseUrlText = await httpClient.GetStringAsync(licenseUrl);
                // If the license text is HTML, try to remove all text
                if (licenseUrlText != null)
                {
                    licenseUrlText = licenseUrlText.Trim();
                    if (licenseUrlText.StartsWith("<"))
                    {
                        try
                        {
                            licenseUrlText =
                                NUglify.Uglify.HtmlToText(licenseUrlText, HtmlToTextOptions.KeepStructure).Code ??
                                licenseUrlText;
                        }
                        catch
                        {
                            // ignored
                        }
                    }
                    // If the license fetched from the URL is bigger, use that one to put into the file
                    if (license == null || licenseUrlText.Length > license.Length)
                    {
                        license = licenseUrlText;
                    }
                }
            }
            catch
            {
                // ignored
            }
        }
        return license;
    }

    private static async Task WriteBufferToTar(TarOutputStream tarOut, string filePath, byte[] buffer,
                                               CancellationToken cancellationToken = default)
    {
        filePath = filePath.Replace(@"\", "/");
        filePath = filePath.TrimStart('/');

        var tarEntry = TarEntry.CreateTarEntry($"package/{filePath}");
        tarEntry.Size = buffer.Length;
        await tarOut.PutNextEntryAsync(tarEntry, cancellationToken);
        await tarOut.WriteAsync(buffer, cancellationToken);
        await tarOut.CloseEntryAsync(cancellationToken);
    }

    private static async Task WriteTextFileToTar(TarOutputStream tarOut, string filePath, string content,
                                                 CancellationToken cancellationToken = default)
    {
        var buffer = Utf8EncodingNoBom.GetBytes(content);
        await WriteBufferToTar(tarOut, filePath, buffer, cancellationToken);
    }

    private static Guid GetStableGuid(PackageIdentity identity, string name)
    {
        return StringToGuid($"{identity.Id}/{name}*");
    }

    private static Guid StringToGuid(string text)
    {
        var guid = new byte[16];
        var inputBytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA1.HashData(inputBytes);
        Array.Copy(hash, 0, guid, 0, guid.Length);

        // Follow UUID for SHA1 based GUID
        const int version = 5; // SHA1 (3 for MD5)
        guid[6] = (byte)((guid[6] & 0x0F) | (version << 4));
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
        return new Guid(guid);
    }
}
}
