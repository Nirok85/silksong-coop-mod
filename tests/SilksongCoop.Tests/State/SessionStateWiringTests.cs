using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SilksongCoop.Tests.State
{
    /// <summary>
    /// Source-structure regression tests for the shared <c>CoopSessionState</c> composition.
    /// The plugin owns the single production instance and injects it into <c>SessionService</c>,
    /// so the UI/hooks and every sync manager observe the same Connected/RemotePeerId state.
    /// </summary>
    public class SessionStateWiringTests
    {
        private const string PluginRelativePath = "src/SilksongCoop.Mod/SilksongCoopPlugin.cs";
        private const string SessionServiceRelativePath = "src/SilksongCoop.Mod/Services/SessionService.cs";

        private static readonly Regex NewCoopSessionStatePattern =
            new Regex(@"new\s+CoopSessionState\s*\(", RegexOptions.Compiled);

        private static readonly Regex SessionServiceConstructionPattern =
            new Regex(@"new\s+SessionService\s*\([\s\S]*?SessionState\s*\)", RegexOptions.Compiled);

        private static readonly Regex SessionServiceConstructorPattern =
            new Regex(
                @"public\s+SessionService\s*\([\s\S]*?CoopSessionState\s+sessionState\s*\)",
                RegexOptions.Compiled);

        private static readonly Regex SessionStateAssignmentPattern =
            new Regex(
                @"SessionState\s*=\s*sessionState\s*\?\?\s*throw",
                RegexOptions.Compiled);

        [Fact]
        public void ProductionCode_CreatesExactlyOneCoopSessionState_InThePlugin()
        {
            var occurrences = ReadProductionSources()
                .SelectMany(source => NewCoopSessionStatePattern
                    .Matches(source.Value)
                    .Select(_ => source.Key))
                .ToList();

            Assert.True(
                occurrences.Count == 1,
                "Expected exactly one production 'new CoopSessionState(' but found " +
                $"{occurrences.Count}:{Environment.NewLine}{string.Join(Environment.NewLine, occurrences)}");

            Assert.EndsWith(
                PluginRelativePath,
                ToRelativePath(occurrences[0]).Replace('\\', '/'),
                StringComparison.Ordinal);
        }

        [Fact]
        public void SessionService_DoesNotCreateItsOwnCoopSessionState()
        {
            var source = ReadSourceFile(SessionServiceRelativePath);

            Assert.False(
                NewCoopSessionStatePattern.IsMatch(source),
                $"{SessionServiceRelativePath} must not create a second CoopSessionState; " +
                "it must receive the plugin's instance through its constructor.");
        }

        [Fact]
        public void SessionService_StoresTheInjectedCoopSessionState()
        {
            var source = ReadSourceFile(SessionServiceRelativePath);

            Assert.Matches(SessionServiceConstructorPattern, source);
            Assert.Matches(SessionStateAssignmentPattern, source);
        }

        [Fact]
        public void Plugin_ConstructsSessionService_WithThePluginSessionState()
        {
            var source = ReadSourceFile(PluginRelativePath);

            Assert.Single(SessionServiceConstructionPattern.Matches(source).Cast<Match>());
        }

        /// <summary>
        /// Walks up from the test binaries until the repository root (SilksongCoop.sln) is found,
        /// so the static check does not depend on the output directory depth.
        /// </summary>
        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SilksongCoop.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"Could not locate the repository root (SilksongCoop.sln) above '{AppContext.BaseDirectory}'.");
        }

        private static IReadOnlyList<KeyValuePair<string, string>> ReadProductionSources()
        {
            var repositoryRoot = FindRepositoryRoot();
            var sourceRoot = Path.Combine(repositoryRoot, "src");

            if (!Directory.Exists(sourceRoot))
                throw new InvalidOperationException($"Production source directory not found: '{sourceRoot}'.");

            return Directory
                .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsBuildOutput(repositoryRoot, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => new KeyValuePair<string, string>(path, File.ReadAllText(path)))
                .ToList();
        }

        private static string ReadSourceFile(string relativePath)
        {
            var path = Path.Combine(
                FindRepositoryRoot(),
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Expected source file not found: '{path}'.");
            return File.ReadAllText(path);
        }

        private static string ToRelativePath(string path) =>
            Path.GetRelativePath(FindRepositoryRoot(), path);

        private static bool IsBuildOutput(string repositoryRoot, string path)
        {
            var parts = Path.GetRelativePath(repositoryRoot, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return parts.Any(part =>
                string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase));
        }
    }
}
