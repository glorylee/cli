// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.DotNet.ProjectModel.Graph;
using Microsoft.DotNet.ProjectModel.Resolution;
using NuGet.Frameworks;

namespace Microsoft.DotNet.ProjectModel
{
    public class ProjectContextBuilder
    {
        private Project Project { get; set; }

        private LockFile LockFile { get; set; }

        private GlobalSettings GlobalSettings { get; set; }

        private NuGetFramework TargetFramework { get; set; }

        private IEnumerable<string> RuntimeIdentifiers { get; set; } = Enumerable.Empty<string>();

        private string RootDirectory { get; set; }

        private string ProjectDirectory { get; set; }

        private string PackagesDirectory { get; set; }

        private string ReferenceAssembliesPath { get; set; }

        private Func<string, Project> ProjectResolver { get; set; }

        private Func<string, LockFile> LockFileResolver { get; set; }

        private ProjectReaderSettings Settings { get; set; } = ProjectReaderSettings.ReadFromEnvironment();

        public ProjectContextBuilder()
        {
            ProjectResolver = ResolveProject;
            LockFileResolver = ResolveLockFile;
        }

        public ProjectContextBuilder WithLockFile(LockFile lockFile)
        {
            LockFile = lockFile;
            return this;
        }

        public ProjectContextBuilder WithProject(Project project)
        {
            Project = project;
            return this;
        }

        public ProjectContextBuilder WithProjectDirectory(string projectDirectory)
        {
            ProjectDirectory = projectDirectory;
            return this;
        }

        public ProjectContextBuilder WithTargetFramework(NuGetFramework targetFramework)
        {
            TargetFramework = targetFramework;
            return this;
        }

        public ProjectContextBuilder WithTargetFramework(string targetFramework)
        {
            TargetFramework = NuGetFramework.Parse(targetFramework);
            return this;
        }

        public ProjectContextBuilder WithRuntimeIdentifiers(IEnumerable<string> runtimeIdentifiers)
        {
            RuntimeIdentifiers = runtimeIdentifiers;
            return this;
        }

        public ProjectContextBuilder WithPackagesDirectory(string packagesDirectory)
        {
            PackagesDirectory = packagesDirectory;
            return this;
        }

        public ProjectContextBuilder WithRootDirectory(string rootDirectory)
        {
            RootDirectory = rootDirectory;
            return this;
        }

        public ProjectContextBuilder WithProjectResolver(Func<string, Project> projectResolver)
        {
            ProjectResolver = projectResolver;
            return this;
        }

        public ProjectContextBuilder WithLockFileResolver(Func<string, LockFile> lockFileResolver)
        {
            LockFileResolver = lockFileResolver;
            return this;
        }

        public ProjectContextBuilder WithReaderSettings(ProjectReaderSettings settings)
        {
            Settings = settings;
            return this;
        }

        public IEnumerable<ProjectContext> BuildAllTargets()
        {
            ProjectDirectory = Project?.ProjectDirectory ?? ProjectDirectory;
            EnsureProjectLoaded();
            LockFile = LockFile ?? LockFileResolver(ProjectDirectory);

            if (LockFile != null)
            {
                foreach (var target in LockFile.Targets)
                {
                    yield return new ProjectContextBuilder()
                    .WithProject(Project)
                    .WithLockFile(LockFile)
                    .WithTargetFramework(target.TargetFramework)
                    .WithRuntimeIdentifiers(new[] { target.RuntimeIdentifier })
                    .Build();
                }
            }
        }

        public ProjectContext Build()
        {
            ProjectDirectory = Project?.ProjectDirectory ?? ProjectDirectory;

            if (GlobalSettings == null && ProjectDirectory != null)
            {
                RootDirectory = ProjectRootResolver.ResolveRootDirectory(ProjectDirectory);

                GlobalSettings globalSettings;
                if (GlobalSettings.TryGetGlobalSettings(RootDirectory, out globalSettings))
                {
                    GlobalSettings = globalSettings;
                }
            }

            RootDirectory = GlobalSettings?.DirectoryPath ?? RootDirectory;
            PackagesDirectory = PackagesDirectory ?? PackageDependencyProvider.ResolvePackagesPath(RootDirectory, GlobalSettings);

            LockFileLookup lockFileLookup = null;
            EnsureProjectLoaded();

            LockFile = LockFile ?? LockFileResolver(ProjectDirectory);

            var validLockFile = true;
            string lockFileValidationMessage = null;

            if (LockFile != null)
            {
                if (Project != null)
                {
                    validLockFile = LockFile.IsValidForProject(Project, out lockFileValidationMessage);
                }

                lockFileLookup = new LockFileLookup(LockFile);
            }

            var libraries = new Dictionary<string, LibraryDescription>();
            var projectResolver = new ProjectDependencyProvider(ProjectResolver);

            ProjectDescription mainProject = null;
            if (Project != null)
            {
                mainProject = projectResolver.GetDescription(TargetFramework, Project, targetLibrary: null);

                // Add the main project
                libraries.Add(mainProject.Identity.Name, mainProject);
            }

            LockFileTarget target = null;
            if (lockFileLookup != null)
            {
                target = SelectTarget(LockFile);
                if (target != null)
                {
                    var packageResolver = new PackageDependencyProvider(PackagesDirectory, frameworkReferenceResolver);
                    ScanLibraries(target, lockFileLookup, libraries, packageResolver, projectResolver);
                }
            }

            // Resolve the dependencies
            ResolveDependencies(libraries);

            var diagnostics = new List<DiagnosticMessage>();

            // REVIEW: Should this be in NuGet (possibly stored in the lock file?)
            if (LockFile == null)
            {
                diagnostics.Add(new DiagnosticMessage(
                    ErrorCodes.NU1009,
                    $"The expected lock file doesn't exist. Please run \"dotnet restore\" to generate a new lock file.",
                    Path.Combine(Project.ProjectDirectory, LockFile.FileName),
                    DiagnosticMessageSeverity.Error));
            }

            if (!validLockFile)
            {
                diagnostics.Add(new DiagnosticMessage(
                    ErrorCodes.NU1006,
                    $"{lockFileValidationMessage}. Please run \"dotnet restore\" to generate a new lock file.",
                    Path.Combine(Project.ProjectDirectory, LockFile.FileName),
                    DiagnosticMessageSeverity.Warning));
            }

            // Create a library manager
            var libraryManager = new LibraryManager(libraries.Values.ToList(), diagnostics, Project?.ProjectFilePath);

            return new ProjectContext(
                GlobalSettings,
                mainProject,
                TargetFramework,
                target?.RuntimeIdentifier,
                PackagesDirectory,
                libraryManager,
                LockFile);
        }

