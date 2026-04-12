using System.Xml.Linq;

namespace RepoMerger;

internal static class SlnxImporter
{
    public static async Task<string> ImportUnderFolderAsync(
        string sourceSolutionContent,
        string targetSolutionPath,
        string rootFolderName)
    {
        if (string.IsNullOrWhiteSpace(sourceSolutionContent))
            return "Skipped target solution update because no source .slnx content was available.";

        if (!File.Exists(targetSolutionPath))
            return $"Skipped target solution update because '{targetSolutionPath}' does not exist.";

        var sourceDocument = XDocument.Parse(sourceSolutionContent);
        var targetText = await File.ReadAllTextAsync(targetSolutionPath).ConfigureAwait(false);
        var targetDocument = XDocument.Parse(targetText);

        var sourceRoot = sourceDocument.Element("Solution")
            ?? throw new InvalidOperationException("The source .slnx content does not contain a <Solution> root element.");
        var targetRoot = targetDocument.Element("Solution")
            ?? throw new InvalidOperationException($"The target .slnx file '{targetSolutionPath}' does not contain a <Solution> root element.");

        var normalizedRootFolder = NormalizeFolderName(rootFolderName);
        RemoveExistingImportedNodes(targetRoot, normalizedRootFolder);

        var projectPaths = targetRoot.Descendants("Project")
            .Attributes("Path")
            .Select(static attribute => attribute.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filePaths = targetRoot.Descendants("File")
            .Attributes("Path")
            .Select(static attribute => attribute.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var addedFolderCount = 1;
        var addedProjectCount = 0;
        var addedFileCount = 0;

        targetRoot.Add(new XElement("Folder", new XAttribute("Name", normalizedRootFolder)));
        foreach (var sourceElement in sourceRoot.Elements())
        {
            var importedElement = ImportElement(
                sourceElement,
                normalizedRootFolder,
                projectPaths,
                filePaths,
                ref addedFolderCount,
                ref addedProjectCount,
                ref addedFileCount);
            if (importedElement is not null)
                targetRoot.Add(importedElement);
        }

        var updatedText = targetDocument.ToString();
        if (string.Equals(targetText, updatedText, StringComparison.Ordinal))
            return $"Target solution '{targetSolutionPath}' already contained the imported Razor folder structure.";

        await File.WriteAllTextAsync(targetSolutionPath, updatedText).ConfigureAwait(false);
        return
            $"Updated '{targetSolutionPath}' with {addedProjectCount} imported project(s), " +
            $"{addedFileCount} file item(s), and {addedFolderCount} folder entr{(addedFolderCount == 1 ? "y" : "ies")} under '{normalizedRootFolder}'.";
    }

    private static XElement? ImportElement(
        XElement sourceElement,
        string normalizedRootFolder,
        HashSet<string> projectPaths,
        HashSet<string> filePaths,
        ref int addedFolderCount,
        ref int addedProjectCount,
        ref int addedFileCount)
    {
        return sourceElement.Name.LocalName switch
        {
            "Folder" => ImportFolder(
                sourceElement,
                normalizedRootFolder,
                projectPaths,
                filePaths,
                ref addedFolderCount,
                ref addedProjectCount,
                ref addedFileCount),
            "Project" => ImportPathElement(sourceElement, "Path", projectPaths, ref addedProjectCount),
            "File" => ImportPathElement(sourceElement, "Path", filePaths, ref addedFileCount),
            _ => new XElement(sourceElement),
        };
    }

    private static XElement ImportFolder(
        XElement sourceFolder,
        string normalizedRootFolder,
        HashSet<string> projectPaths,
        HashSet<string> filePaths,
        ref int addedFolderCount,
        ref int addedProjectCount,
        ref int addedFileCount)
    {
        var folderName = sourceFolder.Attribute("Name")?.Value ?? string.Empty;
        var importedFolder = new XElement("Folder", new XAttribute("Name", PrefixFolderName(normalizedRootFolder, folderName)));
        addedFolderCount++;

        foreach (var child in sourceFolder.Elements())
        {
            var importedChild = ImportElement(
                child,
                normalizedRootFolder,
                projectPaths,
                filePaths,
                ref addedFolderCount,
                ref addedProjectCount,
                ref addedFileCount);
            if (importedChild is not null)
                importedFolder.Add(importedChild);
        }

        return importedFolder;
    }

    private static XElement? ImportPathElement(
        XElement sourceElement,
        string attributeName,
        HashSet<string> seenPaths,
        ref int addedCount)
    {
        var path = sourceElement.Attribute(attributeName)?.Value;
        if (string.IsNullOrWhiteSpace(path) || !seenPaths.Add(path))
            return null;

        addedCount++;
        return new XElement(sourceElement);
    }

    private static void RemoveExistingImportedNodes(XElement targetRoot, string normalizedRootFolder)
    {
        foreach (var element in targetRoot.Elements().Where(element => ShouldRemove(element, normalizedRootFolder)).ToArray())
            element.Remove();
    }

    private static bool ShouldRemove(XElement element, string normalizedRootFolder)
    {
        if (element.Name.LocalName == "Folder")
        {
            var folderName = element.Attribute("Name")?.Value ?? string.Empty;
            return folderName.StartsWith(normalizedRootFolder, StringComparison.OrdinalIgnoreCase);
        }

        if (element.Name.LocalName is "Project" or "File")
        {
            var path = element.Attribute("Path")?.Value ?? string.Empty;
            return path.StartsWith("src/Razor/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(@"src\Razor\", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string PrefixFolderName(string normalizedRootFolder, string sourceFolderName)
    {
        var rootFolder = normalizedRootFolder.TrimEnd('/');
        var trimmedSourceFolder = sourceFolderName.Trim().Trim('/');
        return string.IsNullOrWhiteSpace(trimmedSourceFolder)
            ? normalizedRootFolder
            : $"{rootFolder}/{trimmedSourceFolder}/";
    }

    private static string NormalizeFolderName(string folderName)
    {
        var trimmed = folderName.Trim().Trim('/');
        if (trimmed.Length == 0)
            throw new InvalidOperationException("Solution folder name must not be empty.");

        return $"/{trimmed}/";
    }
}
