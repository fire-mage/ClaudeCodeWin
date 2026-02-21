using System.IO;
using System.Text.RegularExpressions;

namespace ClaudeCodeWin.Services;

public class InstructionsService
{
    private static readonly string UserProfile =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string GetGlobalClaudeMdPath() =>
        Path.Combine(UserProfile, ".claude", "CLAUDE.md");

    public string GetProjectClaudeMdPath(string workingDir) =>
        Path.Combine(workingDir, "CLAUDE.md");

    public string GetMemoryPath(string workingDir)
    {
        var encoded = EncodePath(Path.GetFullPath(workingDir));
        return Path.Combine(UserProfile, ".claude", "projects", encoded, "memory", "MEMORY.md");
    }

    /// <summary>
    /// Encodes a path to Claude CLI's folder name format.
    /// C:\Users\foo\Project → C--Users-foo-Project
    /// </summary>
    public static string EncodePath(string path)
    {
        // Normalize to forward slashes first, then replace
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        // Replace ':' and '\' with '-'
        return normalized.Replace(':', '-').Replace('\\', '-');
    }

    public string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public bool WriteFile(string path, string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool FileExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    public long GetFileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    /// <summary>
    /// Returns best-practice default content for a new global CLAUDE.md.
    /// </summary>
    public static string GetDefaultGlobalClaudeMd() =>
        """
        # CLAUDE.md (Global)

        This file provides global guidance to Claude Code across all projects.

        ## Communication Language

        Communicate with the user in English.

        ## Bug Fix Policy

        If the first fix attempt does not resolve the problem, stop guessing and start investigating.
        Read logs, add diagnostics, trace the actual execution flow — determine the root cause before making the next fix.

        ## Response Formatting

        After completing a task, write a clear completion summary separated from the working process.
        """;

    /// <summary>
    /// Returns a template for a new project-level CLAUDE.md.
    /// </summary>
    public static string GetDefaultProjectClaudeMd() =>
        """
        # CLAUDE.md

        ## Project Overview

        [Describe your project here]

        ## Tech Stack

        [List technologies used]

        ## Build & Run

        ```
        [Add build/run commands]
        ```

        ## Key Rules

        [Project-specific rules for Claude]
        """;

    /// <summary>
    /// Finds section headers in the project CLAUDE.md that are duplicated in the global CLAUDE.md.
    /// Sections are delimited by ## headers.
    /// </summary>
    public List<string> FindDuplicateBlocks(string globalContent, string projectContent)
    {
        var globalSections = ParseSections(globalContent);
        var projectSections = ParseSections(projectContent);
        var duplicates = new List<string>();

        foreach (var (header, body) in projectSections)
        {
            if (string.IsNullOrWhiteSpace(header)) continue;

            foreach (var (gHeader, gBody) in globalSections)
            {
                if (NormalizeForComparison(header) == NormalizeForComparison(gHeader)
                    && NormalizeForComparison(body) == NormalizeForComparison(gBody))
                {
                    duplicates.Add(header);
                    break;
                }
            }
        }

        return duplicates;
    }

    /// <summary>
    /// Removes sections with the given headers from the content.
    /// Returns the cleaned content, or null if nothing remains.
    /// </summary>
    public string? RemoveDuplicateBlocks(string content, List<string> duplicateHeaders)
    {
        if (duplicateHeaders.Count == 0) return content;

        var sections = ParseSections(content);
        var remaining = new List<(string header, string body)>();

        foreach (var (header, body) in sections)
        {
            var isDuplicate = duplicateHeaders.Any(dh =>
                NormalizeForComparison(dh) == NormalizeForComparison(header));
            if (!isDuplicate)
                remaining.Add((header, body));
        }

        if (remaining.Count == 0)
            return null;

        // Reconstruct: keep only non-empty sections
        var lines = new List<string>();
        foreach (var (header, body) in remaining)
        {
            if (!string.IsNullOrWhiteSpace(header))
            {
                if (lines.Count > 0) lines.Add("");
                lines.Add(header);
            }
            if (!string.IsNullOrWhiteSpace(body))
                lines.Add(body.TrimEnd());
        }

        var result = string.Join("\n", lines).Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    /// <summary>
    /// Parses markdown content into sections by ## headers.
    /// Each section is (header_line, body_text).
    /// Content before the first ## header is returned with an empty header.
    /// </summary>
    private static List<(string header, string body)> ParseSections(string content)
    {
        var sections = new List<(string header, string body)>();
        var lines = content.Split('\n');
        string currentHeader = "";
        var bodyLines = new List<string>();

        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"^#{1,3}\s+"))
            {
                // Save previous section
                if (!string.IsNullOrWhiteSpace(currentHeader) || bodyLines.Count > 0)
                    sections.Add((currentHeader, string.Join("\n", bodyLines)));

                currentHeader = line.TrimEnd('\r');
                bodyLines = [];
            }
            else
            {
                bodyLines.Add(line.TrimEnd('\r'));
            }
        }

        // Save last section
        if (!string.IsNullOrWhiteSpace(currentHeader) || bodyLines.Count > 0)
            sections.Add((currentHeader, string.Join("\n", bodyLines)));

        return sections;
    }

    private static string NormalizeForComparison(string text) =>
        Regex.Replace(text.Trim(), @"\s+", " ").ToLowerInvariant();
}