        private void ResolveDependencies(Dictionary<string, LibraryDescription> libraries)
        {
            foreach (var pair in libraries.ToList())
            {
                var library = pair.Value;
                library.Framework = library.Framework ?? TargetFramework;

                foreach (var dependency in library.Dependencies)
                {
                    LibraryDescription dependencyDescription;
                    if (!libraries.TryGetValue(dependency.Name, out dependencyDescription))
                    {
                        // a dependency which type is unspecified fails to match, then try to find a 
                        // reference assembly type dependency
                        dependencyDescription = UnresolvedDependencyProvider.GetDescription(dependency, TargetFramework);
                        libraries[dependency.Name] = dependencyDescription;
                    }

                    dependencyDescription.RequestedRanges.Add(dependency);
                    dependencyDescription.Parents.Add(library);
                }
            }
        }

        private void ScanLibraries(LockFileTarget target, 
                                   LockFileLookup lockFileLookup, 
                                   Dictionary<string, LibraryDescription> libraries, 
                                   PackageDependencyProvider packageResolver, 
                                   ProjectDependencyProvider projectDependencyProvider)
        {
            foreach (var library in target.Libraries)
            {
                LibraryDescription description = null;
                var type = LibraryType.Unspecified;

                if (string.Equals(library.Type, "project"))
                {
                    var projectLibrary = lockFileLookup.GetProject(library.Name);

                    if (projectLibrary != null)
                    {
                        var path = Path.GetFullPath(Path.Combine(ProjectDirectory, projectLibrary.Path));
                        description = projectDependencyProvider.GetDescription(library.Name, path, library, ProjectResolver);
                    }

                    type = LibraryType.Project;
                }
                else
                {
                    var packageEntry = lockFileLookup.GetPackage(library.Name, library.Version);

                    if (packageEntry != null)
                    {
                        description = packageResolver.GetDescription(TargetFramework, packageEntry, library);
                    }

                    type = LibraryType.Package;
                }

                description = description ?? UnresolvedDependencyProvider.GetDescription(new LibraryRange(library.Name, type), target.TargetFramework);

                libraries.Add(library.Name, description);
            }
        }

        private void EnsureProjectLoaded()
        {
            if (Project == null && ProjectDirectory != null)
            {
                Project = ProjectResolver(ProjectDirectory);
            }
        }

        private LockFileTarget SelectTarget(LockFile lockFile)
        {
            foreach (var runtimeIdentifier in RuntimeIdentifiers)
            {
                foreach (var scanTarget in lockFile.Targets)
                {
                    if (Equals(scanTarget.TargetFramework, TargetFramework) && string.Equals(scanTarget.RuntimeIdentifier, runtimeIdentifier, StringComparison.Ordinal))
                    {
                        return scanTarget;
                    }
                }
            }

            foreach (var scanTarget in lockFile.Targets)
            {
                if (Equals(scanTarget.TargetFramework, TargetFramework) && string.IsNullOrEmpty(scanTarget.RuntimeIdentifier))
                {
                    return scanTarget;
                }
            }

            return null;
        }

        private Project ResolveProject(string projectDirectory)
        {
            // TODO: Handle diagnostics
            Project project;
            if (ProjectReader.TryGetProject(projectDirectory, out project, diagnostics: null, settings: Settings))
            {
                return project;
            }
            else
            {
                return null;
            }
        }

        private static LockFile ResolveLockFile(string projectDir)
        {
            var projectLockJsonPath = Path.Combine(projectDir, LockFile.FileName);
            return File.Exists(projectLockJsonPath) ?
                        LockFileReader.Read(Path.Combine(projectDir, LockFile.FileName)) :
                        null;
        }
    }
}
